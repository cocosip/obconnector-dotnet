using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Oracle;

public sealed class ClobLocatorInputTests
{
    public static TheoryData<string, bool, bool> Cases()
    {
        var cases = new TheoryData<string, bool, bool>();
        foreach (var row in ProfileTheoryData.OraclePrepareExecuteObjects())
        {
            var profile = IntegrationEnvironment.GetProfile(row);
            if (!profile.CreateBuilder().UseLobLocators) continue;
            foreach (var large in new[] { false, true })
                foreach (var asynchronous in new[] { false, true }) cases.Add(profile.Name, large, asynchronous);
        }
        return cases;
    }

    [IntegrationTheory]
    [MemberData(nameof(Cases))]
    public Task ClobLocatorInputsPreserveTemporaryScalarStoredRawAndReadback(string profile, bool large, bool asynchronous)
        => Verify(IntegrationEnvironment.GetProfile(profile), large, asynchronous);

    internal static async Task Verify(IntegrationProfile profile, bool large, bool asynchronous)
    {
        var settings = profile.CreateBuilder();
        if (!profile.Oracle || !profile.CreateObjects || !settings.UseLobLocators || !settings.UseOraclePrepareExecute)
            throw new InvalidOperationException("An explicitly authorized Oracle object/PrepareExecute/LOB profile is required.");
        await using var db = new DatabaseContext(profile);
        await using var connection = await db.OpenAsync();
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY, v CLOB", CancellationToken.None);
        await using var transaction = (OceanBaseTransaction)await connection.BeginTransactionAsync();
        const string sample = "患者😀\\'";
        var text = large ? string.Concat(Enumerable.Repeat(sample, 20000)) : sample;
        var prefix = string.Concat(text.EnumerateRunes().Take(32).Select(r => r.ToString()));
        var length = text.EnumerateRunes().LongCount();
        await using var lob = asynchronous ? await connection.CreateTemporaryLobAsync(OceanBaseDbType.Clob, transaction)
            : connection.CreateTemporaryLob(OceanBaseDbType.Clob, transaction);
        Exception? failure = null;
        try
        {
            if (asynchronous) await lob.WriteTextAsync(0, text); else lob.WriteText(0, text);
            using (var source = lob.OpenTextReader()) Assert.Equal(text, asynchronous ? await source.ReadToEndAsync() : source.ReadToEnd());

            async Task<object?> Scalar(string sql, OceanBaseLob? input = null)
            {
                using var command = db.Command(connection, sql, transaction);
                if (input != null) DatabaseContext.Parameter(command, "v", input);
                return asynchronous ? await command.ExecuteScalarAsync() : command.ExecuteScalar();
            }
            async Task CheckBound(OceanBaseLob value)
            {
                Assert.Equal(length, DatabaseContext.IntegerResult(await Scalar("SELECT DBMS_LOB.GETLENGTH(:v) AS n FROM DUAL", value)));
                Assert.Equal(prefix, Assert.IsType<string>(await Scalar("SELECT DBMS_LOB.SUBSTR(:v,32,1) AS v FROM DUAL", value)));
            }
            await CheckBound(lob);
            using (var command = db.Command(connection, "INSERT INTO " + table + " VALUES (:id, :v)", transaction))
            {
                DatabaseContext.Parameter(command, "id", connection.CreateArrayType("NUMBER", transaction).CreateValue(1, 2));
                DatabaseContext.Parameter(command, "v", connection.CreateArrayType("CLOB", transaction).CreateValue(lob, null));
                var result = asynchronous ? await command.ExecuteArrayAsync(2) : command.ExecuteArray(2);
                Assert.True(result.IsComplete); Assert.Equal(2, result.RecordsAffected);
            }
            Assert.Equal(prefix, Assert.IsType<string>(await Scalar("SELECT DBMS_LOB.SUBSTR(v,32,1) AS v FROM " + table + " WHERE id=1")));
            Assert.Equal(Encoding.UTF8.GetBytes(prefix), Assert.IsType<byte[]>(await Scalar("SELECT UTL_RAW.CAST_TO_RAW(DBMS_LOB.SUBSTR(v,32,1)) AS v FROM " + table + " WHERE id=1")));
            Assert.Equal(1L, DatabaseContext.IntegerResult(await Scalar("SELECT COUNT(*) AS n FROM " + table + " WHERE id=2 AND v IS NULL")));
            using var stored = Assert.IsType<OceanBaseLob>(await Scalar("SELECT v FROM " + table + " WHERE id=1"));
            Assert.Equal(Encoding.UTF8.GetByteCount(text), stored.ByteLength);
            if (large) Assert.False(stored.Locator.HasInlineData);
            await CheckBound(stored);
            using var reader = stored.OpenTextReader(); Assert.Equal(text, asynchronous ? await reader.ReadToEndAsync() : reader.ReadToEnd());
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            try { if (asynchronous) await lob.FreeTemporaryAsync(); else lob.FreeTemporary(); }
            catch (Exception cleanup) when (failure != null) { throw new AggregateException(failure, cleanup); }
        }
        await transaction.RollbackAsync();
    }
}
