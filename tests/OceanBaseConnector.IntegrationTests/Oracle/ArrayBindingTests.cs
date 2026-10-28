using System;
using System.Collections.Generic;
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
    public Task ArrayInsertBelongsToExplicitTransaction(string profileName)
        => ArrayInsertCore(profileName, asynchronous: true, prepared: false);

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.OraclePrepareExecuteObjects), MemberType = typeof(ProfileTheoryData))]
    public Task PreparedArrayInsertSync(string profileName) => ArrayInsertCore(profileName, asynchronous: false, prepared: true);

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.OraclePrepareExecuteObjects), MemberType = typeof(ProfileTheoryData))]
    public Task PreparedArrayInsertAsync(string profileName) => ArrayInsertCore(profileName, asynchronous: true, prepared: true);

    public static IEnumerable<object[]> FetchHintProfiles()
    {
        foreach (var profile in ProfileTheoryData.OraclePrepareExecuteObjects())
            foreach (var asynchronous in new[] { false, true })
                foreach (var prepared in new[] { false, true })
                    foreach (var fetchSize in new[] { 1, int.MaxValue })
                        yield return new object[] { profile, asynchronous, prepared, fetchSize };
    }

    [IntegrationTheory]
    [MemberData(nameof(FetchHintProfiles))]
    public Task ArrayFetchHintPreservesRowsAndRollback(string profileName, bool asynchronous, bool prepared, int fetchSize)
        => ArrayInsertCore(profileName, asynchronous, prepared, fetchSize);

    private static async Task ArrayInsertCore(string profileName, bool asynchronous, bool prepared, int fetchSize = 0)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        Assert.True(db.Oracle);
        await using var connection = await db.OpenAsync();
        var table = await db.TableAsync(connection, "id INTEGER PRIMARY KEY", CancellationToken.None);
        await using var transaction = (OceanBaseTransaction)await connection.BeginTransactionAsync();
        var type = connection.CreateArrayType("NUMBER", transaction);
        await using var command = db.Command(connection, "INSERT INTO " + table + " VALUES (:id)", transaction);
        command.FetchSize = fetchSize;
        DatabaseContext.Parameter(command, "id", type.CreateValue(1, 2, 3));
        if (prepared)
        {
            if (asynchronous) await command.PrepareAsync(); else command.Prepare();
            Assert.Equal(0, await db.CountAsync(connection, table, CancellationToken.None, transaction));
        }
        var result = asynchronous ? await command.ExecuteArrayAsync(3) : command.ExecuteArray(3);
        Assert.True(result.IsComplete);
        Assert.Equal(3, result.Items.Count);
        Assert.All(result.Items, item => Assert.Equal(OceanBaseArrayBindingStatus.Succeeded, item.Status));
        Assert.Equal(3, result.RecordsAffected);
        Assert.Equal(fetchSize, command.FetchSize);
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
