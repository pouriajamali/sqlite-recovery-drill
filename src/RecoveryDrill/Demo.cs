using Microsoft.Data.Sqlite;

namespace RecoveryDrill;

internal static class Demo
{
    internal static readonly string[] Cases = ["valid", "stale-valid", "foreign-key", "null-value", "text-integer", "blob-integer", "real-integer", "range", "bad-status", "batch-count", "schema-version", "truncated", "corrupt-page"];
    internal static void Create(string path, string name)
    {
        if (!Cases.Contains(name, StringComparer.Ordinal)) throw new ArgumentException("Unknown synthetic fixture");
        if (File.Exists(path) || Directory.Exists(path)) throw new IOException("Fresh fixture required");
        long jobPage = 0, pageSize = 0;
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            connection.Open();
            Inspector.Execute(connection, "PRAGMA journal_mode=DELETE; PRAGMA foreign_keys=OFF; PRAGMA application_id=" + Inspector.ApplicationId + "; PRAGMA user_version=1;");
            Inspector.Execute(connection, Inspector.BatchSql + ";" + Inspector.JobSql + ";");
            Inspector.Execute(connection, "INSERT INTO batches VALUES(1,2),(2,1); INSERT INTO jobs VALUES(1,1,'queued',0),(2,1,'done',1),(3,2,'running',1);");
            string sql = name switch
            {
                "stale-valid" => "DELETE FROM jobs WHERE id=3; DELETE FROM batches WHERE id=2;",
                "foreign-key" => "UPDATE jobs SET batch_id=999 WHERE id=1;",
                "null-value" => "UPDATE jobs SET attempts=NULL WHERE id=1;",
                "text-integer" => "UPDATE jobs SET attempts='not-an-integer' WHERE id=1;",
                "blob-integer" => "UPDATE jobs SET attempts=X'0001' WHERE id=1;",
                "real-integer" => "UPDATE jobs SET attempts=1.25 WHERE id=1;",
                "range" => "UPDATE jobs SET attempts=9223372036854775807 WHERE id=1;",
                "bad-status" => "UPDATE jobs SET status='unsupported' WHERE id=1;",
                "batch-count" => "UPDATE batches SET expected_jobs=99 WHERE id=1;",
                "schema-version" => "PRAGMA user_version=2;",
                _ => ""
            };
            if (sql.Length > 0) Inspector.Execute(connection, sql);
            if (name == "corrupt-page")
            {
                using var page = connection.CreateCommand(); page.CommandText = "SELECT rootpage FROM sqlite_schema WHERE name='jobs';"; jobPage = (long)page.ExecuteScalar()!;
                page.CommandText = "PRAGMA page_size;"; pageSize = (long)page.ExecuteScalar()!;
            }
        }
        if (name == "truncated") using (var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None)) file.SetLength(100);
        if (name == "corrupt-page")
        {
            // SQLite b-tree header byte7 is fragmented free-byte count, at most60.
            // Only this fresh synthetic fixture is damaged; no input repair API exists.
            using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            file.Position = checked((jobPage - 1) * pageSize + 7); file.WriteByte(255);
        }
    }
    internal static async Task<int> RunAsync(string directory)
    {
        string root = Drill.LocalPath(directory); Drill.NoLinks(root);
        if (Directory.Exists(root) || File.Exists(root)) throw new DrillFailure("OUTPUT_EXISTS");
        Directory.CreateDirectory(root);
        foreach (string name in new[] { "valid", "foreign-key", "null-value", "batch-count", "stale-valid" })
        {
            string source = Path.Combine(root, name + ".sqlite"); Create(source, name);
            RunReport result = await Drill.CheckAsync(source, Path.Combine(root, name + " report"));
            int expected = name is "valid" or "stale-valid" ? 0 : 2;
            Console.WriteLine($"{name}: {result.Outcome}; exit {result.ExitCode}; expected {expected}; freshness NOT_CHECKED");
            if (result.ExitCode != expected || !result.Complete) return 1;
        }
        Console.WriteLine("DEMO PASS: five synthetic snapshots checked. Read each report.html with its summary.json. No backup, repair, live source or freshness proof.");
        return 0;
    }
}
