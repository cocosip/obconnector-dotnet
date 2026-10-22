using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    public sealed class IntervalConsumerTests
    {
        public static IEnumerable<object[]> Assets()
        {
            foreach (var asset in new[] { "netstandard2.0", "netstandard2.1", "net8.0", "net9.0", "net10.0" })
                foreach (var binary in new[] { false, true })
                    foreach (var sequential in new[] { false, true }) yield return new object[] { asset, binary, sequential };
        }

        [Theory]
        [MemberData(nameof(Assets))]
        public async Task EveryAssetReadsAndBindsExactIntervalsThroughPublicApis(string target, bool binary, bool sequential)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            var greeting = Convert.FromBase64String(fixture.RootElement.GetProperty("payloadBase64").GetString()!);
            byte[] Metadata(byte type, byte scale) => new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 105, 1, 105, 12, 63, 0, 32, 0, 0, 0, type, 0, 0, scale, 0, 0 };
            var ymBytes = new byte[] { 1, 255, 201, 154, 59, 11, 9 };
            var dsBytes = new byte[] { 1, 255, 201, 154, 59, 23, 59, 59, 255, 201, 154, 59, 9, 9 };
            var eof = new byte[] { 254, 0, 0, 2, 0 };
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token); var channel = new PacketChannel(peer.GetStream());
                await channel.WritePacketAsync(greeting, budget.Token); await channel.ReadPacketAsync(budget.Token);
                await channel.WritePacketAsync(SessionTimeZoneFixture.Report, budget.Token);
                await OceanBaseConnector.UnitTests.TestInfrastructure.ServerIdentity.ReplyAsync(channel, token: budget.Token); channel.BeginCommand();
                Task<byte[]> ReadCommand() => SessionTimeZoneFixture.ReadCommand(channel, budget.Token, expectProbe: false);
                Assert.Equal(binary ? 22 : 3, (await ReadCommand())[0]);
                if (binary)
                {
                    await channel.WritePacketAsync(new byte[] { 0, 129, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, budget.Token); channel.BeginCommand();
                    Assert.Equal(23, (await ReadCommand())[0]);
                }
                await channel.WritePacketAsync(new byte[] { 2 }, budget.Token);
                await channel.WritePacketAsync(Metadata(204, 9), budget.Token); await channel.WritePacketAsync(Metadata(205, 99), budget.Token);
                await channel.WritePacketAsync(eof, budget.Token);
                await channel.WritePacketAsync((binary ? new byte[] { 0, 0 } : Array.Empty<byte>()).Concat(new byte[] { 7 }).Concat(ymBytes).Concat(new byte[] { 14 }).Concat(dsBytes).ToArray(), budget.Token);
                await channel.WritePacketAsync(binary ? new byte[] { 0, 12 } : new byte[] { 251, 251 }, budget.Token);
                await channel.WritePacketAsync(eof, budget.Token); channel.BeginCommand();
                if (binary) { Assert.Equal(25, (await ReadCommand())[0]); channel.BeginCommand(); }
                Assert.Equal(22, (await ReadCommand())[0]);
                await channel.WritePacketAsync(new byte[] { 0, 130, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0 }, budget.Token);
                await channel.WritePacketAsync(Metadata(204, 9), budget.Token); await channel.WritePacketAsync(Metadata(205, 99), budget.Token);
                await channel.WritePacketAsync(eof, budget.Token); channel.BeginCommand();
                var execute = await ReadCommand();
                Assert.Equal(23, execute[0]);
                Assert.Equal(new byte[] { 0, 1, 204, 0, 205, 0, 7 }.Concat(ymBytes).Concat(new byte[] { 14 }).Concat(dsBytes), execute.AsSpan(10).ToArray());
                await channel.WritePacketAsync(new byte[] { 0, 1, 0, 2, 0, 0, 0 }, budget.Token); channel.BeginCommand();
                Assert.Equal(25, (await ReadCommand())[0]);
            });
            var context = new AssemblyLoadContext("interval-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled";
                await connection.OpenAsync(budget.Token);
                object[] values;
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "interval"; if (binary) await command.PrepareAsync(budget.Token);
                    using var reader = await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default, budget.Token);
                    var schema = reader.GetColumnSchema(); var table = reader.GetSchemaTable()!;
                    Assert.True(await reader.ReadAsync(budget.Token)); values = new object[2];
                    var names = new[] { "YearToMonth", "DayToSecond" };
                    var texts = new[] { "-999999999-11", "-999999999 23:59:59.999999999" };
                    for (var i = 0; i < 2; i++)
                    {
                        var type = assembly.GetType("OceanBaseConnector.OceanBaseInterval" + names[i])!;
                        Assert.Equal(type, reader.GetFieldType(i)); Assert.Equal(type, reader.GetProviderSpecificFieldType(i));
                        Assert.Equal(i == 0 ? "INTERVAL YEAR TO MONTH" : "INTERVAL DAY TO SECOND", reader.GetDataTypeName(i));
                        Assert.Equal(i + 5, schema[i]["ProviderType"]); Assert.Equal(type, table.Rows[i]["ProviderSpecificDataType"]);
                        Assert.Null(schema[i].NumericPrecision); Assert.Null(schema[i].NumericScale);
                        values[i] = await reader.GetFieldValueAsync<object>(i, budget.Token);
                        Assert.IsType(type, values[i]); Assert.Equal(texts[i], values[i].ToString());
                        Assert.Equal(values[i], reader.GetProviderSpecificValue(i));
                        Assert.Equal((byte)9, type.GetProperty("LeadingPrecision")!.GetValue(values[i]));
                        Assert.True((bool)type.GetProperty("IsNegative")!.GetValue(values[i])!);
                    }
                    Assert.True(await reader.ReadAsync(budget.Token));
                    Assert.True(reader.IsDBNull(0)); Assert.Equal(DBNull.Value, reader.GetValue(0)); Assert.Equal(DBNull.Value, reader.GetProviderSpecificValue(1));
                    Assert.False(await reader.ReadAsync(budget.Token));
                }
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "select :ym, :ds";
                    for (var i = 0; i < 2; i++)
                    {
                        var parameter = command.CreateParameter(); parameter.ParameterName = i == 0 ? "ym" : "ds"; parameter.Value = values[i];
                        parameter.GetType().GetProperty("OceanBaseDbType")!.SetValue(parameter,
                            Enum.Parse(assembly.GetType("OceanBaseConnector.OceanBaseDbType")!, i == 0 ? "IntervalYearToMonth" : "IntervalDayToSecond"));
                        Assert.Equal(DbType.Object, parameter.DbType); command.Parameters.Add(parameter);
                    }
                    Assert.Equal(1, await command.ExecuteNonQueryAsync(budget.Token));
                }
                await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }
    }
}
