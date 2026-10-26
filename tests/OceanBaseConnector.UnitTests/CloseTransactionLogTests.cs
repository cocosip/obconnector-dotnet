using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        public static IEnumerable<object[]> CloseTransactionLogCases()
        {
            foreach (var oracle in new[] { false, true })
                foreach (var asynchronous in new[] { false, true })
                    foreach (var otherBorrower in new[] { false, true })
                        foreach (var openAsync in new[] { false, true })
                            foreach (var fault in new[] { 0, 1, 2 })
                                foreach (var wait in new[] { 0, 1 })
                                    yield return new object[] { oracle, asynchronous, otherBorrower, openAsync, fault, wait };
        }

        [Theory]
        [MemberData(nameof(CloseTransactionLogCases))]
        public async Task CloseTransactionLoggerReopensOnlyAfterCapturedPoolCleanup(bool oracle, bool asynchronous, bool otherBorrower, bool openAsync, int fault, int wait)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var commands = new List<string>(); var resets = 0; var injected = 0;
            var serving = Task.Run(async () =>
            {
                var peers = new List<Task>();
                for (var i = 0; i < (fault == 0 ? 1 : 2); i++)
                {
                    var peer = await listener.AcceptTcpClientAsync(stop.Token);
                    peers.Add(CallbackPoolSession(peer, oracle, stop.Token, commands, () => Interlocked.Increment(ref resets), response: sql =>
                        (fault == 1 && sql == "ROLLBACK" || fault == 2 && sql == "set autocommit = 1") && Interlocked.Exchange(ref injected, 1) == 0 ? Error() : null));
                }
                await Task.WhenAll(peers);
            });
            var config = Configuration(((IPEndPoint)listener.LocalEndpoint).Port) + ";Pooling=true;Max Pool Size=1;Pool Wait Timeout=" + wait;
            var logs = new RecordingLoggerFactory();
            using var connection = new OceanBaseConnection(config, logs);
            using var other = new OceanBaseConnection(config);
            var target = otherBorrower ? other : connection;
            var attempts = 0; Exception? logError = null; var order = new List<int>(); Exception? failure = null;
            try
            {
                await connection.OpenAsync(stop.Token); var original = connection.GetOpenSession();
                using var tx = connection.BeginTransaction(IsolationLevel.ReadCommitted);
                logs.OnLog = id =>
                {
                    order.Add(id.Id);
                    if (id.Id != 1200 && id.Id != 1201 || attempts++ != 0) return;
                    logError = Record.Exception(() =>
                    {
                        if (openAsync) target.OpenAsync(stop.Token).GetAwaiter().GetResult(); else target.Open();
                        using var inside = target.CreateCommand(); inside.CommandText = "inside transaction logger";
                        Assert.Equal(1, inside.ExecuteNonQuery());
                    });
                };
                await Task.Run(async () => { if (asynchronous) await connection.CloseAsync(); else connection.Close(); }).WaitAsync(stop.Token);
                Assert.Null(logError); Assert.Null(tx.Connection); Assert.Equal(ConnectionState.Open, target.State);
                if (fault == 0) Assert.Same(original, target.GetOpenSession()); else Assert.NotSame(original, target.GetOpenSession());
                Assert.Equal(fault == 0 ? 1 : 0, resets);
                Assert.Equal(fault == 2 ? 2 : 1, attempts);
                Assert.Equal(fault == 0 ? new[] { 1200, 1003 } : fault == 1 ? new[] { 1201, 1003 } : new[] { 1201, 1200, 1003 },
                    order.Where(id => id == 1200 || id == 1201 || id == 1003));
                using var after = target.CreateCommand(); after.CommandText = "after transaction logger";
                Assert.Equal(1, after.ExecuteNonQuery());
                Assert.Equal(fault == 1 ? new[] { "SET SESSION TRANSACTION ISOLATION LEVEL READ COMMITTED", "set autocommit = 0", "ROLLBACK", "inside transaction logger", "after transaction logger" }
                    : new[] { "SET SESSION TRANSACTION ISOLATION LEVEL READ COMMITTED", "set autocommit = 0", "ROLLBACK", "set autocommit = 1", "inside transaction logger", "after transaction logger" }, commands);
                if (fault != 0)
                    Assert.Contains(logs.Entries, entry => entry.EventId.Id == 1201 && entry.Message.Contains("ErrorCode=1064", StringComparison.Ordinal));
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                logs.OnLog = null; OceanBaseConnection.ClearPool(target); target.Close(); connection.Close();
                stop.Cancel(); listener.Stop();
                try { await serving; }
                catch (OperationCanceledException) when (failure != null) { }
            }
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task ThrowingCloseTransactionLoggerCannotDetachItsReopenedTransaction(bool oracle, bool asynchronous)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var commands = new List<string>();
            var server = CallbackPoolServer(oracle, stop.Token, commands);
            var logs = new RecordingLoggerFactory();
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Pooling=true;Max Pool Size=1;Pool Wait Timeout=1", logs);
            DbTransaction? next = null;
            try
            {
                await connection.OpenAsync(stop.Token); var original = connection.GetOpenSession();
                using var old = connection.BeginTransaction(IsolationLevel.ReadCommitted);
                logs.OnLog = id =>
                {
                    if (id.Name != "TransactionRollbackCompleted") return;
                    connection.Open(); next = connection.BeginTransaction(IsolationLevel.Serializable);
                    throw new InvalidOperationException("application logger failure");
                };
                if (asynchronous) await connection.CloseAsync(); else connection.Close();
                Assert.NotNull(next); Assert.Null(old.Connection); Assert.Same(connection, next.Connection);
                Assert.Same(original, connection.GetOpenSession());
                Assert.Contains(logs.Entries, entry => entry.EventId.Id == 1003);
                using var command = connection.CreateCommand(); command.Transaction = next; command.CommandText = "after throwing logger";
                Assert.Equal(1, command.ExecuteNonQuery());
                Assert.Equal(new[] { "SET SESSION TRANSACTION ISOLATION LEVEL READ COMMITTED", "set autocommit = 0", "ROLLBACK", "set autocommit = 1",
                    "SET SESSION TRANSACTION ISOLATION LEVEL SERIALIZABLE", "set autocommit = 0", "after throwing logger" }, commands);
            }
            finally
            {
                logs.OnLog = null; OceanBaseConnection.ClearPool(connection); next?.Dispose(); connection.Close();
                stop.Cancel(); server.Stop(); await server.Completion;
            }
        }
    }
}
