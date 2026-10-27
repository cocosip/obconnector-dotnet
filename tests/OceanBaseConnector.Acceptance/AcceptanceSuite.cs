using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector.Acceptance;

internal sealed record AcceptanceCase(string Name, string Group, Func<CaseContext, CancellationToken, Task> Run,
    string? Mode = null, bool RequiresObjects = false, bool RequiresPrepareExecute = false,
    bool RequiresLob = false, string? DeferredReason = null, bool ConnectionGate = false,
    bool RequiresSlowQuery = false, bool RequiresAlternateDatabase = false, bool RequiresZeroDateProfile = false,
    bool RequiresNetworkFaultProfile = false, bool RequiresXaCrashProfile = false);
internal sealed class AssertionFailure(string message) : Exception(message);
internal sealed record ServerObservation(string Mode, string Version);
internal sealed record CaseResult(string Name, string Group, string Status, string? Detail, long ElapsedMilliseconds = 0);
internal sealed class CreatedObject(string kind, string name)
{
    public string Kind { get; } = kind;
    public string Name { get; } = name;
    public string Cleanup { get; set; } = "pending";
}
internal sealed class AcceptanceReport
{
    public string Mode { get; init; } = "";
    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
    public string DriverVersion { get; } = typeof(OceanBaseConnection).Assembly.GetName().Version!.ToString();
    public string Runtime { get; } = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
    public List<CaseResult> Results { get; } = new();
    public List<CreatedObject> Objects { get; } = new();
    public List<ServerObservation> Servers { get; } = new();
    public int Passed => Results.Count(r => r.Status == "passed");
    public int Failed => Results.Count(r => r.Status == "failed");
    public int Skipped => Results.Count(r => r.Status == "skipped");
    public int Cancelled => Results.Count(r => r.Status == "cancelled");
    public int CleanupFailures => Objects.Count(item => item.Cleanup == "failed");
    public int ExitCode => Cancelled > 0 ? 130 : Failed > 0 || CleanupFailures > 0 ? 1 : Passed == 0 ? 2 : 0;
}
internal static class AcceptanceSuite
{
    public static async Task<AcceptanceReport> RunAsync(AcceptanceConfig config, IEnumerable<AcceptanceCase> cases,
        TextWriter output, CancellationToken token, string? resolvedConnectionString = null)
    {
        var connectionString = resolvedConnectionString ?? config.ResolveConnectionString(Environment.GetEnvironmentVariable);
        var builder = new OceanBaseConnectionStringBuilder(connectionString);
        var report = new AcceptanceReport { Mode = config.Mode };
        var gateFailed = false;
        foreach (var test in cases)
        {
            var reason = SkipReason(config, builder, test);
            string status; string? detail; var watch = Stopwatch.StartNew();
            if (token.IsCancellationRequested)
            {
                status = report.Cancelled == 0 ? "cancelled" : "skipped";
                detail = "Run cancelled by caller.";
            }
            else if (reason != null || gateFailed)
            {
                status = "skipped"; detail = reason ?? "Connection prerequisite failed.";
            }
            else
            {
                var context = new CaseContext(config, connectionString, report.Objects, report.Servers);
                try
                {
                    await test.Run(context, token).ConfigureAwait(false);
                    status = "passed"; detail = null;
                }
                catch (Exception error) when (token.IsCancellationRequested &&
                    (error is OperationCanceledException ||
                     error is OceanBaseException { IsExecutionOutcomeUnknown: true, InnerException: OperationCanceledException }))
                { status = "cancelled"; detail = "Run cancelled by caller."; }
                catch (Exception error)
                { status = "failed"; detail = Describe(error); if (test.ConnectionGate) gateFailed = true; }
                var cleanupErrors = await context.CleanupAsync().ConfigureAwait(false);
                if (cleanupErrors.Count > 0)
                {
                    if (status != "cancelled") status = "failed";
                    detail = (detail == null ? "" : detail + "; ") + string.Join("; ", cleanupErrors);
                }
            }
            var result = new CaseResult(test.Name, test.Group, status, detail, watch.ElapsedMilliseconds);
            report.Results.Add(result);
            await output.WriteLineAsync($"{status.ToUpperInvariant(),-9} {test.Name}" + (detail == null ? "" : ": " + detail));
        }
        return report;
    }

    internal static string? SkipReason(AcceptanceConfig config, OceanBaseConnectionStringBuilder builder, AcceptanceCase test)
    {
        if (config.Groups.Length != 0 && !config.Groups.Contains(test.Group, StringComparer.Ordinal)) return "Group not selected.";
        if (test.Mode != null && test.Mode != config.Mode) return "Requires " + test.Mode + " mode.";
        if (test.DeferredReason != null) return test.DeferredReason;
        if (test.RequiresXaCrashProfile && !config.XaCrashProfile) return "Requires xaCrashProfile=true in a dedicated test schema.";
        if (test.RequiresNetworkFaultProfile)
        {
            if (!config.NetworkFaultProfile) return "Requires networkFaultProfile=true in a dedicated test schema.";
            if (builder.TlsMode != OceanBaseTlsMode.Disabled) return "Network fault relay requires TLS Mode=Disabled; no downgrade is performed.";
            if (builder.UseCompression) return "Network fault relay does not support compression.";
            if (builder.UseOb20) return "Network fault relay does not support OB20.";
            if (Uri.CheckHostName(builder.Server) is not (UriHostNameType.Dns or UriHostNameType.IPv4))
                return "Network fault relay requires a single plain host and separate Port.";
        }
        if (test.RequiresZeroDateProfile && !config.ZeroDateProfile) return "Requires zeroDateProfile=true and a session SQL_MODE allowing zero dates.";
        if (test.RequiresSlowQuery && string.IsNullOrWhiteSpace(config.SlowQuery)) return "Requires a read-only slowQuery taking more than two seconds.";
        if (test.RequiresAlternateDatabase && string.IsNullOrWhiteSpace(config.AlternateDatabase)) return "Requires alternateDatabase.";
        if (test.Group == "xa" && config.Mode == "Oracle" && !builder.UseOraclePrepareExecute) return "Oracle XA requires Use Oracle Prepare Execute=true.";
        if (test.RequiresObjects && !config.AllowCreateObjects) return "Requires allowCreateObjects=true in a dedicated test schema.";
        if (test.RequiresPrepareExecute && !builder.UseOraclePrepareExecute) return "Requires Use Oracle Prepare Execute=true.";
        if (test.RequiresLob && (!builder.UseLobLocators || !builder.UseOraclePrepareExecute)) return "Requires Use Lob Locators=true and Use Oracle Prepare Execute=true.";
        return null;
    }

    internal static string Describe(Exception error)
    {
        if (error is AssertionFailure) return error.Message;
        if (error is AggregateException aggregate) return string.Join("; ", aggregate.InnerExceptions.Select(Describe));
        if (error is OceanBaseException database)
        {
            var state = database.SqlState;
            var safeState = state != null && state.Length == 5 && state.All(char.IsAsciiLetterOrDigit) ? state : "unknown";
            return $"OceanBaseException code={database.ServerErrorCode}, SQLSTATE={safeState}, outcomeUnknown={database.IsExecutionOutcomeUnknown}";
        }
        // Exception messages and stacks can contain credentials, SQL and bound values.
        return error.GetType().Name;
    }
}
