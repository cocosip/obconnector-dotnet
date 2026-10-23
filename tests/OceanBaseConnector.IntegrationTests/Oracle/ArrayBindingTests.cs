using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Oracle;

public sealed class ArrayBindingTests
{
    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.OraclePrepareExecuteObjects), MemberType = typeof(ProfileTheoryData))]
    public async Task ArrayInsertBelongsToExplicitTransaction(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        Assert.True(db.Oracle);
        await using var connection = await db.OpenAsync();
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY", CancellationToken.None);
        await using var transaction = (OceanBaseTransaction)await connection.BeginTransactionAsync();
        var type = connection.CreateArrayType("NUMBER", transaction);
        await using var command = db.Command(connection, "INSERT INTO " + table + " VALUES (:id)", transaction);
        DatabaseContext.Parameter(command, "id", type.CreateValue(1, 2, 3));
        var result = await command.ExecuteArrayAsync(3);
        Assert.True(result.IsComplete);
        Assert.Equal(3, result.Items.Count);
        Assert.All(result.Items, item => Assert.Equal(OceanBaseArrayBindingStatus.Succeeded, item.Status));
        Assert.Equal(3, result.RecordsAffected);
        Assert.Equal(3, await db.CountAsync(connection, table, CancellationToken.None, transaction));
        await transaction.RollbackAsync();
        Assert.Equal(0, await db.CountAsync(connection, table, CancellationToken.None));
    }

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.OraclePrepareExecuteObjects), MemberType = typeof(ProfileTheoryData))]
    public async Task RowCountBoundsAreEnforced(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        await using var transaction = (OceanBaseTransaction)await connection.BeginTransactionAsync();
        var type = connection.CreateArrayType("NUMBER", transaction);
        await using var command = db.Command(connection, "SELECT 1 FROM DUAL", transaction);
        DatabaseContext.Parameter(command, "id", type.CreateValue(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => command.ExecuteArray(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => command.ExecuteArray(65536));
        await transaction.RollbackAsync();
    }
}
