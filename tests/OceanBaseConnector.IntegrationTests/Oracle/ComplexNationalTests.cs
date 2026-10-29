using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Oracle;

public sealed class ComplexNationalTests
{
    public static IEnumerable<object[]> Profiles()
    {
        foreach (var profile in ProfileTheoryData.OraclePrepareExecuteObjects())
            foreach (var encoding in new[] { "utf8mb4", "utf16" }.Concat(ComplexNationalClientEncodingTests.Samples.Select(s => s.Name)))
                foreach (var nationalType in new[] { "NCHAR", "NVARCHAR2" })
                    foreach (var asynchronous in new[] { false, true })
                        foreach (var collection in new[] { false, true })
                            yield return new object[] { profile, encoding, nationalType, asynchronous, collection };
    }

    private static string NationalText(string encoding, string nationalType)
    {
        var sample = ComplexNationalClientEncodingTests.Samples.FirstOrDefault(s => s.Name == encoding);
        var text = sample.Name == null ? (nationalType == "NCHAR" ? "中文AZ" : "中文🙂") : sample.Text;
        return nationalType == "NCHAR" ? text + new string(' ', 4 - text.EnumerateRunes().Count()) : text;
    }

    private static async Task SetNationalAsync(DatabaseContext db, OceanBaseConnection connection, string encoding)
    {
        await db.ExecuteAsync(connection, "SET NCHARACTER_SET_CONNECTION = '" + encoding + "'", CancellationToken.None);
        Assert.Equal(encoding == "utf8mb3" ? "utf8mb4" : encoding,
            await db.ScalarAsync(connection, "SELECT @@ncharacter_set_connection FROM DUAL", CancellationToken.None));
        Assert.Equal("utf8mb4", await db.ScalarAsync(connection, "SELECT @@character_set_connection FROM DUAL", CancellationToken.None));
    }

    [IntegrationTheory]
    [MemberData(nameof(Profiles))]
    public async Task NamedNationalMembersRoundTripThroughBindingAndOutputs(string profileName, string encoding, string nationalType, bool asynchronous, bool collection)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        await SetNationalAsync(db, connection, encoding);
        var name = db.NewObject("TYPE");
        await db.ExecuteAsync(connection, "CREATE TYPE " + name + (collection ? " AS TABLE OF " : " AS OBJECT (v ") + nationalType + "(4)" + (collection ? "" : ", tag NUMBER)"), CancellationToken.None);
        var procedure = db.NewObject("PROCEDURE");
        await db.ExecuteAsync(connection, "CREATE PROCEDURE " + procedure + "(a IN " + name + ", b OUT " + name + ", c IN OUT " + name + ") AS BEGIN b := a; c := a; END;", CancellationToken.None);
        var table = collection ? null : await db.TableAsync(connection, "id INTEGER PRIMARY KEY, value " + name, CancellationToken.None);
        var owner = Convert.ToString(await db.ScalarAsync(connection, "SELECT USER FROM DUAL", CancellationToken.None), CultureInfo.InvariantCulture)!;
        var type = asynchronous ? await connection.GetComplexTypeAsync(owner, name) : connection.GetComplexType(owner, name);
        Assert.Equal(nationalType, type.Members[0].TypeName);
        var text = NationalText(encoding, nationalType);
        var value = collection ? type.CreateValue(text, null) : type.CreateValue(text, 7);
        await using (var command = db.Command(connection, procedure))
        {
            command.CommandType = CommandType.StoredProcedure;
            DatabaseContext.Parameter(command, "a", value);
            var output = DatabaseContext.Parameter(command, "b", DBNull.Value); output.ComplexType = type; output.Direction = ParameterDirection.Output;
            var inout = DatabaseContext.Parameter(command, "c", value); inout.Direction = ParameterDirection.InputOutput;
            if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
            AssertValue(output.Value); AssertValue(inout.Value);
        }
        if (table != null)
        {
            await using (var insert = db.Command(connection, "INSERT INTO " + table + " VALUES (1, :value)"))
            {
                DatabaseContext.Parameter(insert, "value", value);
                if (asynchronous) await insert.ExecuteNonQueryAsync(); else insert.ExecuteNonQuery();
            }
            foreach (var sequential in new[] { false, true })
            {
                await using var select = db.Command(connection, "SELECT value, 42 FROM " + table);
                if (asynchronous) await select.PrepareAsync(); else select.Prepare();
                await using var reader = asynchronous ? await select.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default)
                    : select.ExecuteReader(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                AssertValue(reader.GetValue(0));
                DatabaseContext.EqualNumber(reader.GetValue(1), 42, "Complex framing must retain the following column.");
                Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
            }
        }

        void AssertValue(object? actual)
        {
            var result = Assert.IsType<OceanBaseComplexValue>(actual); Assert.Same(type, result.Type);
            var values = result.GetValues();
            Assert.Equal(text, values[0]);
            if (collection) Assert.Null(values[1]);
            else DatabaseContext.EqualNumber(values[1], 7, "Ordinary member must retain its value.");
        }
    }

    [IntegrationTheory]
    [MemberData(nameof(Profiles))]
    public async Task NestedNationalMembersKeepTheirNamedIdentity(string profileName, string encoding, string nationalType, bool asynchronous, bool collection)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        await SetNationalAsync(db, connection, encoding);
        var childName = db.NewObject("TYPE");
        await db.ExecuteAsync(connection, "CREATE TYPE " + childName + " AS OBJECT(v " + nationalType + "(4), n NUMBER)", CancellationToken.None);
        var parentName = db.NewObject("TYPE");
        await db.ExecuteAsync(connection, "CREATE TYPE " + parentName + " AS OBJECT(child " + childName + ", tag VARCHAR2(8))", CancellationToken.None);
        var collectionName = db.NewObject("TYPE");
        await db.ExecuteAsync(connection, "CREATE TYPE " + collectionName + " AS TABLE OF " + parentName, CancellationToken.None);
        var name = collection ? collectionName : parentName;
        var procedure = db.NewObject("PROCEDURE");
        await db.ExecuteAsync(connection, "CREATE PROCEDURE " + procedure + "(a IN " + name + ", b OUT " + name + ", c IN OUT " + name + ") AS BEGIN b := a; c := a; END;", CancellationToken.None);
        var owner = Convert.ToString(await db.ScalarAsync(connection, "SELECT USER FROM DUAL", CancellationToken.None), CultureInfo.InvariantCulture)!;
        var type = asynchronous ? await connection.GetComplexTypeAsync(owner, name) : connection.GetComplexType(owner, name);
        var parent = collection ? type.Members[0].ComplexType! : type;
        var child = parent.Members[0].ComplexType!;
        var text = NationalText(encoding, nationalType);
        var nested = parent.CreateValue(child.CreateValue(text, 42), " x ");
        var value = collection ? type.CreateValue(nested, null) : nested;
        await using var command = db.Command(connection, procedure); command.CommandType = CommandType.StoredProcedure;
        DatabaseContext.Parameter(command, "a", value);
        var output = DatabaseContext.Parameter(command, "b", DBNull.Value); output.Direction = ParameterDirection.Output; output.ComplexType = type;
        var inout = DatabaseContext.Parameter(command, "c", value); inout.Direction = ParameterDirection.InputOutput;
        if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
        foreach (var actual in new[] { output.Value, inout.Value })
        {
            var result = Assert.IsType<OceanBaseComplexValue>(actual); Assert.Same(type, result.Type);
            if (collection) { Assert.Null(result.GetValues()[1]); result = Assert.IsType<OceanBaseComplexValue>(result.GetValues()[0]); }
            Assert.Same(parent, result.Type); Assert.Equal(" x ", result.GetValues()[1]);
            var resultChild = Assert.IsType<OceanBaseComplexValue>(result.GetValues()[0]); Assert.Same(child, resultChild.Type);
            Assert.Equal(text, resultChild.GetValues()[0]); DatabaseContext.EqualNumber(resultChild.GetValues()[1], 42, "Nested NUMBER must keep its value.");
        }
    }

    [IntegrationTheory]
    [MemberData(nameof(Profiles))]
    public async Task AnonymousNationalArraysBindDmlIterations(string profileName, string encoding, string nationalType, bool asynchronous, bool collection)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        await SetNationalAsync(db, connection, encoding);
        var text = NationalText(encoding, nationalType);
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY, v " + nationalType + "(4)", CancellationToken.None);
        await using var transaction = (OceanBaseTransaction)await connection.BeginTransactionAsync();
        var type = connection.CreateArrayType(nationalType, transaction);
        await using (var command = db.Command(connection, "INSERT INTO " + table + " VALUES (:id, :v)", transaction))
        {
            DatabaseContext.Parameter(command, "id", connection.CreateArrayType("NUMBER", transaction).CreateValue(1, 2));
            DatabaseContext.Parameter(command, "v", type.CreateValue(collection ? null : text, collection ? text : null));
            var result = asynchronous ? await command.ExecuteArrayAsync(2) : command.ExecuteArray(2);
            Assert.True(result.IsComplete); Assert.Equal(2, result.RecordsAffected);
        }
        await using (var command = db.Command(connection, "SELECT v FROM " + table + " ORDER BY id", transaction))
        {
            await using var reader = asynchronous ? await command.ExecuteReaderAsync() : command.ExecuteReader();
            Assert.True(await reader.ReadAsync()); Assert.Equal(collection ? DBNull.Value : text, reader.GetValue(0));
            Assert.True(await reader.ReadAsync()); Assert.Equal(collection ? text : DBNull.Value, reader.GetValue(0));
            Assert.False(await reader.ReadAsync());
        }
        await transaction.RollbackAsync();
    }

    public static IEnumerable<object[]> ReturningProfiles()
    {
        foreach (var row in Profiles()) if (!(bool)row[4]) yield return new[] { row[0], row[1], row[2], row[3] };
    }

    [IntegrationTheory]
    [MemberData(nameof(ReturningProfiles))]
    public async Task NationalObjectReturningRetainsOutputWhenSourceIdentityIsUnresolved(string profileName, string encoding, string nationalType, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        await SetNationalAsync(db, connection, encoding);
        var name = db.NewObject("TYPE");
        await db.ExecuteAsync(connection, "CREATE TYPE " + name + " AS OBJECT(v " + nationalType + "(4), n NUMBER)", CancellationToken.None);
        var table = await db.TableAsync(connection, "id INTEGER, v " + name, CancellationToken.None);
        var owner = Convert.ToString(await db.ScalarAsync(connection, "SELECT USER FROM DUAL", CancellationToken.None), CultureInfo.InvariantCulture)!;
        var type = await connection.GetComplexTypeAsync(owner, name);
        await using var command = db.Command(connection, "INSERT INTO " + table + " VALUES(1, :v) RETURNING v INTO :o");
        DatabaseContext.Parameter(command, "v", type.CreateValue(NationalText(encoding, nationalType), 7));
        var output = DatabaseContext.Parameter(command, "o", "old"); output.Direction = ParameterDirection.Output; output.ComplexType = type;
        var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
        Assert.True(error.IsExecutionOutcomeUnknown); Assert.Contains("Complex output metadata", error.InnerException!.Message);
        Assert.Equal("old", output.Value); Assert.Equal(ConnectionState.Broken, connection.State);
    }
}
