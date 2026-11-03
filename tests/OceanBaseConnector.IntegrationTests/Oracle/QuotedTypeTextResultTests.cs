using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Oracle;

public sealed class QuotedTypeTextResultTests
{
    public static TheoryData<string, bool> Rows()
    {
        var data = new TheoryData<string, bool>();
        foreach (var profile in ProfileTheoryData.OracleWithCreateObjects())
            foreach (var asynchronous in new[] { false, true }) data.Add(profile, asynchronous);
        return data;
    }

    [IntegrationTheory]
    [MemberData(nameof(Rows))]
    public async Task QuotedNamedObjectTextResultsUseExactDescriptorNames(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        var builder = db.Profile.CreateBuilder(); builder.Pooling = false; builder.AllowMultiStatements = true;
        await using var connection = new OceanBaseConnection(builder.ConnectionString);
        await connection.OpenAsync();
        var prefix = db.NewObject("TYPE", registerCleanup: false);
        var childName = prefix + " 子 type";
        var rootName = prefix + " (root)";
        var childSql = Quote(childName); var rootSql = Quote(rootName);
        await db.ExecuteAsync(connection, "CREATE TYPE " + childSql + " AS OBJECT(n NUMBER,s VARCHAR2(80))", CancellationToken.None);
        db.RegisterDropCleanup("TYPE", childSql);
        await db.ExecuteAsync(connection, "CREATE TYPE " + rootSql + " AS OBJECT(child " + childSql + ",id NUMBER)", CancellationToken.None);
        db.RegisterDropCleanup("TYPE", rootSql);
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY,value " + rootSql, CancellationToken.None);
        await db.ExecuteAsync(connection, "INSERT INTO " + table + " VALUES(1," + rootSql + "(" + childSql + "(7,'患者, (c) ''q'''),9))", CancellationToken.None);
        await db.ExecuteAsync(connection, "INSERT INTO " + table + " VALUES(2," + rootSql + "(NULL,NULL))", CancellationToken.None);
        var owner = Convert.ToString(await db.ScalarAsync(connection, "SELECT USER FROM DUAL", CancellationToken.None))!;
        var child = await connection.GetComplexTypeAsync(owner, childName);
        var root = await connection.GetComplexTypeAsync(owner, rootName);

        // Prove the quoted types and binary representation work before exercising the text parser.
        var sql = "SELECT value FROM " + table + " ORDER BY id";
        await using (var control = db.Command(connection, sql))
        {
            if (asynchronous) await control.PrepareAsync(); else control.Prepare();
            await using var reader = asynchronous ? await control.ExecuteReaderAsync() : control.ExecuteReader();
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            Verify(reader.GetFieldValue<OceanBaseComplexValue>(0), root, child);
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.All(reader.GetFieldValue<OceanBaseComplexValue>(0).GetValues(), Assert.Null);
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
        }
        await using (var command = db.Command(connection, sql))
        {
            await using var reader = asynchronous ? await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess) : command.ExecuteReader(CommandBehavior.SequentialAccess);
            Assert.Equal(rootName, reader.GetDataTypeName(0));
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            Verify(reader.GetFieldValue<OceanBaseComplexValue>(0), root, child);
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.All(reader.GetFieldValue<OceanBaseComplexValue>(0).GetValues(), Assert.Null);
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
        }
        await using (var command = db.Command(connection, sql + "; SELECT 42 FROM DUAL; " + sql))
        {
            await using var reader = asynchronous ? await command.ExecuteReaderAsync() : command.ExecuteReader();
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            Verify(reader.GetFieldValue<OceanBaseComplexValue>(0), root, child);
            // NextResult must discard the unread second object before the following scalar response.
            Assert.True(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            DatabaseContext.EqualNumber(reader.GetValue(0), 42, "Following script result mismatch.");
            Assert.True(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            Verify(reader.GetFieldValue<OceanBaseComplexValue>(0), root, child);
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.All(reader.GetFieldValue<OceanBaseComplexValue>(0).GetValues(), Assert.Null);
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
        }
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1, "Text readers must release the connection.");
    }

    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    private static void Verify(OceanBaseComplexValue value, OceanBaseComplexType root, OceanBaseComplexType child)
    {
        Assert.Same(root, value.Type);
        var nested = Assert.IsType<OceanBaseComplexValue>(value.GetValues()[0]);
        Assert.Same(child, nested.Type);
        DatabaseContext.EqualNumber(nested.GetValues()[0], 7, "Quoted child NUMBER mismatch.");
        Assert.Equal("患者, (c) 'q'", nested.GetValues()[1]);
        DatabaseContext.EqualNumber(value.GetValues()[1], 9, "Quoted root NUMBER mismatch.");
    }
}
