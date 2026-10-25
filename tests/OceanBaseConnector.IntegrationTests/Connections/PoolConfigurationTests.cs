using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Connections;

public sealed class PoolConfigurationTests
{
    public static IEnumerable<object[]> Profiles()
    {
        foreach (var profile in IntegrationEnvironment.Profiles)
            foreach (var asynchronous in new[] { false, true }) yield return new object[] { profile.Name, asynchronous };
    }

    [IntegrationTheory]
    [MemberData(nameof(Profiles))]
    public async Task EquivalentConfigurationsReuseAndClearTheActualSession(string profileName, bool asynchronous)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        var builder = Builder(db);
        await using var first = new OceanBaseConnection(builder.ConnectionString);
        await using var second = new OceanBaseConnection(Equivalent(builder));
        using var unopened = new OceanBaseConnection(Equivalent(builder));
        try
        {
            await Open(first); var original = first.GetOpenSession();
            await Close(first); await Open(second);
            Assert.Same(original, second.GetOpenSession());
            DatabaseContext.EqualNumber(await db.ScalarAsync(second, db.SelectOne, CancellationToken.None), 1, "Reused pool session must execute SQL.");
            await Close(second);
            OceanBaseConnection.ClearPool(unopened);
            await Open(first);
            Assert.NotSame(original, first.GetOpenSession());
            DatabaseContext.EqualNumber(await db.ScalarAsync(first, db.SelectOne, CancellationToken.None), 1, "Replacement session must execute SQL.");
        }
        finally { first.Close(); second.Close(); OceanBaseConnection.ClearPool(first); OceanBaseConnection.ClearPool(second); }

        Task Open(OceanBaseConnection connection) => asynchronous ? connection.OpenAsync() : Task.Run(connection.Open);
        Task Close(OceanBaseConnection connection) => asynchronous ? connection.CloseAsync() : Task.Run(connection.Close);
    }

    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.All), MemberType = typeof(ProfileTheoryData))]
    public async Task EquivalentConfigurationWaitsForTheSameCapacityAndCanCancel(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        var builder = Builder(db);
        await using var first = new OceanBaseConnection(builder.ConnectionString);
        await using var second = new OceanBaseConnection(Equivalent(builder));
        try
        {
            await first.OpenAsync();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.OpenAsync(cancellation.Token));
            DatabaseContext.EqualNumber(await db.ScalarAsync(first, db.SelectOne, CancellationToken.None), 1, "Waiting cancellation must preserve the borrower.");
            var original = first.GetOpenSession();
            await first.CloseAsync(); await second.OpenAsync();
            Assert.Same(original, second.GetOpenSession());
            OceanBaseConnection.ClearPool(first);
            DatabaseContext.EqualNumber(await db.ScalarAsync(second, db.SelectOne, CancellationToken.None), 1, "Clear must preserve the active borrower.");
        }
        finally { first.Close(); second.Close(); OceanBaseConnection.ClearPool(first); OceanBaseConnection.ClearPool(second); }
    }

    private static OceanBaseConnectionStringBuilder Builder(DatabaseContext db)
    {
        var builder = db.Profile.CreateBuilder();
        builder.Pooling = true; builder.MaxPoolSize = 1; builder.MinPoolSize = 0;
        builder.PoolWaitTimeout = 5;
        return builder;
    }

    private static string Equivalent(OceanBaseConnectionStringBuilder builder)
    {
        var result = new DbConnectionStringBuilder();
        foreach (var key in builder.Keys.Cast<string>().Reverse()) result[key] = builder[key];
        result["Pooling"] = "TRUE";
        result["Connection Idle Lifetime"] = builder.ConnectionIdleLifetime;
        result["Connection Lifetime"] = builder.ConnectionLifetime;
        result["Load Balance"] = builder.LoadBalance.ToString().ToLowerInvariant();
        return result.ConnectionString;
    }
}
