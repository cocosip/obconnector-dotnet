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
