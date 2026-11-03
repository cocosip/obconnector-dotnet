using System;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace OceanBaseConnector.IntegrationTests.MySql;

public sealed class NestedArrayStringWireTests
{
    private readonly ITestOutputHelper _output;
    public NestedArrayStringWireTests(ITestOutputHelper output) => _output = output;
    private static string Type(int depth) => string.Concat(Enumerable.Repeat("ARRAY(", depth)) + "INT" + new string(')', depth);
    private static string Nested(string value, int depth) => new string('[', depth - 1) + value + new string(']', depth - 1);
    private static string? Sample(int row, int depth) => row switch
    {
        0 => Nested("[-2147483648,0,2147483647]", depth),
        1 => Nested("[1,NULL,-2]", depth),
        2 => Nested("[]", depth),
        3 => "[NULL]",
        4 => null,
        _ => depth == 1 ? "[1,2,3]" : Nested("[[1,2],[],NULL,[3]]", depth - 1),
    };

    [IntegrationTheory]
    [MemberData(nameof(VectorArrayStringWireTests.Cases), MemberType = typeof(VectorArrayStringWireTests))]
    public async Task IntegerArraysPreserveEverySupportedNestingDepth(string profileName, bool asynchronous, bool prepared)
    {
        var profile = IntegrationEnvironment.GetProfile(profileName);
        var builder = profile.CreateBuilder(); builder.Pooling = false;
        await using var db = new DatabaseContext(new IntegrationProfile { Name = profile.Name, Mode = profile.Mode, ConnectionString = builder.ConnectionString, CreateObjects = true });
        await using var connection = await db.OpenAsync();
        var columns = Enumerable.Range(1, 6).Select(depth => "a" + depth + " " + Type(depth)).ToArray();
        var table = await db.TableAsync(connection, "id INT PRIMARY KEY," + string.Join(",", columns), CancellationToken.None);
        const int sampleCount = 6;
        using (var insert = db.Command(connection, "INSERT INTO " + table + " VALUES(?,?,?,?,?,?,?)"))
        {
            var id = DatabaseContext.Parameter(insert, "", 0, DbType.Int32);
            var arrays = Enumerable.Range(1, 6).Select(_ => DatabaseContext.Parameter(insert, "", DBNull.Value, DbType.String)).ToArray();
            if (prepared) { if (asynchronous) await insert.PrepareAsync(); else insert.Prepare(); }
            for (var row = 0; row < sampleCount; row++)
            {
                id.Value = row + 1;
                for (var depth = 1; depth <= 6; depth++) arrays[depth - 1].Value = Sample(row, depth) ?? (object)DBNull.Value;
                try { Assert.Equal(1, asynchronous ? await insert.ExecuteNonQueryAsync() : insert.ExecuteNonQuery()); }
                catch (OceanBaseException error)
                {
                    _output.WriteLine("Row {0}: {1}/{2}: {3}", row, error.ServerErrorCode, error.SqlState, error.Message);
                    throw;
                }
            }
            id.Value = 99; arrays[0].Value = "[[1]]";
            var bad = await Assert.ThrowsAsync<OceanBaseException>(async () => { if (asynchronous) await insert.ExecuteNonQueryAsync(); else insert.ExecuteNonQuery(); });
            _output.WriteLine("Bad shape: {0}/{1}: {2}", bad.ServerErrorCode, bad.SqlState, bad.Message);
            Assert.False(bad.IsExecutionOutcomeUnknown); Assert.Equal(7602, bad.ServerErrorCode); Assert.Equal("22000", bad.SqlState); Assert.Equal(ConnectionState.Open, connection.State);
            arrays[0].Value = "[]"; arrays[1].Value = "[]";
            var empty = await Assert.ThrowsAsync<OceanBaseException>(async () => { if (asynchronous) await insert.ExecuteNonQueryAsync(); else insert.ExecuteNonQuery(); });
            Assert.Equal(7602, empty.ServerErrorCode); Assert.Equal("22000", empty.SqlState); Assert.False(empty.IsExecutionOutcomeUnknown);
        }

        using (var literal = db.Command(connection, "SELECT CAST('[]' AS ARRAY(ARRAY(INT)))"))
        {
            var empty = await Assert.ThrowsAsync<OceanBaseException>(async () => { if (asynchronous) await literal.ExecuteScalarAsync(); else literal.ExecuteScalar(); });
            Assert.Equal(7602, empty.ServerErrorCode); Assert.Equal("22000", empty.SqlState); Assert.False(empty.IsExecutionOutcomeUnknown);
        }

        await using var observer = await db.OpenAsync();
        foreach (var binary in new[] { false, true })
        {
            using var query = db.Command(observer, "SELECT * FROM " + table + " ORDER BY id");
            if (binary) { if (asynchronous) await query.PrepareAsync(); else query.Prepare(); }
            using var reader = asynchronous ? await query.ExecuteReaderAsync(CommandBehavior.SequentialAccess) : query.ExecuteReader(CommandBehavior.SequentialAccess);
            var schema = Assert.IsType<OceanBaseDataReader>(reader).GetColumnSchema();
            for (var depth = 1; depth <= 6; depth++)
            {
                Assert.Equal("CHAR", reader.GetDataTypeName(depth)); Assert.Equal(typeof(string), reader.GetFieldType(depth));
                Assert.Equal((int)OceanBaseDbType.String, schema[depth]["ProviderType"]);
            }
            for (var row = 0; row < sampleCount; row++)
            {
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                DatabaseContext.EqualNumber(reader.GetValue(0), row + 1, "Nested ARRAY row order must remain stable.");
                for (var depth = 1; depth <= 6; depth++)
                    if (Sample(row, depth) == null) Assert.True(reader.IsDBNull(depth));
                    else Assert.Equal(Sample(row, depth), reader.GetString(depth));
            }
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
        }
        DatabaseContext.EqualNumber(await db.ScalarAsync(observer, "SELECT COUNT(*) FROM " + table + " WHERE id=99", CancellationToken.None), 0, "Rejected nested input must not insert a row.");

        var invalidTable = db.NewObject("TABLE", registerCleanup: false);
        using (var ddl = db.Command(connection, "CREATE TABLE " + invalidTable + "(a " + Type(7) + ")"))
        {
            var error = await Record.ExceptionAsync(async () => { if (asynchronous) await ddl.ExecuteNonQueryAsync(); else ddl.ExecuteNonQuery(); });
            if (error == null) db.RegisterDropCleanup("TABLE", invalidTable);
            var rejected = Assert.IsType<OceanBaseException>(error); Assert.False(rejected.IsExecutionOutcomeUnknown); Assert.Equal(1235, rejected.ServerErrorCode); Assert.Equal("0A000", rejected.SqlState);
            _output.WriteLine("Depth seven: {0}/{1}: {2}", rejected.ServerErrorCode, rejected.SqlState, rejected.Message);
        }
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1, "Depth rejection must preserve the session.");
    }
}
