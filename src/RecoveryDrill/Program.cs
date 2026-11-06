namespace RecoveryDrill;

internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 3 && args[0] == "--internal-worker")
            {
                string path = Path.GetFullPath(args[1]); string token = args[2];
                if (token.Length != 32 || Environment.GetEnvironmentVariable("DRILL_INTERNAL_TOKEN") != token ||
                    Path.GetFileName(path) != "trial.sqlite" || File.ReadAllText(Path.Combine(Path.GetDirectoryName(path)!, ".drill-owned")) != token) return 1;
                Drill.NoLinks(path); Drill.NoSidecars(path);
                using (var input = File.OpenRead(path)) { if (input.Length > Drill.ByteLimit) return 1; Drill.Header(input); }
                Console.WriteLine(Results.Serialize(Inspector.Inspect(path))); return 0;
            }
            if (args is ["--version"]) { Console.WriteLine("SQLite Recovery Drill " + Results.Version); return 0; }
            if (args is ["demo", var demoDirectory]) return await Demo.RunAsync(demoDirectory);
            if (args is ["check", var source, var output, "--profile", Results.Profile])
            {
                RunReport result = await Drill.CheckAsync(source, output); Console.WriteLine(Results.Serialize(result)); return result.ExitCode;
            }
            Console.WriteLine(Results.Serialize(Results.Incomplete("ARGUMENTS: --version | demo <new-directory> | check <source.sqlite> <new-report-directory> --profile synthetic-jobs-v1")));
            return 1;
        }
        catch (Exception)
        {
            // Raw exceptions may contain private paths or SQLite schema/data. Never emit them.
            if (args.Length > 0 && args[0] == "--internal-worker") return 1;
            Console.WriteLine(Results.Serialize(Results.Incomplete("OPERATION_FAILED"))); return 1;
        }
    }
}
