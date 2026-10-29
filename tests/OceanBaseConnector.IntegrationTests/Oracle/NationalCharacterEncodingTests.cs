using System;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Oracle;

public sealed class NationalCharacterEncodingTests
{
    public static TheoryData<string, string, string, bool, bool> Cases()
    {
        var cases = new TheoryData<string, string, string, bool, bool>();
        foreach (var profile in ProfileTheoryData.OracleWithCreateObjects())
            foreach (var (name, text, _) in ComplexNationalClientEncodingTests.Samples)
                foreach (var prepareExecute in new[] { false, true })
                    foreach (var asynchronous in new[] { false, true }) cases.Add(profile, name, text, prepareExecute, asynchronous);
        return cases;
    }

    [IntegrationTheory]
    [MemberData(nameof(Cases))]
    public async Task ExplicitNationalEncodingsPreserveStoredScalarValues(string profileName, string charset, string sample, bool prepareExecute, bool asynchronous)
    {
        var profile = IntegrationEnvironment.GetProfile(profileName);
        await using var db = new DatabaseContext(profile);
        await using var verifier = await db.OpenAsync();
        var table = await db.TableAsync(verifier, "id INTEGER PRIMARY KEY, n NCHAR(16), v NVARCHAR2(2000)", CancellationToken.None);
        var builder = profile.CreateBuilder(); builder.CharacterSet = "utf8mb4";
        builder.UseLobLocators = false; builder.UseOraclePrepareExecute = prepareExecute;
        await using var connection = new OceanBaseConnection(builder.ConnectionString);
        if (asynchronous) await connection.OpenAsync(); else connection.Open();
        await db.ExecuteAsync(connection, "SET NCHARACTER_SET_CONNECTION='" + charset + "'", CancellationToken.None);
        Assert.Equal(charset == "utf8mb3" ? "utf8mb4" : charset,
            await db.ScalarAsync(connection, "SELECT @@ncharacter_set_connection FROM DUAL", CancellationToken.None));
        var fixedText = sample + "'\\\0  ";
        var varyingText = new string('A', 1023) + sample + "'\\\0  ";
        using (var insert = db.Command(connection, "INSERT INTO " + table + " VALUES (:id,:n,:v)"))
        {
            DatabaseContext.Parameter(insert, "id", 1);
            insert.Parameters.Add(new OceanBaseParameter { ParameterName = "n", Value = fixedText, OceanBaseDbType = OceanBaseDbType.NChar });
            insert.Parameters.Add(new OceanBaseParameter { ParameterName = "v", Value = varyingText, OceanBaseDbType = OceanBaseDbType.NVarchar2 });
            Assert.Equal(1, asynchronous ? await insert.ExecuteNonQueryAsync() : insert.ExecuteNonQuery());
            insert.Parameters[0].Value = 2; insert.Parameters[1].Value = DBNull.Value; insert.Parameters[2].Value = DBNull.Value;
            Assert.Equal(1, asynchronous ? await insert.ExecuteNonQueryAsync() : insert.ExecuteNonQuery());
        }
        fixedText += new string(' ', 16 - fixedText.EnumerateRunes().Count());
        // Storage is verified through an independent default UTF8 connection, not a symmetric codec round trip.
        foreach (var source in new[] { verifier, connection })
            foreach (var binary in new[] { false, true })
                foreach (var sequential in new[] { false, true })
                {
                    using var read = db.Command(source, "SELECT n,v FROM " + table + " ORDER BY id");
                    if (binary) { if (asynchronous) await read.PrepareAsync(); else read.Prepare(); }
                    using var reader = asynchronous ? await read.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default)
                        : read.ExecuteReader(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.Equal(fixedText, reader.GetString(0));
                    using (var text = reader.GetTextReader(1)) Assert.Equal(varyingText, asynchronous ? await text.ReadToEndAsync() : text.ReadToEnd());
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.True(reader.IsDBNull(0)); Assert.True(reader.IsDBNull(1));
                    Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                }
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, "SELECT 1 FROM DUAL", CancellationToken.None), 1, "The national session remains usable.");
    }
}
