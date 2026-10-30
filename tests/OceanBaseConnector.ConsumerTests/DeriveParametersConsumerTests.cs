using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.ExceptionServices;
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
        [MemberData(nameof(DecimalScaleAssets))]
        public Task EveryAssetDerivesRoutineParametersWithSignedScaleAndAtomicFailure(string target, bool asynchronous, bool mysql)
            => VerifyRoutineDerivation(target, asynchronous, mysql);

        public static IEnumerable<object[]> OracleFloatDerivationAssets()
        {
            foreach (var target in ConsumerTargetMatrix.Representative)
                foreach (var asynchronous in new[] { false, true })
                    foreach (var type in new[] { "FLOAT", "NUMBER_FLOAT" })
                        yield return new object[] { target, asynchronous, type };
        }

        [Theory]
        [MemberData(nameof(OracleFloatDerivationAssets))]
        public Task EveryAssetDerivesAndExecutesOracleFloatWithoutDecimalPrecision(string target, bool asynchronous, string type)
            => VerifyRoutineDerivation(target, asynchronous, mysql: false, type);

        public static IEnumerable<object[]> OracleUnconstrainedNumberDerivationAssets()
        {
            foreach (var target in ConsumerTargetMatrix.Representative)
                foreach (var asynchronous in new[] { false, true }) yield return new object[] { target, asynchronous };
        }

        [Theory]
        [MemberData(nameof(OracleUnconstrainedNumberDerivationAssets))]
        public Task EveryAssetDerivesAndExecutesOracleUnconstrainedNumber(string target, bool asynchronous)
            => VerifyRoutineDerivation(target, asynchronous, mysql: false, unconstrainedNumber: true);

        public static IEnumerable<object[]> GeometryDerivationAssets()
        {
            foreach (var target in ConsumerTargetMatrix.Representative)
                foreach (var asynchronous in new[] { false, true })
                    foreach (var type in new[] { "geometry", "point", "linestring", "polygon", "multipoint", "multilinestring", "multipolygon", "geometrycollection", "geomcollection" })
                        yield return new object[] { target, asynchronous, type };
        }

        [Theory]
        [MemberData(nameof(GeometryDerivationAssets))]
        public Task EveryAssetDerivesGeometryRoutineArgumentsAsBinary(string target, bool asynchronous, string type)
            => VerifyRoutineDerivation(target, asynchronous, mysql: true, geometryType: type);

        private static async Task VerifyRoutineDerivation(string target, bool asynchronous, bool mysql, string? floatType = null, string? geometryType = null, bool unconstrainedNumber = false)
        {
            var variableScale = floatType != null || unconstrainedNumber;
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(budget.Token); var stream = peer.GetStream(); var channel = new PacketChannel(stream);
                await Handshake(channel, stream, "unrequested", budget.Token);
                async Task<byte[]> Read()
                {
                    byte[] packet;
                    do { channel.BeginCommand(); packet = await SessionTimeZoneFixture.ReadCommand(channel, budget.Token, expectProbe: false); } while (packet[0] == 25);
                    return packet;
                }
                byte[] Column(string name)
                {
                    using var column = new MemoryStream();
                    foreach (var field in new[] { "def", "", "", "", name, name })
                    { var bytes = Encoding.UTF8.GetBytes(field); column.WriteByte((byte)bytes.Length); column.Write(bytes); }
                    column.Write(new byte[] { 12, 45, 0, 255, 0, 0, 0, 253, 0, 0, 0, 0, 0 }); return column.ToArray();
                }
                for (var round = 0; round < 3; round++)
                {
                    var arguments = round == 1; var id = (byte)(71 + round);
                    var request = await Read(); Assert.Equal(22, request[0]);
                    var sql = Encoding.UTF8.GetString(request, 1, request.Length - 1);
                    Assert.Contains(mysql ? arguments ? "INFORMATION_SCHEMA.PARAMETERS" : "INFORMATION_SCHEMA.ROUTINES" : arguments ? "ALL_ARGUMENTS" : "ALL_PROCEDURES", sql);
                    if (!mysql && arguments)
                    {
                        Assert.Contains("WHEN IN_OUT IN ('IN/OUT', 'INOUT') THEN 'INOUT'", sql);
                        Assert.Contains("DATA_TYPE IN ('NUMBER', 'FLOAT', 'NUMBER_FLOAT') THEN DATA_PRECISION", sql);
                        Assert.Contains("NULL AS CHARACTER_LENGTH", sql);
                    }
                    var values = mysql ? arguments ? new object[] { "PROCEDURE", "db", "proc" } : new object[] { "db", "proc" }
                        : arguments ? new object[] { 1, "OWNER", "PROC" } : new object[] { "OWNER", "OWNER", "PROC" };
                    await channel.WritePacketAsync(new byte[] { 0, id, 0, 0, 0, 0, 0, (byte)values.Length, 0, 0, 0, 0 }, budget.Token);
                    for (var i = 0; i < values.Length; i++) await channel.WritePacketAsync(Column("p"), budget.Token);
                    await channel.WritePacketAsync(new byte[] { 254, 0, 0, 6, 0 }, budget.Token);
                    Assert.Equal(PreparedParameters.Execute(id, PreparedParameters.Encode(values.Select(v => new OceanBaseParameter { Value = v }).ToArray(), mysql ? OceanBaseCompatibilityMode.MySql : OceanBaseCompatibilityMode.Oracle)), await Read());
                    var names = new[] { "ROUTINE_CATALOG", "ROUTINE_SCHEMA", "ROUTINE_NAME", "PACKAGE_NAME", "OVERLOAD", "SUBPROGRAM_ID", "ROUTINE_TYPE" };
                    string?[] row = { mysql ? "db" : null, mysql ? null : "OWNER", mysql ? "proc" : "PROC", null, null, mysql ? null : "1", "PROCEDURE" };
                    if (arguments)
                    {
                        names = names.Concat(new[] { "PARAMETER_NAME", "ORDINAL_POSITION", "PARAMETER_MODE", "DATA_TYPE", "DATA_LENGTH", "NUMERIC_PRECISION", "NUMERIC_SCALE", "DATETIME_PRECISION", "TYPE_OWNER", "TYPE_NAME", "TYPE_SUBNAME", "TYPE_DECLARATION", "CHARACTER_LENGTH", "DECLARED_PRECISION", "DECLARED_SCALE" }).ToArray();
                        row = row.Concat(new[] { "amount", "1", "INOUT", geometryType ?? floatType ?? (mysql ? "decimal" : "NUMBER"), null, geometryType != null || unconstrainedNumber ? null : floatType != null ? "126" : "3",
                            geometryType != null ? null : variableScale ? "-85" : mysql ? "1" : "-2", null, null, null, null, geometryType ?? (mysql ? "decimal(3,1)" : null), null, null, null }).ToArray();
                    }
                    await channel.WritePacketAsync(new[] { (byte)names.Length }, budget.Token);
                    foreach (var name in names) await channel.WritePacketAsync(Column(name), budget.Token);
                    await channel.WritePacketAsync(new byte[] { 254, 0, 0, 6, 0 }, budget.Token);
                    for (var record = 0; record < (round == 2 ? 2 : 1); record++)
                    {
                        using var payload = new MemoryStream(); payload.WriteByte(0);
                        var nulls = new byte[(row.Length + 9) / 8];
                        for (var i = 0; i < row.Length; i++) if (row[i] == null) nulls[(i + 2) / 8] |= (byte)(1 << ((i + 2) % 8));
                        payload.Write(nulls);
                        foreach (var value in row)
                        { if (value == null) continue; var bytes = Encoding.UTF8.GetBytes(value); payload.WriteByte((byte)bytes.Length); payload.Write(bytes); }
                        await channel.WritePacketAsync(payload.ToArray(), budget.Token);
                    }
                    await channel.WritePacketAsync(new byte[] { 254, 0, 0, 6, 0 }, budget.Token);
                    if (arguments && variableScale)
                    {
                        var encoded = PreparedParameters.Encode(new[] { new OceanBaseParameter { Value = 7.2500m } }, OceanBaseCompatibilityMode.Oracle);
                        Assert.Equal(OraclePrepareExecutePacket.Build(0, "BEGIN owner.proc(?); END;", 1, encoded), await Read());
                        await channel.WritePacketAsync(new byte[] { 0, 91, 0, 0, 0, 1, 0, 1, 0, 0, 0, 0, 4, 0, 0, 0, 1 }, budget.Token);
                        var number = Column("amount"); number[^6] = 206; number[^3] = 171; number[^2] = 126;
                        await channel.WritePacketAsync(number, budget.Token);
                        await channel.WritePacketAsync(new byte[] { 254, 0, 0, 6, 0 }, budget.Token);
                        await channel.WritePacketAsync(number, budget.Token);
                        await channel.WritePacketAsync(new byte[] { 254, 0, 0, 6, 0 }, budget.Token);
                        await channel.WritePacketAsync(new byte[] { 0, 0, 6, 56, 46, 53, 48, 48, 48 }, budget.Token);
                        await channel.WritePacketAsync(new byte[] { 254, 0, 0, 6, 0 }, budget.Token);
                        await channel.WritePacketAsync(new byte[] { 0, 0, 0, 6, 0, 0, 0 }, budget.Token);
                    }
                }
                channel.BeginCommand();
                Assert.Equal(new byte[] { 25, 73, 0, 0, 0 }, await channel.ReadPacketAsync(budget.Token));
                await ObserveClose(stream, budget.Token);
            });
            var context = new AssemblyLoadContext("derive-parameters-" + target, isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(RepositoryLocator.GetProductAssemblyPath(target)); var connectionType = assembly.GetType("OceanBaseConnector.OceanBaseConnection")!;
                using var connection = (DbConnection)Activator.CreateInstance(connectionType)!;
                connection.ConnectionString = $"Server=127.0.0.1;Port={((IPEndPoint)listener.LocalEndpoint).Port};User ID=u;TLS Mode=Disabled;Use Oracle Prepare Execute={variableScale}";
                if (asynchronous) await connection.OpenAsync(budget.Token); else connection.Open();
                if (mysql)
                {
                    // Internal authenticated-mode fixture; no real MySQL-mode server is involved.
                    var session = connectionType.GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection)!;
                    var field = session.GetType().GetField("_info", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var info = field.GetValue(session)!; var infoType = info.GetType(); object Fact(string name) => infoType.GetProperty(name)!.GetValue(info)!;
                    field.SetValue(session, Activator.CreateInstance(infoType, Fact("ServerVersion"), Fact("ConnectionId"), Enum.Parse(assembly.GetType("OceanBaseConnector.OceanBaseCompatibilityMode")!, "MySql"), Fact("Capabilities"), Fact("ServerStatus"), Fact("ObCapabilityFlag"), Fact("SessionVariables")));
                }
                using var command = connection.CreateCommand(); command.CommandType = CommandType.StoredProcedure; command.CommandText = mysql ? "db.proc" : "owner.proc";
                var original = command.CreateParameter(); original.ParameterName = "old"; original.Value = 42; command.Parameters.Add(original);
                async Task Derive()
                {
                    try
                    {
                        var method = command.GetType().GetMethod(asynchronous ? "DeriveParametersAsync" : "DeriveParameters", asynchronous ? new[] { typeof(CancellationToken) } : new[] { typeof(string) })!;
                        var result = method.Invoke(command, asynchronous ? new object?[] { budget.Token } : new object?[] { null });
                        if (asynchronous) await (Task)result!;
                    }
                    catch (TargetInvocationException error) when (error.InnerException != null) { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
                }
                await Derive(); var parameter = Assert.IsAssignableFrom<DbParameter>(command.Parameters[0]);
                Assert.NotSame(original, parameter); Assert.Equal("amount", parameter.ParameterName); Assert.Equal(ParameterDirection.InputOutput, parameter.Direction);
                Assert.Equal(variableScale || geometryType != null ? 0 : 3, parameter.Precision);
                Assert.Equal(variableScale || geometryType != null ? (int?)null : mysql ? 1 : -2, parameter.GetType().GetProperty("DecimalScale")!.GetValue(parameter)); Assert.Equal(DBNull.Value, parameter.Value);
                if (geometryType != null)
                {
                    Assert.Equal(DbType.Binary, parameter.DbType); Assert.Equal(0, parameter.Size);
                    Assert.Equal("Binary", parameter.GetType().GetProperty("OceanBaseDbType")!.GetValue(parameter)!.ToString());
                    using var dataTypes = connection.GetSchema("DataTypes");
                    var geometry = Assert.Single(dataTypes.Rows.Cast<DataRow>(), r => (string)r["TypeName"] == "GEOMETRY");
                    Assert.True((bool)geometry["IsSupportedForParameters"]); Assert.True((bool)geometry["IsSupportedForResults"]);
                }
                if (variableScale)
                {
                    parameter.Value = 7.2500m;
                    Assert.Equal(0, asynchronous ? await command.ExecuteNonQueryAsync(budget.Token) : command.ExecuteNonQuery());
                    Assert.Equal("OceanBaseConnector.OceanBaseDecimal", parameter.Value!.GetType().FullName);
                    Assert.Equal("8.5000", parameter.Value.ToString());
                }
                await Assert.ThrowsAsync<NotSupportedException>(Derive);
                Assert.Same(parameter, Assert.Single(command.Parameters.Cast<DbParameter>())); Assert.Equal(ConnectionState.Open, connection.State);
                connection.Close(); await serving.WaitAsync(budget.Token);
            }
            catch (Exception error) when (serving.IsFaulted) { throw new AggregateException(error, serving.Exception!); }
            finally { budget.Cancel(); listener.Stop(); context.Unload(); }
        }
    }
}
