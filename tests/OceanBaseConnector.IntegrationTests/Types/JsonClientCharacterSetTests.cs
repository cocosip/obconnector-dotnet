using System;
using System.Data;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace OceanBaseConnector.IntegrationTests.Types;

public sealed class JsonClientCharacterSetTests(ITestOutputHelper output)
{
    public static TheoryData<string, string, string, bool> Cases()
    {
        var cases = new TheoryData<string, string, string, bool>();
        foreach (var profile in IntegrationEnvironment.Profiles)
            if (!profile.Oracle && profile.CreateObjects)
                foreach (var (charset, text) in new[] { ("ascii", "ASCII"), ("latin1", "é€"), ("gbk", "中文"), ("utf8mb3", "中é"), ("gb18030", "😀中") })
                    foreach (var async in new[] { false, true }) cases.Add(profile.Name, charset, text, async);
        return cases;
    }

    [IntegrationTheory]
    [MemberData(nameof(Cases))]
    public async Task JsonClientStringsConvertToJsonStorageAndKeepSqlNullDistinct(string profileName, string charset, string text, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        var builder = db.Profile.CreateBuilder(); builder.CharacterSet = charset;
        await using var connection = new OceanBaseConnection(builder.ConnectionString);
        if (asynchronous) await connection.OpenAsync(); else connection.Open();
        output.WriteLine("Profile={0}; server={1}; charset={2}; async={3}", profileName, connection.ServerVersion, charset, asynchronous);
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY, value JSON", CancellationToken.None);
        await using (var insert = db.Command(connection, "INSERT INTO " + table + " VALUES (?, ?)"))
        {
            var id = DatabaseContext.Parameter(insert, "id", 0, DbType.Int32);
            var value = DatabaseContext.Parameter(insert, "value", null); value.OceanBaseDbType = OceanBaseDbType.Json;
            if (asynchronous) await insert.PrepareAsync(); else insert.Prepare();
            var values = new object[] { "{\"x\":\"" + text + "\"}", "{\"x\":\"\\u4E2D\\uD83D\\uDE00\"}", "null", DBNull.Value };
            for (var i = 0; i < values.Length; i++)
            {
                id.Value = i + 1; value.Value = values[i];
                Assert.Equal(1, asynchronous ? await insert.ExecuteNonQueryAsync() : insert.ExecuteNonQuery());
            }
            id.Value = 5; value.Value = "{\"x\":\"\ud800\"}";
            await Assert.ThrowsAsync<EncoderFallbackException>(() => asynchronous ? insert.ExecuteNonQueryAsync() : Task.FromResult(insert.ExecuteNonQuery()));
            value.Value = "{broken";
            var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? insert.ExecuteNonQueryAsync() : Task.FromResult(insert.ExecuteNonQuery()));
            Assert.False(error.IsExecutionOutcomeUnknown); Assert.NotEqual(0, error.ServerErrorCode);
            value.Value = "{\"x\":\"ok\"}";
            Assert.Equal(1, asynchronous ? await insert.ExecuteNonQueryAsync() : insert.ExecuteNonQuery());
        }
        foreach (var prepared in new[] { false, true })
        {
            await using var query = db.Command(connection, "SELECT value FROM " + table + " ORDER BY id");
            if (prepared) { if (asynchronous) await query.PrepareAsync(); else query.Prepare(); }
            await using var reader = asynchronous ? await query.ExecuteReaderAsync() : query.ExecuteReader();
            foreach (var expected in new string?[] { text, "中😀", "null", null, "ok" })
            {
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                if (expected is null) Assert.True(reader.IsDBNull(0));
                else
                {
                    using var json = JsonDocument.Parse(reader.GetString(0));
                    if (expected == "null") Assert.Equal(JsonValueKind.Null, json.RootElement.ValueKind);
                    else Assert.Equal(expected, json.RootElement.GetProperty("x").GetString());
                }
            }
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
        }
    }
}
