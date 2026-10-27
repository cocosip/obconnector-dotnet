using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector.Acceptance;

internal static partial class DatabaseCases
{
    private static async Task PoolResetReuse(CaseContext c, bool asynchronous, CancellationToken token)
    {
        using var configuration = c.CreateConnection();
        var builder = new OceanBaseConnectionStringBuilder(configuration.ConnectionString)
        {
            Pooling = true, MinPoolSize = 0, MaxPoolSize = 1,
            ConnectionLifetime = 0, ConnectionIdleLifetime = 0,
        };
        await using var source = new OceanBaseDataSourceBuilder(builder.ConnectionString).Build();
        using var capture = new ProtocolChannelCapture();
        await using var first = (OceanBaseConnection)source.CreateConnection();
        await using var second = (OceanBaseConnection)source.CreateConnection();
        if (asynchronous) await first.OpenAsync(token); else first.Open();
        await using var command = c.Command(first, c.SelectOne);
        if (asynchronous) await command.PrepareAsync(token); else command.Prepare();
        c.EqualNumber(asynchronous ? await command.ExecuteScalarAsync(token) : command.ExecuteScalar(), 1,
            "The prepared command must execute before reset.");
        c.Check(capture.HasSingleChannel, "The initial physical channel must be observable.");
        if (asynchronous) await first.CloseAsync(); else first.Close();

        if (asynchronous) await second.OpenAsync(token); else second.Open();
        await using (var query = c.Command(second, c.SelectOne))
            c.EqualNumber(asynchronous ? await query.ExecuteScalarAsync(token) : query.ExecuteScalar(), 1,
                "The next borrower must execute SQL after reset.");
        c.Check(capture.HasSingleChannel, "Reset/reuse must retain the same physical channel; replacement is insufficient.");
        if (asynchronous) await second.CloseAsync(); else second.Close();

        if (asynchronous) await first.OpenAsync(token); else first.Open();
        c.EqualNumber(asynchronous ? await command.ExecuteScalarAsync(token) : command.ExecuteScalar(), 1,
            "The original prepared command must execute after reopening.");
        c.Check(capture.HasSingleChannel, "Reopening must retain the same physical channel.");
    }
}
