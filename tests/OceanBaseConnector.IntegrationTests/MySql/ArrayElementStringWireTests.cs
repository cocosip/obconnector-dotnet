using System;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.MySql;

public sealed class ArrayElementStringWireTests
{
    private static readonly string[] Types = { "TINYINT", "SMALLINT", "BIGINT", "FLOAT", "DOUBLE", "VARCHAR(256)" };
    private static readonly string[] Strings = { "患者😀", "", "NULL", "a,b", "q\"uote", "back\\slash", "line\nfeed", "nul\0x" };
    private static readonly string[] Inputs =
    {
        "[-128,0,127]", "[-32768,0,32767]", "[-9223372036854775808,0,9223372036854775807]",
        "[0.5,-1.25,16777216]", "[0.5,-1.25,9007199254740992]",
        "[\"患者😀\",\"\",\"NULL\",\"a,b\",\"q\\\"uote\",\"back\\\\slash\",\"line\\nfeed\",\"nul\\u0000x\"]",
    };
    // The target server formats FLOAT and emits raw VARCHAR controls; ARRAY text is opaque, not JSON.
    private static readonly string[] Results =
    {
        Inputs[0], Inputs[1], Inputs[2], "[0.5,-1.25,16777200]", "[0.5,-1.25,9.007199254740992e15]",
        "[\"患者😀\",\"\",\"NULL\",\"a,b\",\"q\\\"uote\",\"back\\slash\",\"line\nfeed\",\"nul\0x\"]",
    };
    private static string? Sample(int row, int column, bool result) => row switch
    {
        1 => "[NULL]",
        2 => "[]",
        3 => null,
        _ => (result ? Results : Inputs)[column],
    };

    [IntegrationTheory]
    [MemberData(nameof(VectorArrayStringWireTests.Cases), MemberType = typeof(VectorArrayStringWireTests))]
    public async Task ArraysUseServerStringRepresentationAndKeepStoredElements(string profileName, bool asynchronous, bool prepared)
    {
        var profile = IntegrationEnvironment.GetProfile(profileName);
        var builder = profile.CreateBuilder(); builder.Pooling = false;
        var db = new DatabaseContext(new IntegrationProfile { Name = profile.Name, Mode = profile.Mode, ConnectionString = builder.ConnectionString, CreateObjects = true });
        Exception? failure = null;
        try
        {
            await using var connection = await db.OpenAsync();
            var definitions = Types.Select((type, i) => "a" + i + " ARRAY(" + type + ")");
            var table = await db.TableAsync(connection, "id INT PRIMARY KEY," + string.Join(",", definitions), CancellationToken.None);
            using (var insert = db.Command(connection, "INSERT INTO " + table + " VALUES(?,?,?,?,?,?,?)"))
            {
                var id = DatabaseContext.Parameter(insert, "", 0, DbType.Int32);
                var arrays = Types.Select(_ => DatabaseContext.Parameter(insert, "", DBNull.Value, DbType.String)).ToArray();
                if (prepared) { if (asynchronous) await insert.PrepareAsync(); else insert.Prepare(); }
                for (var row = 0; row < 4; row++)
                {
                    id.Value = row + 1;
                    for (var column = 0; column < Types.Length; column++) arrays[column].Value = Sample(row, column, false) ?? (object)DBNull.Value;
                    Assert.Equal(1, asynchronous ? await insert.ExecuteNonQueryAsync() : insert.ExecuteNonQuery());
                }
                id.Value = 99;
                for (var column = 0; column < Types.Length; column++) arrays[column].Value = Inputs[column];
                for (var column = 0; column < Types.Length; column++)
                {
                    arrays[column].Value = "[1,";
                    var error = await Assert.ThrowsAsync<OceanBaseException>(async () => { if (asynchronous) await insert.ExecuteNonQueryAsync(); else insert.ExecuteNonQuery(); });
                    Assert.Equal(3140, error.ServerErrorCode); Assert.Equal("22032", error.SqlState);
                    Assert.False(error.IsExecutionOutcomeUnknown); Assert.Equal(ConnectionState.Open, connection.State);
                    arrays[column].Value = Inputs[column];
                }
                id.Value = 5;
                Assert.Equal(1, asynchronous ? await insert.ExecuteNonQueryAsync() : insert.ExecuteNonQuery());
            }

            await using var observer = await db.OpenAsync();
            foreach (var binary in new[] { false, true })
            {
                using (var query = db.Command(observer, "SELECT *,29 FROM " + table + " ORDER BY id"))
                {
                    if (binary) { if (asynchronous) await query.PrepareAsync(); else query.Prepare(); }
                    using var reader = asynchronous ? await query.ExecuteReaderAsync(CommandBehavior.SequentialAccess) : query.ExecuteReader(CommandBehavior.SequentialAccess);
                    var schema = Assert.IsType<OceanBaseDataReader>(reader).GetColumnSchema();
                    for (var column = 1; column <= Types.Length; column++)
                    {
                        Assert.Equal("CHAR", reader.GetDataTypeName(column)); Assert.Equal(typeof(string), reader.GetFieldType(column));
                        Assert.Equal((int)OceanBaseDbType.String, schema[column]["ProviderType"]);
                    }
                    for (var row = 0; row < 5; row++)
                    {
                        Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                        DatabaseContext.EqualNumber(reader.GetValue(0), row + 1, "ARRAY row order differs.");
                        for (var column = 0; column < Types.Length; column++)
                            if (Sample(row, column, true) == null) Assert.True(reader.IsDBNull(column + 1));
                            else Assert.Equal(Sample(row, column, true), reader.GetString(column + 1));
                        DatabaseContext.EqualNumber(reader.GetValue(7), 29, "Field after ARRAY differs.");
                    }
                    Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                }
                var elements = string.Join(",", Enumerable.Range(1, Strings.Length).Select(i => "ELEMENT_AT(a5," + i + ")"));
                using (var query = db.Command(observer, "SELECT ELEMENT_AT(a3,3),ELEMENT_AT(a4,3)," + elements + ",29 FROM " + table + " WHERE id=1"))
                {
                    if (binary) { if (asynchronous) await query.PrepareAsync(); else query.Prepare(); }
                    using var reader = asynchronous ? await query.ExecuteReaderAsync() : query.ExecuteReader();
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                    Assert.Equal(binary ? 16777216f : 16777200f, reader.GetFloat(0));
                    Assert.Equal(9007199254740992d, reader.GetDouble(1));
                    for (var i = 0; i < Strings.Length; i++) Assert.Equal(Strings[i], reader.GetString(i + 2));
                    DatabaseContext.EqualNumber(reader.GetValue(10), 29, "Field after extracted elements differs.");
                    Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                }
            }
            DatabaseContext.EqualNumber(await db.ScalarAsync(observer, "SELECT COUNT(*) FROM " + table + " WHERE id=99", CancellationToken.None), 0, "Rejected inputs must not insert rows.");
            using (var literal = db.Command(connection, "SELECT CAST('[0.5,-1.25,16777216]' AS ARRAY(FLOAT)),CAST('[0.5,-1.25,9007199254740992]' AS ARRAY(DOUBLE))"))
            {
                using var reader = asynchronous ? await literal.ExecuteReaderAsync() : literal.ExecuteReader();
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                Assert.Equal(Results[3], reader.GetString(0)); Assert.Equal(Results[4], reader.GetString(1));
                Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
            }
            DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1, "ARRAY failures must leave the session usable.");
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            try { await db.DisposeAsync(); }
            catch (Exception cleanup) when (failure != null) { throw new AggregateException("ARRAY element test and cleanup failed.", failure, cleanup); }
        }
    }
}
