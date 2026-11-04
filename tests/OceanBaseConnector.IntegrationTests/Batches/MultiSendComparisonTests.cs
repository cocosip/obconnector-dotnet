using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace OceanBaseConnector.IntegrationTests.Batches;

public sealed class MultiSendComparisonTests
{
    private const int Commands = 16;
    private const int Executions = 8;
    private const int Repetitions = 5;
    private static readonly int[] GroupSizes = { 0, 1, 4, 16 };
    private readonly ITestOutputHelper _output;
    public MultiSendComparisonTests(ITestOutputHelper output) => _output = output;

    public static TheoryData<string, bool> Profiles() => MultiSendTests.PreparedRows();

    [IntegrationTheory]
    [MemberData(nameof(Profiles))]
    public async Task PreparedDmlComparisonPreservesEveryStoredIncrement(string profileName, bool asynchronous)
    {
        var profile = IntegrationEnvironment.GetProfile(profileName);
        await using var db = new DatabaseContext(profile);
        await using var setup = await db.OpenAsync();
        var table = await db.TableAsync(setup, "id INTEGER PRIMARY KEY, n INTEGER", CancellationToken.None);
        for (var id = 0; id < Commands * GroupSizes.Length; id++)
        {
            using var insert = db.Command(setup, db.ParameterSql("INSERT INTO " + table + " VALUES(:id,0)"));
            DatabaseContext.Parameter(insert, "id", id, DbType.Int32);
            await insert.ExecuteNonQueryAsync();
        }

        var samples = new List<Sample>();
        var attemptedSamples = 0;
        var storageVerified = false;
        string? version = null;
        try
        {
            for (var repetition = 0; repetition < Repetitions; repetition++)
                for (var position = 0; position < GroupSizes.Length; position++)
                {
                    var variant = (repetition + position) % GroupSizes.Length;
                    attemptedSamples++;
                    var builder = profile.CreateBuilder();
                    builder.Pooling = false; builder.StatementCacheSize = 0;
                    builder.UseBatchMultiSend = variant != 0;
                    builder.BatchMultiSendNumber = variant == 0 ? 1 : GroupSizes[variant];
                    if (profile.Oracle) { builder.UseOraclePrepareExecute = false; builder.UseLobLocators = false; }
                    await using var connection = new OceanBaseConnection(builder.ConnectionString);
                    if (asynchronous) await connection.OpenAsync(); else connection.Open();
                    version = connection.ServerVersion;
                    await using var transaction = asynchronous ? (OceanBaseTransaction)await connection.BeginTransactionAsync() : connection.BeginTransaction();
                    await using var batch = connection.CreateBatch(); batch.Transaction = transaction;
                    for (var id = 0; id < Commands; id++)
                    {
                        var command = batch.CreateBatchCommand();
                        command.CommandText = db.ParameterSql("UPDATE " + table + " SET n=n+1 WHERE id=:id");
                        command.Parameters.Add(new OceanBaseParameter { ParameterName = "id", DbType = DbType.Int32, Value = variant * Commands + id });
                        batch.BatchCommands.Add(command);
                    }
                    if (asynchronous) await batch.PrepareAsync(); else batch.Prepare();
                    Assert.Equal(Commands, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
                    using var process = Process.GetCurrentProcess();
                    process.Refresh();
                    var workingSetBefore = process.WorkingSet64;
                    var cpuBefore = process.TotalProcessorTime;
                    var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
                    var latencies = new double[Executions];
                    for (var execution = 0; execution < Executions; execution++)
                    {
                        var started = Stopwatch.GetTimestamp();
                        var affected = asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery();
                        latencies[execution] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                        Assert.Equal(Commands, affected);
                        foreach (OceanBaseBatchCommand command in batch.BatchCommands) Assert.Equal(1, command.RecordsAffected);
                    }
                    var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
                    process.Refresh();
                    var sample = new Sample(repetition, GroupSizes[variant], latencies,
                        (process.TotalProcessorTime - cpuBefore).TotalMilliseconds, allocated, workingSetBefore, process.WorkingSet64);
                    if (asynchronous) await transaction.CommitAsync(); else transaction.Commit();
                    samples.Add(sample);
                }

            await using var observer = await db.OpenAsync();
            using var query = db.Command(observer, "SELECT id,n FROM " + table + " ORDER BY id");
            using var reader = await query.ExecuteReaderAsync();
            for (var id = 0; id < Commands * GroupSizes.Length; id++)
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(id, DatabaseContext.IntegerResult(reader.GetValue(0)));
                Assert.Equal(Repetitions * (Executions + 1), DatabaseContext.IntegerResult(reader.GetValue(1)));
            }
            Assert.False(await reader.ReadAsync());
            Assert.Equal(Repetitions * GroupSizes.Length, samples.Count);
            storageVerified = true;
        }
        finally
        {
            _output.WriteLine(JsonSerializer.Serialize(new
            {
                Profile = profileName,
                Asynchronous = asynchronous,
                ServerVersion = version,
                TlsMode = profile.CreateBuilder().TlsMode.ToString(),
                Ob20Requested = profile.CreateBuilder().UseOb20,
                Runtime = RuntimeInformation.FrameworkDescription,
                OS = RuntimeInformation.OSDescription,
                Commands,
                Executions,
                Repetitions,
                AttemptedSamples = attemptedSamples,
                CompletedSamples = samples.Count,
                FailedSamples = attemptedSamples - samples.Count,
                SampleFailureRate = attemptedSamples == 0 ? (double?)null : (attemptedSamples - samples.Count) / (double)attemptedSamples,
                StorageVerified = storageVerified,
                Summaries = GroupSizes.Select(group =>
                {
                    var runs = samples.Where(sample => sample.GroupSize == group).ToArray();
                    var latencies = runs.SelectMany(sample => sample.BatchMilliseconds).OrderBy(value => value).ToArray();
                    return new
                    {
                        GroupSize = group,
                        CompletedRepetitions = runs.Length,
                        CompletedExecutions = latencies.Length,
                        P50Milliseconds = Percentile(latencies, 0.5),
                        P95Milliseconds = Percentile(latencies, 0.95),
                        CommandsPerSecond = latencies.Length == 0 ? (double?)null : Commands * latencies.Length * 1000 / latencies.Sum(),
                    };
                }),
                Samples = samples,
            }));
        }
    }

    private static double? Percentile(double[] sorted, double fraction)
        => sorted.Length == 0 ? null : sorted[(int)Math.Ceiling(sorted.Length * fraction) - 1];

    private sealed record Sample(int Repetition, int GroupSize, double[] BatchMilliseconds, double CpuMilliseconds,
        long AllocatedBytes, long WorkingSetBefore, long WorkingSetAfter);
}
