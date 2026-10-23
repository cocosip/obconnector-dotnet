using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Types;

/// <summary>
/// TINYINT Boolean mapping and matched/changed UPDATE row counts are client
/// behavior switches: every combination must be observable on a real session.
/// </summary>
public sealed class MySqlClientBehaviorTests
{
    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.MySqlWithCreateObjects), MemberType = typeof(ProfileTheoryData))]
    public async Task TinyBooleanAndAffectedRowsCombinationsHold(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        foreach (var tinyBoolean in new[] { false, true })
        foreach (var affectedRows in new[] { false, true })
        {
            var builder = db.Profile.CreateBuilder();
            builder.TreatTinyAsBoolean = tinyBoolean;
            builder.UseAffectedRows = affectedRows;
            await using var connection = new OceanBaseConnection(builder.ConnectionString);
            await connection.OpenAsync();
            var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY, flag TINYINT(1)", CancellationToken.None);
            await db.ExecuteAsync(connection, "INSERT INTO " + table + " VALUES (1,2),(2,0),(3,NULL)", CancellationToken.None);
            var unchanged = await db.ExecuteAsync(connection, "UPDATE " + table + " SET flag=2 WHERE id=1", CancellationToken.None);
            Assert.Equal(affectedRows ? 0 : 1, unchanged);
            Assert.Equal(1, await db.ExecuteAsync(connection, "UPDATE " + table + " SET flag=-1 WHERE id=1", CancellationToken.None));
            foreach (var binary in new[] { false, true })
            {
                await using var command = db.Command(connection, "SELECT flag FROM " + table + (binary ? " WHERE id>=?" : "") + " ORDER BY id");
                if (binary) command.Parameters.Add(new OceanBaseParameter { DbType = DbType.Int32, Value = 1 });
                await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess);
                Assert.Equal(tinyBoolean ? typeof(bool) : typeof(sbyte), reader.GetFieldType(0));
                Assert.True(await reader.ReadAsync());
                Assert.Equal(tinyBoolean ? (object)true : (sbyte)-1, reader.GetValue(0));
                Assert.True(await reader.ReadAsync());
                Assert.Equal(tinyBoolean ? (object)false : (sbyte)0, reader.GetValue(0));
                Assert.True(await reader.ReadAsync() && reader.IsDBNull(0), "NULL must remain NULL with either mapping.");
                Assert.False(await reader.ReadAsync());
            }
        }
    }
}
