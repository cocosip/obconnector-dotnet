using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Oracle;

public sealed class SdoGeometryTests
{
    public static TheoryData<string, bool> InputProfiles()
    {
        var data = new TheoryData<string, bool>();
        foreach (var profileName in ProfileTheoryData.OraclePrepareExecuteObjects())
            foreach (var asynchronous in new[] { false, true }) data.Add(profileName, asynchronous);
        return data;
    }

    [IntegrationTheory]
    [MemberData(nameof(InputProfiles))]
    public async Task SdoGeometryCanBeBoundAsAComplexInput(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var table = db.NewObject("TABLE");
        await db.ExecuteAsync(connection, "CREATE TABLE " + table + " (id NUMBER, g SDO_GEOMETRY)", CancellationToken.None);
        var type = await connection.GetComplexTypeAsync("SYS", "SDO_GEOMETRY");
        var elemInfo = type.Members[3].ComplexType!;
        var ordinates = type.Members[4].ComplexType!;
        var value = type.CreateValue(2003, null, null, elemInfo.CreateValue(1, 1003, 1), ordinates.CreateValue(0, 0, 10, 0, 10, 10, 0, 10));
        await using (var insert = db.Command(connection, "INSERT INTO " + table + " VALUES (1, :g)"))
        {
            DatabaseContext.Parameter(insert, "g", value);
            if (asynchronous) await insert.ExecuteNonQueryAsync(); else insert.ExecuteNonQuery();
        }
        await using var select = db.Command(connection, "SELECT g FROM " + table);
        if (asynchronous) await select.PrepareAsync(); else select.Prepare();
        await using var reader = asynchronous ? await select.ExecuteReaderAsync() : select.ExecuteReader();
        Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
        var actual = Assert.IsType<OceanBaseComplexValue>(reader.GetValue(0));
        DatabaseContext.EqualNumber(actual.GetValues()[0], 2003, "SDO_GTYPE mismatch after input binding.");
        var actualElemInfo = Assert.IsType<OceanBaseComplexValue>(actual.GetValues()[3]).GetValues();
        var actualOrdinates = Assert.IsType<OceanBaseComplexValue>(actual.GetValues()[4]).GetValues();
        var expectedElemInfo = new long[] { 1, 1003, 1 };
        var expectedOrdinates = new long[] { 0, 0, 10, 0, 10, 10, 0, 10 };
        Assert.Equal(expectedElemInfo.Length, actualElemInfo.Length);
        Assert.Equal(expectedOrdinates.Length, actualOrdinates.Length);
        for (var i = 0; i < expectedElemInfo.Length; i++) DatabaseContext.EqualNumber(actualElemInfo[i], expectedElemInfo[i], "SDO_ELEM_INFO mismatch after input binding.");
        for (var i = 0; i < expectedOrdinates.Length; i++) DatabaseContext.EqualNumber(actualOrdinates[i], expectedOrdinates[i], "SDO_ORDINATES mismatch after input binding.");
        Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
    }

    public static TheoryData<string, bool> Profiles()
    {
        var data = new TheoryData<string, bool>();
        foreach (var profile in IntegrationEnvironment.Profiles.Where(p => p.Oracle && p.CreateObjects && p.HasTag("direct")))
        {
            data.Add(profile.Name, false);
            data.Add(profile.Name, true);
        }
        return data;
    }

    [IntegrationTheory]
    [MemberData(nameof(Profiles))]
    public async Task PreparedSdoGeometryColumnUsesSysComplexType(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var table = db.NewObject("TABLE");
        await db.ExecuteAsync(connection, "CREATE TABLE " + table + " (id NUMBER, g SDO_GEOMETRY)", CancellationToken.None);
        await db.ExecuteAsync(connection, "INSERT INTO " + table + " VALUES (1, SDO_GEOMETRY(2001, NULL, SDO_POINT_TYPE(1, 2, NULL), NULL, NULL))", CancellationToken.None);
        await db.ExecuteAsync(connection, "INSERT INTO " + table + " VALUES (2, NULL)", CancellationToken.None);

        await using var command = db.Command(connection, "SELECT g FROM " + table + " ORDER BY id");
        if (asynchronous) await command.PrepareAsync(); else command.Prepare();
        await using var reader = asynchronous ? await command.ExecuteReaderAsync() : command.ExecuteReader();
        Assert.Equal("SDO_GEOMETRY", reader.GetDataTypeName(0));
        Assert.Equal(typeof(OceanBaseComplexValue), reader.GetFieldType(0));

        Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
        var geometry = Assert.IsType<OceanBaseComplexValue>(reader.GetValue(0));
        Assert.Equal("SYS", geometry.Type.SchemaName);
        Assert.Equal("SDO_GEOMETRY", geometry.Type.TypeName);
        var values = geometry.GetValues();
        DatabaseContext.EqualNumber(values[0], 2001, "SDO_GTYPE mismatch.");
        Assert.Null(values[1]);
        var point = Assert.IsType<OceanBaseComplexValue>(values[2]);
        var pointValues = point.GetValues();
        DatabaseContext.EqualNumber(pointValues[0], 1, "SDO_POINT.X mismatch.");
        DatabaseContext.EqualNumber(pointValues[1], 2, "SDO_POINT.Y mismatch.");
        Assert.Null(pointValues[2]);
        Assert.Null(values[3]);
        Assert.Null(values[4]);
        Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
        Assert.True(reader.IsDBNull(0));
        Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
    }
}
