using System;
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
    public static TheoryData<string, bool> LiveProfiles()
    {
        var rows = new TheoryData<string, bool>();
        foreach (var profile in IntegrationEnvironment.Profiles)
            if (profile.CreateObjects)
            {
                rows.Add(profile.Name, true);
                rows.Add(profile.Name, false);
            }
        return rows;
    }

    [IntegrationTheory]
    [MemberData(nameof(LiveProfiles))]
    public async Task SchemaOnlyDoesNotUploadInputStream(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        using var source = new MemoryStream(new byte[] { 1, 2, 3 });
        await using var command = db.Command(connection, db.Oracle ? "SELECT :payload FROM DUAL" : "SELECT ?");
        command.Parameters.Add(new OceanBaseParameter { ParameterName = "payload", Value = source });

        await using var reader = asynchronous
            ? await command.ExecuteReaderAsync(CommandBehavior.SchemaOnly)
            : command.ExecuteReader(CommandBehavior.SchemaOnly);

        Assert.Equal(1, reader.FieldCount);
        Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
        Assert.Equal(0, source.Position);
        Assert.True(source.CanRead);
    }

    [IntegrationTheory]
    [MemberData(nameof(LiveProfiles))]
    public async Task KeyInfoReportsPrimaryKeyAndHidesMissingKeyColumn(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var table = await db.TableAsync(connection,
            "id INTEGER NOT NULL PRIMARY KEY, payload " + (db.Oracle ? "VARCHAR2(40)" : "VARCHAR(40)"),
            CancellationToken.None);

        await using var command = db.Command(connection, "SELECT payload FROM " + table);
        var behavior = CommandBehavior.SchemaOnly | CommandBehavior.KeyInfo;
        await using var reader = asynchronous ? await command.ExecuteReaderAsync(behavior) : command.ExecuteReader(behavior);

        Assert.Equal(2, reader.FieldCount);
        Assert.Equal(1, reader.VisibleFieldCount);
        var columns = reader.GetColumnSchema();
        Assert.False(columns[0].IsHidden);
        Assert.True(columns[1].IsHidden);
        Assert.True(columns[1].IsKey);
        Assert.Equal(db.Oracle ? "ID" : "id", columns[1].BaseColumnName);
        Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
    }

    [IntegrationTheory]
    [MemberData(nameof(LiveProfiles))]
    public async Task KeyInfoAppendsCompositePrimaryColumnsForAliasedProjection(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var unique = "OBI_UQ_" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var table = await db.TableAsync(connection,
            "id_a INTEGER NOT NULL, id_b INTEGER NOT NULL, payload " + (db.Oracle ? "VARCHAR2(40)" : "VARCHAR(40)") +
            ", CONSTRAINT " + unique + " UNIQUE (payload)",
            CancellationToken.None);
        await db.ExecuteAsync(connection, "ALTER TABLE " + table + " ADD CONSTRAINT " + table + "_PK PRIMARY KEY (id_a, id_b)", CancellationToken.None);
        await db.ExecuteAsync(connection, "INSERT INTO " + table + " (id_a, id_b, payload) VALUES (1, 2, 'payload')", CancellationToken.None);

        await using var command = db.Command(connection, "SELECT t.payload AS payload_alias FROM " + table + " t");
        var behavior = CommandBehavior.KeyInfo;
        await using var reader = asynchronous ? await command.ExecuteReaderAsync(behavior) : command.ExecuteReader(behavior);
        var columns = reader.GetColumnSchema();
        Assert.Equal(3, columns.Count);
        Assert.Equal(1, reader.VisibleFieldCount);
        Assert.False(columns[0].IsHidden);
        Assert.True(columns[0].IsAliased);
        Assert.Equal("payload", columns[0].BaseColumnName, ignoreCase: true);
        Assert.False(columns[0].IsKey);
        Assert.True(columns[0].IsUnique);
        for (var i = 1; i < 3; i++)
        {
            Assert.True(columns[i].IsHidden);
            Assert.True(columns[i].IsKey);
            Assert.False(columns[i].IsUnique);
            Assert.Equal(i == 1 ? "id_a" : "id_b", columns[i].BaseColumnName, ignoreCase: true);
        }
        Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
        Assert.Equal("payload", reader.GetString(0));
        DatabaseContext.EqualNumber(reader.GetValue(1), 1, "First hidden composite key mismatch.");
        DatabaseContext.EqualNumber(reader.GetValue(2), 2, "Second hidden composite key mismatch.");
        Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
    }

    [IntegrationTheory]
    [MemberData(nameof(LiveProfiles))]
    public async Task KeyInfoDoesNotPromoteNullableUniqueColumnToRowKey(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var table = await db.TableAsync(connection,
            "nullable_value " + (db.Oracle ? "VARCHAR2(40)" : "VARCHAR(40)") + " NULL, CONSTRAINT " +
            "UQ_" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant() + " UNIQUE (nullable_value)",
            CancellationToken.None);

        await using var command = db.Command(connection, "SELECT nullable_value FROM " + table);
        var behavior = CommandBehavior.SchemaOnly | CommandBehavior.KeyInfo;
        await using var reader = asynchronous ? await command.ExecuteReaderAsync(behavior) : command.ExecuteReader(behavior);
        var column = Assert.Single(reader.GetColumnSchema());
        Assert.False(column.IsHidden);
        Assert.False(column.IsKey);
        Assert.True(column.IsUnique);
        Assert.True(column.AllowDBNull);
        Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
    }
}
