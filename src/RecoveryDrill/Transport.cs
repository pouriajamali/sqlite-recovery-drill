using System.Diagnostics;

namespace RecoveryDrill;
internal sealed record TransportResult(int ExitCode, byte[] Stdout, string Problem);
internal static class Transport
{
    internal const int OutputLimit = 16 * 1024, ErrorLimit = 4 * 1024;
    internal static ProcessStartInfo OwnWorker(string trial, string token)
    {
        string host = Environment.ProcessPath ?? throw new InvalidOperationException("HOST_UNAVAILABLE");
        var start = new ProcessStartInfo(host);
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(Transport).Assembly.Location);
        start.ArgumentList.Add("--internal-worker");
        start.ArgumentList.Add(trial);
        start.ArgumentList.Add(token);
        start.Environment["DRILL_INTERNAL_TOKEN"] = token;
        return start;
    }

    internal static async Task<TransportResult> RunAsync(ProcessStartInfo start, int deadlineMs = 10_000)
    {
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using var process = new Process
        {
            StartInfo = start
        };
        if (!process.Start())
            return new(-1, [], "WORKER_START_FAILED");
        var overflow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        async Task<byte[]> Drain(Stream stream, int limit)
        {
            using var stored = new MemoryStream();
            byte[] buffer = new byte[512];
            while (true)
            {
                int n = await stream.ReadAsync(buffer, cancellation.Token);
                if (n == 0)
                    break;
                if (stored.Length + n > limit)
                {
                    overflow.TrySetResult();
                    continue;
                }

                stored.Write(buffer, 0, n);
            }

            return stored.ToArray();
        }

        Task<byte[]> stdout = Drain(process.StandardOutput.BaseStream, OutputLimit);
        Task<byte[]> stderr = Drain(process.StandardError.BaseStream, ErrorLimit);
        Task exited = process.WaitForExitAsync();
        Task deadline = Task.Delay(deadlineMs);
        Task winner = await Task.WhenAny(exited, deadline, overflow.Task);
        string problem = winner == deadline ? "WORKER_TIMEOUT" : winner == overflow.Task ? "WORKER_OUTPUT_LIMIT" : "";
        if (problem.Length > 0)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        }

        try
        {
            await exited.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception error)when (error is TimeoutException or IOException or OperationCanceledException)
        {
            cancellation.Cancel();
            return new(-1, [], "WORKER_DRAIN_FAILED");
        }

        if (overflow.Task.IsCompleted)
            problem = "WORKER_OUTPUT_LIMIT";
        if (problem.Length == 0 && stderr.Result.Length > 0)
            problem = "WORKER_STDERR";
        if (problem.Length == 0 && process.ExitCode != 0)
            problem = "WORKER_FAILED";
        return new(process.ExitCode, stdout.Result, problem);
    }
}
