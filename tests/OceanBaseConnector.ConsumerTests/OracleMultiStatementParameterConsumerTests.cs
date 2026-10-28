using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Net;
using System.Net.Sockets;
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
        public static IEnumerable<object[]> OracleMultiParameterAssets()
        {
            foreach (var target in ConsumerTargetMatrix.Representative)
                foreach (var asynchronous in new[] { false, true })
                    foreach (var ob20 in new[] { false, true })
                        yield return new object[] { target, asynchronous, ob20 };
        }

        [Theory]
        [MemberData(nameof(OracleMultiParameterAssets))]
        public Task PublicAssetsBindOracleMultiStatementInputsWithoutServerPreparation(string target, bool asynchronous, bool ob20)
            => ScalarMultiStatementConsumer(target, asynchronous, ob20, mysql: false);

        [Theory]
        [MemberData(nameof(OracleMultiParameterAssets))]
        public Task PublicAssetsBindMySqlMultiStatementInputsWithoutServerPreparation(string target, bool asynchronous, bool ob20)
            => ScalarMultiStatementConsumer(target, asynchronous, ob20, mysql: true);

        private static async Task ScalarMultiStatementConsumer(string target, bool asynchronous, bool ob20, bool mysql)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token);
                var stream = peer.GetStream(); var channel = new PacketChannel(stream);
                await Handshake(channel, stream, ob20 ? "enabled" : "unrequested", budget.Token, mysql: mysql);
                if (mysql)
                {
                    channel.BeginCommand();
                    var state = await ReadRequest(stream, channel, ob20, 1, budget.Token);
                    Assert.Equal(3, state[0]);
                    Assert.Equal("SELECT @@character_set_client, @@character_set_connection, @@collation_connection, @@sql_mode",
                        Encoding.UTF8.GetString(state, 1, state.Length - 1));
                    var values = TextResult("utf8mb4", "utf8mb4", "utf8mb4_bin", "NO_BACKSLASH_ESCAPES");
                    await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, 1, 1, values) : values, budget.Token);
                }
                for (uint execution = 1; execution <= 2; execution++)
                {
                    var requestId = execution + (ob20 || mysql ? 1u : 0u);
                    channel.BeginCommand();
                    var request = await ReadRequest(stream, channel, ob20, requestId, budget.Token);
                    Assert.Equal(3, request[0]);
                    Assert.Equal(execution == 1
                        ? mysql
                            ? "insert into t values ((REPLACE('x','x',_utf8mb4 X'71756F7465275CE4B8ADE696873B203A70'))); update t set s=(REPLACE('x','x',_utf8mb4 X'71756F7465275CE4B8ADE696873B203A70'))"
                            : "insert into t values ((CAST('quote''\\中文; :p' AS VARCHAR2(4000)))); update t set s=(CAST('quote''\\中文; :p' AS VARCHAR2(4000)))"
                        : "after public multi parameters", Encoding.UTF8.GetString(request, 1, request.Length - 1));
                    using var response = new MemoryStream();
                    response.Write(Frame(1, execution == 1 ? new byte[] { 0, 2, 0, 14, 0, 0, 0 } : Report(false, 1)));
                    if (execution == 1) response.Write(Frame(2, Report(false, 3)));
                    var bytes = response.ToArray();
                    await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, requestId, 1, bytes) : bytes, budget.Token);
                }
                await ObserveClose(stream, budget.Token);
            });
            var context = new AssemblyLoadContext("scalar-multi-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled;Allow Multi Statements=true;Use OB20={ob20}";
                if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = mysql ? "insert into t values (?); update t set s=?" : "insert into t values (:p); update t set s=:P";
                var parameter = command.CreateParameter(); parameter.ParameterName = "p"; parameter.Value = "quote'\\中文; :p";
                command.Parameters.Add(parameter);
                if (mysql) { var second = command.CreateParameter(); second.Value = parameter.Value; command.Parameters.Add(second); }
                Assert.Equal(5, asynchronous ? await command.ExecuteNonQueryAsync(budget.Token) : command.ExecuteNonQuery());
                Assert.Equal("quote'\\中文; :p", parameter.Value);
                command.Parameters.Clear(); command.CommandText = "after public multi parameters";
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync(budget.Token) : command.ExecuteNonQuery());
                if (asynchronous) await connection.CloseAsync(); else connection.Close();
                await serving.WaitAsync(budget.Token);
            }
            catch { if (serving.IsFaulted) await serving; throw; }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }
    }
}
