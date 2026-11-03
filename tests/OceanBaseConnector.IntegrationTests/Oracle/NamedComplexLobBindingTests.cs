using System;
using System.Data;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Oracle;

public sealed class NamedComplexLobBindingTests
{
    public static TheoryData<string, bool> Cases()
    {
        var data = new TheoryData<string, bool>();
        foreach (var profileName in ProfileTheoryData.OraclePrepareExecuteObjects())
        {
            var profile = IntegrationEnvironment.GetProfile(profileName);
            if (!profile.CreateBuilder().UseLobLocators) continue;
            foreach (var asynchronous in new[] { false, true }) data.Add(profile.Name, asynchronous);
        }
        return data;
    }

    [IntegrationTheory]
    [MemberData(nameof(Cases))]
    public async Task NamedObjectLocatorInputUsesConstructorAndKeepsConnectionReusable(string profileName, bool asynchronous)
    {
        var profile = IntegrationEnvironment.GetProfile(profileName);
        if (!profile.Oracle || !profile.CreateObjects || !profile.CreateBuilder().UseLobLocators || !profile.CreateBuilder().UseOraclePrepareExecute)
            throw new InvalidOperationException("An explicitly authorized Oracle object/PrepareExecute/LOB profile is required.");

        await using var db = new DatabaseContext(profile);
        await using var connection = await db.OpenAsync();
        var table = await db.TableAsync(connection, "n NUMBER, len NUMBER, v RAW(32)", CancellationToken.None);
        var typeName = db.NewObject("TYPE");
        await db.ExecuteAsync(connection, "CREATE TYPE " + typeName + " AS OBJECT (a BLOB, n NUMBER)", CancellationToken.None);
        var procedure = db.NewObject("PROCEDURE");
        await db.ExecuteAsync(connection, "CREATE PROCEDURE " + procedure + "(p IN " + typeName + ") IS BEGIN INSERT INTO " + table +
            " VALUES (p.n, DBMS_LOB.GETLENGTH(p.a), DBMS_LOB.SUBSTR(p.a, 32, 1)); END;", CancellationToken.None);
        var owner = Convert.ToString(await db.ScalarAsync(connection, "SELECT USER FROM DUAL", CancellationToken.None), CultureInfo.InvariantCulture)!;
        await using var transaction = (OceanBaseTransaction)await connection.BeginTransactionAsync();
        var type = await connection.GetComplexTypeAsync(owner, typeName, transaction);
        await using var lob = asynchronous ? await connection.CreateTemporaryLobAsync(OceanBaseDbType.Blob, transaction)
            : connection.CreateTemporaryLob(OceanBaseDbType.Blob, transaction);
        var bytes = new byte[] { 0, 1, 92, 39, 128, 255 };
        if (asynchronous) await lob.WriteBytesAsync(0, bytes, 0, bytes.Length); else lob.WriteBytes(0, bytes, 0, bytes.Length);

        await using (var command = db.Command(connection, "BEGIN " + procedure + "(:p); END;", transaction))
        {
            DatabaseContext.Parameter(command, "p", type.CreateValue(lob, 7));
            if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
            if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
        }

        await transaction.CommitAsync();
        await using var verification = await db.OpenAsync();
        using var query = db.Command(verification, "SELECT n, len, v FROM " + table + " ORDER BY n");
        using var reader = asynchronous ? await query.ExecuteReaderAsync() : query.ExecuteReader();
        for (var i = 0; i < 2; i++)
        {
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.Equal(7, DatabaseContext.IntegerResult(reader.GetValue(0)));
            Assert.Equal(bytes.Length, DatabaseContext.IntegerResult(reader.GetValue(1)));
            Assert.Equal(bytes, Assert.IsType<byte[]>(reader.GetValue(2)));
        }
        Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
    }
}
