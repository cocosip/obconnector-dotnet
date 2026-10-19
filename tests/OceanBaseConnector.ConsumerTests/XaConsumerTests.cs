using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
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
        public static IEnumerable<object[]> XaAssets()
        {
            foreach (var target in new[] { "netstandard2.0", "netstandard2.1", "net8.0", "net9.0", "net10.0" })
                foreach (var asynchronous in new[] { false, true })
                    foreach (var ob20 in new[] { false, true })
                        foreach (var kind in new[] { "success", "reject", "disconnect", "boundary" })
                            yield return new object[] { target, asynchronous, ob20, kind };
        }

        [Theory]
        [MemberData(nameof(XaAssets))]
        public Task EveryAssetPinsXaPhasesAndRecoversWithoutReplaying(string target, bool asynchronous, bool ob20, string kind)
            => PinsXaPhasesAndRecoversWithoutReplaying(target, asynchronous, ob20, kind, encrypted: false);

        [Theory]
        [MemberData(nameof(XaAssets))]
        public Task EveryAssetPinsXaPhasesAndRecoversWithoutReplayingOverTls(string target, bool asynchronous, bool ob20, string kind)
            => PinsXaPhasesAndRecoversWithoutReplaying(target, asynchronous, ob20, kind, encrypted: true);

        private static async Task PinsXaPhasesAndRecoversWithoutReplaying(string target, bool asynchronous, bool ob20, string kind, bool encrypted)
        {
            using var certificate = encrypted ? TestCertificateFactory.CreateSelfSigned() : null;
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var emptyBranch = kind == "reject";
            var identity = emptyBranch ? "0x0027ff,X'',0x80000000" : "0x0027ff,0x00ff,0x80000000";
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token);
                using var tls = encrypted ? new SslStream(peer.GetStream()) : null;
                Stream stream = tls is null ? peer.GetStream() : tls;
                var channel = new PacketChannel(peer.GetStream());
                await Handshake(channel, ob20 ? "enabled" : "unrequested", budget.Token, tls: tls, certificate: certificate);
                var commands = new List<string> { "XA START " + identity, "update sample", "XA END " + identity, "XA PREPARE " + identity };
                if (kind == "reject") commands.Add("XA PREPARE " + identity);
                if (kind == "success" || kind == "reject") commands.AddRange(new[] { "XA COMMIT " + identity, "XA RECOVER", "XA ROLLBACK " + identity });
                for (uint i = 0; i < commands.Count; i++)
                {
                    var query = await ReadRequest(stream, channel, ob20, i, budget.Token);
                    Assert.Equal(3, query[0]); Assert.Equal(commands[(int)i], Encoding.UTF8.GetString(query, 1, query.Length - 1));
                    if (i == 3 && kind == "disconnect") return;
                    var response = commands[(int)i] == "XA RECOVER" ? XaRecoveryFrames(emptyBranch)
                        : Frame(1, kind == "reject" && i == 3
                            ? new byte[] { 255, 119, 5, 35, 88, 65, 69, 48, 55 } // 1399/XAE07: ordinary phase rejection.
                            : new byte[] { 0, 0, 0, (byte)(i == 1 ? 3 : 2), 0, 0, 0 });
                    if (i == 3 && kind == "boundary")
                    {
                        if (ob20) response = response.Concat(Frame(2, new byte[] { 0 })).ToArray();
                        else response = Frame(1, new byte[] { 0, 0, 0, 10, 0, 0, 0 }); // Forbidden MORE_RESULTS.
                    }
                    await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, i, 1, response) : response, budget.Token);
                    if (i == 3 && kind == "boundary") break;
                    if (!ob20) channel.BeginCommand();
                }
                await ObserveClose(stream, budget.Token);
            });
            var context = new AssemblyLoadContext("xa-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                var connectionType = assembly.GetType("OceanBaseConnector.OceanBaseConnection")!;
                using var connection = (DbConnection)Activator.CreateInstance(connectionType)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode={(encrypted ? "Required" : "Disabled")};Use OB20={ob20}";
                if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open();
                // A05 public MySQL identification remains strict; this is an explicit post-auth fixture.
                var session = connectionType.GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection)!;
                var field = session.GetType().GetField("_info", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var info = field.GetValue(session)!; var infoType = info.GetType();
                object Fact(string name) => infoType.GetProperty(name)!.GetValue(info)!;
                field.SetValue(session, Activator.CreateInstance(infoType, Fact("ServerVersion"), Fact("ConnectionId"),
                    Enum.Parse(assembly.GetType("OceanBaseConnector.OceanBaseCompatibilityMode")!, "MySql"), Fact("Capabilities"),
                    Fact("ServerStatus"), Fact("ObCapabilityFlag"), Fact("SessionVariables")));
                var resource = connectionType.GetMethod("GetXaResource")!.Invoke(connection, null)!;
                Assert.True(resource.GetType().IsSealed); Assert.False(resource is IDisposable);
                var xidType = assembly.GetType("OceanBaseConnector.OceanBaseXid")!;
                var xid = Activator.CreateInstance(xidType, int.MinValue, new byte[] { 0, 39, 255 }, emptyBranch ? Array.Empty<byte>() : new byte[] { 0, 255 })!;
                var startMode = Enum.Parse(assembly.GetType("OceanBaseConnector.OceanBaseXaStartMode")!, "New");
                var endMode = Enum.Parse(assembly.GetType("OceanBaseConnector.OceanBaseXaEndMode")!, "Success");
                async Task<object?> Invoke(string method, params object[] parameters)
                {
                    try
                    {
                        var args = asynchronous ? parameters.Append((object)budget.Token).ToArray() : parameters;
                        var result = resource.GetType().GetMethod(method + (asynchronous ? "Async" : ""))!.Invoke(resource, args);
                        if (result is Task task) { await task; return task.GetType().GetProperty("Result")?.GetValue(task); }
                        return result;
                    }
                    catch (TargetInvocationException error) when (error.InnerException != null) { throw error.InnerException; }
                }
                using var command = connection.CreateCommand(); command.CommandText = "update sample";
                Assert.Throws<InvalidOperationException>(() => command.ExecuteNonQuery());
                await Invoke("Start", xid, startMode);
                Assert.Throws<InvalidOperationException>(() => connection.BeginTransaction());
                Assert.Equal(0, asynchronous ? await command.ExecuteNonQueryAsync(budget.Token) : command.ExecuteNonQuery());
                await Invoke("End", xid, endMode);
                if (kind == "disconnect" || kind == "boundary")
                {
                    var error = await Assert.ThrowsAnyAsync<DbException>(() => Invoke("Prepare", xid));
                    Assert.True((bool)error.GetType().GetProperty("IsExecutionOutcomeUnknown")!.GetValue(error)!);
                    Assert.Equal(ConnectionState.Broken, connection.State);
                    await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke("Prepare", xid));
                }
                else
                {
                    if (kind == "reject")
                    {
                        var error = await Assert.ThrowsAnyAsync<DbException>(() => Invoke("Prepare", xid));
                        Assert.False((bool)error.GetType().GetProperty("IsExecutionOutcomeUnknown")!.GetValue(error)!);
                        Assert.Equal(1399, error.GetType().GetProperty("ServerErrorCode")!.GetValue(error));
                    }
                    Assert.Equal("Prepared", (await Invoke("Prepare", xid))!.ToString());
                    await Invoke("Commit", xid, false);
                    var recovered = Assert.IsAssignableFrom<Array>(await Invoke("Recover")); Assert.Single(recovered);
                    Assert.Equal(xid, recovered.GetValue(0)); await Invoke("Rollback", recovered.GetValue(0)!);
                }
                connection.Close(); await serving.WaitAsync(budget.Token);
                await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke("Recover"));
            }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }

        private static byte[] XaRecoveryFrames(bool emptyBranch)
        {
            byte[] XaColumn(string name, byte type, byte charset)
            {
                var prefix = new byte[] { 3, 100, 101, 102, 0, 0, 0, (byte)name.Length }.Concat(Encoding.ASCII.GetBytes(name))
                    .Concat(new[] { (byte)name.Length }).Concat(Encoding.ASCII.GetBytes(name));
                return prefix.Concat(new byte[] { 12, charset, 0, 128, 0, 0, 0, type, 0, 0, 0, 0, 0 }).ToArray();
            }
            var row = new List<byte>();
            foreach (var field in new[] { Encoding.ASCII.GetBytes("2147483648"), new byte[] { 51 }, new byte[] { emptyBranch ? (byte)48 : (byte)50 },
                emptyBranch ? new byte[] { 0, 39, 255 } : new byte[] { 0, 39, 255, 0, 255 } })
            { row.Add((byte)field.Length); row.AddRange(field); }
            return Frame(1, new byte[] { 4 }).Concat(Frame(2, XaColumn("formatID", 8, 63)))
                .Concat(Frame(3, XaColumn("gtrid_length", 3, 63))).Concat(Frame(4, XaColumn("bqual_length", 3, 63)))
                .Concat(Frame(5, XaColumn("data", 252, 63))).Concat(Frame(6, new byte[] { 254, 0, 0, 2, 0 }))
                .Concat(Frame(7, row.ToArray())).Concat(Frame(8, new byte[] { 254, 0, 0, 2, 0 })).ToArray();
        }
    }
}
