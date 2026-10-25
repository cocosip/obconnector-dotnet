using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Loader;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    public sealed partial class Ob20ConsumerTests
    {
        public static IEnumerable<object[]> PooledReaderCloseAssets()
        {
            foreach (var target in new[] { "netstandard2.0", "netstandard2.1", "net8.0", "net9.0", "net10.0" })
                foreach (var mysql in new[] { false, true })
                    foreach (var asynchronous in new[] { false, true })
                        yield return new object[] { target, mysql, asynchronous };
        }

        [Theory]
        [MemberData(nameof(PooledReaderCloseAssets))]
        public async Task EveryAssetKeepsReopenedPooledLeaseWhenOldReaderCloses(string target, bool mysql, bool asynchronous)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var serving = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync(stop.Token);
                var stream = client.GetStream(); var channel = new PacketChannel(stream);
                await Handshake(channel, stream, "unrequested", stop.Token, mysql: mysql);
                var sql = await channel.ReadPacketAsync(stop.Token);
                Assert.Equal(3, sql[0]); Assert.Equal("old reader", Encoding.UTF8.GetString(sql, 1, sql.Length - 1));
                await stream.WriteAsync(TextResult("1"), stop.Token);
                channel.BeginCommand();
                Assert.Equal(new byte[] { 31 }, await channel.ReadPacketAsync(stop.Token));
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 2, 0, 0, 0 }, stop.Token);
                if (!mysql) await OceanBaseConnector.UnitTests.TestInfrastructure.ServerIdentity.ReplyOracleAutocommitAsync(channel, stop.Token);
                channel.BeginCommand();
                Assert.Equal(new byte[] { 14 }, await channel.ReadPacketAsync(stop.Token));
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 2, 0, 0, 0 }, stop.Token);
                channel.BeginCommand();
                var next = mysql ? await channel.ReadPacketAsync(stop.Token) : await SessionTimeZoneFixture.ReadCommand(channel, stop.Token);
                Assert.Equal(3, next[0]); Assert.Equal("new lease", Encoding.UTF8.GetString(next, 1, next.Length - 1));
                await channel.WritePacketAsync(new byte[] { 0, 1, 0, 2, 0, 0, 0 }, stop.Token);
                await ObserveClose(stream, stop.Token);
            });
            var context = new AssemblyLoadContext("pooled-reader-close-" + target, isCollectible: true);
            Exception? failure = null;
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                var type = assembly.GetType("OceanBaseConnector.OceanBaseConnection")!;
                using var connection = (DbConnection)Activator.CreateInstance(type)!;
                connection.ConnectionString = new DbConnectionStringBuilder
                {
                    ["Server"] = "127.0.0.1",
                    ["Port"] = ((IPEndPoint)listener.LocalEndpoint).Port,
                    ["User ID"] = "u",
                    ["TLS Mode"] = "Disabled",
                    ["Pooling"] = true,
                    ["Max Pool Size"] = 1,
                    ["Pool Wait Timeout"] = 1,
                }.ConnectionString;
                if (asynchronous) await connection.OpenAsync(stop.Token); else connection.Open();
                using var command = connection.CreateCommand(); command.CommandText = "old reader";
                using var reader = asynchronous ? await command.ExecuteReaderAsync(CommandBehavior.CloseConnection, stop.Token) : command.ExecuteReader(CommandBehavior.CloseConnection);
                Assert.True(asynchronous ? await reader.ReadAsync(stop.Token) : reader.Read());
                Assert.False(asynchronous ? await reader.ReadAsync(stop.Token) : reader.Read());
                Assert.False(asynchronous ? await reader.NextResultAsync(stop.Token) : reader.NextResult());
                if (asynchronous) await connection.CloseAsync(); else connection.Close();
                if (asynchronous) await connection.OpenAsync(stop.Token); else connection.Open();
                if (asynchronous) await reader.DisposeAsync(); else reader.Dispose();
                Assert.Equal(ConnectionState.Open, connection.State);
                using var next = connection.CreateCommand(); next.CommandText = "new lease";
                Assert.Equal(1, asynchronous ? await next.ExecuteNonQueryAsync(stop.Token) : next.ExecuteNonQuery());
                type.GetMethod("ClearPool")!.Invoke(null, new object[] { connection });
                if (asynchronous) await connection.CloseAsync(); else connection.Close();
                await serving.WaitAsync(stop.Token);
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                stop.Cancel(); listener.Stop(); context.Unload();
                try { await serving; }
                catch (OperationCanceledException) when (failure != null && stop.IsCancellationRequested) { }
                catch (SocketException) when (failure != null && stop.IsCancellationRequested) { }
                catch (ObjectDisposedException) when (failure != null && stop.IsCancellationRequested) { }
                catch (Exception error) when (failure != null) { throw new AggregateException(failure, error); }
            }
        }
    }
}
