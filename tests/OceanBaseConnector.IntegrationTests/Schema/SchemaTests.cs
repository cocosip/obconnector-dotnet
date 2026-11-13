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
    [MemberData(nameof(ProfileTheoryData.WithCreateObjects), MemberType = typeof(ProfileTheoryData))]
    public async Task ExtendedColumnMetadataPreservesDeclarationsAndGeneration(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var name = await db.TableAsync(connection, db.Oracle
            ? "ID NUMBER(18,0), N NUMBER(18,4) DEFAULT 1.25, B VARCHAR2(12 BYTE), C VARCHAR2(12 CHAR), R RAW(24), V NUMBER GENERATED ALWAYS AS (N+1) VIRTUAL, T TIMESTAMP(3) WITH TIME ZONE, I INTERVAL DAY(4) TO SECOND(9)"
            : "id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY, n DECIMAL(18,4) DEFAULT 1.25, literal VARCHAR(40) DEFAULT 'uuid()' COMMENT 'metadata', expr VARCHAR(40) DEFAULT (uuid()), v INT GENERATED ALWAYS AS (n+1) VIRTUAL, s INT GENERATED ALWAYS AS (n+1) STORED, ts TIMESTAMP(6) DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6)", CancellationToken.None);
        var owner = db.Oracle
            ? Convert.ToString(await db.ScalarAsync(connection, "SELECT USER FROM DUAL", CancellationToken.None), CultureInfo.InvariantCulture)
            : connection.Database;
        var restrictions = db.Oracle ? new string?[] { null, owner, name } : new string?[] { owner, null, name };
        using var sync = connection.GetSchema("Columns", restrictions);
        using var asynchronous = await connection.GetSchemaAsync("Columns", restrictions);
        Assert.Equal(sync.Rows.Count, asynchronous.Rows.Count);
        foreach (var table in new[] { sync, asynchronous })
        {
            Assert.Equal(23, table.Columns.Count);
            Assert.Equal(typeof(bool), table.Columns["IS_COMPUTED"]!.DataType);
            DataRow Column(string column) => Assert.Single(table.Rows.Cast<DataRow>(), row => (string)row["COLUMN_NAME"] == column);
            if (db.Oracle)
            {
                Assert.Equal("VARCHAR2(12 BYTE)", Column("B")["TYPE_DECLARATION"]);
                Assert.Equal("VARCHAR2(12 CHAR)", Column("C")["TYPE_DECLARATION"]);
                Assert.Equal("RAW(24)", Column("R")["TYPE_DECLARATION"]);
                Assert.Equal("NUMBER(18,4)", Column("N")["TYPE_DECLARATION"]);
                Assert.Equal("TIMESTAMP(3) WITH TIME ZONE", Column("T")["TYPE_DECLARATION"]);
                Assert.Equal("INTERVAL DAY(4) TO SECOND(9)", Column("I")["TYPE_DECLARATION"]);
                Assert.Equal(DBNull.Value, Column("ID")["IS_IDENTITY"]);
                Assert.Equal(DBNull.Value, Column("N")["IS_COMPUTED"]);
                Assert.Equal(DBNull.Value, Column("N")["DEFAULT_VALUE_SQL"]);
                Assert.Equal(true, Column("V")["IS_COMPUTED"]);
                Assert.Equal(false, Column("V")["IS_STORED"]);
                Assert.False(Column("V").IsNull("COMPUTED_EXPRESSION"));
                Assert.Equal(false, Column("V")["HAS_DEFAULT"]);
            }
            else
            {
                Assert.Contains("unsigned", (string)Column("id")["TYPE_DECLARATION"], StringComparison.OrdinalIgnoreCase);
                Assert.Equal(true, Column("id")["IS_IDENTITY"]);
                Assert.Equal("decimal(18,4)", ((string)Column("n")["TYPE_DECLARATION"]).ToLowerInvariant());
                Assert.Equal(true, Column("n")["HAS_DEFAULT"]);
                foreach (var column in new[] { "literal", "expr" })
                {
                    Assert.Equal(true, Column(column)["HAS_DEFAULT"]);
                    Assert.Equal(DBNull.Value, Column(column)["DEFAULT_VALUE_SQL"]);
                }
                Assert.Equal("metadata", Column("literal")["COLUMN_COMMENT"]);
                Assert.Equal(true, Column("v")["IS_COMPUTED"]); Assert.Equal(false, Column("v")["IS_STORED"]);
                Assert.Equal(true, Column("s")["IS_COMPUTED"]); Assert.Equal(true, Column("s")["IS_STORED"]);
                Assert.Equal(true, Column("ts")["HAS_ON_UPDATE"]); Assert.Equal("CURRENT_TIMESTAMP(6)", Column("ts")["ON_UPDATE_SQL"]);
                Assert.Equal("CURRENT_TIMESTAMP(6)", Column("ts")["DEFAULT_VALUE_SQL"]);
            }
        }
    }

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
    public async Task CompositeKeysIndexesAndForeignKeysHaveOrderedMetadata(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var suffix = Guid.NewGuid().ToString("N").Substring(0, 10).ToUpperInvariant();
        var pk = "OBI_PK_" + suffix; var fk = "OBI_FK_" + suffix; var index = "OBI_IX_" + suffix;
        var parent = await db.TableAsync(connection, "A INTEGER, B INTEGER, CONSTRAINT " + pk + " PRIMARY KEY (A, B)", CancellationToken.None);
        var child = await db.TableAsync(connection, "A INTEGER, B INTEGER, CONSTRAINT " + fk + " FOREIGN KEY (A, B) REFERENCES " + parent + " (A, B) ON DELETE CASCADE", CancellationToken.None);
        await db.ExecuteAsync(connection, "CREATE INDEX " + index + " ON " + parent + " (B, A)", CancellationToken.None);
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
    }

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.WithCreateObjects), MemberType = typeof(ProfileTheoryData))]
    public async Task CreatedViewIsDiscoverable(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var table = await db.TableAsync(connection, "A INTEGER", CancellationToken.None);
        var view = db.NewObject("VIEW", registerCleanup: false);
        await db.ExecuteAsync(connection, "CREATE VIEW " + view + " AS SELECT A FROM " + table, CancellationToken.None);
        db.RegisterDropCleanup("VIEW", view);
        var owner = db.Oracle ? Convert.ToString(await db.ScalarAsync(connection, "SELECT USER FROM DUAL", CancellationToken.None), CultureInfo.InvariantCulture) : connection.Database;
        using var views = await connection.GetSchemaAsync("Views", db.Oracle ? new[] { null, owner, view } : new[] { owner, null, view });
        Assert.Equal("VIEW", Assert.Single(views.Rows.Cast<DataRow>())["TABLE_TYPE"]);
    }

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.OracleWithCreateObjects), MemberType = typeof(ProfileTheoryData))]
    public async Task OracleTypesAndSequencesAreDiscoverableWithoutAdvancingSequence(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var type = db.NewObject("TYPE", registerCleanup: false);
        await db.ExecuteAsync(connection, "CREATE TYPE " + type + " AS OBJECT (A NUMBER)", CancellationToken.None);
        db.RegisterDropCleanup("TYPE", type);
        var sequence = db.NewObject("SEQUENCE", registerCleanup: false);
        await db.ExecuteAsync(connection, "CREATE SEQUENCE " + sequence + " START WITH 1 MAXVALUE 9999999999999999999999999999", CancellationToken.None);
        db.RegisterDropCleanup("SEQUENCE", sequence);
        var owner = Convert.ToString(await db.ScalarAsync(connection, "SELECT USER FROM DUAL", CancellationToken.None), CultureInfo.InvariantCulture);
        using var types = await connection.GetSchemaAsync("Types", new[] { null, owner, type });
        var typeRow = Assert.Single(types.Rows.Cast<DataRow>()); Assert.Equal("OBJECT", typeRow["TYPE_CODE"]); Assert.Equal(1, typeRow["ATTRIBUTE_COUNT"]);
        using var sequences = await connection.GetSchemaAsync("Sequences", new[] { null, owner, sequence });
        var sequenceRow = Assert.Single(sequences.Rows.Cast<DataRow>());
        Assert.Equal("9999999999999999999999999999", Assert.IsType<OceanBaseDecimal>(sequenceRow["MAXIMUM_VALUE"]).ToString());
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, "SELECT " + sequence + ".NEXTVAL FROM DUAL", CancellationToken.None), 1, "Metadata must not advance sequences.");
    }

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.OracleWithCreateObjects), MemberType = typeof(ProfileTheoryData))]
    public async Task OracleCreatedSynonymIsDiscoverable(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var table = await db.TableAsync(connection, "A INTEGER", CancellationToken.None);
        var synonym = db.NewObject("SYNONYM", registerCleanup: false);
        await db.ExecuteAsync(connection, "CREATE SYNONYM " + synonym + " FOR " + table, CancellationToken.None);
        db.RegisterDropCleanup("SYNONYM", synonym);
        var owner = Convert.ToString(await db.ScalarAsync(connection, "SELECT USER FROM DUAL", CancellationToken.None), CultureInfo.InvariantCulture);
        using var synonyms = await connection.GetSchemaAsync("Synonyms", new[] { null, owner, synonym });
        Assert.Equal(table, Assert.Single(synonyms.Rows.Cast<DataRow>())["OBJECT_NAME"]);
    }
}
