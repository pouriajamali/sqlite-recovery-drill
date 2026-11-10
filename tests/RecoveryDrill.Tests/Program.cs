using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RecoveryDrill;

internal static class Program
{
    private static string Root = "";
    private static int Count, Skipped;
    private static readonly List<object> Observations = [];
    private static void Require(bool okay, string message)
    {
        if (!okay)
            throw new Exception(message);
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static string Fresh(string name)
    {
        string path = Path.Combine(Root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static byte[] Bytes(WorkerReceipt receipt) => Encoding.UTF8.GetBytes(Results.Serialize(receipt));
    private static WorkerReceipt Passing() => new(1, true, Results.Profile, "pass", Results.Ids.Select(id => new CheckResult(id, "pass", "OK")).ToArray());
    private static async Task<TransportResult> Inspect(string trial, string token)
    {
        await Task.Yield();
        try
        {
            return new(0, Bytes(Inspector.Inspect(trial)), "");
        }
        catch
        {
            return new(1, [], "WORKER_FAILED");
        }
    }

    private static async Task Case(string name, Func<Task> action)
    {
        await action();
        Count++;
        Observations.Add(new { name, result = "pass" });
        Console.WriteLine("PASS " + name);
    }

    private static async Task Fixture(string name, int exit, string? failedCode)
    {
        string folder = Fresh("fixture-" + name), source = Path.Combine(folder, "source.sqlite"), output = Path.Combine(folder, "report with spaces");
        Demo.Create(source, name);
        string before = Hash(source);
        RunReport report = await Drill.CheckAsync(source, output, new(Worker: Inspect));
        Require(report.ExitCode == exit, name + ": unexpected exit " + report.Reason);
        if (failedCode is not null)
            Require(report.Checks.Any(c => c.Code == failedCode), name + ": missing named failure");
        Require(before == Hash(source), name + ": source changed");
        Require(!File.Exists(Path.Combine(output, "trial.sqlite")), name + ": trial retained");
        Require(File.Exists(Path.Combine(output, "summary.json")) && File.Exists(Path.Combine(output, "report.html")), name + ": reports missing");
        Require(report.Freshness == "NOT_CHECKED" && report.ExternalCompleteness == "NOT_CHECKED", "False freshness/completeness");
        Require(!File.ReadAllText(Path.Combine(output, "summary.json")).Contains(folder, StringComparison.Ordinal), "Private path leaked");
    }

    private static ProcessStartInfo Self(string mode)
    {
        string host = Environment.ProcessPath!;
        var start = new ProcessStartInfo(host);
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--probe");
        start.ArgumentList.Add(mode);
        return start;
    }

    private static async Task<int> Probe(string mode)
    {
        if (mode == "hang")
        {
            await Task.Delay(Timeout.Infinite);
            return 0;
        }

        if (mode == "stdout-flood")
        {
            for (int i = 0; i < 1000; i++)
                Console.Write(new string ('x', 1024));
            return 0;
        }

        if (mode == "stderr-flood")
        {
            for (int i = 0; i < 1000; i++)
                Console.Error.Write(new string ('x', 1024));
            return 0;
        }

        if (mode == "failure")
            return 17;
        Console.WriteLine(Results.Serialize(Passing()));
        return 0;
    }

    public static async Task<int> Main(string[] args)
    {
        if (args is ["--probe", var mode])
            return await Probe(mode);
        string repo = Directory.GetCurrentDirectory();
        Root = Path.GetFullPath(args is ["--work", var requested] ? requested : Path.Combine(repo, "artifacts", "tests-" + Guid.NewGuid().ToString("N")));
        if (Directory.Exists(Root) || File.Exists(Root))
        {
            Console.Error.WriteLine("Fresh test work path required");
            return 1;
        }

        Directory.CreateDirectory(Root);
        try
        {
            foreach (var c in new (string Name, int Exit, string? Code)[]
            {
                ("valid", 0, null),
                ("stale-valid", 0, null),
                ("foreign-key", 2, "FOREIGN_KEY_VIOLATION"),
                ("null-value", 2, "STORAGE_CLASS_MISMATCH"),
                ("text-integer", 2, "STORAGE_CLASS_MISMATCH"),
                ("blob-integer", 2, "STORAGE_CLASS_MISMATCH"),
                ("real-integer", 2, "STORAGE_CLASS_MISMATCH"),
                ("range", 2, "VALUE_RANGE"),
                ("bad-status", 2, "APPLICATION_INVARIANT"),
                ("batch-count", 2, "APPLICATION_INVARIANT"),
                ("schema-version", 2, "SCHEMA_MISMATCH"),
                ("truncated", 1, null),
                ("corrupt-page", 2, "INTEGRITY_FAILED")
            }

            )
            {
                var current = c;
                await Case("fixture/" + c.Name, () => Fixture(current.Name, current.Exit, current.Code));
            }

            foreach (string suffix in new[]
            {
                "-wal",
                "-shm",
                "-journal"
            }

            )
                await Case("sidecar/" + suffix, async () =>
                {
                    string f = Fresh("sidecar" + suffix), input = Path.Combine(f, "source.sqlite");
                    Demo.Create(input, "valid");
                    File.WriteAllText(input + suffix, "synthetic sidecar; must not be removed");
                    string before = Hash(input), side = Hash(input + suffix);
                    var report = await Drill.CheckAsync(input, Path.Combine(f, "out"), new(Worker: Inspect));
                    Require(report.ExitCode == 1 && report.Reason == "SIDECAR_UNSUPPORTED", "Sidecar accepted");
                    Require(before == Hash(input) && side == Hash(input + suffix), "Source/sidecar modified");
                });
            await Case("wal-header-is-unsupported-not-corrupt", async () =>
            {
                string f = Fresh("wal-header"), input = Path.Combine(f, "source.sqlite");
                Demo.Create(input, "valid");
                using (var file = File.OpenWrite(input))
                {
                    file.Position = 18;
                    file.WriteByte(2);
                    file.WriteByte(2);
                }

                var r = await Drill.CheckAsync(input, Path.Combine(f, "out"), new(Worker: Inspect));
                Require(r.ExitCode == 1 && r.Reason == "WAL_MODE_UNSUPPORTED", "WAL header misclassified");
            });
            await Case("actual-stream-byte-cap", () =>
            {
                using var input = new MemoryStream(new byte[2049]);
                using var output = new MemoryStream();
                try
                {
                    Drill.CopyBounded(input, output, 2048);
                    throw new Exception("Oversized stream accepted");
                }
                catch (DrillFailure e)
                {
                    Require(e.Message == "INPUT_BYTE_LIMIT" && output.Length <= 2048, "Copy cap failed");
                }

                return Task.CompletedTask;
            });
            await Case("initial-file-cap", async () =>
            {
                string f = Fresh("file-cap"), input = Path.Combine(f, "large.sqlite");
                using (var file = File.Create(input))
                    file.SetLength(Drill.ByteLimit + 1);
                var r = await Drill.CheckAsync(input, Path.Combine(f, "out"));
                Require(r.ExitCode == 1 && r.Reason == "INPUT_BYTE_LIMIT", "Stat cap failed");
            });
            await Case("network-style-path-refused-before-io", async () =>
            {
                var r = await Drill.CheckAsync("//not-a-real-host/share/source.sqlite", Path.Combine(Root, "network-out"));
                Require(r.ExitCode == 1 && r.Reason == "NETWORK_PATH_UNSUPPORTED" && !Directory.Exists(Path.Combine(Root, "network-out")), "Network-style path touched");
            });
            await Case("source-rehash-detects-same-size-change", async () =>
            {
                string f = Fresh("source-change"), input = Path.Combine(f, "source.sqlite");
                Demo.Create(input, "valid");
                DateTime stamp = File.GetLastWriteTimeUtc(input);
                var r = await Drill.CheckAsync(input, Path.Combine(f, "out"), new(Worker: Inspect, AfterCopy: _ =>
                {
                    using (var file = File.OpenWrite(input))
                    {
                        file.Position = 100;
                        file.WriteByte(0x5a);
                    }

                    File.SetLastWriteTimeUtc(input, stamp);
                }));
                Require(r.ExitCode == 1 && r.Reason == "INPUT_CHANGED", "Source hash change accepted");
            });
            await Case("copy-rehash-detects-tamper", async () =>
            {
                string f = Fresh("copy-change"), input = Path.Combine(f, "source.sqlite");
                Demo.Create(input, "valid");
                var r = await Drill.CheckAsync(input, Path.Combine(f, "out"), new(Worker: Inspect, AfterCopy: copy =>
                {
                    using var file = File.OpenWrite(copy);
                    file.Position = 100;
                    file.WriteByte(0x5a);
                }));
                Require(r.ExitCode == 1 && r.Reason == "COPY_MISMATCH", "Copy hash change accepted");
            });
            await Case("sidecar-created-during-copy", async () =>
            {
                string f = Fresh("new-sidecar"), input = Path.Combine(f, "source.sqlite");
                Demo.Create(input, "valid");
                var r = await Drill.CheckAsync(input, Path.Combine(f, "out"), new(Worker: Inspect, AfterCopy: _ => File.WriteAllText(input + "-wal", "synthetic")));
                Require(r.ExitCode == 1 && r.Reason == "SIDECAR_UNSUPPORTED" && File.Exists(input + "-wal"), "Late sidecar missed or removed");
            });
            await Case("source-change-during-inspection-never-passes", async () =>
            {
                string f = Fresh("source-worker-change"), input = Path.Combine(f, "source.sqlite");
                Demo.Create(input, "valid");
                var r = await Drill.CheckAsync(input, Path.Combine(f, "out"), new(Worker: (_, _) =>
                {
                    using (var file = File.OpenWrite(input))
                    {
                        file.Position = 100;
                        file.WriteByte(0x5a);
                    }

                    return Task.FromResult(new TransportResult(0, Bytes(Passing()), ""));
                }));
                Require(r.ExitCode == 1 && r.Reason == "INPUT_CHANGED", "Late source change accepted");
            });
            await Case("trial-sidecar-preserved-and-cleanup-incomplete", async () =>
            {
                string f = Fresh("trial-sidecar"), input = Path.Combine(f, "source.sqlite"), output = Path.Combine(f, "out");
                Demo.Create(input, "valid");
                var r = await Drill.CheckAsync(input, output, new(Worker: (trial, _) =>
                {
                    File.WriteAllText(trial + "-wal", "synthetic owned-sidecar fault");
                    return Task.FromResult(new TransportResult(0, Bytes(Passing()), ""));
                }));
                Require(r.ExitCode == 1 && r.Reason == "CLEANUP_FAILED" && r.TemporaryDataMayRemain && File.Exists(Path.Combine(output, "trial.sqlite-wal")), "Journal removed or cleanup passed");
            });
            await Case("existing-output-and-collision-preserved", async () =>
            {
                string f = Fresh("existing"), input = Path.Combine(f, "source.sqlite"), output = Path.Combine(f, "out");
                Demo.Create(input, "valid");
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, "sentinel"), "untouched");
                var r = await Drill.CheckAsync(input, output);
                Require(r.Reason == "OUTPUT_EXISTS" && File.ReadAllText(Path.Combine(output, "sentinel")) == "untouched", "Output overwritten");
                r = await Drill.CheckAsync(input, input);
                Require(r.Reason == "PATH_COLLISION", "Source/output collision accepted");
            });
            await Case("cleanup-failure-is-incomplete", async () =>
            {
                string f = Fresh("cleanup-failure"), input = Path.Combine(f, "source.sqlite"), output = Path.Combine(f, "out");
                Demo.Create(input, "valid");
                var r = await Drill.CheckAsync(input, output, new(Worker: Inspect, DeleteTrial: _ => throw new IOException("synthetic private-path-not-for-report")));
                Require(r.ExitCode == 1 && !r.Complete && r.Outcome == "incomplete" && r.Cleanup == "failed" && r.TemporaryDataMayRemain, "Cleanup failure passed");
                string json = File.ReadAllText(Path.Combine(output, "summary.json"));
                Require(json.Contains("CLEANUP_FAILED") && !json.Contains("private-path-not-for-report"), "Wrong cleanup report");
                // Only this known generated test copy is removed; never recursively delete a caller path.
                string owned = Path.GetFullPath(Path.Combine(output, "trial.sqlite"));
                Require(owned.StartsWith(Root + Path.DirectorySeparatorChar, Drill.PathComparison), "Unsafe test cleanup");
                File.Delete(owned);
            });
            await Case("report-collision-does-not-publish-pass-json", async () =>
            {
                string f = Fresh("report-collision"), input = Path.Combine(f, "source.sqlite"), output = Path.Combine(f, "out");
                Demo.Create(input, "valid");
                var r = await Drill.CheckAsync(input, output, new(Worker: Inspect, AfterCopy: copy => File.WriteAllText(Path.Combine(Path.GetDirectoryName(copy)!, "report.html"), "sentinel")));
                Require(r.ExitCode == 1 && r.Reason == "REPORT_WRITE_FAILED" && !File.Exists(Path.Combine(output, "summary.json")), "Report failure published PASS");
            });
            await Case("row-cap-before-app-reads", async () =>
            {
                string f = Fresh("row-cap"), input = Path.Combine(f, "source.sqlite");
                Demo.Create(input, "valid");
                using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = input, Pooling = false }.ToString()))
                {
                    c.Open();
                    Inspector.Execute(c, "WITH RECURSIVE n(x) AS (SELECT 4 UNION ALL SELECT x+1 FROM n WHERE x<10001) INSERT INTO jobs SELECT x,1,'queued',0 FROM n;");
                }

                var r = await Drill.CheckAsync(input, Path.Combine(f, "out"), new(Worker: Inspect));
                Require(r.ExitCode == 2 && r.Checks.Any(c => c.Code == "ROW_LIMIT"), "Row cap missed");
            });
            await Case("unexpected-schema-object-before-app-reads", async () =>
            {
                string f = Fresh("schema-object"), input = Path.Combine(f, "source.sqlite");
                Demo.Create(input, "valid");
                using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = input, Pooling = false }.ToString()))
                {
                    c.Open();
                    Inspector.Execute(c, "CREATE VIEW surprise AS SELECT * FROM jobs;");
                }

                var r = await Drill.CheckAsync(input, Path.Combine(f, "out"), new(Worker: Inspect));
                Require(r.ExitCode == 2 && r.Checks.Any(c => c.Code == "SCHEMA_MISMATCH"), "Unexpected object accepted");
            });
            foreach (string protocolCase in new[]
            {
                "empty",
                "truncated",
                "extra",
                "unknown-field",
                "duplicate-field",
                "wrong-version",
                "incomplete",
                "missing-check",
                "false-pass",
                "bad-order"
            }

            )
                await Case("worker-protocol/" + protocolCase, async () =>
                {
                    string f = Fresh("protocol-" + protocolCase), input = Path.Combine(f, "source.sqlite");
                    Demo.Create(input, "valid");
                    string valid = Results.Serialize(Passing());
                    string data = protocolCase switch
                    {
                        "empty" => "",
                        "truncated" => valid[..^4],
                        "extra" => valid + "extra",
                        "unknown-field" => valid.Replace("\"schema\": 1", "\"extra\": 2, \"schema\": 1", StringComparison.Ordinal),
                        "duplicate-field" => valid.Replace("\"schema\": 1", "\"schema\": 1, \"schema\": 1", StringComparison.Ordinal),
                        "wrong-version" => Results.Serialize(Passing()with { Schema = 99 }),
                        "incomplete" => Results.Serialize(Passing()with { Complete = false }),
                        "missing-check" => Results.Serialize(Passing()with { Checks = Passing().Checks[..^1] }),
                        "false-pass" => Results.Serialize(Passing()with { Checks = Results.Unchecked() }),
                        _ => Results.Serialize(Passing()with { Checks = Passing().Checks.Reverse().ToArray() })};
                    var r = await Drill.CheckAsync(input, Path.Combine(f, "out"), new(Worker: (_, _) => Task.FromResult(new TransportResult(0, Encoding.UTF8.GetBytes(data), ""))));
                    Require(r.ExitCode == 1 && r.Reason == "WORKER_PROTOCOL_INVALID" && !r.Complete, "Invalid worker output passed");
                });
            await Case("oversized-receipt-before-json-parse", async () =>
            {
                string f = Fresh("oversized-receipt"), input = Path.Combine(f, "source.sqlite");
                Demo.Create(input, "valid");
                var r = await Drill.CheckAsync(input, Path.Combine(f, "out"), new(Worker: (_, _) => Task.FromResult(new TransportResult(0, new byte[Transport.OutputLimit + 1], ""))));
                Require(r.ExitCode == 1 && r.Reason == "WORKER_OUTPUT_LIMIT", "Oversized receipt parsed or passed");
            });
            foreach (var c in new[]
            {
                ("hang", "WORKER_TIMEOUT"),
                ("stdout-flood", "WORKER_OUTPUT_LIMIT"),
                ("stderr-flood", "WORKER_OUTPUT_LIMIT"),
                ("failure", "WORKER_FAILED")
            }

            )
                await Case("owned-process/" + c.Item1, async () =>
                {
                    var watch = Stopwatch.StartNew();
                    var r = await Transport.RunAsync(Self(c.Item1), c.Item1 == "hang" ? 300 : 3000);
                    Require(r.Problem == c.Item2 && watch.Elapsed < TimeSpan.FromSeconds(7), "Process/pipe boundary failed: " + r.Problem);
                    Require(r.Stdout.Length <= Transport.OutputLimit, "Output allocation cap failed");
                });
            await Case("html-escapes-dynamic-text", () =>
            {
                var r = Results.Incomplete("<script>alert('x')</script>")with
                {
                    ToolVersion = "<img src=x onerror=alert(1)>"
                };
                string html = Results.Html(r);
                Require(!html.Contains("<script>") && !html.Contains("<img src=x") && html.Contains("&lt;script&gt;"), "HTML injection");
                Require(!html.Contains("<script ") && !html.Contains("https://"), "Unexpected network asset");
                return Task.CompletedTask;
            });
            string linkFolder = Fresh("link"), linkTarget = Path.Combine(linkFolder, "target.sqlite"), link = Path.Combine(linkFolder, "input-link.sqlite");
            Demo.Create(linkTarget, "valid");
            try
            {
                File.CreateSymbolicLink(link, linkTarget);
                await Case("symlink-source-refused", async () =>
                {
                    var r = await Drill.CheckAsync(link, Path.Combine(linkFolder, "out"));
                    Require(r.ExitCode == 1 && r.Reason == "LINK_PATH_UNSUPPORTED", "Symlink accepted");
                });
            }
            catch (Exception error)when (OperatingSystem.IsWindows() && (error is UnauthorizedAccessException || error is IOException && error.HResult == unchecked((int)0x80070522)))
            {
                Skipped++;
                Observations.Add(new { name = "symlink-source-refused", result = "skip", reason = "Local OS symbolic-link privilege unavailable; matching-OS CI must exercise it" });
            }

            string directoryLink = Path.Combine(linkFolder, "directory-link");
            try
            {
                Directory.CreateSymbolicLink(directoryLink, linkFolder);
                await Case("symlink-output-component-refused", async () =>
                {
                    var r = await Drill.CheckAsync(linkTarget, Path.Combine(directoryLink, "new-out"));
                    Require(r.ExitCode == 1 && r.Reason == "LINK_PATH_UNSUPPORTED", "Directory link accepted");
                });
            }
            catch (Exception error)when (OperatingSystem.IsWindows() && (error is UnauthorizedAccessException || error is IOException && error.HResult == unchecked((int)0x80070522)))
            {
                Skipped++;
                Observations.Add(new { name = "symlink-output-component-refused", result = "skip", reason = "Local OS symbolic-link privilege unavailable; matching-OS CI must exercise it" });
            }

            await Case("actual-cli-demo-and-argument-rejection", async () =>
            {
                string host = Environment.GetEnvironmentVariable("DRILL_TEST_APP") ?? Path.Combine(repo, "src", "RecoveryDrill", "bin", "Release", "net10.0", OperatingSystem.IsWindows() ? "drill.exe" : "drill");
                if (!File.Exists(host))
                    throw new Exception("Build source app first");
                var start = new ProcessStartInfo(host);
                start.ArgumentList.Add("demo");
                start.ArgumentList.Add(Path.Combine(Root, "actual CLI demo"));
                var r = await Transport.RunAsync(start, 20000);
                Require(r.ExitCode == 0 && Encoding.UTF8.GetString(r.Stdout).Contains("DEMO PASS"), "CLI demo failed: " + r.Problem);
                start = new(host);
                start.ArgumentList.Add("--sql");
                start.ArgumentList.Add("not-executed");
                r = await Transport.RunAsync(start);
                Require(r.ExitCode == 1, "Untrusted SQL option accepted");
            });
            File.WriteAllText(Path.Combine(Root, "receipt.json"), JsonSerializer.Serialize(new { status = "R1_LOCAL_TESTS_PASS", passed = Count, failed = 0, skipped = Skipped, cases = Observations }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"SUMMARY passed={Count} failed=0 skipped={Skipped}");
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(Root, "failure.txt"), error.ToString());
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
