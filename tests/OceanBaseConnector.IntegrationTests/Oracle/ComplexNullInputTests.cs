using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Oracle;

public sealed class ComplexNullInputTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var profile in ProfileTheoryData.OraclePrepareExecuteObjects())
        {
            var lobs = IntegrationEnvironment.GetProfile(profile).CreateBuilder().UseLobLocators;
            foreach (var scalar in new[] { "NUMBER", "BLOB", "CLOB" })
                foreach (var shape in new[] { "object", "collection", "nested" })
                    foreach (var prepareExecute in new[] { false, true })
                        foreach (var asynchronous in new[] { false, true })
                            if ((prepareExecute && lobs) || scalar == "NUMBER") yield return new object[] { profile, scalar, shape, prepareExecute, asynchronous };
        }
    }

    [IntegrationTheory]
    [MemberData(nameof(Cases))]
    public async Task NamedNullInputPreservesRootIdentityAcrossPrepareAndReuse(string profileName, string scalar, string shape, bool prepareExecute, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        var builder = db.Profile.CreateBuilder(); builder.Pooling = false; builder.UseOraclePrepareExecute = prepareExecute;
        if (!prepareExecute) builder.UseLobLocators = false;
        await using var connection = new OceanBaseConnection(builder.ConnectionString);
        if (asynchronous) await connection.OpenAsync(); else connection.Open();
        var name = db.NewObject("TYPE");
        await db.ExecuteAsync(connection, "CREATE TYPE " + name + (shape == "collection" ? " AS TABLE OF " + scalar : " AS OBJECT(a " + scalar + ")"), CancellationToken.None);
        if (shape == "nested")
        {
            var root = db.NewObject("TYPE");
            await db.ExecuteAsync(connection, "CREATE TYPE " + root + " AS OBJECT(a " + name + ")", CancellationToken.None); name = root;
        }
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY,n INTEGER", CancellationToken.None);
        var procedure = db.NewObject("PROCEDURE");
        await db.ExecuteAsync(connection, "CREATE PROCEDURE " + procedure + "(k IN NUMBER,p IN " + name + ") IS n INTEGER; BEGIN " +
            "IF p IS NULL THEN n:=1; ELSE n:=0; END IF; INSERT INTO " + table + " VALUES(k,n); END;", CancellationToken.None);
        var owner = Convert.ToString(await db.ScalarAsync(connection, "SELECT USER FROM DUAL", CancellationToken.None), CultureInfo.InvariantCulture)!;
        var type = asynchronous ? await connection.GetComplexTypeAsync(owner, name) : connection.GetComplexType(owner, name);
        await using var observer = await db.OpenAsync();
        using var command = db.Command(connection, "BEGIN " + procedure + "(:k,:p); END;");
        var id = DatabaseContext.Parameter(command, "k", 1, DbType.Int32);
        var input = DatabaseContext.Parameter(command, "p", DBNull.Value); input.ComplexType = type;
        var session = connection.GetOpenSession();
        if (asynchronous) await command.PrepareAsync(); else command.Prepare();
        Assert.Equal(0, await db.CountAsync(observer, table, CancellationToken.None));
        for (var i = 0; i < 4; i++)
        {
            id.Value = i + 1;
            input.Value = i == 0 ? null : i == 2 ? DBNull.Value
                : shape == "nested" ? type.CreateValue(type.Members[0].ComplexType!.CreateValue((object?)null)) : type.CreateValue((object?)null);
            if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
            Assert.Same(session, connection.GetOpenSession());
        }
        using (var query = db.Command(observer, "SELECT id,n FROM " + table + " ORDER BY id"))
        {
            if (asynchronous) await query.PrepareAsync(); else query.Prepare();
            using var reader = asynchronous ? await query.ExecuteReaderAsync(CommandBehavior.SequentialAccess) : query.ExecuteReader(CommandBehavior.SequentialAccess);
            for (var i = 0; i < 4; i++)
            {
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                DatabaseContext.EqualNumber(reader.GetValue(0), i + 1, "NULL input execution order mismatch.");
                DatabaseContext.EqualNumber(reader.GetValue(1), i % 2 == 0 ? 1 : 0, "A root NULL must differ from a non-NULL complex value containing NULL members.");
            }
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
        }
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1, "The NULL input session must remain usable.");
    }
}
