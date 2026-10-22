using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;

namespace OceanBaseConnector.Acceptance;

internal static partial class DatabaseCases
{
    private static async Task MySqlClientBehavior(CaseContext c, CancellationToken token)
    {
        foreach (var tinyBoolean in new[] { false, true })
        foreach (var affectedRows in new[] { false, true })
        {
            using var configured = c.CreateConnection();
            var builder = new OceanBaseConnectionStringBuilder(configured.ConnectionString)
            {
                TreatTinyAsBoolean = tinyBoolean,
                UseAffectedRows = affectedRows,
            };
            await using var connection = new OceanBaseConnection(builder.ConnectionString);
            await connection.OpenAsync(token);
            c.ObserveServer(connection);
            var table = await c.TableAsync(connection, "id INTEGER PRIMARY KEY, flag TINYINT(1)", token);
            await c.ExecuteAsync(connection, "INSERT INTO " + table + " VALUES (1,2),(2,0),(3,NULL)", token);
            var unchanged = await c.ExecuteAsync(connection, "UPDATE " + table + " SET flag=2 WHERE id=1", token);
            c.Check(unchanged == (affectedRows ? 0 : 1), "UPDATE must honor the configured matched/changed-row behavior.");
            c.Check(await c.ExecuteAsync(connection, "UPDATE " + table + " SET flag=-1 WHERE id=1", token) == 1,
                "Changed UPDATE must report one row with either setting.");
            foreach (var binary in new[] { false, true })
            {
                await using var command = c.Command(connection, "SELECT flag FROM " + table + (binary ? " WHERE id>=?" : "") + " ORDER BY id");
                if (binary) command.Parameters.Add(new OceanBaseParameter { DbType = DbType.Int32, Value = 1 });
                await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);
                c.Check(reader.GetFieldType(0) == (tinyBoolean ? typeof(bool) : typeof(sbyte)), "TINYINT metadata must honor its mapping option.");
                c.Check(await reader.ReadAsync(token), "The nonzero TINYINT row must exist.");
                c.Check(Equals(reader.GetValue(0), tinyBoolean ? (object)true : (sbyte)-1), "Nonzero TINYINT must decode according to its option.");
                c.Check(await reader.ReadAsync(token), "The zero TINYINT row must exist.");
                c.Check(Equals(reader.GetValue(0), tinyBoolean ? (object)false : (sbyte)0), "Zero TINYINT must decode according to its option.");
                c.Check(await reader.ReadAsync(token) && reader.IsDBNull(0), "NULL must remain NULL with either mapping.");
                c.Check(!await reader.ReadAsync(token), "The fixture must return exactly three rows.");
            }
        }
    }
}
