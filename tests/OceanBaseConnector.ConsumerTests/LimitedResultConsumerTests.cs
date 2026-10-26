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
        public static IEnumerable<object[]> LimitedResultAssets()
        {
            foreach (var target in new[] { "netstandard2.0", "netstandard2.1", "net8.0", "net9.0", "net10.0" })
                foreach (var asynchronous in new[] { false, true })
                    foreach (var mysql in new[] { false, true })
                    {
                        foreach (var shape in new[] { 0, 1, 2 }) yield return new object[] { target, asynchronous, mysql, 0, CommandBehavior.SingleRow, shape };
                        if (target.StartsWith("netstandard", StringComparison.Ordinal)) continue;
                        foreach (var owner in new[] { 1, 2 })
                            foreach (var behavior in new[] { CommandBehavior.SingleRow, CommandBehavior.SingleResult, CommandBehavior.SingleRow | CommandBehavior.SingleResult | CommandBehavior.CloseConnection })
                                yield return new object[] { target, asynchronous, mysql, owner, behavior, 0 };
                    }
        }

        [Theory]
        [MemberData(nameof(LimitedResultAssets))]
        public async Task AssetsLimitResultsAcrossCommandsAndBatches(string target, bool asynchronous, bool mysql, int owner, CommandBehavior behavior, int shape)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var closesConnection = owner == 2 || (behavior & CommandBehavior.CloseConnection) != 0;
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token); var stream = peer.GetStream(); var channel = new PacketChannel(stream);
                await Handshake(channel, stream, "unrequested", budget.Token, mysql: mysql);
                async Task Query(string sql)
                {
                    channel.BeginCommand(); var request = await SessionTimeZoneFixture.ReadCommand(channel, budget.Token, expectProbe: false);
                    Assert.Equal(3, request[0]); Assert.Equal(sql, Encoding.UTF8.GetString(request, 1, request.Length - 1));
                }
                async Task Ok(byte count, bool more = false)
                {
                    var report = Report(false, count); if (mysql) report[3] &= 251; if (more) report[3] |= 8;
                    await channel.WritePacketAsync(report, budget.Token);
                }
                async Task Rows(string[] values, bool more = false)
                {
                    await channel.WritePacketAsync(new byte[] { 1 }, budget.Token); await channel.WritePacketAsync(KeyConsumerColumn("value"), budget.Token);
                    await channel.WritePacketAsync(new byte[] { 254, 0, 0, 6, 0 }, budget.Token);
                    foreach (var value in values)
                    {
                        var bytes = Encoding.UTF8.GetBytes(value); var row = new byte[bytes.Length + 1]; row[0] = (byte)bytes.Length; bytes.CopyTo(row, 1);
                        await channel.WritePacketAsync(row, budget.Token);
                    }
                    await channel.WritePacketAsync(new byte[] { 254, 0, 0, more ? (byte)14 : (byte)6, 0 }, budget.Token);
                }
                await Query("prefix");
                if (shape == 2) await Ok(2, more: true);
                else { await Rows(shape == 0 ? new[] { "alpha", "bravo" } : Array.Empty<string>(), more: true); await Ok(2, more: true); }
                await Rows(new[] { "hidden" });
                if (owner != 0) { await Query("suffix"); await Rows(new[] { "also hidden" }, more: true); await Ok(3); }
                if (!closesConnection) { await Query("after limited"); await Ok(1); }
                await ObserveClose(stream, budget.Token);
            });
            var context = new AssemblyLoadContext("limited-" + target, isCollectible: true); Exception? clientError = null;
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                var factory = (DbProviderFactory)assembly.GetType("OceanBaseConnector.OceanBaseProviderFactory")!.GetField("Instance")!.GetValue(null)!;
                using var connection = factory.CreateConnection()!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled";
                await using var source = owner == 2 ? factory.CreateDataSource(connection.ConnectionString) : null;
                if (owner != 2) { if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open(); }
                using var batch = owner == 0 ? null : owner == 2 ? source!.CreateBatch() : connection.CreateBatch();
                using var command = owner == 0 ? connection.CreateCommand() : null;
                if (batch != null)
                    foreach (var sql in new[] { "prefix", "suffix" }) { var item = batch.CreateBatchCommand(); item.CommandText = sql; batch.BatchCommands.Add(item); }
                else command!.CommandText = "prefix";
                using (var reader = batch != null
                    ? asynchronous ? await batch.ExecuteReaderAsync(behavior | CommandBehavior.SequentialAccess, budget.Token) : batch.ExecuteReader(behavior | CommandBehavior.SequentialAccess)
                    : asynchronous ? await command!.ExecuteReaderAsync(behavior | CommandBehavior.SequentialAccess, budget.Token) : command!.ExecuteReader(behavior | CommandBehavior.SequentialAccess))
                {
                    Assert.Equal(shape == 0, reader.HasRows); Assert.Equal(shape == 0, asynchronous ? await reader.ReadAsync(budget.Token) : reader.Read());
                    if (shape == 0)
                    {
                        using (var field = reader.GetTextReader(0)) Assert.Equal('a', field.Read());
                        if ((behavior & CommandBehavior.SingleRow) == 0) { Assert.True(asynchronous ? await reader.ReadAsync(budget.Token) : reader.Read()); Assert.Equal("bravo", reader.GetString(0)); }
                    }
                    Assert.False(asynchronous ? await reader.ReadAsync(budget.Token) : reader.Read());
                    Assert.False(asynchronous ? await reader.NextResultAsync(budget.Token) : reader.NextResult());
                    Assert.Equal(owner == 0 ? 2 : 5, reader.RecordsAffected); Assert.Equal(0, reader.VisibleFieldCount);
                    Assert.False(asynchronous ? await reader.NextResultAsync(budget.Token) : reader.NextResult());
                    if (asynchronous) await reader.CloseAsync(); else reader.Close();
                }
                if (batch != null) { Assert.Equal(2, batch.BatchCommands[0].RecordsAffected); Assert.Equal(3, batch.BatchCommands[1].RecordsAffected); }
                if (owner != 2) Assert.Equal(closesConnection ? ConnectionState.Closed : ConnectionState.Open, connection.State);
                if (!closesConnection)
                {
                    using var after = connection.CreateCommand(); after.CommandText = "after limited"; Assert.Equal(1, asynchronous ? await after.ExecuteNonQueryAsync(budget.Token) : after.ExecuteNonQuery());
                }
                connection.Close(); await serving.WaitAsync(budget.Token);
            }
            catch (Exception error) { clientError = error; throw; }
            finally
            {
                budget.Cancel(); listener.Stop();
                try { await serving; }
                catch (OperationCanceledException) when (budget.IsCancellationRequested) { }
                catch (Exception serverError) { if (clientError != null && !ReferenceEquals(clientError, serverError)) throw new AggregateException(clientError, serverError); throw; }
                finally { context.Unload(); }
            }
        }
    }
}
