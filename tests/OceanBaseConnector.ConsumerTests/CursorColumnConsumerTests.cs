using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    public sealed class CursorColumnConsumerTests
    {
        public static IEnumerable<object[]> Assets()
        {
            foreach (var target in new[] { "netstandard2.0", "netstandard2.1", "net8.0", "net9.0", "net10.0" })
                foreach (var asynchronous in new[] { false, true })
                    foreach (var sequential in new[] { false, true })
                        foreach (var deprecated in new[] { false, true })
                            foreach (var publish in new[] { false, true })
                                foreach (var parentFetchSize in new[] { 0, 2 })
                                    yield return new object[] { target, asynchronous, sequential, deprecated, publish, parentFetchSize };
        }

        [Theory]
        [MemberData(nameof(Assets))]
        public async Task EveryAssetOwnsAccessedCursorColumnsAndReleasesSkippedSiblings(string target, bool asynchronous, bool sequential, bool deprecated, bool publish, int parentFetchSize)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            var greeting = Convert.FromBase64String(fixture.RootElement.GetProperty("payloadBase64").GetString()!);
            if (deprecated) greeting[Array.IndexOf(greeting, (byte)0, 1) + 20] |= 1;
            byte[] Column(byte type) => new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 99, 1, 99, 12, 45, 0, 255, 0, 0, 0, type, 0, 0, 0, 0, 0 };
            byte[] End(byte flags) => deprecated ? new byte[] { 254, 0, 0, flags, 0, 0, 0 } : new byte[] { 254, 0, 0, flags, 0 };
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token); var channel = new PacketChannel(peer.GetStream());
                async Task<byte[]> Request(byte code)
                {
                    while (true)
                    {
                        channel.BeginCommand(); var bytes = await channel.ReadPacketAsync(budget.Token);
                        var sql = bytes[0] == 3 ? Encoding.UTF8.GetString(bytes, 1, bytes.Length - 1) : "";
                        var values = sql == "SELECT SESSIONTIMEZONE FROM DUAL" ? new[] { "+00:00" }
                            : sql == "SELECT @@NCHARACTER_SET_CONNECTION, @@CHARACTER_SET_RESULTS, @@CHARACTER_SET_CONNECTION FROM DUAL"
                                ? new[] { "utf8mb4", "utf8mb4", "utf8mb4" } : null;
                        if (values == null) { Assert.Equal(code, bytes[0]); return bytes; }
                        await channel.WritePacketAsync(new byte[] { (byte)values.Length }, budget.Token);
                        foreach (var value in values) await channel.WritePacketAsync(Column(253), budget.Token);
                        if (!deprecated) await channel.WritePacketAsync(End(2), budget.Token);
                        using var row = new MemoryStream();
                        foreach (var value in values) { var text = Encoding.UTF8.GetBytes(value); row.WriteByte((byte)text.Length); row.Write(text); }
                        await channel.WritePacketAsync(row.ToArray(), budget.Token); await channel.WritePacketAsync(End(2), budget.Token);
                    }
                }
                async Task Close(byte id) => Assert.Equal(new byte[] { 25, id, 0, 0, 0 }, await Request(25));
                await channel.WritePacketAsync(greeting, budget.Token); await channel.ReadPacketAsync(budget.Token);
                await channel.WritePacketAsync(SessionTimeZoneFixture.Report, budget.Token);
                await ServerIdentity.ReplyAsync(channel, deprecated: deprecated, token: budget.Token);
                await Request(22);
                await channel.WritePacketAsync(new byte[] { 0, 80, 0, 0, 0, 3, 0, 0, 0, 0, 0, 0 }, budget.Token);
                foreach (var type in new byte[] { 163, 163, 8 }) await channel.WritePacketAsync(Column(type), budget.Token);
                if (!deprecated) await channel.WritePacketAsync(End(2), budget.Token);
                Assert.Equal(new byte[] { 23, 80, 0, 0, 0, 0, 1, 0, 0, 0 }, await Request(23));
                await channel.WritePacketAsync(new byte[] { 3 }, budget.Token);
                foreach (var type in new byte[] { 163, 163, 8 }) await channel.WritePacketAsync(Column(type), budget.Token);
                if (!deprecated) await channel.WritePacketAsync(End(2), budget.Token);
                var row = new byte[18]; row[2] = 91; row[6] = 92; BitConverter.GetBytes(7L).CopyTo(row, 10);
                await channel.WritePacketAsync(row, budget.Token); await channel.WritePacketAsync(End(2), budget.Token);
                if (publish)
                {
                    await Close(92);
                    Assert.Equal(new byte[] { 28, 91, 0, 0, 0, 2, 0, 0, 0 }, await Request(28));
                    await channel.WritePacketAsync(new byte[] { 1 }, budget.Token); await channel.WritePacketAsync(Column(8), budget.Token);
                    if (!deprecated) await channel.WritePacketAsync(End(2), budget.Token);
                    var child = new byte[10]; BitConverter.GetBytes(9L).CopyTo(child, 2);
                    await channel.WritePacketAsync(child, budget.Token); await channel.WritePacketAsync(End(130), budget.Token);
                    await Close(91); await Close(80);
                }
                else { await Close(91); await Close(92); await Close(80); }
                await Request(3); await channel.WritePacketAsync(new byte[] { 0, 1, 0, 2, 0, 0, 0 }, budget.Token);
            }, budget.Token);
            var context = new AssemblyLoadContext("cursor-column-" + Guid.NewGuid(), isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled";
                if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open();
                using var command = connection.CreateCommand(); command.CommandText = "SELECT cursor_columns";
                command.GetType().GetProperty("FetchSize")!.SetValue(command, parentFetchSize);
                if (parentFetchSize == 0) { if (asynchronous) await command.PrepareAsync(budget.Token); else command.Prepare(); }
                var behavior = sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default;
                using var reader = asynchronous ? await command.ExecuteReaderAsync(behavior, budget.Token) : command.ExecuteReader(behavior);
                Assert.Equal("OceanBaseConnector.OceanBaseRefCursor", reader.GetFieldType(0).FullName);
                Assert.True(asynchronous ? await reader.ReadAsync(budget.Token) : reader.Read());
                object? cursor = null;
                if (publish)
                {
                    cursor = asynchronous ? await reader.GetFieldValueAsync<object>(0, budget.Token) : reader.GetValue(0);
                    Assert.Same(cursor, reader.GetProviderSpecificValue(0));
                }
                else Assert.False(reader.IsDBNull(0));
                Assert.Equal(7L, reader.GetInt64(2));
                Assert.False(asynchronous ? await reader.ReadAsync(budget.Token) : reader.Read()); reader.Close(); command.Dispose();
                if (cursor != null)
                {
                    using var lifetime = (IDisposable)cursor;
                    DbDataReader child;
                    if (asynchronous)
                    {
                        var task = (Task)cursor.GetType().GetMethod("GetDataReaderAsync")!.Invoke(cursor, new object[] { 2, behavior, budget.Token })!;
                        await task; child = (DbDataReader)task.GetType().GetProperty("Result")!.GetValue(task)!;
                    }
                    else child = (DbDataReader)cursor.GetType().GetMethod("GetDataReader")!.Invoke(cursor, new object[] { 2, behavior })!;
                    using (child)
                    {
                        Assert.True(asynchronous ? await child.ReadAsync(budget.Token) : child.Read()); Assert.Equal(9L, child.GetInt64(0));
                        Assert.False(asynchronous ? await child.ReadAsync(budget.Token) : child.Read());
                    }
                    lifetime.Dispose();
                }
                using var after = connection.CreateCommand(); after.CommandText = "after";
                Assert.Equal(1, asynchronous ? await after.ExecuteNonQueryAsync(budget.Token) : after.ExecuteNonQuery());
                await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }
    }
}
