using System;
using System.Data;
using System.Data.Common;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace OceanBaseConnector.UnitTests.TestInfrastructure
{
    internal static class PoolWaitHandoffFixture
    {
        internal static async Task Verify(Type type, CompressionServer server, bool firstAsync, bool secondAsync, bool dataSource = false)
        {
            var configuration = $"Server=127.0.0.1;Port={server.Port};User ID=u;TLS Mode=Disabled;Pooling=true;Max Pool Size=1;Pool Wait Timeout=30;Connection Timeout=30";
            using var source = dataSource ? (DbDataSource)type.Assembly.GetType("OceanBaseConnector.OceanBaseDataSourceBuilder")!.GetMethod("Build")!
                .Invoke(Activator.CreateInstance(type.Assembly.GetType("OceanBaseConnector.OceanBaseDataSourceBuilder")!, configuration), null)! : null;
            using var owner = Connection(); using var firstConnection = Connection(); using var secondConnection = Connection();
            using var firstCancel = new CancellationTokenSource(); using var secondCancel = new CancellationTokenSource();
            using var capture = new PoolWaitHandoffCapture();
            await owner.OpenAsync(); var original = Session(owner);
            var first = new PoolWaitHandoffCapture.Attempt(() => Interrupt(firstConnection, firstCancel, firstAsync));
            var second = new PoolWaitHandoffCapture.Attempt(() => Interrupt(secondConnection, secondCancel, secondAsync));
            var firstOpen = Open(firstConnection, first, firstAsync, firstCancel.Token);
            var secondOpen = Open(secondConnection, second, secondAsync, secondCancel.Token);
            try
            {
                await Task.WhenAll(first.Waiting.Task, second.Waiting.Task).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(firstOpen.IsCompleted); Assert.False(secondOpen.IsCompleted);
                await owner.CloseAsync();
                var interrupted = await capture.Interrupted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var canceled = ReferenceEquals(interrupted, first) ? firstOpen : secondOpen;
                var survivor = ReferenceEquals(interrupted, first) ? secondOpen : firstOpen;
                var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
                var canceledAsync = ReferenceEquals(interrupted, first) ? firstAsync : secondAsync;
                if (canceledAsync) Assert.Equal(ReferenceEquals(interrupted, first) ? firstCancel.Token : secondCancel.Token, error.CancellationToken);
                Assert.Equal(ConnectionState.Closed, (ReferenceEquals(interrupted, first) ? firstConnection : secondConnection).State);
                var borrowed = await survivor.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.Same(original, Session(borrowed));
                Assert.Equal(1, server.Authentications); Assert.Equal(1, server.Pings); Assert.Equal(1, server.Resets);
                using (var extra = Connection())
                using (var extraCancel = new CancellationTokenSource())
                {
                    var extraAttempt = new PoolWaitHandoffCapture.Attempt(extraCancel.Cancel);
                    var extraOpen = Open(extra, extraAttempt, true, extraCancel.Token);
                    try
                    {
                        await extraAttempt.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        Assert.False(extraOpen.IsCompleted); Assert.Equal(1, server.Authentications);
                    }
                    finally { extraCancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => extraOpen); }
                    Assert.Equal(ConnectionState.Closed, extra.State);
                }
                using (var command = borrowed.CreateCommand())
                {
                    command.CommandText = "selected"; Assert.Equal(1, await command.ExecuteNonQueryAsync());
                }
                await borrowed.CloseAsync();
                await owner.OpenAsync(); Assert.Same(original, Session(owner));
                using (var command = owner.CreateCommand())
                {
                    command.CommandText = "selected again"; Assert.Equal(1, command.ExecuteNonQuery());
                }
                Assert.Equal(1, server.Authentications); Assert.Equal(2, server.Pings); Assert.Equal(2, server.Queries);
            }
            finally
            {
                firstCancel.Cancel(); secondCancel.Cancel(); firstConnection.Close(); secondConnection.Close();
                try { await Task.WhenAll(Drain(firstOpen), Drain(secondOpen)); }
                finally
                {
                    try { owner.Close(); }
                    finally { Clear(owner); }
                }
            }

            DbConnection Connection() => source?.CreateConnection() ?? (DbConnection)Activator.CreateInstance(type, configuration)!;
            static object Session(DbConnection connection) => connection.GetType().GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection)!;
            void Clear(DbConnection connection)
            {
                if (source != null) source.GetType().GetMethod("Clear")!.Invoke(source, null);
                else type.GetMethod("ClearPool")!.Invoke(null, new object[] { connection });
            }
            Task<DbConnection> Open(DbConnection connection, PoolWaitHandoffCapture.Attempt attempt, bool asynchronous, CancellationToken token) =>
                capture.Run(attempt, async () =>
                {
                    if (asynchronous) await connection.OpenAsync(token); else connection.Open();
                    return connection;
                });
            static void Interrupt(DbConnection connection, CancellationTokenSource cancellation, bool asynchronous)
            {
                if (asynchronous) cancellation.Cancel(); else connection.Close();
            }
            static async Task Drain(Task<DbConnection> pending)
            {
                try { (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Close(); }
                catch (OperationCanceledException) { }
            }
        }
    }
}
