using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Loader;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.Transport;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    public sealed partial class Ob20ConsumerTests
    {
        public static IEnumerable<object[]> LobAssets()
        {
            foreach (var args in OracleAssets())
                foreach (var character in new[] { false, true }) yield return args.Concat(new object[] { character }).ToArray();
        }

        [Theory]
        [MemberData(nameof(LobAssets))]
        public Task EveryAssetReadsBoundLobChunksAndValidatesOb20Completion(string target, bool async, string kind, bool character)
            => EveryAssetReadsBoundLobChunksAndValidatesOb20CompletionCore(target, async, kind, character, encrypted: false);

        [Theory]
        [MemberData(nameof(LobAssets))]
        public Task EveryAssetReadsBoundLobChunksAndValidatesOb20CompletionOverTls(string target, bool async, string kind, bool character)
            => EveryAssetReadsBoundLobChunksAndValidatesOb20CompletionCore(target, async, kind, character, encrypted: true);

        private static async Task EveryAssetReadsBoundLobChunksAndValidatesOb20CompletionCore(string target, bool async, string kind, bool character, bool encrypted)
        {
            using var certificate = encrypted ? TestCertificateFactory.CreateSelfSigned() : null;
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var ob20 = kind != "standard"; var data = Encoding.UTF8.GetBytes("患者😀a"); var type = character ? (byte)211 : (byte)210;
            var locator = new byte[92]; new byte[] { 1, 132, 1, 0, 2, 1, 0, 0, 1, 129, 0, 0 }.CopyTo(locator, 0);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(locator.AsSpan(28), data.Length);
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token);
                using var tls = encrypted ? new SslStream(peer.GetStream()) : null;
                Stream stream = tls is null ? peer.GetStream() : tls;
                var channel = new PacketChannel(peer.GetStream());
                await Handshake(channel, stream, ob20 ? "enabled" : "unrequested", budget.Token, deprecated: true, version: "4.3.5.0", lobLocators: true, tls: tls, certificate: certificate);
                byte[] Column(byte code, bool binary = false) => new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 112, 1, 112, 12, binary ? (byte)63 : (byte)45, 0, 20, 0, 0, 0, code, 0, 0, 0, 0, 0 };
                byte[] Bytes(byte[] value) => new[] { (byte)value.Length }.Concat(value).ToArray();
                async Task Reply(uint id, byte[] bytes)
                {
                    await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, id + 1, 1, bytes) : bytes, budget.Token);
                    if (!ob20) channel.BeginCommand();
                }
                var eof = Report(false, 0); eof[0] = 254;
                var query = await ReadRequest(stream, channel, ob20, 1, budget.Token);
                Assert.Equal("select lob_value", Encoding.UTF8.GetString(query, 1, query.Length - 1));
                await Reply(0, Frame(1, new byte[] { 1 }).Concat(Frame(2, Column(type))).Concat(Frame(3, Bytes(locator))).Concat(Frame(4, eof)).ToArray());
                var request = new ByteCursor(await ReadRequest(stream, channel, ob20, 2, budget.Token));
                Assert.Equal(0xa1, request.ReadUInt8()); request.Skip(9);
                Assert.Equal("BEGIN DBMS_LOB.READ(?, ?, ?, ?); END;", Encoding.UTF8.GetString(request.ReadLengthEncodedBytes()));
                Assert.Equal(4u, request.ReadUInt32()); Assert.Equal(8, request.ReadUInt8()); Assert.Equal(1, request.ReadUInt8());
                Assert.Equal((ushort)type, request.ReadUInt16()); Assert.Equal((ushort)3, request.ReadUInt16()); Assert.Equal((ushort)8, request.ReadUInt16()); Assert.Equal((ushort)6, request.ReadUInt16());
                Assert.Equal(locator, request.ReadLengthEncodedBytes()); Assert.Equal(character ? 8191u : (uint)data.Length, request.ReadUInt32()); Assert.Equal(1ul, request.ReadUInt64());
                var response = Frame(1, new byte[] { 0, 91, 0, 0, 0, 2, 0, 4, 0, 0, 0, 0, 4, 0, 0, 0, 1 });
                for (var i = 0; i < 4; i++) response = response.Concat(Frame((byte)(2 + i), Column(253))).ToArray();
                response = response.Concat(Frame(6, Column(8))).Concat(Frame(7, Column(character ? (byte)253 : (byte)203, !character)))
                    .Concat(Frame(8, new byte[] { 0, 0 }.Concat(BitConverter.GetBytes(character ? 4L : data.Length)).Concat(Bytes(data)).ToArray()))
                    .Concat(Frame(9, eof)).Concat(Frame(10, Report(false, 0))).ToArray();
                if (kind == "trailing") response = response.Concat(Frame(11, new byte[] { 0 })).ToArray();
                await Reply(1, response);
                if (kind != "trailing") Assert.Equal(new byte[] { 25, 91, 0, 0, 0 }, await ReadRequest(stream, channel, ob20, 3, budget.Token));
                await ObserveClose(stream, budget.Token);
            });
            var context = new AssemblyLoadContext("lob-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode={(encrypted ? "Required" : "Disabled")};Use Oracle Prepare Execute=true;Use Lob Locators=true;Use OB20={ob20}";
                if (async) await connection.OpenAsync(budget.Token); else connection.Open();
                using var command = connection.CreateCommand(); command.CommandText = "select lob_value";
                using var reader = async ? await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, budget.Token) : command.ExecuteReader();
                Assert.Equal("OceanBaseConnector.OceanBaseLob", reader.GetFieldType(0).FullName); Assert.Equal(character ? "CLOB" : "BLOB", reader.GetDataTypeName(0));
                var column = Assert.Single(reader.GetColumnSchema()); Assert.True(column.IsLong); Assert.Null(column.ColumnSize);
                Assert.True(async ? await reader.ReadAsync(budget.Token) : reader.Read()); using var lob = Assert.IsAssignableFrom<IDisposable>(reader.GetValue(0));
                Assert.Equal((long)data.Length, lob.GetType().GetProperty("ByteLength")!.GetValue(lob));
                Assert.False(async ? await reader.ReadAsync(budget.Token) : reader.Read());
                async Task Consume()
                {
                    if (character)
                    {
                        using var source = (TextReader)lob.GetType().GetMethod("OpenTextReader")!.Invoke(lob, null)!;
                        Assert.Equal("患者😀a", async ? await source.ReadToEndAsync() : source.ReadToEnd());
                    }
                    else
                    {
                        var source = (Stream)lob.GetType().GetMethod("OpenRead")!.Invoke(lob, null)!;
                        try { using var destination = new MemoryStream(); if (async) await source.CopyToAsync(destination, budget.Token); else source.CopyTo(destination); Assert.Equal(data, destination.ToArray()); }
                        finally { if (async) await source.DisposeAsync(); else source.Dispose(); }
                    }
                }
                if (kind == "trailing")
                {
                    var error = await Assert.ThrowsAnyAsync<DbException>(Consume); Assert.False((bool)error.GetType().GetProperty("IsExecutionOutcomeUnknown")!.GetValue(error)!);
                    Assert.Equal(ConnectionState.Broken, connection.State);
                }
                else { await Consume(); Assert.Equal(ConnectionState.Open, connection.State); }
                connection.Close(); await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }
    }
}
