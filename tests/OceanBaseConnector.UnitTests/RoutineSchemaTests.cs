using System;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        private static readonly string[] RoutineNames = { "ROUTINE_CATALOG", "ROUTINE_SCHEMA", "ROUTINE_NAME", "PACKAGE_NAME", "OVERLOAD", "SUBPROGRAM_ID", "ROUTINE_TYPE" };
        private static readonly string[] RoutineParameterNames = RoutineNames.Concat(new[] { "PARAMETER_NAME", "ORDINAL_POSITION", "PARAMETER_MODE", "DATA_TYPE", "DATA_LENGTH", "NUMERIC_PRECISION", "NUMERIC_SCALE", "DATETIME_PRECISION", "TYPE_OWNER", "TYPE_NAME", "TYPE_SUBNAME" }).ToArray();

        private static byte[][] RoutinePackets(bool parameters, bool mysql, bool binary = false, string? fault = null, bool nativeNumbers = false)
        {
            var names = (string[])(parameters ? RoutineParameterNames : RoutineNames).Clone();
            if (fault == "name") names[2] = "WRONG_NAME";
            var packets = new[] { new[] { (byte)names.Length } }.Concat(names.Select((x, i) => Column(x, nativeNumbers && (i == 5 || i == 8 || i >= 11 && i <= 14) ? (byte)246 : (byte)253))).Append(Eof()).ToList();
            for (var record = 0; record < 2; record++)
            {
                string?[] values = { mysql ? "Db目标" : null, mysql ? null : "Owner目标", "Mixed_%'目标", mysql ? null : "Pkg目标", mysql ? null : (record + 2).ToString(), mysql ? null : (record + 7).ToString(), mysql ? record == 0 ? "FUNCTION" : "PROCEDURE" : null };
                if (parameters) values = values.Concat(new[] { record == 0 ? null : "value目标", record == 0 ? "0" : "1", record == 0 ? "RETURN" : "INOUT", "NUMBER", "4294967295", "38", "-2", null, null, null, null }).ToArray();
                if (parameters && !mysql && record == 1)
                {
                    values[10] = "PL/SQL RECORD"; values[11] = values[12] = values[13] = null;
                    values[15] = "Owner目标"; values[16] = "Pkg目标"; values[17] = "Record目标";
                }
                if (fault == "fraction") values[5] = "7.1";
                if (fault == "overflow") values[5] = "2147483648";
                using var row = new MemoryStream();
                if (binary)
                {
                    row.WriteByte(0); var nulls = new byte[(names.Length + 9) / 8];
                    for (var i = 0; i < values.Length; i++) if (values[i] == null) nulls[(i + 2) / 8] |= (byte)(1 << ((i + 2) % 8));
                    row.Write(nulls);
                }
                foreach (var value in values)
                {
                    if (value == null) { if (!binary) row.WriteByte(251); continue; }
                    var bytes = Encoding.UTF8.GetBytes(value); row.WriteByte((byte)bytes.Length); row.Write(bytes);
                }
                packets.Add(row.ToArray());
            }
            packets.Add(Eof()); return packets.ToArray();
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(true, true, false)]
        [InlineData(false, false, true)]
        [InlineData(true, false, true)]
        [InlineData(false, true, true)]
        [InlineData(true, true, true)]
        public async Task RoutineSchemaReadsBothDictionariesWithoutLosingIdentity(bool asynchronous, bool mysql, bool parameters)
        {
            var server = Server(async channel =>
            {
                var request = await StatementCommand(channel, 3); var sql = Encoding.UTF8.GetString(request, 1, request.Length - 1);
                Assert.Contains(mysql ? parameters ? "INFORMATION_SCHEMA.PARAMETERS" : "INFORMATION_SCHEMA.ROUTINES" : parameters ? "ALL_ARGUMENTS" : "ALL_PROCEDURES", sql);
                if (!mysql)
                {
                    Assert.Contains("CAST(OVERLOAD AS VARCHAR2(40)) AS OVERLOAD", sql);
                    if (parameters) { Assert.Contains("DATA_LEVEL = 0", sql); Assert.Contains("DATA_TYPE IS NOT NULL", sql); Assert.Contains("POSITION = 0", sql); Assert.Contains("'IN/OUT'", sql); }
                    else { Assert.Contains("PROCEDURE_NAME IS NOT NULL", sql); Assert.Contains("'PACKAGE'", sql); }
                }
                foreach (var packet in RoutinePackets(parameters, mysql)) await channel.WritePacketAsync(packet);
                await StatementCommand(channel, 3); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); if (mysql) UseInternalMySqlFixture(connection);
                var name = parameters ? "ProcedureParameters" : "Procedures";
                var table = asynchronous ? await connection.GetSchemaAsync(name) : connection.GetSchema(name);
                Assert.Equal(parameters ? RoutineParameterNames : RoutineNames, table.Columns.Cast<DataColumn>().Select(x => x.ColumnName));
                Assert.Equal(2, table.Rows.Count); Assert.Equal(mysql ? "Db目标" : DBNull.Value, table.Rows[0][0]);
                Assert.Equal(mysql ? DBNull.Value : "Owner目标", table.Rows[0][1]); Assert.Equal("Mixed_%'目标", table.Rows[0][2]);
                Assert.Equal(mysql ? DBNull.Value : "Pkg目标", table.Rows[0][3]);
                Assert.Equal(mysql ? DBNull.Value : 7, table.Rows[0][5]); Assert.Equal(mysql ? DBNull.Value : 8, table.Rows[1][5]);
                Assert.Equal(typeof(int), table.Columns["SUBPROGRAM_ID"]!.DataType);
                if (parameters)
                {
                    Assert.Equal(DBNull.Value, table.Rows[0]["PARAMETER_NAME"]); Assert.Equal(0, table.Rows[0]["ORDINAL_POSITION"]);
                    Assert.Equal("RETURN", table.Rows[0]["PARAMETER_MODE"]); Assert.Equal("INOUT", table.Rows[1]["PARAMETER_MODE"]);
                    Assert.Equal(4294967295L, table.Rows[0]["DATA_LENGTH"]); Assert.Equal(-2, table.Rows[0]["NUMERIC_SCALE"]);
                    Assert.Equal(DBNull.Value, table.Rows[0]["TYPE_OWNER"]); Assert.Equal(DBNull.Value, table.Rows[0]["DATETIME_PRECISION"]);
                    if (!mysql) { Assert.Equal("Owner目标", table.Rows[1]["TYPE_OWNER"]); Assert.Equal("Pkg目标", table.Rows[1]["TYPE_NAME"]); Assert.Equal("Record目标", table.Rows[1]["TYPE_SUBNAME"]); }
                }
                using var command = connection.CreateCommand(); command.CommandText = "after"; Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(false, false, true)]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(true, true, false)]
        public async Task RoutineSchemaRestrictionsUseExactBoundValues(bool mysql, bool parameters, bool routineType)
        {
            const string hostile = "Mixed_%' OR 1=1 --目标";
            var restrictions = new[] { mysql ? "Db目标" : null, mysql ? null : "Owner目标", hostile, parameters ? "value目标" : mysql || routineType ? "FUNCTION" : null, mysql ? null : "Pkg目标" };
            var server = Server(async channel =>
            {
                var prepare = await StatementCommand(channel, 22); var sql = Encoding.UTF8.GetString(prepare, 1, prepare.Length - 1);
                Assert.DoesNotContain(hostile, sql); Assert.DoesNotContain("LIKE", sql);
                if (mysql)
                {
                    Assert.Contains(parameters ? " AND SPECIFIC_SCHEMA = ?" : " AND ROUTINE_SCHEMA = ?", sql);
                    Assert.Contains(parameters ? " AND SPECIFIC_NAME = ?" : " AND ROUTINE_NAME = ?", sql);
                    Assert.Contains(parameters ? " AND PARAMETER_NAME = ?" : " AND ROUTINE_TYPE = ?", sql);
                }
                else
                {
                    Assert.Contains(" AND OWNER = ?", sql);
                    Assert.Contains(parameters ? " AND OBJECT_NAME = ?" : " AND CASE WHEN OBJECT_TYPE = 'PACKAGE' THEN PROCEDURE_NAME ELSE OBJECT_NAME END = ?", sql);
                    Assert.Contains(parameters ? " AND ARGUMENT_NAME = ?" : " AND CASE WHEN OBJECT_TYPE = 'PACKAGE' THEN OBJECT_NAME ELSE NULL END = ?", sql);
                    if (parameters) Assert.Contains(" AND PACKAGE_NAME = ?", sql);
                    if (routineType) Assert.Contains(" AND CASE WHEN OBJECT_TYPE IN ('PROCEDURE', 'FUNCTION') THEN OBJECT_TYPE ELSE NULL END = ?", sql);
                }
                var values = restrictions.Where(x => x != null).Select(x => new OceanBaseParameter { DbType = DbType.String, Value = x }).ToArray();
                await Prepared(channel, 71, checked((ushort)values.Length));
                Assert.Equal(PreparedParameters.Execute(71, PreparedParameters.Encode(values, mysql ? OceanBaseCompatibilityMode.MySql : OceanBaseCompatibilityMode.Oracle)), await StatementCommand(channel, 23));
                foreach (var packet in RoutinePackets(parameters, mysql, binary: true)) await channel.WritePacketAsync(packet);
                await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); if (mysql) UseInternalMySqlFixture(connection);
                var table = await connection.GetSchemaAsync(parameters ? "ProcedureParameters" : "Procedures", restrictions);
                Assert.Equal(2, table.Rows.Count);
            }
            catch (Exception error) when (server.Completion.IsFaulted) { throw new AggregateException(error, server.Completion.Exception!); }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task RoutineSchemaAbsentIdentifierPartsAndMetadataNeedNoIo(bool mysql, bool parameters)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); if (mysql) UseInternalMySqlFixture(connection);
                var transport = new ProtocolFaultStream(Array.Empty<byte>()); InstallFaultStream(connection, transport);
                var collection = parameters ? "ProcedureParameters" : "Procedures";
                var values = new string?[5]; values[mysql ? 1 : 0] = "";
                Assert.Empty(connection.GetSchema(collection, values).Rows.Cast<DataRow>());
                if (mysql) { values = new string?[5]; values[4] = "Pkg"; Assert.Empty((await connection.GetSchemaAsync(collection, values)).Rows.Cast<DataRow>()); }
                var metadata = connection.GetSchema(); Assert.Equal(mysql ? 17 : 19, metadata.Rows.Count);
                Assert.Equal(new[] { 0, 0, 4, 4, 5, 5 }, metadata.Rows.Cast<DataRow>().Take(6).Select(x => (int)x[1]));
                Assert.Equal(new[] { 0, 0, 3, 4, 4, 5 }, metadata.Rows.Cast<DataRow>().Take(6).Select(x => (int)x[2]));
                Assert.Throws<ArgumentException>(() => connection.GetSchema(collection, new string[6]));
                using var canceled = new CancellationTokenSource(); canceled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.GetSchemaAsync(collection, canceled.Token));
                Assert.Empty(transport.Written); Assert.Equal(0, transport.SyncCalls + transport.AsyncCalls);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "name")]
        [InlineData(true, "name")]
        [InlineData(false, "fraction")]
        [InlineData(true, "fraction")]
        [InlineData(false, "overflow")]
        [InlineData(true, "overflow")]
        public async Task RoutineSchemaInvalidResultsDiscardUnreadSessions(bool parameters, string fault)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var input = new MemoryStream(); byte sequence = 1;
                foreach (var packet in RoutinePackets(parameters, false, fault: fault)) input.Write(ResponseFrame(packet, sequence++));
                InstallFaultStream(connection, new ProtocolFaultStream(input.ToArray()));
                var error = await Record.ExceptionAsync(() => connection.GetSchemaAsync(parameters ? "ProcedureParameters" : "Procedures"));
                Assert.NotNull(error); Assert.True(error is NotSupportedException || error is InvalidCastException || error is OverflowException);
                Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task RoutineSchemaRejectsExtraResultsBeforeReturningATable(bool parameters)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var input = new MemoryStream(); byte sequence = 1;
                var packets = RoutinePackets(parameters, false); packets[packets.Length - 1] = Eof(14);
                foreach (var packet in packets.Concat(RoutinePackets(parameters, false))) input.Write(ResponseFrame(packet, sequence++));
                InstallFaultStream(connection, new ProtocolFaultStream(input.ToArray()));
                await Assert.ThrowsAsync<NotSupportedException>(() => connection.GetSchemaAsync(parameters ? "ProcedureParameters" : "Procedures"));
                Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task RoutineSchemaOracleNumbersRetainExactDirectoryTypes(bool parameters)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var input = new MemoryStream(); byte sequence = 1;
                foreach (var packet in RoutinePackets(parameters, false, nativeNumbers: true)) input.Write(ResponseFrame(packet, sequence++));
                InstallFaultStream(connection, new ProtocolFaultStream(input.ToArray()));
                var table = await connection.GetSchemaAsync(parameters ? "ProcedureParameters" : "Procedures");
                Assert.Equal(7, table.Rows[0]["SUBPROGRAM_ID"]);
                if (parameters) { Assert.Equal(0, table.Rows[0]["ORDINAL_POSITION"]); Assert.Equal(4294967295L, table.Rows[0]["DATA_LENGTH"]); Assert.Equal(-2, table.Rows[0]["NUMERIC_SCALE"]); }
                Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task RoutineSchemaPermissionErrorsDoNotRetryOrBreakTheSession(bool parameters)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 3); await channel.WritePacketAsync(new byte[] { 255, 119, 4, 35, 52, 50, 48, 48, 48 });
                await StatementCommand(channel, 3); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => connection.GetSchemaAsync(parameters ? "ProcedureParameters" : "Procedures"));
                Assert.Equal(1143, error.ServerErrorCode); Assert.False(error.IsExecutionOutcomeUnknown); Assert.Equal(ConnectionState.Open, connection.State);
                using var command = connection.CreateCommand(); command.CommandText = "after"; Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task RoutineSchemaAsyncWaitKeepsCommandOwnershipAndHonorsCancellation(bool parameters)
        {
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel => { await StatementCommand(channel, 3); received.TrySetResult(); Assert.Equal(0, await channelStreamRead(channel)); });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            using var cancellation = new CancellationTokenSource();
            try
            {
                await connection.OpenAsync(); var pending = connection.GetSchemaAsync(parameters ? "ProcedureParameters" : "Procedures", cancellation.Token);
                await received.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(pending.IsCompleted);
                using var command = connection.CreateCommand(); command.CommandText = "busy";
                Assert.Throws<InvalidOperationException>(() => command.ExecuteNonQuery());
                cancellation.Cancel(); var error = await Assert.ThrowsAsync<OceanBaseException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.Equal(cancellation.Token, Assert.IsType<OperationCanceledException>(error.InnerException).CancellationToken);
                Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
