using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Protocol;

public sealed class CompressionTests
{
    public static TheoryData<string, bool> Profiles()
    {
        var data = new TheoryData<string, bool>();
        foreach (var profile in IntegrationEnvironment.Profiles)
        {
            if (profile.CreateBuilder().UseOb20) continue;
            data.Add(profile.Name, false);
            data.Add(profile.Name, true);
        }
        return data;
    }

    [IntegrationTheory]
    [MemberData(nameof(Profiles))]
    public async Task RequestedCompressionKeepsBothModesUsableWhenServerDoesNotAdvertiseIt(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        var builder = db.Profile.CreateBuilder();
        builder.UseCompression = true;
        builder.Pooling = false;
        await using var connection = new OceanBaseConnection(builder.ConnectionString);
        if (asynchronous) await connection.OpenAsync(); else connection.Open();

        await using var command = db.Command(connection, db.SelectOne);
        var value = asynchronous ? await command.ExecuteScalarAsync(CancellationToken.None) : command.ExecuteScalar();
        DatabaseContext.EqualNumber(value, 1, "Compression fallback must leave the connection usable.");
    }
}
