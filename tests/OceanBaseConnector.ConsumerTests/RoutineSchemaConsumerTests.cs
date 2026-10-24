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
using Xunit;

namespace OceanBaseConnector.ConsumerTests
{
    public sealed partial class Ob20ConsumerTests
    {
        public static IEnumerable<object[]> RoutineSchemaAssets()
        {
            foreach (var target in new[] { "netstandard2.0", "netstandard2.1", "net8.0", "net9.0", "net10.0" })
                foreach (var asynchronous in target.StartsWith("netstandard", StringComparison.Ordinal) ? new[] { false } : new[] { false, true })
                    foreach (var mysql in new[] { false, true })
                        foreach (var parameters in new[] { false, true }) yield return new object[] { target, asynchronous, mysql, parameters };
        }

        [Theory]
        [MemberData(nameof(RoutineSchemaAssets))]
        public async Task RoutineSchemaEveryAssetPreservesDirectoryTypesAndIdentity(string target, bool asynchronous, bool mysql, bool parameters)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var names = new[] { "ROUTINE_CATALOG", "ROUTINE_SCHEMA", "ROUTINE_NAME", "PACKAGE_NAME", "OVERLOAD", "SUBPROGRAM_ID", "ROUTINE_TYPE" };
            string?[] values = { mysql ? "Db目标" : null, mysql ? null : "Owner目标", "Routine目标", mysql ? null : "Pkg目标", mysql ? null : "2", mysql ? null : "7", mysql ? "FUNCTION" : null };
            if (parameters)
            {
                names = names.Concat(new[] { "PARAMETER_NAME", "ORDINAL_POSITION", "PARAMETER_MODE", "DATA_TYPE", "DATA_LENGTH", "NUMERIC_PRECISION", "NUMERIC_SCALE", "DATETIME_PRECISION", "TYPE_OWNER", "TYPE_NAME", "TYPE_SUBNAME" }).ToArray();
                values = values.Concat(new string?[] { null, "0", "RETURN", "NUMBER", "4294967295", "38", "-2", null, null, null, null }).ToArray();
            }
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token); var stream = peer.GetStream(); var channel = new PacketChannel(stream);
                await Handshake(channel, stream, "unrequested", budget.Token);
                var request = await channel.ReadPacketAsync(budget.Token); Assert.Equal(3, request[0]);
                var sql = Encoding.UTF8.GetString(request, 1, request.Length - 1);
                Assert.Contains(mysql ? parameters ? "INFORMATION_SCHEMA.PARAMETERS" : "INFORMATION_SCHEMA.ROUTINES" : parameters ? "ALL_ARGUMENTS" : "ALL_PROCEDURES", sql);
                await channel.WritePacketAsync(new[] { (byte)names.Length }, budget.Token);
                foreach (var name in names)
                {
                    using var column = new MemoryStream();
                    foreach (var field in new[] { "def", "", "", "", name, name })
                    { var bytes = Encoding.UTF8.GetBytes(field); column.WriteByte((byte)bytes.Length); column.Write(bytes); }
                    column.Write(new byte[] { 12, 45, 0, 255, 0, 0, 0, 253, 0, 0, 0, 0, 0 });
                    await channel.WritePacketAsync(column.ToArray(), budget.Token);
                }
                await channel.WritePacketAsync(new byte[] { 254, 0, 0, 2, 0 }, budget.Token);
                using var row = new MemoryStream();
                foreach (var value in values)
                {
                    if (value == null) { row.WriteByte(251); continue; }
                    var bytes = Encoding.UTF8.GetBytes(value); row.WriteByte((byte)bytes.Length); row.Write(bytes);
                }
                await channel.WritePacketAsync(row.ToArray(), budget.Token); await channel.WritePacketAsync(new byte[] { 254, 0, 0, 2, 0 }, budget.Token);
                await ObserveClose(stream, budget.Token);
            });
            var context = new AssemblyLoadContext("routine-schema-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target)); var type = assembly.GetType("OceanBaseConnector.OceanBaseConnection")!;
                using var connection = (DbConnection)Activator.CreateInstance(type)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled";
                if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open();
                if (mysql)
                {
                    // Explicit authenticated-mode fixture; this does not prove A05 MySQL identification.
                    var session = type.GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection)!;
                    var field = session.GetType().GetField("_info", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var info = field.GetValue(session)!; var infoType = info.GetType(); object Fact(string name) => infoType.GetProperty(name)!.GetValue(info)!;
                    field.SetValue(session, Activator.CreateInstance(infoType, Fact("ServerVersion"), Fact("ConnectionId"), Enum.Parse(assembly.GetType("OceanBaseConnector.OceanBaseCompatibilityMode")!, "MySql"), Fact("Capabilities"), Fact("ServerStatus"), Fact("ObCapabilityFlag"), Fact("SessionVariables")));
                }
                Assert.Equal(mysql ? 18 : 20, connection.GetSchema().Rows.Count);
                var collection = parameters ? "ProcedureParameters" : "Procedures";
                using var table = asynchronous ? await connection.GetSchemaAsync(collection, budget.Token) : connection.GetSchema(collection);
                Assert.Equal(names, table.Columns.Cast<DataColumn>().Select(x => x.ColumnName)); var row = Assert.Single(table.Rows.Cast<DataRow>());
                Assert.Equal(mysql ? DBNull.Value : 7, row["SUBPROGRAM_ID"]); Assert.Equal(mysql ? "FUNCTION" : DBNull.Value, row["ROUTINE_TYPE"]);
                Assert.Equal(mysql ? DBNull.Value : "Pkg目标", row["PACKAGE_NAME"]); Assert.Equal("Routine目标", row["ROUTINE_NAME"]);
                if (parameters) { Assert.Equal(DBNull.Value, row["PARAMETER_NAME"]); Assert.Equal(0, row["ORDINAL_POSITION"]); Assert.Equal("RETURN", row["PARAMETER_MODE"]); Assert.Equal(4294967295L, row["DATA_LENGTH"]); }
                Assert.Equal(ConnectionState.Open, connection.State); connection.Close(); await serving.WaitAsync(budget.Token);
            }
            catch (Exception error) when (serving.IsFaulted) { throw new AggregateException(error, serving.Exception!); }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }
    }
}
