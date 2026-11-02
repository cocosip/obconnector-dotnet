using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using OceanBaseConnector.Internal.Session;
using Xunit;
using Xunit.Abstractions;

namespace OceanBaseConnector.IntegrationTests.Connections;

public sealed partial class PoolSoakTests(ITestOutputHelper output)
{
    [PoolSoakTheory]
    [MemberData(nameof(ProfileTheoryData.All), MemberType = typeof(ProfileTheoryData))]
    public async Task SustainedMixedResourcesReturnToZero(string profileName)
    {
        var duration = PoolSoakTheoryAttribute.ParseDuration(Environment.GetEnvironmentVariable(PoolSoakTheoryAttribute.DurationVariable))
            ?? throw new InvalidOperationException("Pool soak requires an explicit duration.");
        var profile = IntegrationEnvironment.GetProfile(profileName);
        Assert.True(profile.CreateObjects, "Pool soak requires object creation for independent storage and XA reconciliation.");
        Assert.NotNull(profile.SlowQuery);
        var builder = profile.CreateBuilder();
        Assert.True(!profile.Oracle || builder.UseOraclePrepareExecute && builder.UseLobLocators,
            "Oracle pool soak requires PrepareExecute and LOB locators; do not silently omit resource scenarios.");
        builder.Pooling = false;
        await using var observer = new OceanBaseConnection(builder.ConnectionString);
        await observer.OpenAsync();
        await using var recoveryConnection = new OceanBaseConnection(builder.ConnectionString);
        await recoveryConnection.OpenAsync();
        var db = new DatabaseContext(profile);
        await RunWithDatabaseCleanup(db, RunSoak);

        async Task RunSoak()
        {
            var table = await SoakTable(db, observer, "id INTEGER PRIMARY KEY");
            var lobTable = await SeedLob(db, observer);
            builder.Pooling = true; builder.MinPoolSize = 0; builder.MaxPoolSize = 4;
            builder.PoolWaitTimeout = 10; builder.ConnectionTimeout = 10; builder.CommandTimeout = 10;
            builder.ConnectionLifetime = 0; builder.ConnectionIdleLifetime = 0; builder.StatementCacheSize = 8;
            using var metrics = new PoolSoakMetrics();
            await using var source = new OceanBaseDataSourceBuilder(builder.ConnectionString).Build();
            using var deadline = new CancellationTokenSource(duration + TimeSpan.FromMinutes(2));
            var branches = new ConcurrentDictionary<OceanBaseXid, byte>();
            var globalId = Guid.NewGuid().ToByteArray();
            var sessions = new ConcurrentDictionary<PhysicalSession, byte>();
            var completed = new long[16];
            var scenarios = new long[8];
            var stopwatch = Stopwatch.StartNew();
            var reportPath = Path.Combine(AppContext.BaseDirectory, "TestResults", $"pool-soak-{profile.Mode}-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.csv");
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            using var report = new StreamWriter(reportPath) { AutoFlush = true };
            report.WriteLine("phase,seconds,completed,sessions,leases,waiters,borrows,handles,threads,private_bytes,managed_bytes");
            output.WriteLine("Profile={0}; server={1}; runtime={2}; OS={3}; duration={4}; workers=16; maxPoolSize=4; cache=8; report={5}",
                profileName, observer.ServerVersion, Environment.Version, Environment.OSVersion, duration, reportPath);
            output.WriteLine("XA run prefix={0}; owned tables={1},{2}", Convert.ToHexString(globalId), table, lobTable);
            Sample("start");
            await VerifyWaitCancellation(source, deadline.Token);
            var workers = Enumerable.Range(0, 16).Select(worker => Task.Run(async () =>
            {
                try
                {
                    for (var round = 0; stopwatch.Elapsed < duration || round < (duration.TotalSeconds >= 3600 ? 632 : 8); round++)
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        var asynchronous = (worker + round + round / 8) % 2 == 0;
                        await using var connection = (OceanBaseConnection)source.CreateConnection();
                        var scenario = round % scenarios.Length;
                        var phase = "open";
                        try
                        {
                            if (asynchronous) await connection.OpenAsync(deadline.Token); else connection.Open();
                            sessions.TryAdd(connection.GetOpenSession(), 0);
                            phase = "exercise";
                            await Exercise(db, connection, table, lobTable, worker, scenario, round / 8, asynchronous, branches, globalId, deadline.Token);
                            Interlocked.Increment(ref scenarios[scenario]);
                            Interlocked.Increment(ref completed[worker]);
                            phase = "pace";
                            // This is a correctness soak; cap connection churn so TIME_WAIT does not exhaust the host's ports.
                            await Task.Delay(TimeSpan.FromMilliseconds(250), deadline.Token);
                        }
                        catch (Exception error)
                        {
                            var server = error as OceanBaseException;
                            throw new InvalidOperationException($"Worker={worker}; round={round}; scenario={scenario}; phase={phase}; elapsedSeconds={stopwatch.Elapsed.TotalSeconds:F3}; async={asynchronous}; state={connection.State}; pool={metrics.Snapshot()}; serverCode={server?.ServerErrorCode}; xaCode={server?.XaErrorCode}.", error);
                        }
                    }
                }
                catch { deadline.Cancel(); throw; }
            })).ToArray();
            var all = Task.WhenAll(workers);
            var failures = new List<Exception>();
            try
            {
                var nextClear = TimeSpan.Zero;
                while (!all.IsCompleted)
                {
                    if (stopwatch.Elapsed >= nextClear)
                    {
                        source.Clear();
                        nextClear = stopwatch.Elapsed + TimeSpan.FromSeconds(2);
                    }
                    await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(1)));
                    if ((int)stopwatch.Elapsed.TotalSeconds % 10 == 0) Sample("running");
                }
                await all;
                Assert.All(scenarios, count => Assert.True(count >= 16, "Every worker must exercise every scenario."));
                if (duration.TotalSeconds >= 3600) Assert.True(completed.Sum() >= 10000);
            }
            catch (Exception error) { failures.Add(error); }
            finally
            {
                deadline.Cancel();
                // Always join workers before disposing their source or reconciling branches.
                try { await all; }
                catch (Exception error)
                {
                    if (all.Exception is { } workerErrors)
                        failures.AddRange(workerErrors.InnerExceptions.Where(item => !failures.Contains(item)));
                    else if (!failures.Contains(error)) failures.Add(error);
                }
                await Cleanup(async () =>
                {
                    await source.DisposeAsync();
                    Sample("disposed");
                    Assert.True(source.Pool.IsEmpty, "All pool sessions, waiters and renters must be released.");
                    var final = metrics.Snapshot();
                    Assert.Equal((0L, 0L, 0L), (final.Sessions, final.Leases, final.Waiters));
                    Assert.True(final.Borrows > 0, "The listener must observe actual pool activity.");
                    Assert.All(sessions.Keys, session => Assert.Equal(SessionState.Closed, session.State));
                    sessions.Clear();
                });
                await Cleanup(async () =>
                {
                    var recovery = recoveryConnection.GetXaResource();
                    foreach (var xid in branches.Keys)
                    {
                        await Cleanup(async () =>
                        {
                            try { await recovery.RollbackAsync(xid); }
                            catch (OceanBaseException error) when (error.XaErrorCode == -4 || error.ServerErrorCode == 1397) { }
                        });
                    }
                    var recovered = await recovery.RecoverAsync();
                    Assert.DoesNotContain(recovered, xid => xid.FormatId == 0x4F4253 && xid.GetGlobalTransactionId().Take(globalId.Length).SequenceEqual(globalId));
                });
                await Cleanup(async () =>
                {
                    Assert.Equal(0, await db.CountAsync(observer, table, CancellationToken.None));
                    await VerifyLob(db, observer, lobTable);
                });
            }
            if (failures.Count != 0) throw new AggregateException("Pool soak or independent cleanup failed.", failures);
            Sample("verified");
            output.WriteLine("Completed={0}; scenarios={1}; report={2}", completed.Sum(), string.Join(",", scenarios), reportPath);

            void Sample(string phase)
            {
                foreach (var session in sessions.Keys)
                    if (session.State == SessionState.Closed) sessions.TryRemove(session, out _);
                using var process = Process.GetCurrentProcess();
                process.Refresh();
                var snapshot = metrics.Snapshot();
                report.WriteLine(string.Join(",", phase, stopwatch.Elapsed.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture),
                    completed.Sum(), snapshot.Sessions, snapshot.Leases, snapshot.Waiters, snapshot.Borrows,
                    process.HandleCount, process.Threads.Count, process.PrivateMemorySize64, GC.GetTotalMemory(false)));
            }

            async Task Cleanup(Func<Task> action)
            {
                try { await action(); }
                catch (Exception error) { failures.Add(error); }
            }
        }
    }

    internal static async Task RunWithDatabaseCleanup(DatabaseContext db, Func<Task> run)
    {
        Exception? failure = null;
        try { await run(); }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            try { await db.DisposeAsync(); }
            catch (Exception cleanup) when (failure != null)
            {
                throw new AggregateException("Pool soak and database cleanup failed.", failure, cleanup);
            }
        }
    }
}
