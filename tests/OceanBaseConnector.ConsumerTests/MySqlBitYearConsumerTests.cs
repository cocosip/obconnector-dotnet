using System;
using System.Data.Common;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Loader;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    public sealed partial class Ob20ConsumerTests
    {
        [Theory]
        [MemberData(nameof(OracleMultiParameterAssets))]
        public async Task PublicAssetsBindMySqlBitYearMultiStatements(string target, bool asynchronous, bool ob20)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token);
                var stream = peer.GetStream(); var channel = new PacketChannel(stream);
                await Handshake(channel, stream, ob20 ? "enabled" : "unrequested", budget.Token, mysql: true);
                for (uint requestId = 1; requestId <= 2; requestId++)
                {
                    channel.BeginCommand(); var request = await ReadRequest(stream, channel, ob20, requestId, budget.Token);
                    Assert.Equal(3, request[0]);
                    Assert.Equal(requestId == 1 ? "INSERT INTO t VALUES ((18446744073709551615),(2155)); UPDATE t SET b=(NULL),y=(NULL)"
                        : "after public bit year multi", Encoding.UTF8.GetString(request, 1, request.Length - 1));
                    using var response = new MemoryStream();
                    response.Write(Frame(1, requestId == 1 ? new byte[] { 0, 2, 0, 10, 0, 0, 0 } : Report(false, 1)));
                    if (requestId == 1) response.Write(Frame(2, Report(false, 3)));
                    var bytes = response.ToArray();
                    await stream.WriteAsync(ob20 ? Ob20Packet.Build(1234, requestId, 1, bytes) : bytes, budget.Token);
                }
                await ObserveClose(stream, budget.Token);
            });
            var context = new AssemblyLoadContext("bit-year-multi-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target));
                using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled;Allow Multi Statements=true;Use OB20={ob20}";
                if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open();
                using var command = connection.CreateCommand(); command.CommandText = "INSERT INTO t VALUES (?,?); UPDATE t SET b=?,y=?";
                DbParameter Add(string type, object value, int size = 0, byte precision = 0)
                {
                    var parameter = command.CreateParameter(); parameter.Value = value; parameter.Size = size; parameter.Precision = precision;
                    parameter.GetType().GetProperty("OceanBaseDbType")!.SetValue(parameter, Enum.Parse(assembly.GetType("OceanBaseConnector.OceanBaseDbType")!, type));
                    command.Parameters.Add(parameter); return parameter;
                }
                var bits = Add("Bit", 2UL, size: 1); var year = Add("Year", (ushort)2155, precision: 4);
                Add("Bit", DBNull.Value, size: 64); Add("Year", DBNull.Value, precision: 4);
                Func<Task> execute = () => asynchronous ? command.ExecuteNonQueryAsync(budget.Token) : Task.FromResult(command.ExecuteNonQuery());
                await Assert.ThrowsAsync<ArgumentException>(execute);
                bits.Value = ulong.MaxValue; bits.Size = 64; year.Value = (ushort)1900;
                await Assert.ThrowsAsync<ArgumentException>(execute); year.Value = (ushort)2155;
                Assert.Equal(5, asynchronous ? await command.ExecuteNonQueryAsync(budget.Token) : command.ExecuteNonQuery());
                Assert.Equal(ulong.MaxValue, bits.Value); Assert.Equal((ushort)2155, year.Value);
                command.Parameters.Clear(); command.CommandText = "after public bit year multi";
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync(budget.Token) : command.ExecuteNonQuery());
                if (asynchronous) await connection.CloseAsync(); else connection.Close();
                await serving.WaitAsync(budget.Token);
            }
            catch { if (serving.IsFaulted) await serving; throw; }
            finally
            {
                budget.Cancel(); listener.Stop(); try { await serving; } catch { }
                context.Unload();
            }
        }
    }
}
