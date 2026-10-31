using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Oracle;

public sealed class ComplexReturningTests
{
    public static TheoryData<string, bool> Cases()
    {
        var cases = new TheoryData<string, bool>();
        foreach (var profile in ProfileTheoryData.OraclePrepareExecuteObjects())
            foreach (var asynchronous in new[] { false, true }) cases.Add(profile, asynchronous);
        return cases;
    }

    [IntegrationTheory]
    [MemberData(nameof(Cases))]
    public async Task NamedObjectReturningUsesDeclaredOutputType(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var typeName = db.NewObject("TYPE");
        await db.ExecuteAsync(connection, "CREATE TYPE " + typeName + " AS OBJECT (v NUMBER, tag NUMBER)", CancellationToken.None);
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY, value " + typeName, CancellationToken.None);
        var owner = Convert.ToString(await db.ScalarAsync(connection, "SELECT USER FROM DUAL", CancellationToken.None), CultureInfo.InvariantCulture)!;
        var type = await connection.GetComplexTypeAsync(owner, typeName);
        var input = type.CreateValue(7, 9);

        await using var command = db.Command(connection, "INSERT INTO " + table + " VALUES (1, :v) RETURNING value INTO :o");
        DatabaseContext.Parameter(command, "v", input);
        var output = DatabaseContext.Parameter(command, "o", DBNull.Value);
        output.Direction = ParameterDirection.Output;
        output.ComplexType = type;
        if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();

        var result = Assert.IsType<OceanBaseComplexValue>(output.Value);
        Assert.Same(type, result.Type);
        var values = result.GetValues();
        DatabaseContext.EqualNumber(values[0], 7, "Returned object member mismatch.");
        DatabaseContext.EqualNumber(values[1], 9, "Returned object tag mismatch.");
    }
}
