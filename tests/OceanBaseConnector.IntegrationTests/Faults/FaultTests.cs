using System;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Infrastructure;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Faults;

public sealed class FaultTests
{
    [IntegrationTheory]
    [MemberData(nameof(ProfileTheoryData.All), MemberType = typeof(ProfileTheoryData))]
    public async Task KnownServerErrorLeavesConnectionReusable(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        var missing = "OBI_" + Guid.NewGuid().ToString("N").Substring(0, 20).ToUpperInvariant();
        var rejected = false;
        try { await db.ScalarAsync(connection, "SELECT id FROM " + missing, CancellationToken.None); }
        catch (OceanBaseException error)
        {
            rejected = error.ServerErrorCode != 0 && !error.IsExecutionOutcomeUnknown;
        }
        Assert.True(rejected, "A missing test object must return a known server error.");
        DatabaseContext.EqualNumber(await db.ScalarAsync(connection, db.SelectOne, CancellationToken.None), 1,
            "Connection must remain reusable after a known server error.");
    }

    [IntegrationTheory("slowQuery")]
    [MemberData(nameof(ProfileTheoryData.WithSlowQuery), MemberType = typeof(ProfileTheoryData))]
    public async Task CancellationInterruptsSlowQuery(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        await using var command = db.Command(connection, db.Profile.SlowQuery!);
        command.CommandTimeout = 10;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var interrupted = false;
        try { await command.ExecuteNonQueryAsync(cancellation.Token); }
        catch (Exception error) when (IsExpectedInterruption(error, cancel: true, cancellation.Token))
        {
            interrupted = true;
        }
        Assert.True(interrupted, "The configured slow query must be interrupted by caller cancellation.");
    }

    [IntegrationTheory("slowQuery")]
    [MemberData(nameof(ProfileTheoryData.WithSlowQuery), MemberType = typeof(ProfileTheoryData))]
    public async Task TimeoutInterruptsSlowQuery(string profileName)
    {
        await using var db = new DatabaseContext(IntegrationEnvironment.GetProfile(profileName));
        await using var connection = await db.OpenAsync();
        await using var command = db.Command(connection, db.Profile.SlowQuery!);
        command.CommandTimeout = 1;
        var interrupted = false;
        try { await command.ExecuteNonQueryAsync(CancellationToken.None); }
        catch (Exception error) when (IsExpectedInterruption(error, cancel: false, CancellationToken.None))
        {
            interrupted = true;
        }
        Assert.True(interrupted, "The configured slow query must be interrupted by CommandTimeout.");
    }

    private static bool IsExpectedInterruption(Exception error, bool cancel, CancellationToken caller)
    {
        var cause = error is OceanBaseException database && database.IsExecutionOutcomeUnknown
            ? database.InnerException : error;
        return cancel
            ? caller.IsCancellationRequested && cause is OperationCanceledException canceled && canceled.CancellationToken == caller
            : cause is TimeoutException;
    }
}
