using System;
using System.Data;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace OceanBaseConnector.IntegrationTests.Types;

public sealed class EucCharacterSetTests(ITestOutputHelper output)
{
    internal static readonly (string Name, string Text, string Hex)[] Samples =
    {
        ("gb2312", "中文丨", "D6D0CEC4D8AD"),
        ("euckr", "한글", "C7D1B1DB"),
    };

    public static TheoryData<string, string, string, bool, bool> Cases()
    {
        var cases = new TheoryData<string, string, string, bool, bool>();
        foreach (var profile in ProfileTheoryData.WithCreateObjects())
            foreach (var (charset, sample, _) in Samples)
                foreach (var prepared in new[] { false, true })
                    foreach (var asynchronous in new[] { false, true }) cases.Add(profile, charset, sample, prepared, asynchronous);
        return cases;
    }

    [IntegrationTheory]
    [MemberData(nameof(Cases))]
    public async Task ClientEncodingPreservesStorageAndRejectsUnrepresentableText(string profileName, string charset, string sample, bool prepared, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var observer = await db.OpenAsync();
        var builder = db.Profile.CreateBuilder(); builder.CharacterSet = charset; builder.Pooling = false; builder.UseLobLocators = false;
        await using var connection = new OceanBaseConnection(builder.ConnectionString);
        output.WriteLine("Profile={0}; charset={1}; prepared={2}; async={3}; stage=open", profileName, charset, prepared, asynchronous);
        try { if (asynchronous) await connection.OpenAsync(); else connection.Open(); }
        catch (OceanBaseException error)
        { throw new InvalidOperationException($"{charset} open failed with {error.ServerErrorCode}/{error.SqlState}.", error); }
        output.WriteLine("Server={0}; stage=session", connection.ServerVersion);
        await using (var state = db.Command(connection, db.ScalarSql("@@character_set_client,@@character_set_connection,@@character_set_results,@@collation_connection")))
        await using (var reader = asynchronous ? await state.ExecuteReaderAsync() : state.ExecuteReader())
        {
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.Equal(charset, reader.GetString(0)); Assert.Equal(charset, reader.GetString(1));
            Assert.Equal(db.Oracle ? "utf8mb4" : charset, reader.GetString(2));
            Assert.StartsWith(charset + "_", reader.GetString(3));
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
        }
        var session = connection.GetOpenSession();
        var table = await db.TableAsync(observer, "id INTEGER PRIMARY KEY, v " + (db.Oracle ? "VARCHAR2(2000)" : "VARCHAR(2000) CHARACTER SET utf8mb4"), CancellationToken.None);
        var literal = sample + "'  ";
        await using (var command = db.Command(connection, "INSERT INTO " + table + " VALUES (1,'" + literal.Replace("'", "''") + "')"))
            Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
        var longValue = new string('A', 1023) + sample + "'\\\0  ";
        var values = new object?[] { null, longValue, sample, DBNull.Value };
        await using (var insert = db.Command(connection, db.ParameterSql("INSERT INTO " + table + " VALUES (:id,:v)")))
        {
            var id = DatabaseContext.Parameter(insert, "id", 2, DbType.Int32);
            var value = DatabaseContext.Parameter(insert, "v", null, DbType.String);
            if (prepared) { if (asynchronous) await insert.PrepareAsync(); else insert.Prepare(); }
            for (var i = 0; i < values.Length; i++)
            {
                id.Value = i + 2; value.Value = values[i];
                Assert.Equal(1, asynchronous ? await insert.ExecuteNonQueryAsync() : insert.ExecuteNonQuery());
            }
            id.Value = 6; value.Value = "😀";
            await Assert.ThrowsAsync<EncoderFallbackException>(() => asynchronous ? insert.ExecuteNonQueryAsync() : Task.FromResult(insert.ExecuteNonQuery()));
            Assert.Equal(5, await db.CountAsync(observer, table, CancellationToken.None));
            value.Value = sample;
            Assert.Equal(1, asynchronous ? await insert.ExecuteNonQueryAsync() : insert.ExecuteNonQuery());
        }
        await using (var invalidSql = db.Command(connection, db.ScalarSql("'😀'")))
            await Assert.ThrowsAsync<EncoderFallbackException>(() => asynchronous ? invalidSql.ExecuteScalarAsync() : Task.FromResult(invalidSql.ExecuteScalar()));
        Assert.Same(session, connection.GetOpenSession());
        output.WriteLine("stage=independent storage and client results");
        foreach (var source in new[] { observer, connection })
            foreach (var binary in new[] { false, true })
                foreach (var sequential in new[] { false, true })
                {
                    await using var query = db.Command(source, "SELECT id,v FROM " + table + " ORDER BY id");
                    if (binary) { if (asynchronous) await query.PrepareAsync(); else query.Prepare(); }
                    await using var reader = asynchronous ? await query.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default)
                        : query.ExecuteReader(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                    var expected = new string?[] { literal, null, longValue, sample, null, sample };
                    for (var i = 0; i < expected.Length; i++)
                    {
                        Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                        DatabaseContext.EqualNumber(reader.GetValue(0), i + 1, "Character storage order mismatch.");
                        if (expected[i] is null) Assert.True(reader.IsDBNull(1));
                        else
                        {
                            using var text = reader.GetTextReader(1);
                            var actual = new StringBuilder(); var buffer = new char[1];
                            while ((asynchronous ? await text.ReadAsync(buffer, 0, 1) : text.Read(buffer, 0, 1)) != 0) actual.Append(buffer[0]);
                            Assert.Equal(expected[i], actual.ToString());
                        }
                    }
                    Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                    Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                }
    }
}
