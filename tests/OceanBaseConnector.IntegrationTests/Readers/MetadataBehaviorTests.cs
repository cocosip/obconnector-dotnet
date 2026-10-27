using System.Data;
using System.Data.Common;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Readers;

public sealed class MetadataBehaviorTests
{
    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.All), MemberType = typeof(ProfileTheoryData))]
    public async Task SchemaOnlyDoesNotUploadInputStream(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        using var source = new MemoryStream(new byte[] { 1, 2, 3 });
        await using var command = db.Command(connection, db.Oracle ? "SELECT :payload FROM DUAL" : "SELECT ?");
        command.Parameters.Add(new OceanBaseParameter { ParameterName = "payload", Value = source });

        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SchemaOnly);

        Assert.Equal(1, reader.FieldCount);
        Assert.False(await reader.ReadAsync());
        Assert.Equal(0, source.Position);
        Assert.True(source.CanRead);
    }

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.WithCreateObjects), MemberType = typeof(ProfileTheoryData))]
    public async Task KeyInfoReportsPrimaryKeyAndHidesMissingKeyColumn(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var table = await db.TableAsync(connection,
            "id INTEGER NOT NULL PRIMARY KEY, payload " + (db.Oracle ? "VARCHAR2(40)" : "VARCHAR(40)"),
            CancellationToken.None);

        await using var command = db.Command(connection, "SELECT payload FROM " + table);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SchemaOnly | CommandBehavior.KeyInfo);

        Assert.Equal(2, reader.FieldCount);
        Assert.Equal(1, reader.VisibleFieldCount);
        var columns = reader.GetColumnSchema();
        Assert.False(columns[0].IsHidden);
        Assert.True(columns[1].IsHidden);
        Assert.True(columns[1].IsKey);
        Assert.Equal(db.Oracle ? "ID" : "id", columns[1].BaseColumnName);
        Assert.False(await reader.ReadAsync());
    }
}
