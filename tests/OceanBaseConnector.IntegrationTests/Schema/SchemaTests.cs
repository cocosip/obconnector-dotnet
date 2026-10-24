using System;
using System.Globalization;
using System.Data;
using System.Linq;
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
        using var collections = connection.GetSchema();
        Assert.Equal(db.Oracle ? 20 : 18, collections.Rows.Count);
        foreach (DataRow collection in collections.Rows)
        {
            var name = (string)collection["CollectionName"];
            using var sync = connection.GetSchema(name);
            using var asynchronous = await connection.GetSchemaAsync(name);
            Assert.True(sync.Columns.Count > 0, $"Collection '{name}' must describe its shape.");
            Assert.Equal(sync.Columns.Count, asynchronous.Columns.Count);
            if (name == "MetaDataCollections") Assert.Equal(collections.Rows.Count, sync.Rows.Count);
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
    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.WithCreateObjects), MemberType = typeof(ProfileTheoryData))]
    public async Task CompositeKeysIndexesForeignKeysAndViewsHaveOrderedMetadata(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var suffix = Guid.NewGuid().ToString("N").Substring(0, 10).ToUpperInvariant();
        var pk = "OBI_PK_" + suffix; var fk = "OBI_FK_" + suffix; var index = "OBI_IX_" + suffix;
        var parent = await db.TableAsync(connection, "A INTEGER, B INTEGER, CONSTRAINT " + pk + " PRIMARY KEY (A, B)", CancellationToken.None);
        var child = await db.TableAsync(connection, "A INTEGER, B INTEGER, CONSTRAINT " + fk + " FOREIGN KEY (A, B) REFERENCES " + parent + " (A, B) ON DELETE CASCADE", CancellationToken.None);
        await db.ExecuteAsync(connection, "CREATE INDEX " + index + " ON " + parent + " (B, A)", CancellationToken.None);
        var view = db.NewObject("VIEW");
        await db.ExecuteAsync(connection, "CREATE VIEW " + view + " AS SELECT A FROM " + parent, CancellationToken.None);
        var owner = db.Oracle ? Convert.ToString(await db.ScalarAsync(connection, "SELECT USER FROM DUAL", CancellationToken.None), CultureInfo.InvariantCulture) : connection.Database;
        string?[] Filter(string table, string? item = null) => db.Oracle ? new[] { null, owner, table, item } : new[] { owner, null, table, item };
        using var keys = await connection.GetSchemaAsync("Keys", Filter(parent));
        Assert.Contains(keys.Rows.Cast<DataRow>(), row => (string)row["CONSTRAINT_TYPE"] == "PRIMARY KEY");
        using var indexes = await connection.GetSchemaAsync("Indexes", Filter(parent, index));
        Assert.Single(indexes.Rows.Cast<DataRow>());
        using var indexColumns = await connection.GetSchemaAsync("IndexColumns", Filter(parent, index));
        Assert.Equal(new[] { "B", "A" }, indexColumns.Rows.Cast<DataRow>().Select(row => (string)row["COLUMN_NAME"]));
        Assert.Equal(new[] { 1, 2 }, indexColumns.Rows.Cast<DataRow>().Select(row => (int)row["ORDINAL_POSITION"]));
        using var constraints = await connection.GetSchemaAsync("Constraints", Filter(child, fk));
        Assert.Equal("FOREIGN KEY", Assert.Single(constraints.Rows.Cast<DataRow>())["CONSTRAINT_TYPE"]);
        using var columns = await connection.GetSchemaAsync("ConstraintColumns", Filter(child, fk));
        Assert.Equal(new[] { "A", "B" }, columns.Rows.Cast<DataRow>().Select(row => (string)row["COLUMN_NAME"]));
        using var foreignKeys = await connection.GetSchemaAsync("ForeignKeys", Filter(child, fk));
        var key = Assert.Single(foreignKeys.Rows.Cast<DataRow>());
        Assert.True(string.Equals(parent, (string)key["REFERENCED_TABLE_NAME"], db.Oracle ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase));
        Assert.Equal("CASCADE", key["DELETE_RULE"]);
        using var foreignColumns = await connection.GetSchemaAsync("ForeignKeyColumns", Filter(child, fk));
        Assert.Equal(new[] { "A", "B" }, foreignColumns.Rows.Cast<DataRow>().Select(row => (string)row["COLUMN_NAME"]));
        Assert.Equal(new[] { "A", "B" }, foreignColumns.Rows.Cast<DataRow>().Select(row => (string)row["REFERENCED_COLUMN_NAME"]));
        using var views = await connection.GetSchemaAsync("Views", Filter(view).Take(3).ToArray());
        Assert.Equal("VIEW", Assert.Single(views.Rows.Cast<DataRow>())["TABLE_TYPE"]);
    }

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.OracleWithCreateObjects), MemberType = typeof(ProfileTheoryData))]
    public async Task OracleTypesSequencesAndSynonymsAreDiscoverableWithoutAdvancingSequence(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var type = db.NewObject("TYPE"); await db.ExecuteAsync(connection, "CREATE TYPE " + type + " AS OBJECT (A NUMBER)", CancellationToken.None);
        var sequence = db.NewObject("SEQUENCE"); await db.ExecuteAsync(connection, "CREATE SEQUENCE " + sequence + " START WITH 1 MAXVALUE 9999999999999999999999999999", CancellationToken.None);
        var table = await db.TableAsync(connection, "A INTEGER", CancellationToken.None);
        var synonym = db.NewObject("SYNONYM"); await db.ExecuteAsync(connection, "CREATE SYNONYM " + synonym + " FOR " + table, CancellationToken.None);
        var owner = Convert.ToString(await db.ScalarAsync(connection, "SELECT USER FROM DUAL", CancellationToken.None), CultureInfo.InvariantCulture);
        using var types = await connection.GetSchemaAsync("Types", new[] { null, owner, type });
        var typeRow = Assert.Single(types.Rows.Cast<DataRow>()); Assert.Equal("OBJECT", typeRow["TYPE_CODE"]); Assert.Equal(1, typeRow["ATTRIBUTE_COUNT"]);
        using var sequences = await connection.GetSchemaAsync("Sequences", new[] { null, owner, sequence });
        var sequenceRow = Assert.Single(sequences.Rows.Cast<DataRow>());
        Assert.Equal("9999999999999999999999999999", Assert.IsType<OceanBaseDecimal>(sequenceRow["MAXIMUM_VALUE"]).ToString());
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, "SELECT " + sequence + ".NEXTVAL FROM DUAL", CancellationToken.None), 1, "Metadata must not advance sequences.");
        using var synonyms = await connection.GetSchemaAsync("Synonyms", new[] { null, owner, synonym });
        Assert.Equal(table, Assert.Single(synonyms.Rows.Cast<DataRow>())["OBJECT_NAME"]);
    }
}
