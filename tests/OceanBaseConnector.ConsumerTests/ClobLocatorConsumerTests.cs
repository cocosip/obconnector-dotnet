using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    public sealed partial class Ob20ConsumerTests
    {
        public static IEnumerable<object[]> ClobEncodingAssets()
        {
            foreach (var target in new[] { "netstandard2.0", "netstandard2.1", "net8.0", "net9.0", "net10.0" })
                foreach (var asynchronous in new[] { false, true })
                    foreach (var array in new[] { false, true }) yield return new object[] { target, asynchronous, array };
        }

        [Theory]
        [MemberData(nameof(ClobEncodingAssets))]
        public async Task EveryAssetEncodesClobLocatorsAndRejectsMalformedUtf8BeforeTarget(string target, bool asynchronous, bool array)
        {
            const string text = "ḿ\ue7c7😀\\'";
            byte[] Locator(byte[] payload) => new byte[] { 1, 132, 1, 0, 2, 161, 0, 0 }.Concat(payload).ToArray();
            var original = Locator(Encoding.UTF8.GetBytes(text));
            var encoded = Locator(Convert.FromHexString("A8BC8135F4379439FC365C27"));
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token); var stream = peer.GetStream(); var channel = new PacketChannel(stream);
                await Handshake(channel, stream, "unrequested", budget.Token, lobLocators: true);
                await ClientCharacterSetFixture.Initialize(channel, "gb18030", true, false, budget.Token);
                async Task<byte[]> Next()
                {
                    while (true)
                    {
                        var request = await ClientCharacterSetFixture.Read(channel, true, "gb18030", false, budget.Token);
                        if (request[0] != 3 || Encoding.ASCII.GetString(request, 1, request.Length - 1) != "SELECT @@character_set_client, @@character_set_connection, @@character_set_results, @@collation_connection FROM DUAL") return request;
                        await ClientCharacterSetFixture.State(channel, "gb18030", true, false, budget.Token);
                    }
                }
                foreach (var locator in new[] { original, Locator(new byte[] { 0xc3, 0x28 }) })
                {
                    var source = await Next(); Assert.Equal(3, source[0]); Assert.Equal("select lob_value", Encoding.ASCII.GetString(source, 1, source.Length - 1));
                    var column = ClientCharacterSetFixture.Column(); column[column.Length - 6] = 211;
                    await channel.WritePacketAsync(new byte[] { 1 }, budget.Token); await channel.WritePacketAsync(column, budget.Token);
                    await channel.WritePacketAsync(EofClob(), budget.Token);
                    await channel.WritePacketAsync(new[] { (byte)locator.Length }.Concat(locator).ToArray(), budget.Token);
                    await channel.WritePacketAsync(EofClob(), budget.Token);
                    if (!ReferenceEquals(locator, original)) continue;
                    var request = new ByteCursor(await Next()); Assert.Equal(0xa1, request.ReadUInt8()); request.Skip(9);
                    Assert.Equal("BEGIN pkg.p(?); END;", Encoding.ASCII.GetString(request.ReadLengthEncodedBytes())); Assert.Equal(1u, request.ReadUInt32());
                    var prefix = array ? new byte[] { 0, 1, 160, 0, 0, 0, 0, 211, 1, 0 } : new byte[] { 0, 1, 211, 0 };
                    var expected = prefix.Concat(new[] { (byte)encoded.Length }).Concat(encoded).ToArray();
                    Assert.Equal(expected, request.ReadBytes(expected.Length));
                    await channel.WritePacketAsync(new byte[] { 0, 42, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, budget.Token);
                    await channel.WritePacketAsync(ClientCharacterSetFixture.Column(), budget.Token); await channel.WritePacketAsync(EofClob(), budget.Token);
                    await channel.WritePacketAsync(new byte[] { 0, 1, 0, 6, 0, 0, 0 }, budget.Token);
                }
                var after = await Next(); Assert.Equal(3, after[0]); Assert.Equal("after", Encoding.ASCII.GetString(after, 1, after.Length - 1));
                await channel.WritePacketAsync(new byte[] { 0, 1, 0, 6, 0, 0, 0 }, budget.Token);
            });
            var context = new AssemblyLoadContext("clob-client-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                var connectionType = assembly.GetType("OceanBaseConnector.OceanBaseConnection")!;
                using var connection = (DbConnection)Activator.CreateInstance(connectionType)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled;Character Set=gb18030;Use Oracle Prepare Execute=true;Use Lob Locators=true";
                if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open();
                for (var invalid = 0; invalid < 2; invalid++)
                {
                    using var source = connection.CreateCommand(); source.CommandText = "select lob_value";
                    using var lob = Assert.IsAssignableFrom<IDisposable>(asynchronous ? await source.ExecuteScalarAsync(budget.Token) : source.ExecuteScalar());
                    object value = lob;
                    if (array)
                    {
                        var type = connectionType.GetMethod("CreateArrayType", new[] { typeof(string), assembly.GetType("OceanBaseConnector.OceanBaseTransaction")! })!.Invoke(connection, new object?[] { "CLOB", null })!;
                        var create = type.GetType().GetMethod("CreateValue")!;
                        if (invalid != 0)
                        {
                            Assert.IsType<DecoderFallbackException>(Assert.Throws<TargetInvocationException>(() => create.Invoke(type, new object[] { new object?[] { lob } })).InnerException);
                            continue;
                        }
                        value = create.Invoke(type, new object[] { new object?[] { lob } })!;
                    }
                    using var command = connection.CreateCommand(); command.CommandText = "pkg.p"; command.CommandType = CommandType.StoredProcedure;
                    var p = command.CreateParameter(); p.ParameterName = "p"; p.Value = value; command.Parameters.Add(p);
                    if (invalid == 0)
                    {
                        Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync(budget.Token) : command.ExecuteNonQuery());
                        using var reader = (TextReader)lob.GetType().GetMethod("OpenTextReader")!.Invoke(lob, null)!;
                        Assert.Equal(text, await reader.ReadToEndAsync());
                    }
                    else await Assert.ThrowsAsync<DecoderFallbackException>(() => asynchronous ? command.ExecuteNonQueryAsync(budget.Token) : Task.FromResult(command.ExecuteNonQuery()));
                }
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync(budget.Token));
                connection.Close(); await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }

        private static byte[] EofClob() => new byte[] { 254, 0, 0, 6, 0 };
    }
}
