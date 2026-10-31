using System;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Oracle;

public sealed class ComplexLobInputTests
{
    public static TheoryData<string, bool, bool, int> NamedBlobDmlCases()
    {
        var cases = new TheoryData<string, bool, bool, int>();
        foreach (var profileName in ProfileTheoryData.OraclePrepareExecuteObjects())
        {
            var profile = IntegrationEnvironment.GetProfile(profileName);
            if (!profile.CreateBuilder().UseLobLocators) continue;
            foreach (var nested in new[] { false, true })
                foreach (var asynchronous in new[] { false, true })
                    foreach (var size in new[] { 3, 90007 }) cases.Add(profile.Name, nested, asynchronous, size);
        }
        return cases;
    }

    [IntegrationTheory]
    [MemberData(nameof(NamedBlobDmlCases))]
    public Task NamedBlobObjectMembersCanBeStored(string profile, bool nested, bool asynchronous, int size)
        => VerifyNamedBlobDml(IntegrationEnvironment.GetProfile(profile), nested, asynchronous, size);

    internal static async Task VerifyNamedBlobDml(IntegrationProfile profile, bool nested, bool asynchronous, int size)
    {
        var settings = profile.CreateBuilder();
        if (!profile.Oracle || !profile.CreateObjects || !settings.UseLobLocators || !settings.UseOraclePrepareExecute)
            throw new InvalidOperationException("An explicitly authorized Oracle object/PrepareExecute/LOB profile is required.");

        await using var db = new DatabaseContext(profile);
        await using var connection = await db.OpenAsync();
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY, v BLOB", CancellationToken.None);
        var childName = nested ? db.NewObject("TYPE") : null;
        if (childName != null)
            await db.ExecuteAsync(connection, "CREATE TYPE " + childName + " AS OBJECT (a BLOB)", CancellationToken.None);
        var rootName = db.NewObject("TYPE");
        await db.ExecuteAsync(connection, "CREATE TYPE " + rootName + " AS OBJECT (" + (nested ? "c " + childName : "a BLOB") + ")", CancellationToken.None);
        var procedure = db.NewObject("PROCEDURE");
        var member = nested ? "p.c.a" : "p.a";
        await db.ExecuteAsync(connection, "CREATE PROCEDURE " + procedure + "(p IN " + rootName + ") IS BEGIN INSERT INTO " + table + " VALUES (1, " + member + "); END;", CancellationToken.None);
        var owner = Convert.ToString(await db.ScalarAsync(connection, "SELECT USER FROM DUAL", CancellationToken.None), CultureInfo.InvariantCulture)!;
        var type = await connection.GetComplexTypeAsync(owner, rootName);
        var bytes = Enumerable.Range(0, size).Select(i => (byte)(i * 31)).ToArray();
        var value = nested ? type.CreateValue(type.Members[0].ComplexType!.CreateValue(bytes)) : type.CreateValue(bytes);

        await using (var command = db.Command(connection, procedure))
        {
            command.CommandType = CommandType.StoredProcedure;
            DatabaseContext.Parameter(command, "p", value);
            if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
        }

        await using var verification = await db.OpenAsync();
        await using var query = db.Command(verification, "SELECT v FROM " + table + " WHERE id=1");
        await using var reader = asynchronous ? await query.ExecuteReaderAsync() : query.ExecuteReader();
        Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
        using var lob = reader.GetFieldValue<OceanBaseLob>(0);
        using var source = lob.OpenRead();
        using var actual = new MemoryStream();
        if (asynchronous) await source.CopyToAsync(actual); else source.CopyTo(actual);
        Assert.Equal(bytes, actual.ToArray());
        Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
    }

    public static TheoryData<string, bool, string, bool> Cases()
    {
        var cases = new TheoryData<string, bool, string, bool>();
        foreach (var row in ProfileTheoryData.OraclePrepareExecuteObjects())
        {
            var profile = IntegrationEnvironment.GetProfile(row);
            if (!profile.CreateBuilder().UseLobLocators) continue;
            foreach (var character in new[] { false, true })
                foreach (var shape in new[] { "object", "collection", "array" })
                    foreach (var asynchronous in new[] { false, true }) cases.Add(profile.Name, character, shape, asynchronous);
        }
        return cases;
    }

    [IntegrationTheory]
    [MemberData(nameof(Cases))]
    public Task ComplexLobPayloadsReachNamedMembersAndLocatorsReachAnonymousArrays(string profile, bool character, string shape, bool asynchronous)
        => Verify(IntegrationEnvironment.GetProfile(profile), character, shape, asynchronous);

    internal static async Task Verify(IntegrationProfile profile, bool character, string shape, bool asynchronous)
    {
        var settings = profile.CreateBuilder();
        if (!profile.Oracle || !profile.CreateObjects || !settings.UseLobLocators || !settings.UseOraclePrepareExecute)
            throw new InvalidOperationException("An explicitly authorized Oracle object/PrepareExecute/LOB profile is required.");
        if (shape != "object" && shape != "collection" && shape != "array") throw new ArgumentException("Unknown complex LOB input shape.", nameof(shape));
        await using var db = new DatabaseContext(profile);
        await using var connection = await db.OpenAsync();
        var scalar = character ? "CLOB" : "BLOB";
        string? procedure = null; OceanBaseComplexType? named = null; string? table = null;
        if (shape == "array") table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY, v " + scalar, CancellationToken.None);
        else
        {
            var typeName = db.NewObject("TYPE");
            await db.ExecuteAsync(connection, "CREATE TYPE " + typeName + (shape == "object" ? " AS OBJECT (a " + scalar + ")" : " AS TABLE OF " + scalar), CancellationToken.None);
            var owner = Convert.ToString(await db.ScalarAsync(connection, "SELECT USER FROM DUAL", CancellationToken.None), CultureInfo.InvariantCulture)!;
            named = await connection.GetComplexTypeAsync(owner, typeName);
            procedure = db.NewObject("PROCEDURE");
            var member = shape == "object" ? "p.a" : "p(1)";
            var nullGuard = shape == "collection" ? "IF p.COUNT <> 2 OR p(2) IS NOT NULL THEN RAISE_APPLICATION_ERROR(-20001, 'Invalid NULL member'); END IF; " : "";
            await db.ExecuteAsync(connection, "CREATE PROCEDURE " + procedure + "(p IN " + typeName + ", n OUT NUMBER, v OUT " + (character ? "VARCHAR2" : "RAW") + ") IS BEGIN " +
                nullGuard + "n := DBMS_LOB.GETLENGTH(" + member + "); v := DBMS_LOB.SUBSTR(" + member + ", 32, 1); END;", CancellationToken.None);
            // Creating the procedure invalidates observed dictionary snapshots.
            named = await connection.GetComplexTypeAsync(owner, typeName);
        }
        await using var transaction = (OceanBaseTransaction)await connection.BeginTransactionAsync();
        if (named != null) named = await connection.GetComplexTypeAsync(named.SchemaName, named.TypeName, transaction);
        var text = "患者😀\\'"; var bytes = new byte[] { 0, 1, 92, 39, 128, 255 };
        await using var lob = asynchronous ? await connection.CreateTemporaryLobAsync(character ? OceanBaseDbType.Clob : OceanBaseDbType.Blob, transaction)
            : connection.CreateTemporaryLob(character ? OceanBaseDbType.Clob : OceanBaseDbType.Blob, transaction);
        Exception? failure = null;
        try
        {
            if (character) { if (asynchronous) await lob.WriteTextAsync(0, text); else lob.WriteText(0, text); }
            else { if (asynchronous) await lob.WriteBytesAsync(0, bytes, 0, bytes.Length); else lob.WriteBytes(0, bytes, 0, bytes.Length); }
            using var command = db.Command(connection, shape == "array" ? "INSERT INTO " + table + " VALUES (:id, :v)" : procedure!, transaction);
            if (shape == "array")
            {
                DatabaseContext.Parameter(command, "id", connection.CreateArrayType("NUMBER", transaction).CreateValue(1, 2));
                DatabaseContext.Parameter(command, "v", connection.CreateArrayType(scalar, transaction).CreateValue(lob, null));
                var result = asynchronous ? await command.ExecuteArrayAsync(2) : command.ExecuteArray(2);
                Assert.True(result.IsComplete); Assert.Equal(2, result.RecordsAffected);
                using var query = db.Command(connection, "SELECT v FROM " + table + " ORDER BY id", transaction);
                using var reader = asynchronous ? await query.ExecuteReaderAsync() : query.ExecuteReader();
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                using var actual = reader.GetFieldValue<OceanBaseLob>(0);
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.True(reader.IsDBNull(0));
                Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                if (character) { using var source = actual.OpenTextReader(); Assert.Equal(text, asynchronous ? await source.ReadToEndAsync() : source.ReadToEnd()); }
                else { using var source = actual.OpenRead(); using var destination = new MemoryStream(); if (asynchronous) await source.CopyToAsync(destination); else source.CopyTo(destination); Assert.Equal(bytes, destination.ToArray()); }
            }
            else
            {
                command.CommandType = CommandType.StoredProcedure;
                Assert.Throws<NotSupportedException>(() => named!.CreateValue(lob));
                object inputValue = character ? text : bytes;
                DatabaseContext.Parameter(command, "p", shape == "object" ? named!.CreateValue(inputValue) : named!.CreateValue(inputValue, null));
                var length = new OceanBaseParameter { ParameterName = "n", Direction = ParameterDirection.Output, DbType = DbType.Decimal };
                var value = new OceanBaseParameter { ParameterName = "v", Direction = ParameterDirection.Output, DbType = character ? DbType.String : DbType.Binary };
                command.Parameters.Add(length); command.Parameters.Add(value);
                if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
                Assert.Equal(character ? text.EnumerateRunes().Count() : bytes.Length, DatabaseContext.IntegerResult(length.Value));
                if (character) Assert.Equal(text, Assert.IsType<string>(value.Value)); else Assert.Equal(bytes, Assert.IsType<byte[]>(value.Value));
            }
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
