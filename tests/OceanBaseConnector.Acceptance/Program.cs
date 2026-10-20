using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector.Acceptance;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        try { return await RunAsync(args, Console.Out, cancellation.Token); }
        finally { Console.CancelKeyPress -= handler; }
    }

    internal static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken token)
    {
        const string usage = "Usage: dotnet run --project tests/OceanBaseConnector.Acceptance -c Release -- --config <file.json> [--report <file.json>] | --list | --help";
        try
        {
            string? configPath = null; string? reportPath = null; var list = false;
            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < args.Length; i++)
            {
                if (!seen.Add(args[i])) { await output.WriteLineAsync(usage); return 2; }
                switch (args[i])
                {
                    case "--help": if (args.Length != 1) return 2; await output.WriteLineAsync(usage); return 0;
                    case "--list": list = true; break;
                    case "--config" when i + 1 < args.Length: configPath = args[++i]; break;
                    case "--report" when i + 1 < args.Length: reportPath = args[++i]; break;
                    default: await output.WriteLineAsync(usage); return 2;
                }
            }
            if ((!list && configPath == null) || (list && reportPath != null)) { await output.WriteLineAsync(usage); return 2; }
            var config = configPath == null ? null : AcceptanceConfig.Parse(await File.ReadAllTextAsync(configPath, token));
            var cases = DatabaseCases.All();
            if (list)
            {
                await output.WriteLineAsync("Groups: " + string.Join(", ", DatabaseCases.Groups));
                foreach (var test in cases)
                    await output.WriteLineAsync($"{test.Name} [{test.Group}]" + (test.Mode == null ? "" : " mode=" + test.Mode) +
                        (test.RequiresObjects ? " creates-test-objects" : "") + (test.DeferredReason == null ? "" : " SKIPPED: " + test.DeferredReason));
                return 0;
            }
            var resolved = config!.ResolveConnectionString(Environment.GetEnvironmentVariable);
            reportPath ??= Path.Combine("TestResults", "acceptance-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");
            if (string.Equals(Path.GetFullPath(reportPath), Path.GetFullPath(configPath!), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Report cannot overwrite configuration.");
            var directory = Path.GetDirectoryName(Path.GetFullPath(reportPath))!;
            Directory.CreateDirectory(directory);
            // Reserve a new report before connecting: never overwrite configuration or an earlier result.
            await using var reportFile = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await output.WriteLineAsync("Mode=" + config!.Mode + "; each case uses public driver APIs. Object-writing cases require a dedicated test schema.");
            var report = await AcceptanceSuite.RunAsync(config, cases, output, token, resolved);
            await JsonSerializer.SerializeAsync(reportFile, report, new JsonSerializerOptions { WriteIndented = true }, CancellationToken.None);
            await output.WriteLineAsync($"Passed={report.Passed}; Failed={report.Failed}; Skipped={report.Skipped}; Cancelled={report.Cancelled}; CleanupFailures={report.CleanupFailures}");
            await output.WriteLineAsync("Report: " + Path.GetFullPath(reportPath) + "; exit code=" + report.ExitCode);
            return report.ExitCode;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { await output.WriteLineAsync("Cancelled before a test run was started."); return 130; }
        catch (Exception error)
        {
            await output.WriteLineAsync("Configuration/report error: " + error.GetType().Name + ". Check configuration, connection environment variable, finite timeouts and report path. No exception text is printed to protect credentials.");
            return 2;
        }
    }
}
