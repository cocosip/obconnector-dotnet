using System.Data;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Ob20;

/// <summary>
/// Runs only against profiles whose options enable OB 2.0 protocol framing
/// (optionally with full-link trace): ordinary SQL, prepared statements and
/// cursor fetches must behave identically under the negotiated framing.
/// </summary>
public sealed class Ob20Tests
{
    [IntegrationTheory("ob20")]
    [MemberData(nameof(ProfileTheoryData.WithTag), new object[] { "ob20" }, MemberType = typeof(ProfileTheoryData))]
    public async Task TextAndPreparedSqlSucceedUnderOb20(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        Assert.True(db.Profile.CreateBuilder().UseOb20, "The ob20 tag requires the useOb20 option.");
        await using var connection = await db.OpenAsync();
        Assert.Equal(db.Profile.Mode, connection.CompatibilityMode);
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1,
            "Text SQL must succeed under OB20 framing.");
        await using var command = db.Command(connection,
            "SELECT " + db.ParameterSql(":value") + (db.Oracle ? " FROM DUAL" : ""));
        DatabaseContext.Parameter(command, "value", 42, DbType.Int32);
        await command.PrepareAsync();
        DatabaseContext.EqualNumber(await command.ExecuteScalarAsync(), 42,
            "Prepared SQL must succeed under OB20 framing.");
    }

    [IntegrationTheory("trace")]
    [MemberData(nameof(ProfileTheoryData.WithOb20Trace), MemberType = typeof(ProfileTheoryData))]
    public async Task FullLinkTraceKeepsSqlWorking(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        Assert.True(db.Profile.CreateBuilder().EnableFullLinkTrace, "This suite requires the full-link trace option.");
        await using var connection = await db.OpenAsync();
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1,
            "SQL must succeed while full-link trace context is attached.");
    }
}
