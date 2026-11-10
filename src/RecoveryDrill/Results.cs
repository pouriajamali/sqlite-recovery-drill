using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace RecoveryDrill;
internal sealed record CheckResult(string Id, string Status, string Code);
internal sealed record WorkerReceipt(int Schema, bool Complete, string Profile, string Outcome, CheckResult[] Checks);
internal sealed record RunReport(string Schema, string ToolVersion, string Profile, string Outcome, int ExitCode, string InputSha256, long InputBytes, CheckResult[] Checks, string Cleanup, string Reason, bool TemporaryDataMayRemain, string Freshness = "NOT_CHECKED", string ExternalCompleteness = "NOT_CHECKED", bool Complete = true, string InputProvenance = "UNVERIFIED", string LiveSourceConsistency = "NOT_PROVEN");
internal static class Results
{
    internal const string Profile = "synthetic-jobs-v1";
    internal static readonly string[] Ids = ["sqlite-integrity", "schema", "row-counts", "foreign-keys", "storage-classes", "value-ranges", "application-invariants"];
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    internal static string Version => typeof(Results).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.1.0-preview.1";

    internal static CheckResult[] Unchecked() => Ids.Select(id => new CheckResult(id, "not_checked", "PREREQUISITE_NOT_MET")).ToArray();
    internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    internal static RunReport Incomplete(string reason, string hash = "NOT_AVAILABLE", long bytes = 0) => new("recovery-drill-report-v1", Version, Profile, "incomplete", 1, hash, bytes, Unchecked(), "not_created", reason, false, Complete: false);
    internal static WorkerReceipt ParseWorker(byte[] data)
    {
        using var doc = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 8 });
        var o = doc.RootElement;
        Fields(o, "schema", "complete", "profile", "outcome", "checks");
        if (o.GetProperty("schema").GetInt32() != 1 || o.GetProperty("complete").ValueKind != JsonValueKind.True || o.GetProperty("profile").GetString() != Profile)
            throw new InvalidDataException("WORKER_PROTOCOL");
        string? outcome = o.GetProperty("outcome").GetString();
        if (outcome is not ("pass" or "fail"))
            throw new InvalidDataException("WORKER_PROTOCOL");
        var array = o.GetProperty("checks");
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() != Ids.Length)
            throw new InvalidDataException("WORKER_PROTOCOL");
        var checks = new List<CheckResult>();
        string[] failureCodes = ["INTEGRITY_FAILED", "SCHEMA_MISMATCH", "ROW_LIMIT", "FOREIGN_KEY_VIOLATION", "STORAGE_CLASS_MISMATCH", "VALUE_RANGE", "APPLICATION_INVARIANT"];
        int index = 0;
        foreach (var c in array.EnumerateArray())
        {
            Fields(c, "id", "status", "code");
            string? id = c.GetProperty("id").GetString(), status = c.GetProperty("status").GetString(), code = c.GetProperty("code").GetString();
            if (id != Ids[index] || !((status == "pass" && code == "OK") || (status == "fail" && code == failureCodes[index]) || (status == "not_checked" && code == "PREREQUISITE_NOT_MET")))
                throw new InvalidDataException("WORKER_PROTOCOL");
            checks.Add(new(id!, status!, code!));
            index++;
        }

        if ((outcome == "pass") != checks.All(c => c.Status == "pass") || (outcome == "fail" && !checks.Any(c => c.Status == "fail")))
            throw new InvalidDataException("WORKER_PROTOCOL");
        bool stopped = false;
        foreach (var c in checks)
        {
            if (stopped && c.Status != "not_checked")
                throw new InvalidDataException("WORKER_PROTOCOL");
            if (!stopped && c.Status == "not_checked")
                throw new InvalidDataException("WORKER_PROTOCOL");
            stopped |= c.Status == "fail";
        }

        return new(1, true, Profile, outcome!, checks.ToArray());
    }

    private static void Fields(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("WORKER_PROTOCOL");
        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!actual.Add(property.Name))
                throw new InvalidDataException("WORKER_PROTOCOL");
        if (!actual.SetEquals(expected))
            throw new InvalidDataException("WORKER_PROTOCOL");
    }

    internal static string Html(RunReport report)
    {
        static string E(string value) => WebUtility.HtmlEncode(value);
        var b = new StringBuilder("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'\"><title>SQLite Recovery Drill</title><style>body{font:16px system-ui;max-width:900px;margin:3rem auto;padding:0 1rem;color:#17212b;background:#f5f7fa}table{border-collapse:collapse;width:100%;background:white}th,td{text-align:left;padding:.8rem;border-bottom:1px solid #dde3ea}code{overflow-wrap:anywhere}.limit{padding:1rem;background:#fff4d9}h1{line-height:1.15}</style></head><body><h1>SQLite Recovery Drill</h1>");
        b.Append("<p>Use only with a complete summary.json from this directory.</p><p>Outcome: <strong>").Append(E(report.Outcome)).Append("</strong> · exit ").Append(report.ExitCode).Append("</p><p>Tool: <code>").Append(E(report.ToolVersion)).Append("</code></p><p>Profile: ").Append(E(report.Profile)).Append("</p><p>Input SHA256: <code>").Append(E(report.InputSha256)).Append("</code> · bytes ").Append(report.InputBytes).Append("</p><table><thead><tr><th>Check</th><th>Status</th><th>Code</th></tr></thead><tbody>");
        foreach (var c in report.Checks)
            b.Append("<tr><td>").Append(E(c.Id)).Append("</td><td>").Append(E(c.Status)).Append("</td><td>").Append(E(c.Code)).Append("</td></tr>");
        b.Append("</tbody></table><p>Cleanup: ").Append(E(report.Cleanup)).Append(" · ").Append(E(report.Reason)).Append("</p>");
        if (report.TemporaryDataMayRemain)
            b.Append("<p class=\"limit\">Temporary trial data may remain in this report directory. No source or journal was removed.</p>");
        return b.Append("<p class=\"limit\">Freshness and external completeness: NOT_CHECKED. This is one synthetic profile, not proof of real application recoverability, production safety or a backup/repair service. No database row values are included.</p></body></html>\n").ToString();
    }
}
