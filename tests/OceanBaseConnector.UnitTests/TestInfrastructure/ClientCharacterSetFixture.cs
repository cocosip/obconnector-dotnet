using System;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests.TestInfrastructure
{
    internal static class ClientCharacterSetFixture
    {
        internal static readonly (string Name, string Text, string Hex)[] Samples =
        {
            ("utf8mb3", "中é", "E4B8ADC3A9"), ("ascii", "ABC", "414243"),
            ("latin1", "é€", "E980"), ("gbk", "中文", "D6D0CEC4"),
            ("gb18030", "ḿ\ue7c7😀", "A8BC8135F4379439FC36"),
            ("gb18030_2022", "龴\ue81e\ufe10", "FE5982359037A6D9"),
            ("dec8", "¤ŒŸ", "A8D7DD"), ("tis620", "กฺ฿", "A1DADF"),
            ("sjis", "日本語", "93FA967B8CEA"), ("big5", "中•∼", "A4A4A145A1E3"),
            ("hkscs", "\uf308Ā中", "88458856A4A4"), ("hkscs31", "\U0002010cĀ中", "88458856A4A4"),
        };
        private const string StateSql = "SELECT @@character_set_client, @@character_set_connection, @@character_set_results, @@collation_connection";
        private const string Result = "患者😀";

        internal static async Task Verify(Type connectionType, byte[] greeting, int sample, bool oracle, bool asynchronous, bool pooling = false, bool deprecated = false, bool json = false)
        {
            var (name, text, hex) = Samples[sample]; var bytes = Convert.FromHexString(hex);
            if (json) { text = "{\"x\":\"" + text + "\"}"; bytes = Join("{\"x\":\"", bytes, "\"}"); }
            var result = json ? "{\"x\":\"" + Result + "\"}" : Result;
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            using var connection = (DbConnection)Activator.CreateInstance(connectionType)!;
            connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled;Character Set={name};Pooling={pooling};Max Pool Size=1;Connection Timeout=5;Command Timeout=5";
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token);
                var channel = new PacketChannel(peer.GetStream());
                var hello = (byte[])greeting.Clone(); if (deprecated) hello[Array.IndexOf(hello, (byte)0, 1) + 20] |= 1;
                await channel.WritePacketAsync(hello, budget.Token);
                var auth = await channel.ReadPacketAsync(budget.Token); Assert.Equal(45, auth[8]);
                await channel.WritePacketAsync(Ok(oracle), budget.Token);
                await ServerIdentity.ReplyAsync(channel, oracle: oracle, deprecated: deprecated, token: budget.Token);
                await Initialize(channel, name, oracle, deprecated, budget.Token);
                var request = await Read(channel, oracle, name, deprecated, budget.Token);
                Assert.Equal(3, request[0]); Assert.Equal(Join("SELECT '", bytes, "'"), request.AsSpan(1).ToArray());
                await Row(channel, oracle, deprecated, binary: false, budget.Token, json);
                request = await Read(channel, oracle, name, deprecated, budget.Token);
                Assert.Equal(22, request[0]); Assert.Equal(Join("SELECT ? /* ", bytes, " */"), request.AsSpan(1).ToArray());
                await channel.WritePacketAsync(new byte[] { 0, 71, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0 }, budget.Token);
                await channel.WritePacketAsync(Column(), budget.Token);
                if (!deprecated) await channel.WritePacketAsync(End(oracle, false), budget.Token);
                request = await Read(channel, oracle, name, deprecated, budget.Token);
                Assert.Equal(23, request[0]); Assert.Equal(new byte[] { 0, 1, 253, 0 }, request.AsSpan(10, 4).ToArray());
                Assert.Equal(bytes, request.AsSpan(15).ToArray()); Assert.Equal(bytes.Length, request[14]);
                await Row(channel, oracle, deprecated, binary: true, budget.Token, json);
                if (pooling)
                {
                    for (var round = 0; round < 2; round++)
                    {
                        request = await Read(channel, oracle, name, deprecated, budget.Token); Assert.Equal(0x1f, request[0]);
                        await channel.WritePacketAsync(Ok(oracle), budget.Token);
                        await Initialize(channel, name, oracle, deprecated, budget.Token);
                        if (oracle)
                        {
                            request = await Read(channel, oracle, name, deprecated, budget.Token);
                            Assert.Equal("set autocommit = 1", Encoding.ASCII.GetString(request, 1, request.Length - 1));
                            await channel.WritePacketAsync(Ok(oracle), budget.Token);
                        }
                        if (round == 0)
                        {
                            request = await Read(channel, oracle, name, deprecated, budget.Token); Assert.Equal("SELECT 1", Encoding.ASCII.GetString(request, 1, request.Length - 1));
                            await Row(channel, oracle, deprecated, false, budget.Token);
                        }
                    }
                }
            });
            Exception? failure = null;
            try
            {
                if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT '" + text + "'";
                    Assert.Equal(result, asynchronous ? await command.ExecuteScalarAsync(budget.Token) : command.ExecuteScalar());
                    command.CommandText = "SELECT " + (oracle ? ":p" : "?") + " /* " + text + " */";
                    var p = command.CreateParameter(); p.ParameterName = "p"; p.Value = text; command.Parameters.Add(p);
                    if (json)
                    {
                        var property = p.GetType().GetProperty("OceanBaseDbType")!;
                        property.SetValue(p, Enum.Parse(Nullable.GetUnderlyingType(property.PropertyType)!, "Json"));
                    }
                    Assert.Equal(result, asynchronous ? await command.ExecuteScalarAsync(budget.Token) : command.ExecuteScalar());
                }
                if (asynchronous) await connection.CloseAsync(); else connection.Close();
                if (pooling)
                {
                    if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open();
                    using var command = connection.CreateCommand(); command.CommandText = "SELECT 1";
                    Assert.Equal(Result, asynchronous ? await command.ExecuteScalarAsync(budget.Token) : command.ExecuteScalar());
                    if (asynchronous) await connection.CloseAsync(); else connection.Close();
                }
                await serving.WaitAsync(budget.Token);
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                connection.Close();
                if (failure != null) budget.Cancel();
                try { await serving.WaitAsync(TimeSpan.FromSeconds(5)); } catch when (failure != null) { }
            }
        }

        internal static async Task Initialize(PacketChannel channel, string name, bool oracle, bool deprecated, CancellationToken token, string? client = null, string results = "utf8mb4")
        {
            foreach (var sql in new[] { "SET NAMES " + name, "SET character_set_results=utf8mb4" })
            {
                channel.BeginCommand(); var request = await channel.ReadPacketAsync(token);
                Assert.Equal(sql, Encoding.ASCII.GetString(request, 1, request.Length - 1));
                await channel.WritePacketAsync(Ok(oracle), token);
            }
            channel.BeginCommand(); var state = await channel.ReadPacketAsync(token);
            Assert.Equal(StateSql, Encoding.ASCII.GetString(state, 1, state.Length - 1));
            await State(channel, name, oracle, deprecated, token, client, results);
        }

        internal static async Task State(PacketChannel channel, string name, bool oracle, bool deprecated, CancellationToken token, string? client = null, string results = "utf8mb4", bool extra = false)
            => await Values(channel, new[] { client ?? name, name, results, name + "_bin" }, oracle, deprecated, token, extra);

        internal static async Task<byte[]> Read(PacketChannel channel, bool oracle, string name, bool deprecated, CancellationToken token)
        {
            while (true)
            {
                channel.BeginCommand(); var request = await channel.ReadPacketAsync(token);
                if (request[0] == 25) continue;
                if (request[0] == 14) { await channel.WritePacketAsync(Ok(oracle), token); continue; }
                if (request[0] != 3) return request;
                var sql = Encoding.ASCII.GetString(request, 1, request.Length - 1);
                if (sql == "SELECT SESSIONTIMEZONE FROM DUAL") await Values(channel, new[] { "+00:00" }, oracle, deprecated, token);
                else if (sql == "SELECT @@NCHARACTER_SET_CONNECTION, @@CHARACTER_SET_RESULTS, @@CHARACTER_SET_CONNECTION FROM DUAL")
                    await Values(channel, new[] { "utf8mb4", "utf8mb4", name }, oracle, deprecated, token);
                else return request;
            }
        }

        internal static async Task Values(PacketChannel channel, string[] values, bool oracle, bool deprecated, CancellationToken token, bool extra = false)
        {
            await channel.WritePacketAsync(new[] { (byte)values.Length }, token);
            foreach (var _ in values) await channel.WritePacketAsync(Column(), token);
            if (!deprecated) await channel.WritePacketAsync(End(oracle, false), token);
            using var row = new MemoryStream();
            foreach (var value in values)
            { var bytes = Encoding.UTF8.GetBytes(value); row.WriteByte((byte)bytes.Length); row.Write(bytes); }
            await channel.WritePacketAsync(row.ToArray(), token);
            // The extra row makes the client abort immediately, so do not race that close with EOF.
            if (extra) await channel.WritePacketAsync(row.ToArray(), token);
            else await channel.WritePacketAsync(End(oracle, deprecated), token);
        }

        private static async Task Row(PacketChannel channel, bool oracle, bool deprecated, bool binary, CancellationToken token, bool json = false)
        {
            await channel.WritePacketAsync(new byte[] { 1 }, token); await channel.WritePacketAsync(Column(json), token);
            if (!deprecated) await channel.WritePacketAsync(End(oracle, false), token);
            var bytes = Encoding.UTF8.GetBytes(json ? "{\"x\":\"" + Result + "\"}" : Result); var row = new byte[bytes.Length + (binary ? 3 : 1)];
            row[binary ? 2 : 0] = (byte)bytes.Length; Buffer.BlockCopy(bytes, 0, row, binary ? 3 : 1, bytes.Length);
            await channel.WritePacketAsync(row, token); await channel.WritePacketAsync(End(oracle, deprecated), token);
        }
        private static byte[] Join(string prefix, byte[] bytes, string suffix)
        { using var s = new MemoryStream(); s.Write(Encoding.ASCII.GetBytes(prefix)); s.Write(bytes); s.Write(Encoding.ASCII.GetBytes(suffix)); return s.ToArray(); }
        internal static byte[] Ok(bool oracle) => new byte[] { 0, 0, 0, oracle ? (byte)6 : (byte)2, 0, 0, 0 };
        private static byte[] End(bool oracle, bool deprecated) => deprecated ? new byte[] { 254, 0, 0, oracle ? (byte)6 : (byte)2, 0, 0, 0 } : new byte[] { 254, 0, 0, oracle ? (byte)6 : (byte)2, 0 };
        internal static byte[] Column(bool json = false)
        {
            using var s = new MemoryStream();
            foreach (var value in new[] { "def", "", "", "", "患者", "患者" })
            { var bytes = Encoding.UTF8.GetBytes(value); s.WriteByte((byte)bytes.Length); s.Write(bytes); }
            s.Write(new byte[] { 12, json ? (byte)63 : (byte)45, 0, 255, 0, 0, 0, json ? (byte)245 : (byte)253, 0, 0, 0, 0, 0 }); return s.ToArray();
        }
    }
}
