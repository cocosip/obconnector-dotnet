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

    [IntegrationTheory]
    [MemberData(nameof(Cases))]
    public async Task StoredProcedureLocatorInputUsesConstructorAfterPrepare(string profileName, bool asynchronous)
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

        await using (var command = db.Command(connection, procedure, transaction))
        {
            command.CommandType = CommandType.StoredProcedure;
            DatabaseContext.Parameter(command, "p", type.CreateValue(lob, 7));
            if (asynchronous) await command.PrepareAsync(); else command.Prepare();
            if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
        }

        await transaction.CommitAsync();
        await using var verification = await db.OpenAsync();
        using var query = db.Command(verification, "SELECT n, len, v FROM " + table);
        using var reader = asynchronous ? await query.ExecuteReaderAsync() : query.ExecuteReader();
        Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
        Assert.Equal(7, DatabaseContext.IntegerResult(reader.GetValue(0)));
        Assert.Equal(bytes.Length, DatabaseContext.IntegerResult(reader.GetValue(1)));
        Assert.Equal(bytes, Assert.IsType<byte[]>(reader.GetValue(2)));
        Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
    }

    public static TheoryData<string, bool, string, bool> ExpandedCases()
    {
        var data = new TheoryData<string, bool, string, bool>();
        foreach (var profileName in ProfileTheoryData.OraclePrepareExecuteObjects())
        {
            var profile = IntegrationEnvironment.GetProfile(profileName);
            if (!profile.CreateBuilder().UseLobLocators) continue;
            foreach (var character in new[] { false, true })
                foreach (var shape in new[] { "object", "collection", "nested-collection" })
                    foreach (var asynchronous in new[] { false, true }) data.Add(profile.Name, character, shape, asynchronous);
        }
        return data;
    }

    [IntegrationTheory]
    [MemberData(nameof(ExpandedCases))]
    public Task NamedObjectAndCollectionLocatorInputsUseConstructors(string profileName, bool character, string shape, bool asynchronous)
        => VerifyExpanded(IntegrationEnvironment.GetProfile(profileName), character, shape, asynchronous);

    public static TheoryData<string, bool, string, bool> PreparedCases()
    {
        var data = new TheoryData<string, bool, string, bool>();
        foreach (var profileName in ProfileTheoryData.OraclePrepareExecuteObjects())
        {
            var profile = IntegrationEnvironment.GetProfile(profileName);
            if (!profile.CreateBuilder().UseLobLocators) continue;
            foreach (var character in new[] { false, true })
                foreach (var shape in new[] { "object", "collection", "nested-collection" })
                    foreach (var asynchronous in new[] { false, true }) data.Add(profile.Name, character, shape, asynchronous);
        }
        return data;
    }

    [IntegrationTheory]
    [MemberData(nameof(PreparedCases))]
    public Task PreparedNamedObjectAndCollectionLocatorInputsUseCachedConstructors(string profileName, bool character, string shape, bool asynchronous)
        => VerifyExpanded(IntegrationEnvironment.GetProfile(profileName), character, shape, asynchronous, prepared: true);

    [IntegrationTheory]
    [MemberData(nameof(PreparedCases))]
    public Task TextLocatorInputWithScalarOutputs(string profileName, bool character, string shape, bool asynchronous)
        => VerifyExpanded(IntegrationEnvironment.GetProfile(profileName), character, shape, asynchronous, prepared: true, outputKind: "text");

    [IntegrationTheory]
    [MemberData(nameof(PreparedCases))]
    public Task ProcedureLocatorInputWithScalarOutputs(string profileName, bool character, string shape, bool asynchronous)
        => VerifyExpanded(IntegrationEnvironment.GetProfile(profileName), character, shape, asynchronous, prepared: true, outputKind: "procedure");

    [IntegrationTheory]
    [MemberData(nameof(PreparedCases))]
    public Task FunctionLocatorInputWithScalarReturn(string profileName, bool character, string shape, bool asynchronous)
        => VerifyExpanded(IntegrationEnvironment.GetProfile(profileName), character, shape, asynchronous, prepared: true, outputKind: "function");

    private static async Task VerifyExpanded(IntegrationProfile profile, bool character, string shape, bool asynchronous, bool prepared = false, string? outputKind = null)
    {
        var settings = profile.CreateBuilder();
        if (!profile.Oracle || !profile.CreateObjects || !settings.UseLobLocators || !settings.UseOraclePrepareExecute)
            throw new InvalidOperationException("An explicitly authorized Oracle object/PrepareExecute/LOB profile is required.");

        await using var db = new DatabaseContext(profile);
        await using var connection = await db.OpenAsync();
        var scalar = character ? "CLOB" : "BLOB";
        var resultType = character ? "VARCHAR2(128)" : "RAW(32)";
        var table = await db.TableAsync(connection, "n NUMBER, len NUMBER, v " + resultType, CancellationToken.None);
        var typeName = db.NewObject("TYPE", registerCleanup: shape == "nested-collection" ? false : true);
        string? nestedTypeName = null;
        if (shape == "nested-collection")
        {
            nestedTypeName = db.NewObject("TYPE", registerCleanup: false);
            RegisterForceTypeCleanup(db, nestedTypeName);
            RegisterForceTypeCleanup(db, typeName);
            await db.ExecuteAsync(connection, "CREATE TYPE " + nestedTypeName + " AS OBJECT (a " + scalar + ", n NUMBER)", CancellationToken.None);
        }
        await db.ExecuteAsync(connection, "CREATE TYPE " + typeName +
            (shape == "object" ? " AS OBJECT (a " + scalar + ")" : " AS TABLE OF " + (nestedTypeName ?? scalar)), CancellationToken.None);
        var owner = Convert.ToString(await db.ScalarAsync(connection, "SELECT USER FROM DUAL", CancellationToken.None), CultureInfo.InvariantCulture)!;
        var function = outputKind == "function";
        var procedure = db.NewObject("PROCEDURE", registerCleanup: false);
        db.RegisterDropCleanup(function ? "FUNCTION" : "PROCEDURE", procedure);
        var member = shape == "object" ? "p.a" : shape == "nested-collection" ? "p(1).a" : "p(1)";
        var nullGuard = shape == "collection" ? "IF p.COUNT <> 2 OR p(2) IS NOT NULL THEN RAISE_APPLICATION_ERROR(-20001, 'Invalid NULL member'); END IF; " :
            shape == "nested-collection" ? "IF p.COUNT <> 2 OR p(2).n <> 8 THEN RAISE_APPLICATION_ERROR(-20001, 'Invalid nested member'); END IF; " : "";
        var outputDeclaration = outputKind != null && !function ? ", n IN OUT NUMBER, result OUT NUMBER" : "";
        var outputBody = function ? " RETURN DBMS_LOB.GETLENGTH(" + member + ");"
            : outputKind != null ? " n := n + 1; result := DBMS_LOB.GETLENGTH(" + member + ");" : "";
        await db.ExecuteAsync(connection, "CREATE " + (function ? "FUNCTION " : "PROCEDURE ") + procedure + "(p IN " + typeName + outputDeclaration + ")" +
            (function ? " RETURN NUMBER" : "") + " IS BEGIN " + nullGuard +
            "INSERT INTO " + table + " VALUES (7, DBMS_LOB.GETLENGTH(" + member + "), DBMS_LOB.SUBSTR(" + member + ", 32, 1));" + outputBody + " END;", CancellationToken.None);
        var type = await connection.GetComplexTypeAsync(owner, typeName);
        await using var transaction = (OceanBaseTransaction)await connection.BeginTransactionAsync();
        type = await connection.GetComplexTypeAsync(owner, typeName, transaction);
        await using var lob = asynchronous ? await connection.CreateTemporaryLobAsync(character ? OceanBaseDbType.Clob : OceanBaseDbType.Blob, transaction)
            : connection.CreateTemporaryLob(character ? OceanBaseDbType.Clob : OceanBaseDbType.Blob, transaction);
        var text = "患者-CLOB";
        var bytes = new byte[] { 0, 1, 92, 39, 128, 255 };
        if (character)
        {
            if (asynchronous) await lob.WriteTextAsync(0, text); else lob.WriteText(0, text);
        }
        else if (asynchronous) await lob.WriteBytesAsync(0, bytes, 0, bytes.Length); else lob.WriteBytes(0, bytes, 0, bytes.Length);

        await using (var command = db.Command(connection, "BEGIN " + procedure + (outputKind != null ? "(:p, :n, :result); END;" : "(:p); END;"), transaction))
        {
            if (outputKind == "procedure" || function) { command.CommandType = CommandType.StoredProcedure; command.CommandText = procedure; }
            var value = shape == "object" ? type.CreateValue(lob)
                : shape == "nested-collection" ? type.CreateValue(type.Members[0].ComplexType!.CreateValue(lob, 7), type.Members[0].ComplexType!.CreateValue(lob, 8))
                : type.CreateValue(lob, null);
            DatabaseContext.Parameter(command, "p", value);
            OceanBaseParameter? counter = null;
            OceanBaseParameter? result = null;
            if (outputKind != null)
            {
                if (!function)
                {
                    counter = DatabaseContext.Parameter(command, "n", 7m, DbType.Decimal);
                    counter.Direction = ParameterDirection.InputOutput;
                }
                result = DatabaseContext.Parameter(command, "result", -1m, DbType.Decimal);
                result.Direction = function ? ParameterDirection.ReturnValue : ParameterDirection.Output;
            }
            if (prepared)
            {
                if (asynchronous) await command.PrepareAsync(); else command.Prepare();
            }
            for (var i = 0; i < 2; i++)
            {
                if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
                if (result != null) Assert.Equal(character ? text.Length : bytes.Length, DatabaseContext.IntegerResult(result.Value));
                if (counter != null) Assert.Equal(8 + i, DatabaseContext.IntegerResult(counter.Value));
            }
        }

        await transaction.CommitAsync();
        await using var verification = await db.OpenAsync();
        using var query = db.Command(verification, "SELECT n, len, v FROM " + table + " ORDER BY n");
        using var reader = asynchronous ? await query.ExecuteReaderAsync() : query.ExecuteReader();
        for (var i = 0; i < 2; i++)
        {
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.Equal(7, DatabaseContext.IntegerResult(reader.GetValue(0)));
            Assert.Equal(character ? text.Length : bytes.Length, DatabaseContext.IntegerResult(reader.GetValue(1)));
            if (character) Assert.Equal(text, Assert.IsType<string>(reader.GetValue(2)));
            else Assert.Equal(bytes, Assert.IsType<byte[]>(reader.GetValue(2)));
        }
        Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
    }

    private static void RegisterForceTypeCleanup(DatabaseContext db, string name)
        => db.RegisterCleanup("TYPE", name, async () =>
        {
            await using var cleanup = await db.OpenAsync(CancellationToken.None);
            await db.ExecuteAsync(cleanup, "DROP TYPE " + name + " FORCE", CancellationToken.None);
        });
}
