using System;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Oracle;

public sealed class ComplexTextResultTests
{
    public static TheoryData<string, bool, bool> Rows()
    {
        var data = new TheoryData<string, bool, bool>();
        foreach (var profile in ProfileTheoryData.OracleWithCreateObjects())
            foreach (var asynchronous in new[] { false, true })
                foreach (var sequential in new[] { false, true })
                    data.Add(profile, asynchronous, sequential);
        return data;
    }

    [IntegrationTheory]
    [MemberData(nameof(Rows))]
    public async Task NamedNestedObjectTextResultsRoundTripWithNullsAndQuotedDelimiters(string profileName, bool asynchronous, bool sequential)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var childTypeName = db.NewObject("TYPE");
        await db.ExecuteAsync(connection, "CREATE TYPE " + childTypeName + " AS OBJECT (n NUMBER, s VARCHAR2(80))", CancellationToken.None);
        var typeName = db.NewObject("TYPE");
        await db.ExecuteAsync(connection, "CREATE TYPE " + typeName + " AS OBJECT (child " + childTypeName + ", id NUMBER)", CancellationToken.None);
        var owner = Convert.ToString(await db.ScalarAsync(connection, "SELECT USER FROM DUAL", CancellationToken.None))!;
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY, value " + typeName + ", marker NUMBER", CancellationToken.None);
        // DDL invalidates the complex-type descriptor cache; load it after all setup DDL.
        var child = await connection.GetComplexTypeAsync(owner, childTypeName);
        var type = await connection.GetComplexTypeAsync(owner, typeName);
        var quoted = "a,b (c) 'q'";
        await using (var insert = db.Command(connection, "INSERT INTO " + table + " VALUES (:id, :value, :marker)"))
        {
            var id = DatabaseContext.Parameter(insert, "id", 1, DbType.Int32);
            var value = DatabaseContext.Parameter(insert, "value", type.CreateValue(child.CreateValue(7, quoted), 9));
            var marker = DatabaseContext.Parameter(insert, "marker", 42, DbType.Int32);
            if (asynchronous) await insert.ExecuteNonQueryAsync(); else insert.ExecuteNonQuery();
            id.Value = 2; value.Value = type.CreateValue(null, null); marker.Value = 43;
            if (asynchronous) await insert.ExecuteNonQueryAsync(); else insert.ExecuteNonQuery();
        }

        await using var select = db.Command(connection, "SELECT value, marker FROM " + table + " ORDER BY id");
        var behavior = sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default;
        await using var reader = asynchronous ? await select.ExecuteReaderAsync(behavior) : select.ExecuteReader(behavior);
        Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
        var first = Assert.IsType<OceanBaseComplexValue>(reader.GetValue(0));
        Assert.Same(type, first.Type);
        var firstChild = Assert.IsType<OceanBaseComplexValue>(first.GetValues()[0]);
        Assert.Same(child, firstChild.Type); DatabaseContext.EqualNumber(firstChild.GetValues()[0], 7, "Nested text NUMBER member mismatch.");
        Assert.Equal(quoted, firstChild.GetValues()[1]); DatabaseContext.EqualNumber(first.GetValues()[1], 9, "Nested text scalar mismatch.");
        DatabaseContext.EqualNumber(reader.GetValue(1), 42, "Following scalar mismatch.");
        Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
        var second = Assert.IsType<OceanBaseComplexValue>(reader.GetValue(0));
        Assert.Null(second.GetValues()[0]); Assert.Null(second.GetValues()[1]); DatabaseContext.EqualNumber(reader.GetValue(1), 43, "Following NULL scalar mismatch.");
        Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
        Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
    }
}
