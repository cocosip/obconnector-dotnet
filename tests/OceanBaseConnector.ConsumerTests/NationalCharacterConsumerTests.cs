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

namespace OceanBaseConnector.ConsumerTests
{
    public sealed class NationalCharacterConsumerTests
    {
        public static IEnumerable<object[]> Assets()
        {
            foreach (var asset in new[] { "netstandard2.0", "netstandard2.1", "net8.0", "net9.0", "net10.0" })
                foreach (var national in new[] { "utf8mb4", "utf16" })
                    foreach (var binary in new[] { false, true })
                        foreach (var sequential in new[] { false, true }) yield return new object[] { asset, national, binary, sequential };
        }

        [Theory]
        [MemberData(nameof(Assets))]
        public async Task EveryAssetReadsStreamsAndBindsNationalStrings(string target, string national, bool binary, bool sequential)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            var greeting = Convert.FromBase64String(fixture.RootElement.GetProperty("payloadBase64").GetString()!);
            const string value = "\ufeff国字é\U0001f600  ";
            Encoding encoding = national == "utf16" ? new UnicodeEncoding(true, false, true) : new UTF8Encoding(false, true);
            var bytes = encoding.GetBytes(value); var field = new byte[] { (byte)bytes.Length }.Concat(bytes).ToArray();
            byte[] Metadata(byte type) => new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 110, 1, 110, 12, 54, 0, 32, 0, 0, 0, type, 0, 0, 0, 0, 0 };
            var eof = new byte[] { 254, 0, 0, 6, 0 };
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token); var channel = new PacketChannel(peer.GetStream());
                await channel.WritePacketAsync(greeting, budget.Token); await channel.ReadPacketAsync(budget.Token);
                await channel.WritePacketAsync(SessionTimeZoneFixture.ReportFor(national), budget.Token);
                await OceanBaseConnector.UnitTests.TestInfrastructure.ServerIdentity.ReplyAsync(channel, token: budget.Token); channel.BeginCommand();
                Task<byte[]> ReadCommand() => SessionTimeZoneFixture.ReadCommand(channel, budget.Token, expectProbe: false, national: national);
                Assert.Equal(binary ? 22 : 3, (await ReadCommand())[0]);
                if (binary)
                {
                    await channel.WritePacketAsync(new byte[] { 0, 134, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, budget.Token); channel.BeginCommand();
                    Assert.Equal(23, (await ReadCommand())[0]);
                }
                await channel.WritePacketAsync(new byte[] { 3 }, budget.Token);
                foreach (var type in new byte[] { 208, 207, 207 }) await channel.WritePacketAsync(Metadata(type), budget.Token);
                await channel.WritePacketAsync(eof, budget.Token);
                await channel.WritePacketAsync((binary ? new byte[] { 0, 0 } : Array.Empty<byte>()).Concat(field).Concat(field).Concat(field).ToArray(), budget.Token);
                await channel.WritePacketAsync(binary ? new byte[] { 0, 28 } : new byte[] { 251, 251, 251 }, budget.Token);
                await channel.WritePacketAsync(eof, budget.Token); channel.BeginCommand();
                if (binary) { Assert.Equal(25, (await ReadCommand())[0]); channel.BeginCommand(); }
                Assert.Equal(22, (await ReadCommand())[0]);
                await channel.WritePacketAsync(new byte[] { 0, 135, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0 }, budget.Token);
                await channel.WritePacketAsync(Metadata(208), budget.Token); await channel.WritePacketAsync(Metadata(207), budget.Token); await channel.WritePacketAsync(eof, budget.Token);
                channel.BeginCommand(); var execute = await ReadCommand(); Assert.Equal(23, execute[0]);
                Assert.Equal(new byte[] { 0, 1, 208, 0, 207, 0 }.Concat(field).Concat(field), execute.AsSpan(10).ToArray());
                await channel.WritePacketAsync(new byte[] { 0, 1, 0, 6, 0, 0, 0 }, budget.Token); channel.BeginCommand();
                Assert.Equal(25, (await ReadCommand())[0]);
            });
            var context = new AssemblyLoadContext("national-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled";
                await connection.OpenAsync(budget.Token);
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "national"; if (binary) await command.PrepareAsync(budget.Token);
                    using var reader = await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default, budget.Token);
                    Assert.Equal(typeof(string), reader.GetFieldType(0)); Assert.Equal(typeof(string), reader.GetProviderSpecificFieldType(1));
                    var schema = reader.GetColumnSchema(); Assert.Equal("NCHAR", schema[0].DataTypeName); Assert.Equal("NVARCHAR2", schema[1].DataTypeName);
                    Assert.Equal(32, schema[0].ColumnSize); Assert.Equal(3, schema[0]["ProviderType"]); Assert.Equal(4, schema[1]["ProviderType"]);
                    Assert.True(await reader.ReadAsync(budget.Token));
                    using (var text = reader.GetTextReader(0)) Assert.Equal(value, await text.ReadToEndAsync());
                    var buffer = new char[value.Length]; Assert.Equal(buffer.Length, reader.GetChars(1, 0, buffer, 0, buffer.Length)); Assert.Equal(value, new string(buffer));
                    Assert.Equal(value, await reader.GetFieldValueAsync<string>(2, budget.Token)); Assert.Equal(value, reader.GetProviderSpecificValue(2));
                    Assert.True(await reader.ReadAsync(budget.Token)); Assert.True(reader.IsDBNull(0)); Assert.Equal(DBNull.Value, reader.GetValue(1));
                    Assert.False(await reader.ReadAsync(budget.Token));
                }
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "select :n, :v";
                    foreach (var name in new[] { "n", "v" })
                    {
                        var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value;
                        parameter.GetType().GetProperty("OceanBaseDbType")!.SetValue(parameter,
                            Enum.Parse(assembly.GetType("OceanBaseConnector.OceanBaseDbType")!, name == "n" ? "NChar" : "NVarchar2"));
                        Assert.Equal(DbType.String, parameter.DbType); command.Parameters.Add(parameter);
                    }
                    Assert.Equal(1, await command.ExecuteNonQueryAsync(budget.Token));
                }
                await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }

        public static IEnumerable<object[]> ComplexAssets()
        {
            foreach (var target in new[] { "netstandard2.0", "netstandard2.1", "net8.0", "net9.0", "net10.0" })
                foreach (var national in new[] { "utf8mb4", "utf16" })
                    foreach (var asynchronous in new[] { false, true })
                        foreach (var type in new[] { "NCHAR", "NVARCHAR2" }) yield return new object[] { target, national, asynchronous, type };
        }

        [Theory]
        [MemberData(nameof(ComplexAssets))]
        public async Task EveryAssetBindsNationalComplexMembersUsingDirectionalEncodings(string target, string national, bool asynchronous, string scalar)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryLocator.FindRoot(), "tests", "fixtures", "protocol", "initial-handshake-oceanbase.json")));
            var greeting = Convert.FromBase64String(fixture.RootElement.GetProperty("payloadBase64").GetString()!);
            const string value = "\ufeff中文🙂  "; var code = scalar == "NCHAR" ? (byte)208 : (byte)207;
            var input = Encoding.UTF8.GetBytes(value);
            Encoding outputEncoding = national == "utf16" ? new UnicodeEncoding(true, false, true) : new UTF8Encoding(false, true);
            var output = outputEncoding.GetBytes(value);
            byte[] Metadata(byte type) => new byte[] { 3, 100, 101, 102, 0, 0, 0, 1, 110, 1, 110, 12, 63, 0, 32, 0, 0, 0, type, 0, 0, 0, 0, 0 };
            var eof = new byte[] { 254, 0, 0, 6, 0 };
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token); var channel = new PacketChannel(peer.GetStream());
                await channel.WritePacketAsync(greeting, budget.Token); await channel.ReadPacketAsync(budget.Token);
                await channel.WritePacketAsync(SessionTimeZoneFixture.ReportFor(national), budget.Token);
                await OceanBaseConnector.UnitTests.TestInfrastructure.ServerIdentity.ReplyAsync(channel, token: budget.Token); channel.BeginCommand();
                Task<byte[]> ReadCommand() => SessionTimeZoneFixture.ReadCommand(channel, budget.Token, expectProbe: false, national: national);
                var request = await ReadCommand(); Assert.Equal(0xa1, request[0]);
                var cursor = new ByteCursor(request); cursor.ReadUInt8(); Assert.Equal(0u, cursor.ReadUInt32()); Assert.Equal(0, cursor.ReadUInt8()); Assert.Equal(1u, cursor.ReadUInt32());
                Assert.Equal("BEGIN pkg.p(?); END;", Encoding.UTF8.GetString(cursor.ReadLengthEncodedBytes())); Assert.Equal(1u, cursor.ReadUInt32());
                var encoded = new byte[] { 0, 1, 160, 0, 0, 0, 0, code, 2, 2, (byte)input.Length }.Concat(input).ToArray();
                Assert.Equal(encoded, cursor.ReadBytes(encoded.Length));
                await channel.WritePacketAsync(new byte[] { 0, 212, 0, 0, 0, 1, 0, 1, 0, 0, 0, 0, 4, 0, 0, 0, 1 }, budget.Token);
                await channel.WritePacketAsync(Metadata(253), budget.Token); await channel.WritePacketAsync(eof, budget.Token);
                await channel.WritePacketAsync(Metadata(160).Concat(new byte[] { 0, 0, 0, code }).ToArray(), budget.Token); await channel.WritePacketAsync(eof, budget.Token);
                await channel.WritePacketAsync(new byte[] { 0, 0, 2, 8, (byte)output.Length }.Concat(output).ToArray(), budget.Token);
                await channel.WritePacketAsync(eof, budget.Token); await channel.WritePacketAsync(new byte[] { 0, 1, 0, 6, 0, 0, 0 }, budget.Token);
                channel.BeginCommand();
                Assert.Equal(25, (await ReadCommand())[0]);
            });
            var context = new AssemblyLoadContext("complex-national-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled;Use Oracle Prepare Execute=true";
                if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open();
                var type = connection.GetType().GetMethod("CreateArrayType", new[] { typeof(string), assembly.GetType("OceanBaseConnector.OceanBaseTransaction")! })!
                    .Invoke(connection, new object?[] { scalar, null })!;
                var valueObject = type.GetType().GetMethod("CreateValue")!.Invoke(type, new object[] { new object?[] { value, null } })!;
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "pkg.p"; command.CommandType = CommandType.StoredProcedure;
                    var parameter = command.CreateParameter(); parameter.ParameterName = "p"; parameter.Direction = ParameterDirection.InputOutput; parameter.Value = valueObject; command.Parameters.Add(parameter);
                    Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync(budget.Token) : command.ExecuteNonQuery());
                    var values = (object?[])parameter.Value!.GetType().GetMethod("GetValues")!.Invoke(parameter.Value, null)!;
                    Assert.Equal(value, values[0]); Assert.Null(values[1]); Assert.Equal(ConnectionState.Open, connection.State);
                }
                await serving.WaitAsync(budget.Token);
            }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }
    }
}
