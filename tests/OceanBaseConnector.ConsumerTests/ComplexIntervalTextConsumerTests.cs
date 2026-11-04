using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.ConsumerTests;

public sealed class ComplexIntervalTextConsumerTests
{
    public static IEnumerable<object[]> Assets()
    {
        foreach (var target in ConsumerTargetMatrix.Representative)
            foreach (var asynchronous in new[] { false, true })
                foreach (var sequential in new[] { false, true }) yield return new object[] { target, asynchronous, sequential };
    }

    [Theory]
    [MemberData(nameof(Assets))]
    public async Task EveryAssetReadsNamedIntervalTextThroughPublicApis(string target, bool asynchronous, bool sequential)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
        var greeting = Convert.FromBase64String(fixture.RootElement.GetProperty("payloadBase64").GetString()!);
        byte[] Column(byte code) => new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 118, 1, 118, 12, 45, 0, 255, 0, 0, 0, code, 0, 0, 0, 0, 0 };
        byte[] Text(string value) { var bytes = Encoding.UTF8.GetBytes(value); return new[] { (byte)bytes.Length }.Concat(bytes).ToArray(); }
        var eof = new byte[] { 254, 0, 0, 2, 0 };
        var serving = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(budget.Token);
            var channel = new PacketChannel(peer.GetStream());
            await channel.WritePacketAsync(greeting, budget.Token); await channel.ReadPacketAsync(budget.Token);
            await channel.WritePacketAsync(SessionTimeZoneFixture.Report, budget.Token);
            await OceanBaseConnector.UnitTests.TestInfrastructure.ServerIdentity.ReplyAsync(channel, token: budget.Token); channel.BeginCommand();
            Task<byte[]> Next() => SessionTimeZoneFixture.ReadCommand(channel, budget.Token, expectProbe: false);
            for (byte id = 81; id <= 82; id++)
            {
                var request = await Next(); Assert.Equal(22, request[0]);
                Assert.Contains(id == 81 ? "ALL_TYPES" : "ALL_TYPE_ATTRS", Encoding.UTF8.GetString(request));
                await channel.WritePacketAsync(new byte[] { 0, id, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0 }, budget.Token);
                await channel.WritePacketAsync(Column(253), budget.Token); await channel.WritePacketAsync(Column(253), budget.Token);
                await channel.WritePacketAsync(eof, budget.Token); channel.BeginCommand();
                Assert.Equal(23, (await Next())[0]);
                var names = id == 81 ? new[] { "TYPE_SCHEMA", "TYPE_NAME", "TYPE_KIND", "MEMBER_COUNT" }
                    : new[] { "MEMBER_NAME", "MEMBER_POSITION", "MEMBER_SCHEMA", "MEMBER_TYPE", "MEMBER_KIND" };
                await channel.WritePacketAsync(new[] { (byte)names.Length }, budget.Token);
                foreach (var name in names)
                {
                    var metadata = Column(253);
                    metadata = metadata.Take(7).Concat(Text(name)).Concat(Text(name)).Concat(metadata.Skip(11)).ToArray();
                    await channel.WritePacketAsync(metadata, budget.Token);
                }
                await channel.WritePacketAsync(eof, budget.Token);
                var rows = id == 81 ? new[] { new[] { "Owner", "Thing", "OBJECT", "2" } }
                    : new[] { new[] { "YM", "1", null, "INTERVAL YEAR(9) TO MONTH", "INTERVAL YEAR(9) TO MONTH" },
                        new[] { "DS", "2", null, "INTERVAL DAY(2) TO SECOND(3)", "INTERVAL DAY(2) TO SECOND(3)" } };
                foreach (var row in rows)
                {
                    byte bitmap = 0;
                    for (var i = 0; i < row.Length; i++) if (row[i] == null) bitmap |= (byte)(1 << (i + 2));
                    await channel.WritePacketAsync(new byte[] { 0, bitmap }.Concat(row.Where(x => x != null).SelectMany(x => Text(x!))).ToArray(), budget.Token);
                }
                await channel.WritePacketAsync(eof, budget.Token); channel.BeginCommand();
                Assert.Equal(25, (await Next())[0]); channel.BeginCommand();
            }
            Assert.Equal(3, (await Next())[0]);
            await channel.WritePacketAsync(new byte[] { 2 }, budget.Token);
            await channel.WritePacketAsync(Column(160).Concat(Text("Owner")).Concat(Text("Thing")).Concat(new byte[] { 0 }).ToArray(), budget.Token);
            await channel.WritePacketAsync(Column(253), budget.Token); await channel.WritePacketAsync(eof, budget.Token);
            await channel.WritePacketAsync(Text("Thing('-123456789-11','+98 23:59:58.123')").Concat(Text("after")).ToArray(), budget.Token);
            await channel.WritePacketAsync(Text("Thing(NULL,NULL)").Concat(Text("after nulls")).ToArray(), budget.Token);
            await channel.WritePacketAsync(eof, budget.Token);
        });
        var context = new AssemblyLoadContext("interval-text-" + target, isCollectible: true);
        try
        {
            var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
            using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
            connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled";
            if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open();
            var type = connection.GetType().GetMethod("GetComplexType")!.Invoke(connection, new object?[] { "Owner", "Thing", null })!;
            using var command = connection.CreateCommand(); command.CommandText = "SELECT v, marker FROM t";
            var behavior = sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default;
            using var reader = asynchronous ? await command.ExecuteReaderAsync(behavior, budget.Token) : command.ExecuteReader(behavior);
            Assert.True(asynchronous ? await reader.ReadAsync(budget.Token) : reader.Read());
            var value = reader.GetValue(0);
            Assert.Equal(assembly.GetType("OceanBaseConnector.OceanBaseComplexValue"), value.GetType());
            Assert.Same(type, value.GetType().GetProperty("Type")!.GetValue(value));
            var values = (object?[])value.GetType().GetMethod("GetValues")!.Invoke(value, null)!;
            Assert.Equal("-123456789-11", values[0]!.ToString()); Assert.Equal("+98 23:59:58.123", values[1]!.ToString());
            Assert.All(values, item => Assert.Same(assembly, item!.GetType().Assembly));
            Assert.Equal((byte)9, values[0]!.GetType().GetProperty("LeadingPrecision")!.GetValue(values[0]));
            Assert.Equal((byte)2, values[1]!.GetType().GetProperty("LeadingPrecision")!.GetValue(values[1]));
            Assert.Equal((byte)3, values[1]!.GetType().GetProperty("Scale")!.GetValue(values[1]));
            Assert.Equal("after", reader.GetString(1));
            Assert.True(asynchronous ? await reader.ReadAsync(budget.Token) : reader.Read());
            value = reader.GetValue(0); Assert.All((object?[])value.GetType().GetMethod("GetValues")!.Invoke(value, null)!, Assert.Null);
            Assert.Equal("after nulls", reader.GetString(1));
            Assert.False(asynchronous ? await reader.ReadAsync(budget.Token) : reader.Read());
            await serving.WaitAsync(budget.Token);
        }
        finally { budget.Cancel(); listener.Stop(); context.Unload(); }
    }
}
