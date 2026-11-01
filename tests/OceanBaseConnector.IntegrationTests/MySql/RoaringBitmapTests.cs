using System;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.MySql;

public sealed class RoaringBitmapTests
{
    public static TheoryData<string, bool> Cases()
    {
        var data = new TheoryData<string, bool>();
        foreach (var profile in ProfileTheoryData.MySqlWithCreateObjects())
            foreach (var asynchronous in new[] { false, true })
                data.Add(profile, asynchronous);
        return data;
    }

    [IntegrationTheory]
    [MemberData(nameof(Cases))]
    public async Task RoaringBitmapUsesBinaryResultsAndSqlFunctionBinding(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        var builder = db.Profile.CreateBuilder(); builder.Pooling = false;
        await using var connection = new OceanBaseConnection(builder.ConnectionString);
        if (asynchronous) await connection.OpenAsync(); else connection.Open();
        var table = await db.TableAsync(connection, "id BIGINT PRIMARY KEY,value ROARINGBITMAP", CancellationToken.None);

        await Execute(connection, "INSERT INTO " + table + " VALUES (1,rb_from_string('1,2,4294967295'))", asynchronous);
        await Execute(connection, "INSERT INTO " + table + " VALUES (2,rb_from_string(''))", asynchronous);
        await Execute(connection, "INSERT INTO " + table + " VALUES (3,NULL)", asynchronous);
        await Execute(connection, "INSERT INTO " + table + " VALUES (4,rb_from_string('18446744073709551615'))", asynchronous);

        byte[] raw;
        using (var query = db.Command(connection, "SELECT id,value,rb_to_string(value),rb_to_array(value) FROM " + table + " ORDER BY id"))
        await using (var reader = asynchronous ? await query.ExecuteReaderAsync() : query.ExecuteReader())
        {
            Assert.Equal("BLOB", reader.GetDataTypeName(1));
            Assert.Equal(typeof(byte[]), reader.GetFieldType(1));
            Assert.Equal((int)OceanBaseDbType.Binary, reader.GetColumnSchema()[1]["ProviderType"]);

            Assert.True(await Read(reader, asynchronous));
            DatabaseContext.EqualNumber(reader.GetValue(0), 1, "RoaringBitmap first row id mismatch.");
            raw = reader.GetFieldValue<byte[]>(1);
            Assert.NotEmpty(raw);
            Assert.Equal("1,2,4294967295", reader.GetValue(2));
            Assert.IsType<string>(reader.GetValue(3));

            Assert.True(await Read(reader, asynchronous));
            DatabaseContext.EqualNumber(reader.GetValue(0), 2, "RoaringBitmap empty row id mismatch.");
            Assert.NotEmpty(reader.GetFieldValue<byte[]>(1));
            Assert.Equal(string.Empty, reader.GetValue(2));
            Assert.IsType<string>(reader.GetValue(3));

            Assert.True(await Read(reader, asynchronous));
            DatabaseContext.EqualNumber(reader.GetValue(0), 3, "RoaringBitmap NULL row id mismatch.");
            Assert.True(reader.IsDBNull(1)); Assert.True(reader.IsDBNull(2)); Assert.True(reader.IsDBNull(3));

            Assert.True(await Read(reader, asynchronous));
            DatabaseContext.EqualNumber(reader.GetValue(0), 4, "RoaringBitmap uint64 row id mismatch.");
            Assert.NotEmpty(reader.GetFieldValue<byte[]>(1));
            Assert.Equal("18446744073709551615", reader.GetValue(2));
            Assert.IsType<string>(reader.GetValue(3));
            Assert.False(await Read(reader, asynchronous));
            Assert.False(await NextResult(reader, asynchronous));
        }

        using (var insert = db.Command(connection, "INSERT INTO " + table + " VALUES (?,rb_build_varbinary(?))"))
        {
            var id = DatabaseContext.Parameter(insert, "", 5L, DbType.Int64);
            var value = DatabaseContext.Parameter(insert, "", raw, DbType.Binary);
            if (asynchronous) await insert.PrepareAsync(); else insert.Prepare();
            Assert.Equal(1, asynchronous ? await insert.ExecuteNonQueryAsync() : insert.ExecuteNonQuery());
            Assert.Same(raw, value.Value);
        }

        using (var invalid = db.Command(connection, "INSERT INTO " + table + " VALUES (?,rb_build_varbinary(?))"))
        {
            DatabaseContext.Parameter(invalid, "", 6L, DbType.Int64);
            DatabaseContext.Parameter(invalid, "", new byte[] { 1, 2, 3 }, DbType.Binary);
            var error = asynchronous
                ? await Assert.ThrowsAsync<OceanBaseException>(() => invalid.ExecuteNonQueryAsync())
                : Assert.Throws<OceanBaseException>(() => invalid.ExecuteNonQuery());
            Assert.NotEqual(0, error.ServerErrorCode);
            Assert.False(error.IsExecutionOutcomeUnknown);
        }

        using (var direct = db.Command(connection, "INSERT INTO " + table + " VALUES (?,?)"))
        {
            DatabaseContext.Parameter(direct, "", 7L, DbType.Int64);
            DatabaseContext.Parameter(direct, "", raw, DbType.Binary);
            var error = asynchronous
                ? await Assert.ThrowsAsync<OceanBaseException>(() => direct.ExecuteNonQueryAsync())
                : Assert.Throws<OceanBaseException>(() => direct.ExecuteNonQuery());
            Assert.Equal(1235, error.ServerErrorCode);
            Assert.Equal("0A000", error.SqlState);
            Assert.False(error.IsExecutionOutcomeUnknown);
        }

        using (var verify = db.Command(connection, "SELECT value,rb_to_string(value) FROM " + table + " WHERE id=5"))
        await using (var reader = asynchronous ? await verify.ExecuteReaderAsync() : verify.ExecuteReader())
        {
            Assert.True(await Read(reader, asynchronous));
            Assert.Equal(raw, reader.GetFieldValue<byte[]>(0));
            Assert.Equal("1,2,4294967295", reader.GetValue(1));
            Assert.False(await Read(reader, asynchronous));
            Assert.False(await NextResult(reader, asynchronous));
        }

        Assert.Equal(5L, await db.CountAsync(connection, table, CancellationToken.None));
    }

    private static async Task Execute(OceanBaseConnection connection, string sql, bool asynchronous)
    {
        await using var command = new OceanBaseCommand { Connection = connection, CommandText = sql };
        if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
    }

    private static Task<bool> Read(DbDataReader reader, bool asynchronous) =>
        asynchronous ? reader.ReadAsync() : Task.FromResult(reader.Read());

    private static Task<bool> NextResult(DbDataReader reader, bool asynchronous) =>
        asynchronous ? reader.NextResultAsync() : Task.FromResult(reader.NextResult());
}
