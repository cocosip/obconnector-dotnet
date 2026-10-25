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
        public static IEnumerable<object[]> OracleXaAssets()
        {
            foreach (var target in new[] { "netstandard2.0", "netstandard2.1", "net8.0", "net9.0", "net10.0" })
                foreach (var asynchronous in new[] { false, true })
                    foreach (var ob20 in new[] { false, true })
                        foreach (var kind in new[] { "success", "readonly", "code", "disconnect", "boundary" })
                            yield return new object[] { target, asynchronous, ob20, kind };
        }

        [Theory]
        [MemberData(nameof(OracleXaAssets))]
        public Task EveryAssetExecutesOracleXaAndValidatesRecoveryBeforePublishing(string target, bool asynchronous, bool ob20, string kind)
            => EveryAssetExecutesOracleXaAndValidatesRecoveryBeforePublishingCore(target, asynchronous, ob20, kind, encrypted: false);

        [Theory]
        [MemberData(nameof(OracleXaAssets))]
        public Task EveryAssetExecutesOracleXaAndValidatesRecoveryBeforePublishingOverTls(string target, bool asynchronous, bool ob20, string kind)
            => EveryAssetExecutesOracleXaAndValidatesRecoveryBeforePublishingCore(target, asynchronous, ob20, kind, encrypted: true);

        private static async Task EveryAssetExecutesOracleXaAndValidatesRecoveryBeforePublishingCore(string target, bool asynchronous, bool ob20, string kind, bool encrypted)
        {
            using var certificate = encrypted ? TestCertificateFactory.CreateSelfSigned() : null;
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token);
                using var tls = encrypted ? new SslStream(peer.GetStream()) : null;
                Stream stream = tls is null ? peer.GetStream() : tls;
                var channel = new PacketChannel(peer.GetStream());
                await Handshake(channel, stream, ob20 ? "enabled" : "unrequested", budget.Token, tls: tls, certificate: certificate);
                uint requestId = 0;
                async Task<byte[]> Read() => await ReadRequest(stream, channel, ob20, requestId++ + 2, budget.Token);
                async Task Reply(byte[] response)
                {
                    await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, requestId + 1, 1, response) : response, budget.Token);
                    if (!ob20) channel.BeginCommand();
                }
                async Task Text(string sql)
                { var request = await Read(); Assert.Equal(3, request[0]); Assert.Equal(sql, Encoding.UTF8.GetString(request, 1, request.Length - 1)); }
                async Task Stage(string operation, int? flag = null, bool close = true)
                {
                    if (close) { Assert.Equal(new byte[] { 25, 42, 0, 0, 0 }, await Read()); if (!ob20) channel.BeginCommand(); }
                    var cursor = new ByteCursor(await Read());
                    Assert.Equal(0xa1, cursor.ReadUInt8()); Assert.Equal(0u, cursor.ReadUInt32());
                    Assert.Equal(0, cursor.ReadUInt8()); Assert.Equal(1u, cursor.ReadUInt32());
                    Assert.Equal(operation == "RECOVER"
                        ? "declare x DBMS_XA_XID_ARRAY := DBMS_XA_XID_ARRAY(); BEGIN x := DBMS_XA.XA_RECOVER(); ? := x; END;"
                        : "select DBMS_XA.XA_" + operation + (flag.HasValue ? "(?, ?) from dual" : "(?) from dual"), Encoding.UTF8.GetString(cursor.ReadLengthEncodedBytes()));
                    Assert.Equal(flag.HasValue ? 2u : 1u, cursor.ReadUInt32());
                    Assert.Equal(operation == "RECOVER" ? 1 : 0, cursor.ReadUInt8()); Assert.Equal(1, cursor.ReadUInt8());
                    Assert.Equal(operation == "RECOVER" ? 6 : 160, cursor.ReadUInt16());
                    if (operation != "RECOVER")
                    {
                        Assert.Equal("SYS", Encoding.UTF8.GetString(cursor.ReadLengthEncodedBytes()));
                        Assert.Equal("DBMS_XA_XID", Encoding.UTF8.GetString(cursor.ReadLengthEncodedBytes()));
                        Assert.Equal(0, cursor.ReadUInt8());
                    }
                    if (flag.HasValue) Assert.Equal(operation == "COMMIT" ? 1 : 3, cursor.ReadUInt16());
                    if (operation != "RECOVER")
                    {
                        Assert.Equal(0, cursor.ReadUInt8()); Assert.Equal("-2147483648", Encoding.ASCII.GetString(cursor.ReadLengthEncodedBytes()));
                        Assert.Equal(new byte[] { 0, 39, 255 }, cursor.ReadLengthEncodedBytes()); Assert.Empty(cursor.ReadLengthEncodedBytes());
                        if (flag.HasValue) Assert.Equal((uint)flag.Value, operation == "COMMIT" ? cursor.ReadUInt8() : cursor.ReadUInt32());
                    }
                    Assert.Equal(0u, cursor.ReadUInt32()); Assert.Equal(0u, cursor.ReadUInt32()); cursor.ReadUInt32(); Assert.Equal(0u, cursor.ReadUInt32());
                    Assert.False(cursor.HasRemaining);
                }
                await Text("set autocommit = 0"); await Reply(Frame(1, new byte[] { 0, 0, 0, 0, 0, 0, 0 }));
                await Stage("START", 0, false); await Reply(OracleXaFrames(2, rowCompletion: asynchronous));
                await Stage("END", 0x04000000); await Reply(OracleXaFrames(2, rowCompletion: asynchronous));
                await Stage("PREPARE");
                if (kind == "disconnect") return;
                var prepared = OracleXaFrames(1, kind == "readonly" ? 3 : kind == "code" ? 100 : 0, badStatus: kind == "boundary" && !ob20, rowCompletion: asynchronous);
                if (kind == "boundary" && ob20) prepared = prepared.Concat(Frame(9, new byte[] { 0 })).ToArray();
                await Reply(prepared);
                if (kind == "success" || kind == "readonly")
                {
                    if (kind == "success") { await Stage("COMMIT", 0); await Reply(OracleXaFrames(2, rowCompletion: asynchronous)); }
                    await Stage("RECOVER"); await Reply(OracleXaFrames(1, recovery: true, rowCompletion: asynchronous));
                    await Stage("ROLLBACK"); await Reply(OracleXaFrames(1, rowCompletion: asynchronous));
                }
                await ObserveClose(stream, budget.Token);
            });
            var context = new AssemblyLoadContext("oracle-xa-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                var connectionType = assembly.GetType("OceanBaseConnector.OceanBaseConnection")!;
                using var connection = (DbConnection)Activator.CreateInstance(connectionType)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode={(encrypted ? "Required" : "Disabled")};Use Oracle Prepare Execute=true;Use OB20={ob20}";
                if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open();
                var resource = connectionType.GetMethod("GetXaResource")!.Invoke(connection, null)!;
                var xid = Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseXid")!, int.MinValue, new byte[] { 0, 39, 255 }, Array.Empty<byte>())!;
                async Task<object?> Invoke(string method, params object[] parameters)
                {
                    try
                    {
                        var result = resource.GetType().GetMethod(method + (asynchronous ? "Async" : ""))!.Invoke(resource,
                            asynchronous ? parameters.Append((object)budget.Token).ToArray() : parameters);
                        if (result is Task task) { await task; return task.GetType().GetProperty("Result")?.GetValue(task); }
                        return result;
                    }
                    catch (TargetInvocationException error) when (error.InnerException != null) { throw error.InnerException; }
                }
                await Invoke("Start", xid, Enum.Parse(assembly.GetType("OceanBaseConnector.OceanBaseXaStartMode")!, "New"));
                await Invoke("End", xid, Enum.Parse(assembly.GetType("OceanBaseConnector.OceanBaseXaEndMode")!, "Success"));
                if (kind == "success" || kind == "readonly")
                {
                    Assert.Equal(kind == "readonly" ? "ReadOnly" : "Prepared", (await Invoke("Prepare", xid))!.ToString());
                    if (kind == "success") await Invoke("Commit", xid, false);
                    var recovered = Assert.IsAssignableFrom<Array>(await Invoke("Recover")); Assert.Single(recovered); Assert.Equal(xid, recovered.GetValue(0));
                    await Invoke("Rollback", recovered.GetValue(0)!); Assert.Equal(ConnectionState.Open, connection.State);
                }
                else
                {
                    var error = await Assert.ThrowsAnyAsync<DbException>(() => Invoke("Prepare", xid));
                    Assert.Equal(kind != "code", error.GetType().GetProperty("IsExecutionOutcomeUnknown")!.GetValue(error));
                    Assert.Equal(kind == "code" ? (object)100 : null, error.GetType().GetProperty("XaErrorCode")!.GetValue(error));
                    Assert.Equal(0, error.GetType().GetProperty("ServerErrorCode")!.GetValue(error)); Assert.Equal(ConnectionState.Broken, connection.State);
                }
                connection.Close(); await serving.WaitAsync(budget.Token);
                await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke("Recover"));
            }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }

        private static byte[] OracleXaColumn(byte type, string? name = null)
        {
            var column = new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 112, 1, 112, 12, 45, 0, 128, 0, 0, 0, type, 0, 0, 0, 0, 0 };
            return name == null ? column : column.Concat(new byte[] { 3, 83, 89, 83, (byte)name.Length })
                .Concat(Encoding.ASCII.GetBytes(name)).Concat(new byte[] { 0 }).ToArray();
        }

        private static byte[] OracleXaFrames(byte parameters, long code = 0, bool recovery = false, bool badStatus = false, bool rowCompletion = false)
        {
            var complex = OracleXaColumn(160, recovery ? "DBMS_XA_XID_ARRAY" : "DBMS_XA_XID");
            var packets = new List<byte[]> { new byte[] { 0, 42, 0, 0, 0, 1, 0, parameters, 0, 0, 0, 0, recovery ? (byte)4 : (byte)0, 0, 0, 0, 1 }, complex };
            if (parameters == 2) packets.Add(OracleXaColumn(3));
            var eof = new byte[] { 254, 0, 0, (byte)(rowCompletion && !recovery ? 0x40 : 0), 0 }; packets.Add(eof); packets.Add(recovery ? complex : OracleXaColumn(8)); packets.Add(eof);
            packets.Add(recovery
                ? new byte[] { 0, 0, 1, 0, 0, 11 }.Concat(Encoding.ASCII.GetBytes("-2147483648")).Concat(new byte[] { 3, 0, 39, 255, 0 }).ToArray()
                : new byte[] { 0, 0 }.Concat(BitConverter.GetBytes(code)).ToArray());
            packets.Add(eof); packets.Add(new byte[] { 0, (byte)(rowCompletion ? 1 : 0), 0,
                (byte)((badStatus ? 8 : 0) | (rowCompletion && !recovery ? 0x40 : 0)), (byte)(rowCompletion && recovery ? 0x10 : 0), 0, 0 });
            return packets.SelectMany((packet, i) => Frame((byte)(i + 1), packet)).ToArray();
        }
    }
}
