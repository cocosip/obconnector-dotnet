using System;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Oracle;

public sealed class ComplexTypeTests
{
    public static TheoryData<string, bool> CollectionRows()
    {
        var data = new TheoryData<string, bool>();
        foreach (var profile in IntegrationEnvironment.Profiles.Where(p => p.Oracle && p.CreateObjects))
        {
            data.Add(profile.Name, false);
            data.Add(profile.Name, true);
        }
        return data;
    }

    [IntegrationTheory]
    [MemberData(nameof(CollectionRows))]
    public async Task ObjectAndCollectionRoundTrip(string profileName, bool collection)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        Assert.True(db.Oracle);
        await using var connection = await db.OpenAsync();
        var name = db.NewObject("TYPE");
        await db.ExecuteAsync(connection,
            "CREATE TYPE " + name + (collection ? " AS TABLE OF NUMBER" : " AS OBJECT (n NUMBER, s VARCHAR2(40))"),
            CancellationToken.None);
        var owner = Convert.ToString(await db.ScalarAsync(connection, "SELECT USER FROM DUAL", CancellationToken.None), CultureInfo.InvariantCulture)!;
        var type = await connection.GetComplexTypeAsync(owner, name);
        Assert.Equal(collection, type.IsCollection);
        var value = collection ? type.CreateValue(1, 2, 3) : type.CreateValue(7, "中文");
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY, value " + name, CancellationToken.None);
        await using (var insert = db.Command(connection, "INSERT INTO " + table + " VALUES (1, :value)"))
        {
            DatabaseContext.Parameter(insert, "value", value);
            await insert.ExecuteNonQueryAsync();
        }
        foreach (var prepared in new[] { false, true })
        {
            await using var select = db.Command(connection, "SELECT value FROM " + table + " WHERE id=" + (prepared ? ":id" : "1"));
            if (prepared) DatabaseContext.Parameter(select, "id", 1, DbType.Int32);
            else await select.PrepareAsync(); // Named complex results require binary protocol.
            await using var reader = await select.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.IsType<OceanBaseComplexValue>(reader.GetValue(0));
            var actual = ((OceanBaseComplexValue)reader.GetValue(0)).GetValues();
            Assert.Equal(collection ? 3 : 2, actual.Length);
            for (var i = 0; i < (collection ? 3 : 1); i++)
                DatabaseContext.EqualNumber(actual[i], collection ? i + 1 : 7, "Complex numeric member mismatch.");
            if (!collection) Assert.Equal("中文", actual[1]);
            Assert.False(await reader.ReadAsync());
        }
        connection.ClearComplexTypeCache();
    }

    public static TheoryData<string, bool> CollectionProcedureRows()
    {
        var data = new TheoryData<string, bool>();
        foreach (var profile in ProfileTheoryData.OraclePrepareExecuteObjects())
        {
            data.Add(profile, false);
            data.Add(profile, true);
        }
        return data;
    }

    [IntegrationTheory]
    [MemberData(nameof(CollectionProcedureRows))]
    public async Task CollectionProcedureParametersRoundTrip(string profileName, bool inputOutput)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var name = db.NewObject("TYPE");
        await db.ExecuteAsync(connection, "CREATE TYPE " + name + " AS TABLE OF NUMBER", CancellationToken.None);
        var procedure = db.NewObject("PROCEDURE");
        await db.ExecuteAsync(connection, "CREATE PROCEDURE " + procedure + " (p_input IN " + name +
            ", p_output " + (inputOutput ? "IN OUT " : "OUT ") + name + ") AS BEGIN " +
            (inputOutput ? "FOR i IN 1..p_input.COUNT LOOP p_output(i) := p_input(i) + p_output(i); END LOOP;" : "p_output := p_input;") +
            " END;", CancellationToken.None);
        var owner = Convert.ToString(await db.ScalarAsync(connection, "SELECT USER FROM DUAL", CancellationToken.None), CultureInfo.InvariantCulture)!;
        var type = await connection.GetComplexTypeAsync(owner, name);
        Assert.True(type.IsCollection);
        await using var command = db.Command(connection, procedure);
        command.CommandType = CommandType.StoredProcedure;
        DatabaseContext.Parameter(command, "p_input", type.CreateValue(1, 2, 3));
        var output = DatabaseContext.Parameter(command, "p_output", inputOutput ? type.CreateValue(10, 20, 30) : DBNull.Value);
        output.Direction = inputOutput ? ParameterDirection.InputOutput : ParameterDirection.Output;
        output.ComplexType = type;
        await command.ExecuteNonQueryAsync();
        var actual = Assert.IsType<OceanBaseComplexValue>(output.Value).GetValues();
        Assert.Equal(3, actual.Length);
        for (var i = 0; i < actual.Length; i++)
            DatabaseContext.EqualNumber(actual[i], (i + 1) * (inputOutput ? 11 : 1), "Collection procedure parameter mismatch.");
    }

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.OracleWithCreateObjects), MemberType = typeof(ProfileTheoryData))]
    public async Task TypeCacheClearKeepsResolutionUsable(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var name = db.NewObject("TYPE");
        await db.ExecuteAsync(connection, "CREATE TYPE " + name + " AS OBJECT (n NUMBER)", CancellationToken.None);
        var owner = Convert.ToString(await db.ScalarAsync(connection, "SELECT USER FROM DUAL", CancellationToken.None), CultureInfo.InvariantCulture)!;
        var first = connection.GetComplexType(owner, name);
        connection.ClearComplexTypeCache();
        var second = connection.GetComplexType(owner, name);
        Assert.Equal(first.TypeName, second.TypeName);
        Assert.NotNull(second.CreateValue(1));
    }
}
