using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Schema;

public sealed class SchemaTests
{
    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.All), MemberType = typeof(ProfileTheoryData))]
    public async Task AllCollectionsMatchBetweenSyncAndAsync(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        foreach (var name in new[] { "MetaDataCollections", "DataSourceInformation", "Tables", "Columns", "Procedures", "ProcedureParameters" })
        {
            using var sync = connection.GetSchema(name);
            using var asynchronous = await connection.GetSchemaAsync(name);
            Assert.True(sync.Columns.Count > 0, $"Collection '{name}' must describe its shape.");
            Assert.Equal(sync.Columns.Count, asynchronous.Columns.Count);
            if (name == "MetaDataCollections") Assert.Equal(6, sync.Rows.Count);
        }
    }

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.WithCreateObjects), MemberType = typeof(ProfileTheoryData))]
    public async Task CreatedTableAndColumnsAreDiscoverable(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var name = await db.TableAsync(connection,
            "id INTEGER, value " + (db.Oracle ? "VARCHAR2(40)" : "VARCHAR(40)"), CancellationToken.None);
        var owner = db.Oracle
            ? Convert.ToString(await db.ScalarAsync(connection, "SELECT USER FROM DUAL", CancellationToken.None), CultureInfo.InvariantCulture)
            : connection.Database;
        var restrictions = db.Oracle ? new string?[] { null, owner, name } : new string?[] { owner, null, name };
        using var tables = await connection.GetSchemaAsync("Tables", restrictions, CancellationToken.None);
        using var columns = await connection.GetSchemaAsync("Columns", restrictions, CancellationToken.None);
        Assert.Equal(1, tables.Rows.Count);
        Assert.Equal(2, columns.Rows.Count);
    }

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.WithCreateObjects), MemberType = typeof(ProfileTheoryData))]
    public async Task RestrictionsNarrowToZeroRowsForUnknownObject(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var owner = db.Oracle
            ? Convert.ToString(await db.ScalarAsync(connection, "SELECT USER FROM DUAL", CancellationToken.None), CultureInfo.InvariantCulture)
            : connection.Database;
        var missing = "OBI_" + Guid.NewGuid().ToString("N").Substring(0, 12).ToUpperInvariant();
        var restrictions = db.Oracle ? new string?[] { null, owner, missing } : new string?[] { owner, null, missing };
        using var tables = await connection.GetSchemaAsync("Tables", restrictions, CancellationToken.None);
        Assert.Equal(0, tables.Rows.Count);
    }
}
