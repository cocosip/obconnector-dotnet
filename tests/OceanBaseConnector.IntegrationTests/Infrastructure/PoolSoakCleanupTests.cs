using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using OceanBaseConnector.IntegrationTests.Connections;
using Xunit;

namespace OceanBaseConnector.IntegrationTests.Infrastructure;

[Trait("Category", "Configuration")]
public sealed class PoolSoakCleanupTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PreservesRunAndCleanupFailuresWhileAttemptingEveryDrop(bool runFails, bool cleanupFails)
    {
        var db = new DatabaseContext(new IntegrationProfile());
        var primary = new TimeoutException("Original worker or setup failure.");
        var cleanup = new InvalidOperationException("Drop failure.");
        var attempts = new List<int>();
        db.RegisterCleanup("TABLE", "first", () => { attempts.Add(1); return Task.CompletedTask; });
        db.RegisterCleanup("TABLE", "second", () =>
        {
            attempts.Add(2);
            return cleanupFails ? Task.FromException(cleanup) : Task.CompletedTask;
        });

        var error = await Record.ExceptionAsync(() => PoolSoakTests.RunWithDatabaseCleanup(db,
            () => runFails ? Task.FromException(primary) : Task.CompletedTask));

        Assert.Equal(new[] { 2, 1 }, attempts);
        if (runFails && cleanupFails)
        {
            var errors = Assert.IsType<AggregateException>(error).Flatten().InnerExceptions;
            Assert.Contains(primary, errors);
            Assert.Contains(errors, item => ReferenceEquals(item.InnerException, cleanup));
        }
        else if (runFails) Assert.Same(primary, error);
        else if (cleanupFails)
            Assert.Same(cleanup, Assert.Single(Assert.IsType<AggregateException>(error).InnerExceptions).InnerException);
        else Assert.Null(error);
    }
}
