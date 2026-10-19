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
        public static IEnumerable<object[]> OracleAssets()
        {
            foreach (var target in new[] { "netstandard2.0", "netstandard2.1", "net8.0", "net9.0", "net10.0" })
                foreach (var async in new[] { false, true })
                    foreach (var kind in new[] { "standard", "ob20", "trailing" }) yield return new object[] { target, async, kind };
        }

        [Theory]
        [MemberData(nameof(OracleAssets))]
        public Task EveryAssetExecutesOracleOutputsAndValidatesOb20BeforePublishing(string target, bool async, string kind)
            => EveryAssetExecutesOracleOutputsAndValidatesOb20BeforePublishingCore(target, async, kind, encrypted: false);

        [Theory]
        [MemberData(nameof(OracleAssets))]
        public Task EveryAssetExecutesOracleOutputsAndValidatesOb20BeforePublishingOverTls(string target, bool async, string kind)
            => EveryAssetExecutesOracleOutputsAndValidatesOb20BeforePublishingCore(target, async, kind, encrypted: true);

        private static async Task EveryAssetExecutesOracleOutputsAndValidatesOb20BeforePublishingCore(string target, bool async, string kind, bool encrypted)
        {
            using var certificate = encrypted ? TestCertificateFactory.CreateSelfSigned() : null;
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var ob20 = kind != "standard";
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token);
                using var tls = encrypted ? new SslStream(peer.GetStream()) : null;
                Stream stream = tls is null ? peer.GetStream() : tls;
                var channel = new PacketChannel(peer.GetStream());
                await Handshake(channel, ob20 ? "enabled" : "unrequested", budget.Token, deprecated: true, tls: tls, certificate: certificate);
                var version = await ReadRequest(stream, channel, ob20, 0, budget.Token);
                Assert.Equal(3, version[0]); Assert.Equal("select ob_version() from dual", Encoding.UTF8.GetString(version, 1, version.Length - 1));
                byte[] Column(byte type) => new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 112, 1, 112, 12, 45, 0, 20, 0, 0, 0, type, 0, 0, 0, 0, 0 };
                var eof = Report(false, 0); eof[0] = 254;
                var response = Frame(1, new byte[] { 1 }).Concat(Frame(2, Column(253)))
                    .Concat(Frame(3, new byte[] { 5, 52, 46, 50, 46, 48 })).Concat(Frame(4, eof)).ToArray();
                await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, 0, 1, response) : response, budget.Token);
                if (!ob20) channel.BeginCommand();
                var execute = new ByteCursor(await ReadRequest(stream, channel, ob20, 1, budget.Token));
                Assert.Equal(0xa1, execute.ReadUInt8()); Assert.Equal(0u, execute.ReadUInt32()); Assert.Equal(0, execute.ReadUInt8()); Assert.Equal(1u, execute.ReadUInt32());
                Assert.Equal("BEGIN pkg.proc(?, ?); END;", Encoding.UTF8.GetString(execute.ReadLengthEncodedBytes())); Assert.Equal(2u, execute.ReadUInt32());
                Assert.Equal(new byte[] { 2, 1, 8, 0, 6, 0, 7, 0, 0, 0, 0, 0, 0, 0 }, execute.ReadBytes(14));
                response = Frame(1, new byte[] { 0, 91, 0, 0, 0, 1, 0, 2, 0, 0, 0, 0, 4, 0, 0, 0, 1 })
                    .Concat(Frame(2, Column(8))).Concat(Frame(3, Column(8))).Concat(Frame(4, Column(8)))
                    .Concat(Frame(5, new byte[] { 0, 0, 42, 0, 0, 0, 0, 0, 0, 0 })).Concat(Frame(6, eof)).Concat(Frame(7, Report(false, 2))).ToArray();
                if (kind == "trailing") response = response.Concat(Frame(8, new byte[] { 0 })).ToArray();
                await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, 1, 1, response) : response, budget.Token);
                if (!ob20) channel.BeginCommand();
                if (kind != "trailing") Assert.Equal(new byte[] { 25, 91, 0, 0, 0 }, await ReadRequest(stream, channel, ob20, 2, budget.Token));
                await ObserveClose(stream, budget.Token);
            });
            var context = new AssemblyLoadContext("oracle-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode={(encrypted ? "Required" : "Disabled")};Use Oracle Prepare Execute=true;Use OB20={ob20}";
                if (async) await connection.OpenAsync(budget.Token); else connection.Open();
                var command = connection.CreateCommand(); command.CommandText = "pkg.proc"; command.CommandType = CommandType.StoredProcedure;
                var input = command.CreateParameter(); input.ParameterName = "input"; input.Value = 7L; command.Parameters.Add(input);
                var output = command.CreateParameter(); output.ParameterName = "output"; output.Value = "old"; output.Direction = ParameterDirection.Output; command.Parameters.Add(output);
                if (kind == "trailing")
                {
                    var error = await Assert.ThrowsAnyAsync<DbException>(async () => { if (async) await command.ExecuteNonQueryAsync(budget.Token); else command.ExecuteNonQuery(); });
                    Assert.True((bool)error.GetType().GetProperty("IsExecutionOutcomeUnknown")!.GetValue(error)!);
                    Assert.Equal("old", output.Value); Assert.Equal(ConnectionState.Broken, connection.State);
                }
                else
                {
                    Assert.Equal(2, async ? await command.ExecuteNonQueryAsync(budget.Token) : command.ExecuteNonQuery()); Assert.Equal(42L, output.Value);
                }
                if (async) await command.DisposeAsync(); else command.Dispose();
                connection.Close(); await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }
    }
}
