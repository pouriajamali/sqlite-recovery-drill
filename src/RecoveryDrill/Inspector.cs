using Microsoft.Data.Sqlite;

namespace RecoveryDrill;

internal static class Inspector
{
    internal const int ApplicationId = 0x44524C31;
    internal const string BatchSql = "CREATE TABLE batches (id INTEGER PRIMARY KEY, expected_jobs INTEGER)";
    internal const string JobSql = "CREATE TABLE jobs (id INTEGER PRIMARY KEY, batch_id INTEGER REFERENCES batches(id), status TEXT, attempts INTEGER)";

    internal static WorkerReceipt Inspect(string trial)
    {
        var checks = Results.Unchecked();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(trial), Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private, Pooling = false, DefaultTimeout = 2
        }.ToString());
        connection.Open();
        connection.EnableExtensions(false);
        Execute(connection, "PRAGMA trusted_schema=OFF; PRAGMA query_only=ON; PRAGMA cache_size=-4096;");
        bool Stage(int index, bool passed, string code)
        {
            checks[index] = new(Results.Ids[index], passed ? "pass" : "fail", passed ? "OK" : code);
            return passed;
        }
        WorkerReceipt Finish() => new(1, true, Results.Profile, checks.All(c => c.Status == "pass") ? "pass" : "fail", checks);

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA integrity_check(20);";
            using var reader = cmd.ExecuteReader();
            int rows = 0; bool okay = true;
            while (reader.Read()) { rows++; okay &= reader.GetString(0) == "ok"; if (rows >= 20) break; }
            if (!Stage(0, rows == 1 && okay, "INTEGRITY_FAILED")) return Finish();
        }
        bool schema = Scalar(connection, "PRAGMA application_id;") == ApplicationId && Scalar(connection, "PRAGMA user_version;") == 1;
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT type,name,sql FROM sqlite_schema ORDER BY name LIMIT 3;";
            using var reader = cmd.ExecuteReader();
            string[] names = ["batches", "jobs"], sql = [BatchSql, JobSql];
            int i = 0;
            while (reader.Read())
            {
                if (i >= 2 || reader.IsDBNull(2) || reader.GetString(0) != "table" || reader.GetString(1) != names[Math.Min(i, 1)] || reader.GetString(2) != sql[Math.Min(i, 1)]) schema = false;
                i++;
            }
            schema &= i == 2;
        }
        if (!Stage(1, schema, "SCHEMA_MISMATCH")) return Finish();
        if (!Stage(2, Scalar(connection, "SELECT COUNT(*) FROM batches;") <= 1_000 && Scalar(connection, "SELECT COUNT(*) FROM jobs;") <= 10_000, "ROW_LIMIT")) return Finish();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA foreign_key_check;";
            using var reader = cmd.ExecuteReader();
            if (!Stage(3, !reader.Read(), "FOREIGN_KEY_VIOLATION")) return Finish();
        }
        bool types = Scalar(connection, "SELECT COUNT(*) FROM batches WHERE typeof(id)<>'integer' OR typeof(expected_jobs)<>'integer';") == 0 &&
            Scalar(connection, "SELECT COUNT(*) FROM jobs WHERE typeof(id)<>'integer' OR typeof(batch_id)<>'integer' OR typeof(status)<>'text' OR typeof(attempts)<>'integer';") == 0;
        if (!Stage(4, types, "STORAGE_CLASS_MISMATCH")) return Finish();
        bool ranges = Scalar(connection, "SELECT COUNT(*) FROM batches WHERE id<1 OR expected_jobs<0 OR expected_jobs>10000;") == 0 &&
            Scalar(connection, "SELECT COUNT(*) FROM jobs WHERE id<1 OR batch_id<1 OR attempts<0 OR attempts>1000000;") == 0;
        if (!Stage(5, ranges, "VALUE_RANGE")) return Finish();
        bool invariants = Scalar(connection, "SELECT COUNT(*) FROM jobs WHERE status NOT IN ('queued','running','done') OR (status='running' AND attempts=0);") == 0 &&
            Scalar(connection, "SELECT COUNT(*) FROM batches AS b WHERE b.expected_jobs<>(SELECT COUNT(*) FROM jobs AS j WHERE j.batch_id=b.id);") == 0;
        Stage(6, invariants, "APPLICATION_INVARIANT");
        return Finish();
    }

    internal static void Execute(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery();
    }
    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand(); cmd.CommandText = sql;
        return (long)(cmd.ExecuteScalar() ?? throw new InvalidDataException("MISSING_SCALAR"));
    }
}
