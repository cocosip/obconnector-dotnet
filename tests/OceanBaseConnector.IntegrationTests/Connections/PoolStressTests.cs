using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Connections;

public sealed class PoolStressTests
{
    public static TheoryData<string, bool> Profiles()
    {
        var rows = new TheoryData<string, bool>();
        foreach (var profile in IntegrationEnvironment.Profiles)
        {
            rows.Add(profile.Name, false);
            rows.Add(profile.Name, true);
        }
        return rows;
    }

    [IntegrationTheory]
    [MemberData(nameof(Profiles))]
    public async Task PoolBorrowStressPreservesCapacityAndReusesPhysicalSessions(string profileName, bool asynchronous)
    {
        const int workers = 16;
        const int iterations = 625;
        const int maximumPoolSize = 4;
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        var builder = db.Profile.CreateBuilder();
        builder.Pooling = true;
        builder.MinPoolSize = 0;
        builder.MaxPoolSize = maximumPoolSize;
        builder.PoolWaitTimeout = 30;
        builder.ConnectionLifetime = 0;
        builder.ConnectionIdleLifetime = 0;
        await using var source = new OceanBaseDataSourceBuilder(builder.ConnectionString).Build();
        var sessions = new ConcurrentDictionary<object, byte>();
        var completed = 0;

        async Task RunAsyncWorker()
        {
            for (var iteration = 0; iteration < iterations; iteration++)
            {
                await using var connection = (OceanBaseConnection)source.CreateConnection();
                await connection.OpenAsync();
                sessions.TryAdd(connection.GetOpenSession(), 0);
                await using var command = db.Command(connection, db.SelectOne);
                DatabaseContext.EqualNumber(await command.ExecuteScalarAsync(), 1, "Concurrent async pool query mismatch.");
                Interlocked.Increment(ref completed);
            }
        }

        void RunSyncWorker()
        {
            for (var iteration = 0; iteration < iterations; iteration++)
            {
                using var connection = (OceanBaseConnection)source.CreateConnection();
                connection.Open();
                sessions.TryAdd(connection.GetOpenSession(), 0);
                using var command = db.Command(connection, db.SelectOne);
                DatabaseContext.EqualNumber(command.ExecuteScalar(), 1, "Concurrent sync pool query mismatch.");
                Interlocked.Increment(ref completed);
            }
        }

        var tasks = new Task[workers];
        for (var worker = 0; worker < workers; worker++)
            tasks[worker] = asynchronous ? RunAsyncWorker() : Task.Run(RunSyncWorker);
        await Task.WhenAll(tasks);

        Assert.Equal(workers * iterations, completed);
        Assert.InRange(sessions.Count, 1, maximumPoolSize);
    }
}
