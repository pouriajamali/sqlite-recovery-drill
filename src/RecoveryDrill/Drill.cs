using System.Security.Cryptography;
using System.Text;

namespace RecoveryDrill;

internal sealed class DrillFailure(string code) : Exception(code);
internal sealed record RunHooks(Func<string, string, Task<TransportResult>>? Worker = null, Action<string>? AfterCopy = null, Action<string>? DeleteTrial = null);

internal static class Drill
{
    internal const long ByteLimit = 16 * 1024 * 1024;
    internal static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static string LocalPath(string argument)
    {
        if (argument.StartsWith("\\\\", StringComparison.Ordinal) || argument.StartsWith("//", StringComparison.Ordinal)) throw new DrillFailure("NETWORK_PATH_UNSUPPORTED");
        string path = Path.GetFullPath(argument);
        if (OperatingSystem.IsWindows() && (path.StartsWith("\\\\", StringComparison.Ordinal) || new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Network))
            throw new DrillFailure("NETWORK_PATH_UNSUPPORTED");
        return path;
    }

    internal static void NoLinks(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null)
        {
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null || (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)) throw new DrillFailure("LINK_PATH_UNSUPPORTED");
            current = Path.GetDirectoryName(current);
        }
    }
    internal static void NoSidecars(string source)
    {
        foreach (string suffix in new[] { "-wal", "-shm", "-journal" })
        {
            var info = new FileInfo(source + suffix);
            if (info.Exists || Directory.Exists(info.FullName) || info.LinkTarget is not null) throw new DrillFailure("SIDECAR_UNSUPPORTED");
        }
    }

    internal static (long Bytes, string Hash) CopyBounded(Stream input, Stream? output, long limit = ByteLimit)
    {
        byte[] buffer = new byte[16 * 1024]; long total = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int n;
        while ((n = input.Read(buffer)) != 0)
        {
            if (total > limit - n) throw new DrillFailure("INPUT_BYTE_LIMIT");
            output?.Write(buffer, 0, n); hash.AppendData(buffer, 0, n); total += n;
        }
        return (total, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    internal static void Header(Stream input)
    {
        byte[] header = new byte[100];
        if (input.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) != header.Length ||
            !header.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8)) throw new DrillFailure("SQLITE_HEADER_INVALID");
        if (header[18] == 2 || header[19] == 2) throw new DrillFailure("WAL_MODE_UNSUPPORTED");
        if (header[18] != 1 || header[19] != 1) throw new DrillFailure("SQLITE_HEADER_INVALID");
        input.Position = 0;
    }

    internal static async Task<RunReport> CheckAsync(string sourceArgument, string outputArgument, RunHooks? hooks = null)
    {
        hooks ??= new(); string output = "", trial = "", hash = "NOT_AVAILABLE"; long bytes = 0;
        bool ownsOutput = false, ownsTrial = false;
        RunReport report = Results.Incomplete("NOT_STARTED");
        try
        {
            string source = LocalPath(sourceArgument); output = LocalPath(outputArgument);
            NoLinks(source); NoLinks(output);
            if (source.Equals(output, PathComparison)) throw new DrillFailure("PATH_COLLISION");
            if (File.Exists(output) || Directory.Exists(output)) throw new DrillFailure("OUTPUT_EXISTS");
            var before = new FileInfo(source);
            if (!before.Exists || Directory.Exists(source)) throw new DrillFailure("SOURCE_NOT_FILE");
            if (before.Length > ByteLimit) throw new DrillFailure("INPUT_BYTE_LIMIT");
            long expectedBytes = before.Length; DateTime expectedWrite = before.LastWriteTimeUtc;
            NoSidecars(source);
            Directory.CreateDirectory(output);
            string token = Guid.NewGuid().ToString("N");
            using (var owner = new FileStream(Path.Combine(output, ".drill-owned"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                owner.Write(Encoding.ASCII.GetBytes(token));
            ownsOutput = true;
            trial = Path.Combine(output, "trial.sqlite");
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Header(input);
                using var copy = new FileStream(trial, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                ownsTrial = true;
                (bytes, hash) = CopyBounded(input, copy); copy.Flush(flushToDisk: true);
            }
            hooks.AfterCopy?.Invoke(trial);
            NoLinks(source); NoLinks(trial); NoSidecars(source); NoSidecars(trial);
            var after = new FileInfo(source);
            if (!after.Exists || after.Length != expectedBytes || after.LastWriteTimeUtc != expectedWrite || bytes != expectedBytes) throw new DrillFailure("INPUT_CHANGED");
            using (var inputAgain = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var actual = CopyBounded(inputAgain, null);
                if (actual.Bytes != bytes || actual.Hash != hash) throw new DrillFailure("INPUT_CHANGED");
            }
            using (var copyAgain = new FileStream(trial, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var actual = CopyBounded(copyAgain, null);
                if (actual.Bytes != bytes || actual.Hash != hash) throw new DrillFailure("COPY_MISMATCH");
            }
            NoSidecars(source); after.Refresh();
            if (after.Length != expectedBytes || after.LastWriteTimeUtc != expectedWrite) throw new DrillFailure("INPUT_CHANGED");
            TransportResult child = hooks.Worker is null ? await Transport.RunAsync(Transport.OwnWorker(trial, token)) : await hooks.Worker(trial, token);
            if (child.Problem.Length > 0 || child.ExitCode != 0) throw new DrillFailure(child.Problem.Length > 0 ? child.Problem : "WORKER_FAILED");
            if (child.Stdout.Length > Transport.OutputLimit) throw new DrillFailure("WORKER_OUTPUT_LIMIT");
            NoLinks(source); NoLinks(trial); NoSidecars(source); NoSidecars(trial);
            after.Refresh();
            if (after.Length != expectedBytes || after.LastWriteTimeUtc != expectedWrite) throw new DrillFailure("INPUT_CHANGED");
            using (var inputFinal = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var actual = CopyBounded(inputFinal, null);
                if (actual.Bytes != bytes || actual.Hash != hash) throw new DrillFailure("INPUT_CHANGED");
            }
            using (var copyFinal = new FileStream(trial, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var actual = CopyBounded(copyFinal, null);
                if (actual.Bytes != bytes || actual.Hash != hash) throw new DrillFailure("COPY_MISMATCH");
            }
            WorkerReceipt result;
            try { result = Results.ParseWorker(child.Stdout); }
            catch (Exception error) when (error is System.Text.Json.JsonException or InvalidDataException or InvalidOperationException or FormatException or OverflowException)
            { throw new DrillFailure("WORKER_PROTOCOL_INVALID"); }
            report = new("recovery-drill-report-v1", Results.Version, Results.Profile, result.Outcome, result.Outcome == "pass" ? 0 : 2,
                hash, bytes, result.Checks, "pending", "CHECKS_COMPLETE", false);
        }
        catch (DrillFailure error) { report = Results.Incomplete(error.Message, hash, bytes); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
        { report = Results.Incomplete("IO_OR_WORKER_FAILED", hash, bytes); }
        finally
        {
            if (ownsTrial)
            {
                try
                {
                    NoLinks(output); NoLinks(trial); NoSidecars(trial);
                    if (hooks.DeleteTrial is null) File.Delete(trial); else hooks.DeleteTrial(trial);
                    if (File.Exists(trial)) throw new IOException("Owned trial retained");
                    report = report with { Cleanup = "removed" };
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or DrillFailure)
                { report = report with { Outcome = "incomplete", ExitCode = 1, Complete = false, Cleanup = "failed", Reason = "CLEANUP_FAILED", TemporaryDataMayRemain = true }; }
            }
        }
        if (ownsOutput)
        {
            try { WriteReports(output, report); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { report = report with { Outcome = "incomplete", ExitCode = 1, Complete = false, Reason = "REPORT_WRITE_FAILED" }; }
        }
        return report;
    }

    private static void WriteReports(string output, RunReport report)
    {
        NoLinks(output);
        string json = Path.Combine(output, "summary.json"), html = Path.Combine(output, "report.html");
        if (File.Exists(json) || File.Exists(html)) throw new IOException("Report collision");
        static void Fresh(string path, string value)
        {
            using var f = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            f.Write(Encoding.UTF8.GetBytes(value)); f.Flush(flushToDisk: true);
        }
        Fresh(html + ".pending", Results.Html(report)); Fresh(json + ".pending", Results.Serialize(report) + "\n");
        File.Move(html + ".pending", html); File.Move(json + ".pending", json);
    }
}
