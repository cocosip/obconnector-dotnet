using System;
using System.Data;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace OceanBaseConnector.IntegrationTests.Oracle;

public sealed class ComplexIntervalTests
{
    private readonly ITestOutputHelper _output;
    public ComplexIntervalTests(ITestOutputHelper output) => _output = output;
    public static TheoryData<string, bool, bool> Cases()
    {
        var data = new TheoryData<string, bool, bool>();
        foreach (var profile in ProfileTheoryData.OraclePrepareExecuteObjects())
            foreach (var variant in new[] { false, true })
                foreach (var asynchronous in new[] { false, true }) data.Add(profile, variant, asynchronous);
        return data;
    }

    [IntegrationTheory]
    [MemberData(nameof(Cases))]
    public async Task NamedScalarIntervalCollectionsPreserveNullAndDeclaredPrecision(string profileName, bool daySecond, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var declaration = daySecond ? "INTERVAL DAY(2) TO SECOND(3)" : "INTERVAL YEAR(2) TO MONTH";
        var table = await db.TableAsync(connection, "v " + declaration, CancellationToken.None);
        var name = db.NewObject("TYPE");
        await db.ExecuteAsync(connection, "CREATE TYPE " + name + " AS TABLE OF " + declaration, CancellationToken.None);
        var procedure = db.NewObject("PROCEDURE");
        var literal = daySecond ? "INTERVAL '98 23:59:58.987' DAY(2) TO SECOND(3)" : "INTERVAL '98-11' YEAR(2) TO MONTH";
        await db.ExecuteAsync(connection, "CREATE PROCEDURE " + procedure + "(p IN OUT " + name + ", q OUT " + name + ") IS BEGIN " +
            "IF p.COUNT <> 3 OR p(2) IS NOT NULL THEN RAISE_APPLICATION_ERROR(-20001, 'Invalid interval collection'); END IF; " +
            "INSERT INTO " + table + " VALUES (p(1)); q := p; p(1) := " + literal + "; END;", CancellationToken.None);
        var owner = Convert.ToString(await db.ScalarAsync(connection, "SELECT USER FROM DUAL", CancellationToken.None), CultureInfo.InvariantCulture)!;
        var type = asynchronous ? await connection.GetComplexTypeAsync(owner, name) : connection.GetComplexType(owner, name);
        Assert.True(type.IsCollection); Assert.Equal(declaration, type.Members[0].TypeName);
        object input = daySecond ? (object)new OceanBaseIntervalDayToSecond(true, 12, 3, 4, 5, 123000000, 2, 3)
            : new OceanBaseIntervalYearToMonth(true, 12, 3, 2);
        object changed = daySecond ? (object)new OceanBaseIntervalDayToSecond(false, 98, 23, 59, 58, 987000000, 2, 3)
            : new OceanBaseIntervalYearToMonth(false, 98, 11, 2);
        await using var command = db.Command(connection, procedure); command.CommandType = CommandType.StoredProcedure;
        var p = new OceanBaseParameter { ParameterName = "p", Direction = ParameterDirection.InputOutput, Value = type.CreateValue(input, null, input) };
        var q = new OceanBaseParameter { ParameterName = "q", Direction = ParameterDirection.Output, ComplexType = type, Value = "old" };
        command.Parameters.Add(p); command.Parameters.Add(q);
        if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
        Assert.Equal(new object?[] { input, null, input }, Assert.IsType<OceanBaseComplexValue>(q.Value).GetValues());
        Assert.Equal(new object?[] { changed, null, input }, Assert.IsType<OceanBaseComplexValue>(p.Value).GetValues());
        await using var observer = await db.OpenAsync();
        await using var query = db.Command(observer, "SELECT v FROM " + table); await query.PrepareAsync();
        await using (var reader = await query.ExecuteReaderAsync())
        { Assert.True(await reader.ReadAsync()); Assert.Equal(input, reader.GetValue(0)); Assert.False(await reader.ReadAsync()); }
        command.CommandType = CommandType.Text; command.Parameters.Clear(); command.Parameters.Add(q);
        command.CommandText = "BEGIN :q := " + name + "(); END;";
        if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
        Assert.Empty(Assert.IsType<OceanBaseComplexValue>(q.Value).GetValues());
        command.CommandText = "DECLARE v " + name + "; BEGIN :q := v; END;";
        if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
        Assert.Equal(DBNull.Value, q.Value);
    }

    [IntegrationTheory]
    [MemberData(nameof(Cases))]
    public async Task NamedIntervalMembersPreserveInputsAndPlOutputs(string profileName, bool collection, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var table = await db.TableAsync(connection, "ym INTERVAL YEAR(9) TO MONTH, ds INTERVAL DAY(9) TO SECOND(9)", CancellationToken.None);
        var child = db.NewObject("TYPE");
        await db.ExecuteAsync(connection, "CREATE TYPE " + child + " AS OBJECT (ym INTERVAL YEAR(9) TO MONTH, ds INTERVAL DAY(9) TO SECOND(9))", CancellationToken.None);
        var root = collection ? db.NewObject("TYPE") : child;
        if (collection) await db.ExecuteAsync(connection, "CREATE TYPE " + root + " AS TABLE OF " + child, CancellationToken.None);
        var procedure = db.NewObject("PROCEDURE");
        var member = collection ? "p(1)" : "p";
        await db.ExecuteAsync(connection, "CREATE PROCEDURE " + procedure + "(p IN OUT " + root + ", q OUT " + root + ") IS BEGIN " +
            (collection ? "IF p.COUNT <> 3 OR p(2) IS NOT NULL THEN RAISE_APPLICATION_ERROR(-20001, 'Invalid interval collection'); END IF; " : "") +
            "INSERT INTO " + table + " VALUES (" + member + ".ym, " + member + ".ds); q := p; " +
            member + ".ym := INTERVAL '123456789-11' YEAR(9) TO MONTH; " +
            member + ".ds := INTERVAL '-123456789 23:59:58.123456789' DAY(9) TO SECOND(9); END;", CancellationToken.None);
        var owner = Convert.ToString(await db.ScalarAsync(connection, "SELECT USER FROM DUAL", CancellationToken.None), CultureInfo.InvariantCulture)!;
        var type = asynchronous ? await connection.GetComplexTypeAsync(owner, root) : connection.GetComplexType(owner, root);
        var element = collection ? type.Members[0].ComplexType! : type;
        Assert.Equal("INTERVAL YEAR(9) TO MONTH", element.Members[0].TypeName);
        Assert.Equal("INTERVAL DAY(9) TO SECOND(9)", element.Members[1].TypeName);
        var ym = new OceanBaseIntervalYearToMonth(true, 123456789, 11);
        var ds = new OceanBaseIntervalDayToSecond(false, 123456789, 23, 59, 58, 123456789);
        var item = element.CreateValue(ym, ds);
        var input = collection ? type.CreateValue(item, null, item) : item;
        await using var command = db.Command(connection, procedure);
        command.CommandType = CommandType.StoredProcedure;
        var p = new OceanBaseParameter { ParameterName = "p", Direction = ParameterDirection.InputOutput, Value = input };
        var q = new OceanBaseParameter { ParameterName = "q", Direction = ParameterDirection.Output, ComplexType = type, Value = "old" };
        command.Parameters.Add(p); command.Parameters.Add(q);
        try { if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery(); }
        catch (OceanBaseException error)
        {
            _output.WriteLine("Interval procedure error: " + error.ServerErrorCode + "/" + error.SqlState);
            throw;
        }
        var original = Values(q.Value); var changed = Values(p.Value);
        Assert.Equal(ym, original[0]); Assert.Equal(ds, original[1]);
        Assert.Equal(new OceanBaseIntervalYearToMonth(false, ym.Years, ym.Months), changed[0]);
        Assert.Equal(new OceanBaseIntervalDayToSecond(true, ds.Days, ds.Hours, ds.Minutes, ds.Seconds, ds.Nanosecond), changed[1]);
        await using var observer = await db.OpenAsync();
        await using var query = db.Command(observer, "SELECT ym, ds FROM " + table); await query.PrepareAsync();
        await using var reader = await query.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync()); Assert.Equal(ym, reader.GetValue(0)); Assert.Equal(ds, reader.GetValue(1));
        Assert.False(await reader.ReadAsync());

        object?[] Values(object? value)
        {
            var complex = Assert.IsType<OceanBaseComplexValue>(value); Assert.Same(type, complex.Type);
            var values = complex.GetValues();
            if (!collection) return values;
            Assert.Equal(3, values.Length); Assert.Null(values[1]);
            Assert.Equal(item.GetValues(), Assert.IsType<OceanBaseComplexValue>(values[2]).GetValues());
            return Assert.IsType<OceanBaseComplexValue>(values[0]).GetValues();
        }
    }
}
