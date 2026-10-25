using System;
using System.Data.Common;
using System.Linq;
using System.Net.Security;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace OceanBaseConnector.UnitTests.TestInfrastructure
{
    internal static class PoolConfigurationFixture
    {
        internal static string Equivalent(string configuration)
        {
            var original = new DbConnectionStringBuilder { ConnectionString = configuration };
            var result = new DbConnectionStringBuilder();
            foreach (var key in original.Keys.Cast<string>().Reverse()) result[key] = original[key];
            result["Pooling"] = "TRUE";
            result["Command Timeout"] = "003";
            result["Connection Idle Lifetime"] = "000";
            result["Load Balance"] = "sequential";
            return result.ConnectionString;
        }

        internal static async Task Verify(Type type, MutualTlsServer server, X509Certificate2 certificate, bool asynchronous, int scenario)
        {
            RemoteCertificateValidationCallback validation = (_, _, _, _) => true;
            var configuration = new DbConnectionStringBuilder
            {
                ["Server"] = "127.0.0.1",
                ["Port"] = server.Port,
                ["User ID"] = "u",
                ["Pooling"] = true,
                ["Max Pool Size"] = 1,
                ["Connection Timeout"] = 8,
                ["Pool Wait Timeout"] = 8,
                ["Command Timeout"] = 3,
                ["TLS Server Name"] = "ob-loopback",
            }.ConnectionString;
            using var first = Connection(scenario == 3 ? configuration + ";Password2=\"\"" : configuration);
            using var second = Connection(Equivalent(configuration));
            using var unopened = Connection(Equivalent(configuration) + (scenario == 3 ? ";Password2=\"\"" : ""));
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                await Open(first);
                var original = Session(first);
                if (scenario == 0)
                {
                    await Close(first);
                    await Open(second);
                    Assert.Same(original, Session(second));
                    Assert.Equal(1, server.Accepted);
                    await Query(second);
                    await Close(second);
                    Clear(unopened);
                    await Open(first);
                    Assert.NotSame(original, Session(first));
                }
                else if (scenario == 1)
                {
                    var opening = Open(second);
                    await WaitForWaiter(first);
                    Assert.False(opening.IsCompleted);
                    Assert.Equal(1, server.Accepted);
                    // Closing a waiting logical connection cancels both sync and async Open.
                    second.Close();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
                    await Query(first);
                    await Close(first);
                    await Open(second);
                    Assert.Same(original, Session(second));
                    Clear(unopened);
                    await Query(second);
                    await Close(second);
                    await Open(first);
                    Assert.NotSame(original, Session(first));
                }
                else if (scenario == 3)
                {
                    await Close(first);
                    await Open(second);
                    Assert.NotSame(original, Session(second));
                    await Close(second);
                    Clear(second);
                    await Open(first);
                    Assert.Same(original, Session(first));
                    await Close(first);
                    Clear(unopened);
                    await Open(first);
                    Assert.NotSame(original, Session(first));
                }
                else
                {
                    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    server.BeforeCommandReply = async command =>
                    {
                        if (command == 0x1f) { entered.TrySetResult(); await release.Task; }
                    };
                    var closing = Close(first);
                    try
                    {
                        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        Clear(unopened);
                    }
                    finally { release.TrySetResult(); }
                    await closing.WaitAsync(TimeSpan.FromSeconds(5));
                    await Open(second);
                    Assert.NotSame(original, Session(second));
                }
                Assert.Equal(scenario == 3 ? 3 : 2, server.Accepted);
            }
            finally
            {
                release.TrySetResult();
                first.Close(); second.Close();
                Clear(first); Clear(second);
            }

            DbConnection Connection(string text)
            {
                var connection = (DbConnection)Activator.CreateInstance(type, text)!;
                type.GetProperty("ClientCertificate")!.SetValue(connection, certificate);
                type.GetProperty("RemoteCertificateValidationCallback")!.SetValue(connection, validation);
                return connection;
            }
            Task Open(DbConnection connection) => asynchronous ? connection.OpenAsync() : Task.Run(connection.Open);
            Task Close(DbConnection connection) => asynchronous ? connection.CloseAsync() : Task.Run(connection.Close);
            void Clear(DbConnection connection) => type.GetMethod("ClearPool")!.Invoke(null, new object[] { connection });
            object Session(DbConnection connection) => type.GetField("_session", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(connection)!;
        }

        private static async Task Query(DbConnection connection)
        {
            using var command = connection.CreateCommand(); command.CommandText = "selected";
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        private static async Task WaitForWaiter(DbConnection connection)
        {
            var pool = connection.GetType().GetField("_pool", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection)!;
            var field = pool.GetType().GetField("_waiters", BindingFlags.NonPublic | BindingFlags.Instance)!;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while ((int)field.GetValue(pool)! == 0) await Task.Delay(10, deadline.Token);
        }
    }
}
