using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Protocol;

public sealed partial class MultiStatementTests
{
    [IntegrationTheory]
    [MemberData(nameof(OracleRows))]
    public async Task OracleNamedIntervalScriptInputsPreserveIndependentStorage(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = CreateConnection(db, enabled: true); await Open(connection, asynchronous);
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY, ym INTERVAL YEAR(9) TO MONTH, ds INTERVAL DAY(9) TO SECOND(9)", CancellationToken.None);
        var name = db.NewObject("TYPE");
        await db.ExecuteAsync(connection, "CREATE TYPE " + name + " AS OBJECT (ym INTERVAL YEAR(9) TO MONTH, ds INTERVAL DAY(9) TO SECOND(9))", CancellationToken.None);
        var procedure = db.NewObject("PROCEDURE");
        await db.ExecuteAsync(connection, "CREATE PROCEDURE " + procedure + "(p IN " + name + ", k IN NUMBER) IS BEGIN INSERT INTO " + table + " VALUES(k,p.ym,p.ds); END;", CancellationToken.None);
        var owner = Convert.ToString(await db.ScalarAsync(connection, "SELECT USER FROM DUAL", CancellationToken.None), CultureInfo.InvariantCulture)!;
        var type = await connection.GetComplexTypeAsync(owner, name);
        var ym = new OceanBaseIntervalYearToMonth(true, 12, 3, 2);
        var ds = new OceanBaseIntervalDayToSecond(false, 12, 3, 4, 5, 123456789, 2, 9);
        await using var command = db.Command(connection, "CALL " + procedure + "(:p,1); CALL " + procedure + "(:p,2)");
        var parameter = DatabaseContext.Parameter(command, "p", type.CreateValue(ym, ds));
        if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
        command.CommandText = "CALL " + procedure + "(:p,3); CALL " + procedure + "(:p,4)";
        parameter.Value = type.CreateValue(null, null);
        if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
        await using var observer = await db.OpenAsync();
        await using var query = db.Command(observer, "SELECT id,ym,ds FROM " + table + " ORDER BY id"); await query.PrepareAsync();
        await using var reader = await query.ExecuteReaderAsync();
        for (var id = 1; id <= 4; id++)
        {
            Assert.True(await reader.ReadAsync()); Assert.Equal(id, DatabaseContext.IntegerResult(reader.GetValue(0)));
            if (id <= 2)
            {
                Assert.Equal(new OceanBaseIntervalYearToMonth(true, 12, 3), reader.GetValue(1));
                Assert.Equal(new OceanBaseIntervalDayToSecond(false, 12, 3, 4, 5, 123456789), reader.GetValue(2));
            }
            else { Assert.True(reader.IsDBNull(1)); Assert.True(reader.IsDBNull(2)); }
        }
        Assert.False(await reader.ReadAsync());
    }
}
