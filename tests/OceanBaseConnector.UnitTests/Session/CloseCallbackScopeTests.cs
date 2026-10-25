using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal;
using OceanBaseConnector.Internal.Session;
using Xunit;

namespace OceanBaseConnector.UnitTests.Session
{
    public sealed class CloseCallbackScopeTests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task CompletedScopesReleaseCapturedResourcesEvenWhenTheirExecutionContextLives(bool asynchronous, bool fail)
        {
            var (scope, resource) = RetainingScope(fail);
            ExecutionContext? capturedContext;
            using (scope.Enter()) capturedContext = ExecutionContext.Capture();
            if (fail)
            {
                if (asynchronous) await Assert.ThrowsAsync<InvalidOperationException>(() => scope.CompleteAsync());
                else Assert.Throws<InvalidOperationException>(scope.Complete);
            }
            else if (asynchronous) await scope.CompleteAsync();
            else scope.Complete();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Assert.False(resource.IsAlive, "A completed event context must not retain the old cleanup's resource graph.");
            GC.KeepAlive(scope); GC.KeepAlive(capturedContext);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static (CloseCallbackScope Scope, WeakReference Resource) RetainingScope(bool fail)
        {
            var resource = new object();
            Action cleanup = () => { GC.KeepAlive(resource); if (fail) throw new InvalidOperationException("cleanup failed"); };
            return (new CloseCallbackScope(cleanup, async () => { await Task.Yield(); cleanup(); }), new WeakReference(resource));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CompetingCompletionsWaitForTheSingleCleanup(bool asynchronous)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var count = 0;
            var scope = new CloseCallbackScope(() => { Interlocked.Increment(ref count); entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); },
                async () => { Interlocked.Increment(ref count); entered.TrySetResult(); await release.Task; });
            var first = Task.Run(async () => { if (asynchronous) await scope.CompleteAsync(); else scope.Complete(); });
            try
            {
                await entered.Task.WaitAsync(stop.Token);
                var second = scope.CompleteAsync(); Assert.False(second.IsCompleted);
                release.TrySetResult(); await Task.WhenAll(first, second).WaitAsync(stop.Token);
                scope.Complete(); Assert.Equal(1, count);
            }
            finally { release.TrySetResult(); await first; }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CleanupFailuresAreSharedWithoutRepeatedResourceDisposal(bool asynchronous)
        {
            var expected = new InvalidOperationException("cleanup failed"); var count = 0;
            var scope = new CloseCallbackScope(() => { count++; throw expected; }, () => { count++; return Task.FromException(expected); });
            var first = asynchronous ? await Assert.ThrowsAsync<InvalidOperationException>(() => scope.CompleteAsync()) : Assert.Throws<InvalidOperationException>(scope.Complete);
            Assert.Same(expected, first); Assert.Same(expected, Assert.Throws<InvalidOperationException>(scope.Complete));
            Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(() => scope.CompleteAsync())); Assert.Equal(1, count);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CleanupMasksTheWholeAmbientChainAndRestoresIt(bool asynchronous)
        {
            var outerCount = 0; var innerCount = 0;
            var outer = new CloseCallbackScope(() => { outerCount++; CloseCallbackScope.CompletePending(); },
                async () => { outerCount++; await CloseCallbackScope.CompletePendingAsync(); });
            using (outer.Enter())
            {
                var inner = new CloseCallbackScope(() => { innerCount++; CloseCallbackScope.CompletePending(); },
                    async () => { innerCount++; await CloseCallbackScope.CompletePendingAsync(); });
                using (inner.Enter())
                {
                    if (asynchronous) await CloseCallbackScope.CompletePendingAsync(); else CloseCallbackScope.CompletePending();
                    CloseCallbackScope.CompletePending();
                }
                CloseCallbackScope.CompletePending();
            }
            Assert.Equal(1, outerCount); Assert.Equal(1, innerCount);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task DelayedExecutionContextCannotRepeatAnOldCleanup(bool asynchronous)
        {
            var count = 0; var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var scope = new CloseCallbackScope(() => Interlocked.Increment(ref count), () => { Interlocked.Increment(ref count); return Task.CompletedTask; });
            Task delayed;
            using (scope.Enter()) delayed = Task.Run(async () => { await release.Task; if (asynchronous) await CloseCallbackScope.CompletePendingAsync(); else CloseCallbackScope.CompletePending(); });
            scope.Complete(); release.TrySetResult(); await delayed.WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(1, count);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task NewBorrowCancellationDoesNotAbandonTheCapturedCleanup(bool asynchronous)
        {
            using var caller = new CancellationTokenSource(); using var pool = new PhysicalSessionPool();
            var count = 0;
            var scope = new CloseCallbackScope(() => { count++; caller.Cancel(); }, () => { count++; caller.Cancel(); return Task.CompletedTask; });
            var options = new OceanBaseConnectionStringBuilder { Server = "unused.invalid", UserId = "u", TlsMode = OceanBaseTlsMode.Disabled }.Build();
            using (scope.Enter())
            {
                var error = asynchronous
                    ? await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pool.RentAsync(options, null, new EndpointSelector(), caller.Token))
                    : Assert.ThrowsAny<OperationCanceledException>(() => pool.Rent(options, null, new EndpointSelector(), out _, caller.Token));
                Assert.Equal(caller.Token, error.CancellationToken);
            }
            scope.Complete(); Assert.Equal(1, count); Assert.True(pool.IsEmpty);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task FailedCallbackCleanupReleasesThePoolRentReservation(bool asynchronous, bool reserved)
        {
            using var pool = new PhysicalSessionPool();
            var expected = new InvalidOperationException("captured cleanup failed");
            var scope = new CloseCallbackScope(() => throw expected, () => Task.FromException(expected));
            var options = new OceanBaseConnectionStringBuilder { Server = "unused.invalid", UserId = "u", TlsMode = OceanBaseTlsMode.Disabled }.Build();
            if (reserved) pool.BeginRent();
            using (scope.Enter())
            {
                var error = asynchronous
                    ? await Assert.ThrowsAsync<InvalidOperationException>(() => pool.RentAsync(options, null, new EndpointSelector(), reserved: reserved))
                    : Assert.Throws<InvalidOperationException>(() => pool.Rent(options, null, new EndpointSelector(), out _, reserved: reserved));
                Assert.Same(expected, error);
            }
            Assert.True(pool.IsEmpty);
        }
    }
}
