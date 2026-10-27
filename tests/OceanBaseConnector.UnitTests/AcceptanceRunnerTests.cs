using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Acceptance;
using Xunit;

namespace OceanBaseConnector.UnitTests;

public sealed class AcceptanceRunnerTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"mode\":\"Auto\",\"connectionString\":\"Server=x\"}")]
    [InlineData("{\"mode\":\"Oracle\",\"connectionString\":\"x\",\"connectionStringEnvironment\":\"X\"}")]
    [InlineData("{\"mode\":\"Oracle\",\"connectionString\":\"x\",\"largeValueBytes\":0}")]
    [InlineData("{\"mode\":\"Oracle\",\"connectionString\":\"x\",\"groups\":[\"typo\"]}")]
    [InlineData("{\"mode\":\"Oracle\",\"connectionString\":\"x\",\"typo\":true}")]
    [InlineData("{\"mode\":\"Oracle\",\"Mode\":\"MySql\",\"connectionString\":\"x\"}")]
    [InlineData("{\"mode\":\"Oracle\",\"connectionString\":\"x\",\"groups\":null}")]
    public void InvalidConfigurationCannotStart(string json)
    {
        var error = Record.Exception(() => AcceptanceConfig.Parse(json));
        Assert.True(error is ArgumentException or JsonException, "Invalid configuration must fail validation.");
    }

    [Fact]
    public void EnvironmentIsResolvedOnlyWhenRequestedAndModeCannotConflict()
    {
        var config = AcceptanceConfig.Parse("{\"mode\":\"Oracle\",\"connectionStringEnvironment\":\"TEST_DSN\"}");
        Assert.Throws<InvalidOperationException>(() => config.ResolveConnectionString(_ => null));
        Assert.Throws<ArgumentException>(() => config.ResolveConnectionString(_ => "Server=x;User ID=u;ExpectedMode=MySql"));
        var builder = new OceanBaseConnectionStringBuilder(config.ResolveConnectionString(key =>
        {
            Assert.Equal("TEST_DSN", key);
            return "Server=x;User ID=u;Password=sensitive;TLS Mode=Disabled";
        }));
        Assert.Equal(OceanBaseCompatibilityMode.Oracle, builder.ExpectedMode);
        Assert.Equal("sensitive", builder.Password);
    }

    [Fact]
    public async Task RunnerKeepsFailuresContinuesAndNeverPrintsExceptionSecrets()
    {
        var output = new StringWriter();
        var config = Config();
        var cases = new[]
        {
            Case("throws", (_, _) => throw new InvalidOperationException("Password=very-secret;SQL=private")),
            Case("asserts", (context, _) => { context.Check(false, "Value mismatch"); return Task.CompletedTask; }),
            Case("passes", (_, _) => Task.CompletedTask),
        };
        var report = await AcceptanceSuite.RunAsync(config, cases, output, CancellationToken.None);
        Assert.Equal(new[] { "failed", "failed", "passed" }, report.Results.Select(r => r.Status));
        Assert.Equal(1, report.ExitCode);
        var serialized = JsonSerializer.Serialize(report) + output;
        Assert.DoesNotContain("very-secret", serialized);
        Assert.DoesNotContain("private", serialized);
        Assert.Contains("Value mismatch", serialized);
    }

    [Fact]
    public async Task ExplicitFiltersAndPrerequisitesAreSkippedWithoutExecuting()
    {
        var config = Config();
        config.Groups = new[] { "connection" };
        var called = 0;
        Task Run(CaseContext _, CancellationToken __) { called++; return Task.CompletedTask; }
        var cases = new[]
        {
            new AcceptanceCase("selected", "connection", Run),
            new AcceptanceCase("filtered", "types", Run),
            new AcceptanceCase("mode", "connection", Run, Mode: "MySql"),
            new AcceptanceCase("ddl", "connection", Run, RequiresObjects: true),
            new AcceptanceCase("extension", "connection", Run, RequiresPrepareExecute: true),
            new AcceptanceCase("lob", "connection", Run, RequiresLob: true),
            new AcceptanceCase("external", "connection", Run, DeferredReason: "Needs fault injection"),
        };
        var report = await AcceptanceSuite.RunAsync(config, cases, TextWriter.Null, CancellationToken.None);
        Assert.Equal(1, called);
        Assert.Equal(6, report.Results.Count(r => r.Status == "skipped"));
        Assert.All(report.Results.Where(r => r.Status == "skipped"), r => Assert.False(string.IsNullOrWhiteSpace(r.Detail)));
        Assert.Equal(0, report.ExitCode);
    }

    [Fact]
    public async Task FailedConnectionGateSkipsDependentCasesAndPreservesFailure()
    {
        var called = false;
        var report = await AcceptanceSuite.RunAsync(Config(), new[]
        {
            new AcceptanceCase("open", "connection", (_, _) => throw new IOException("secret"), ConnectionGate: true),
            Case("dependent", (_, _) => { called = true; return Task.CompletedTask; }),
        }, TextWriter.Null, CancellationToken.None);
        Assert.False(called);
        Assert.Equal(new[] { "failed", "skipped" }, report.Results.Select(r => r.Status));
        Assert.Equal(1, report.ExitCode);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CancellationIsNotPassOrNormalSkip(bool wrapped, bool linked)
    {
        using var cancellation = new CancellationTokenSource();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        var report = await AcceptanceSuite.RunAsync(Config(), new[]
        {
            Case("cancel", (_, token) =>
            {
                cancellation.Cancel();
                if (wrapped) throw new OceanBaseException("hidden", isExecutionOutcomeUnknown: true,
                    innerException: new OperationCanceledException(linked ? linkedCancellation.Token : token));
                token.ThrowIfCancellationRequested(); return Task.CompletedTask;
            }),
            Case("later", (_, _) => throw new Exception("Must not execute")),
        }, TextWriter.Null, cancellation.Token);
        Assert.Equal(new[] { "cancelled", "skipped" }, report.Results.Select(r => r.Status));
        Assert.Equal(130, report.ExitCode);
    }

    [Fact]
    public async Task CleanupRunsInReverseOrderAndCannotEraseOriginalFailure()
    {
        var order = new System.Collections.Generic.List<int>();
        var report = await AcceptanceSuite.RunAsync(Config(), new[]
        {
            Case("resources", (context, _) =>
            {
                context.RegisterCleanup("TEST", "first", () => { order.Add(1); return Task.CompletedTask; });
                context.RegisterCleanup("TEST", "second", () => { order.Add(2); throw new IOException("secret cleanup SQL"); });
                context.Check(false, "Original assertion"); return Task.CompletedTask;
            }),
        }, TextWriter.Null, CancellationToken.None);
        Assert.Equal(new[] { 2, 1 }, order);
        Assert.Equal("failed", report.Results.Single().Status);
        Assert.Contains("Original assertion", report.Results.Single().Detail);
        Assert.Contains("Cleanup TEST second", report.Results.Single().Detail);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(report));
        Assert.Equal(new[] { "removed", "failed" }, report.Objects.Select(o => o.Cleanup));
        Assert.Equal(1, report.ExitCode);
    }

    [Theory]
    [InlineData("--help", 0)]
    [InlineData("--list", 0)]
    [InlineData("--unknown", 2)]
    [InlineData("--config", 2)]
    public async Task CommandLineIsUsefulWithoutAnyDatabase(string argument, int exitCode)
    {
        var output = new StringWriter();
        Assert.Equal(exitCode, await Program.RunAsync(new[] { argument }, output, CancellationToken.None));
        if (argument == "--list")
        {
            Assert.Contains("oracle.ref-cursor", output.ToString());
            Assert.Contains("matrix.network-outcome-unknown", output.ToString());
        }
    }

    [Fact]
    public async Task ListingConfiguredModeDoesNotResolveCredentials()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "{\"mode\":\"Oracle\",\"connectionStringEnvironment\":\"OBA_DELIBERATELY_UNSET_CREDENTIALS\"}");
            Assert.Equal(0, await Program.RunAsync(new[] { "--config", path, "--list" }, TextWriter.Null, CancellationToken.None));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CatalogHasUniqueNamesAndCoversEveryGroup()
    {
        var cases = DatabaseCases.All();
        Assert.True(cases.Count >= 60);
        Assert.Equal(cases.Count, cases.Select(c => c.Name).Distinct().Count());
        Assert.Equal(DatabaseCases.Groups.OrderBy(g => g), cases.Select(c => c.Group).Distinct().OrderBy(g => g));
        Assert.Equal(2, cases.Count(c => c.DeferredReason != null));
        Assert.All(cases.Where(c => c.Name.StartsWith("matrix.pool-reset", StringComparison.Ordinal)), c => Assert.Null(c.DeferredReason));
    }

    [Fact]
    public void MySqlFixtureParametersUsePositionsAndOracleRetainsNames()
    {
        var config = Config();
        var objects = new System.Collections.Generic.List<CreatedObject>();
        var oracle = new CaseContext(config, config.ResolveConnectionString(_ => null), objects);
        Assert.Equal("INSERT INTO OBA_X VALUES (:id, :value)", oracle.ParameterSql("INSERT INTO OBA_X VALUES (:id, :value)"));
        config.Mode = "MySql";
        var mysql = new CaseContext(config, "Server=unused;User ID=u;TLS Mode=Disabled", objects);
        Assert.Equal("INSERT INTO OBA_X VALUES (?, ?)", mysql.ParameterSql("INSERT INTO OBA_X VALUES (:id, :value)"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InterruptionRecognizesActualDriverUnknownOutcomeWrapper(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        if (cancel) cancellation.Cancel();
        Exception cause = cancel ? new OperationCanceledException(cancellation.Token) : new TimeoutException();
        var error = new OceanBaseException("hidden", isExecutionOutcomeUnknown: true, innerException: cause);
        Assert.True(DatabaseCases.IsExpectedInterruption(error, cancel, cancellation.Token));
        Assert.True(DatabaseCases.IsExpectedInterruption(cause, cancel, cancellation.Token));
        Assert.False(DatabaseCases.IsExpectedInterruption(new OceanBaseException("server", serverErrorCode: 123), cancel, cancellation.Token));
        Assert.False(DatabaseCases.IsExpectedInterruption(new IOException(), cancel, cancellation.Token));
    }

    [Theory]
    [InlineData("User ID=u")]
    [InlineData("Server=unused;User ID=u;Use Lob Locators=true")]
    [InlineData("Server=unused;User ID=u;Enable Full Link Trace=true")]
    public void ProductConfigurationErrorsAreRejectedBeforeAnyTest(string connectionString)
    {
        var config = Config(); config.ConnectionString = connectionString;
        Assert.ThrowsAny<Exception>(() => config.ResolveConnectionString(_ => null));
    }

    [Fact]
    public void MissingScalarIsNeverEquivalentToZero()
    {
        Assert.Throws<AssertionFailure>(() => CaseContext.IntegerResult(null));
        Assert.Throws<AssertionFailure>(() => CaseContext.IntegerResult(DBNull.Value));
        Assert.Equal(0, CaseContext.IntegerResult(0));
        Assert.Equal(3, CaseContext.IntegerResult(new OceanBaseDecimal(3m)));
        Assert.Throws<AssertionFailure>(() => CaseContext.IntegerResult(new OceanBaseDecimal(1.5m)));
        Assert.Throws<AssertionFailure>(() => CaseContext.IntegerResult(1.5m));
        var config = Config(); var context = new CaseContext(config, config.ConnectionString!, new());
        Assert.Throws<AssertionFailure>(() => context.EqualNumber(null, 0, "Missing integer"));
    }

    [Fact]
    public async Task XaCleanupAttemptsEndCloseAndOwnRollbackDespiteEarlierErrors()
    {
        var order = new System.Collections.Generic.List<string>();
        var error = await Assert.ThrowsAsync<AggregateException>(() => DatabaseCases.CleanupXaAsync(
            () => { order.Add("end"); throw new IOException("secret old connection"); },
            () => { order.Add("close"); return Task.CompletedTask; },
            () => { order.Add("rollback"); throw new InvalidOperationException("secret rollback"); }));
        Assert.Equal(new[] { "end", "close", "rollback" }, order);
        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.Equal("IOException; InvalidOperationException", AcceptanceSuite.Describe(error));
    }

    private static AcceptanceConfig Config() => AcceptanceConfig.Parse("{\"mode\":\"Oracle\",\"connectionString\":\"Server=unused;User ID=u;TLS Mode=Disabled\"}");
    private static AcceptanceCase Case(string name, Func<CaseContext, CancellationToken, Task> run) => new(name, "connection", run);
}
