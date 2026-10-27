using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector.Acceptance;

internal static partial class DatabaseCases
{
    private static void AddOb20Trace(List<AcceptanceCase> cases)
    {
        foreach (var profile in new[] { "ob20", "trace-legacy", "trace-modern", "declined-ob20", "declined-trace" })
            foreach (var asynchronous in new[] { false, true })
                cases.Add(new("matrix.ob20-trace-negotiation." + profile + (asynchronous ? ".async" : ".sync"), "faults",
                    (c, t) => Ob20Trace(c, asynchronous, profile, t), Ob20TraceProfile: profile));
    }

    private static async Task Ob20Trace(CaseContext c, bool asynchronous, string profile, CancellationToken token)
    {
        using var configuration = c.CreateConnection();
        var builder = new OceanBaseConnectionStringBuilder(configuration.ConnectionString) { Pooling = false };
        using var capture = new Ob20TraceCapture(profile);
        using var activity = new Activity("OceanBaseConnector.Acceptance.Ob20Trace").Start();
        await using var connection = new OceanBaseConnection(builder.ConnectionString);
        if (asynchronous) await connection.OpenAsync(token); else connection.Open();
        c.ObserveServer(connection);
        capture.BeginQueries();
        for (var i = 0; i < 3; i++)
        {
            using var command = c.Command(connection, c.SelectOne);
            using var cancellation = asynchronous ? default(CancellationTokenRegistration) : token.Register(command.Cancel);
            token.ThrowIfCancellationRequested();
            c.EqualNumber(asynchronous ? await command.ExecuteScalarAsync(token) : command.ExecuteScalar(), 1,
                "Protocol control query must return one.");
        }
        capture.Verify();
        if (asynchronous) await connection.CloseAsync(); else connection.Close();
    }
}
