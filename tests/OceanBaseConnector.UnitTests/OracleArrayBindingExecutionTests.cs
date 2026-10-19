using System;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        private static async Task ArrayMetadata(PacketChannel channel, bool deprecated, ushort parameters = 1, bool outputs = false, bool pl = false)
        {
            await channel.WritePacketAsync(OracleExecuteHeader(parameters, outputs ? (ushort)4 : (ushort)3, pl && outputs ? 6u : outputs ? 3u : 2u));
            for (var i = 0; i < parameters; i++) await channel.WritePacketAsync(Column("p", 8));
            if (!deprecated) await channel.WritePacketAsync(Eof(5));
            await channel.WritePacketAsync(Column("sql_no", 8)); await channel.WritePacketAsync(Column("error_code", 8));
            if (outputs) await channel.WritePacketAsync(Column("out", 0));
            await channel.WritePacketAsync(Column("error_message", 253));
            if (!deprecated) await channel.WritePacketAsync(Eof(5));
        }

        private static byte[] ArrayWireRow(long positionOrCount, long code = 0, string? output = null, bool hasOutput = false)
        {
            var row = new byte[] { 0, code == 0 ? hasOutput ? output == null ? (byte)48 : (byte)32 : (byte)16 : hasOutput ? (byte)16 : (byte)0 }
                .Concat(BitConverter.GetBytes(positionOrCount)).Concat(BitConverter.GetBytes(code));
            if (hasOutput && output != null) row = row.Concat(WithLength(ResultColumn.Utf8.GetBytes(output)));
            if (code != 0) row = row.Concat(new byte[] { 6, 115, 101, 99, 114, 101, 116 });
            return row.ToArray();
        }

        [Theory]
        [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
        public async Task OracleArrayExecutionUsesOneRequestKeepsPartialFailuresAndLeavesTransactionOwnedByCaller(bool asynchronous, bool deprecated)
        {
            var server = Server(async channel =>
            {
                await TransactionBegin(channel); await OracleVersionReply(channel, "4.3.5.0", deprecated);
                var cursor = new ByteCursor(await StatementCommand(channel, 0xa1, deprecated));
                Assert.Equal(0xa1, cursor.ReadUInt8()); Assert.Equal(0u, cursor.ReadUInt32()); Assert.Equal(0, cursor.ReadUInt8()); Assert.Equal(2u, cursor.ReadUInt32());
                Assert.Equal("update t set x=?", ResultColumn.Utf8.GetString(cursor.ReadLengthEncodedBytes())); Assert.Equal(1u, cursor.ReadUInt32());
                Assert.Equal(new byte[] { 0, 1, 160, 0, 0, 0, 0, 0, 3, 2, 1, 49, 1, 50 }, cursor.ReadBytes(14));
                Assert.Equal(128u, cursor.ReadUInt32());
                await ArrayMetadata(channel, deprecated);
                await channel.WritePacketAsync(ArrayWireRow(2)); await channel.WritePacketAsync(ArrayWireRow(1, 1)); await channel.WritePacketAsync(ArrayWireRow(0));
                var end = deprecated ? Ok(0, 5) : Eof(5); end[0] = 254; await channel.WritePacketAsync(end); await channel.WritePacketAsync(Ok(2, 5));
                Assert.Equal(new byte[] { 25, 42, 0, 0, 0 }, await StatementCommand(channel, 25, deprecated));
                channel.BeginCommand(); await TransactionEnd(channel, false);
            }, deprecated);
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); using var transaction = (OceanBaseTransaction)await connection.BeginTransactionAsync();
                using var command = (OceanBaseCommand)connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "update t set x=:p";
                var type = connection.CreateArrayType("NUMBER", transaction); command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", Value = type.CreateValue(1, null, 2) });
                var result = asynchronous ? await command.ExecuteArrayAsync(3) : command.ExecuteArray(3);
                Assert.Same(result, command.ArrayBindingResult); Assert.True(result.IsComplete); Assert.Equal(2, result.RecordsAffected);
                Assert.Equal(new long?[] { 2, null, 0 }, result.Items.Select(i => i.RecordsAffected)); Assert.Equal(1, result.Items[1].ServerErrorCode);
                Assert.Same(connection, transaction.Connection); command.Dispose(); await transaction.RollbackAsync();
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
        public async Task OracleArrayReturningPublishesGroupedMultipleRowsOnlyAfterTerminalValidation(bool asynchronous, bool tailError)
        {
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel =>
            {
                await TransactionBegin(channel); await OracleVersionReply(channel, "4.3.5.0"); await StatementCommand(channel, 0xa1);
                await ArrayMetadata(channel, false, 2, outputs: true);
                await channel.WritePacketAsync(ArrayWireRow(0, output: "42", hasOutput: true));
                await channel.WritePacketAsync(ArrayWireRow(0, output: "43", hasOutput: true));
                await channel.WritePacketAsync(ArrayWireRow(2, hasOutput: true));
                await channel.WritePacketAsync(Eof(5)); waiting.SetResult(); await release.Task;
                await channel.WritePacketAsync(tailError ? Error() : Ok(3, 5));
                await StatementCommand(channel, 25); channel.BeginCommand(); await TransactionEnd(channel, false);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); using var transaction = (OceanBaseTransaction)await connection.BeginTransactionAsync();
                using var command = (OceanBaseCommand)connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "update t set x=:p returning x into :o";
                var type = connection.CreateArrayType("NUMBER", transaction);
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", Value = type.CreateValue(1, 2, 3) });
                var output = new OceanBaseParameter { ParameterName = "o", ComplexType = type, Direction = ParameterDirection.Output, Value = "old" }; command.Parameters.Add(output);
                var running = Task.Run(async () => asynchronous ? await command.ExecuteArrayAsync(3) : command.ExecuteArray(3));
                await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal("old", output.Value); Assert.Null(command.ArrayBindingResult); release.SetResult();
                if (tailError)
                {
                    var error = await Assert.ThrowsAsync<OceanBaseException>(() => running); Assert.False(error.IsExecutionOutcomeUnknown);
                    Assert.Equal("old", output.Value); Assert.False(command.ArrayBindingResult!.IsComplete);
                    Assert.Equal(OceanBaseArrayBindingStatus.Unknown, command.ArrayBindingResult.Items[2].Status);
                }
                else
                {
                    var result = await running; Assert.True(result.IsComplete); Assert.Equal(3, result.RecordsAffected);
                    Assert.Equal(2, result.Items[0].OutputRows.Count); Assert.Empty(result.Items[1].OutputRows); Assert.Equal(DBNull.Value, Assert.Single(result.Items[2].OutputRows)[0]);
                    var groups = Assert.IsType<object[][]>(output.Value); Assert.Equal(3, groups.Length);
                    Assert.Equal(new[] { "42", "43" }, groups[0].Select(x => x.ToString())); Assert.Empty(groups[1]); Assert.Equal(DBNull.Value, Assert.Single(groups[2]));
                }
                command.Dispose(); await transaction.RollbackAsync();
            }
            finally { release.TrySetResult(); connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
        public async Task OracleArrayPlUsesPositionsAndPreservesProvenPrefixWhenServerStops(bool asynchronous, bool failure)
        {
            var server = Server(async channel =>
            {
                await TransactionBegin(channel); await OracleVersionReply(channel, "4.3.5.0");
                var request = new ByteCursor(await StatementCommand(channel, 0xa1)); request.ReadBytes(10);
                Assert.Equal("BEGIN pkg.p(?); END;", ResultColumn.Utf8.GetString(request.ReadLengthEncodedBytes()));
                await ArrayMetadata(channel, false, pl: true); await channel.WritePacketAsync(ArrayWireRow(0));
                await channel.WritePacketAsync(ArrayWireRow(1, failure ? 1403 : 0));
                if (!failure) await channel.WritePacketAsync(ArrayWireRow(2));
                await channel.WritePacketAsync(Eof(5)); await channel.WritePacketAsync(failure ? Error() : Ok(0, 5));
                await StatementCommand(channel, 25); channel.BeginCommand(); await TransactionEnd(channel, false);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); using var transaction = (OceanBaseTransaction)await connection.BeginTransactionAsync();
                using var command = (OceanBaseCommand)connection.CreateCommand(); command.Transaction = transaction; command.CommandType = CommandType.StoredProcedure; command.CommandText = "pkg.p";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", Value = connection.CreateArrayType("NUMBER", transaction).CreateValue(1, 2, 3) });
                if (failure) await Assert.ThrowsAsync<OceanBaseException>(async () => { if (asynchronous) await command.ExecuteArrayAsync(3); else command.ExecuteArray(3); });
                else { var result = asynchronous ? await command.ExecuteArrayAsync(3) : command.ExecuteArray(3); Assert.True(result.IsComplete); }
                var snapshot = command.ArrayBindingResult!; Assert.All(snapshot.Items, item => Assert.Null(item.RecordsAffected));
                Assert.Equal(OceanBaseArrayBindingStatus.Succeeded, snapshot.Items[0].Status);
                Assert.Equal(failure ? 1403 : 0, snapshot.Items[1].ServerErrorCode);
                Assert.Equal(failure ? OceanBaseArrayBindingStatus.Unknown : OceanBaseArrayBindingStatus.Succeeded, snapshot.Items[2].Status);
                command.Dispose(); await transaction.RollbackAsync();
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "cancel")] [InlineData(true, "cancel")] [InlineData(false, "close")] [InlineData(true, "close")]
        [InlineData(false, "disconnect")] [InlineData(true, "disconnect")] [InlineData(false, "timeout")] [InlineData(true, "timeout")]
        [InlineData(false, "count")] [InlineData(true, "count")] [InlineData(false, "extra")] [InlineData(true, "extra")]
        [InlineData(false, "tail-count")] [InlineData(true, "tail-count")] [InlineData(false, "metadata")] [InlineData(true, "metadata")]
        public async Task OracleArrayFailuresNeverReplayAndKeepOnlyReceivedResults(bool asynchronous, string fault)
        {
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel =>
            {
                await TransactionBegin(channel); await OracleVersionReply(channel, "4.3.5.0"); await StatementCommand(channel, 0xa1);
                if (fault == "metadata") { await channel.WritePacketAsync(OracleExecuteHeader(1, 2, 0)); return; }
                await ArrayMetadata(channel, false); await channel.WritePacketAsync(ArrayWireRow(1));
                if (fault == "count" || fault == "extra" || fault == "tail-count")
                {
                    if (fault != "count") await channel.WritePacketAsync(ArrayWireRow(2));
                    if (fault == "extra")
                    { await channel.WritePacketAsync(ArrayWireRow(3)); Assert.Equal(0, await channelStreamRead(channel)); return; }
                    await channel.WritePacketAsync(Eof(5)); await channel.WritePacketAsync(Ok(0, 5)); return;
                }
                waiting.SetResult();
                if (fault != "disconnect") Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); using var transaction = (OceanBaseTransaction)await connection.BeginTransactionAsync();
                using var command = (OceanBaseCommand)connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "update t set x=:p";
                command.CommandTimeout = fault == "timeout" ? 1 : 5;
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", Value = connection.CreateArrayType("NUMBER", transaction).CreateValue(1, 2) });
                var running = Task.Run(async () => asynchronous ? await command.ExecuteArrayAsync(2) : command.ExecuteArray(2));
                if (fault == "cancel" || fault == "close")
                {
                    await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    if (fault == "cancel") command.Cancel(); else connection.Close();
                }
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => running.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.False(command.ArrayBindingResult!.IsComplete); Assert.Null(command.ArrayBindingResult.RecordsAffected);
                // The server's write does not prove the client consumed that row before Cancel/Close.
                if (fault == "cancel" || fault == "close")
                {
                    Assert.Contains(command.ArrayBindingResult.Items[0].Status, new[] { OceanBaseArrayBindingStatus.Unknown, OceanBaseArrayBindingStatus.Succeeded });
                    Assert.Equal(OceanBaseArrayBindingStatus.Unknown, command.ArrayBindingResult.Items[1].Status);
                }
                else Assert.Equal(fault == "metadata" ? OceanBaseArrayBindingStatus.Unknown : OceanBaseArrayBindingStatus.Succeeded, command.ArrayBindingResult.Items[0].Status);
                Assert.DoesNotContain("secret", error.ToString());
                if (fault == "timeout") Assert.IsType<TimeoutException>(error.InnerException);
                Assert.NotEqual(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("scalar")] [InlineData("count")] [InlineData("size")] [InlineData("sql")] [InlineData("fetch")]
        [InlineData("no-column")] [InlineData("output-type")] [InlineData("stale")] [InlineData("pre-cancel")]
        public async Task OracleArrayInvalidDeclarationsAreRejectedBeforeVersionOrSqlAndLeaveTransactionUsable(string fault)
        {
            var server = Server(async channel =>
            {
                await TransactionBegin(channel); await Query(channel, "after"); await channel.WritePacketAsync(Ok(1, 5)); channel.BeginCommand(); await TransactionEnd(channel, false);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); using var transaction = (OceanBaseTransaction)await connection.BeginTransactionAsync();
                using var command = (OceanBaseCommand)connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = fault == "sql" ? "select :p from dual" : fault == "no-column" ? "update t set x=1" : "update t set x=:p";
                var type = connection.CreateArrayType("NUMBER", transaction);
                if (fault != "no-column") command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", Value = fault == "scalar" ? (object)1 : type.CreateValue(1, 2), Size = fault == "size" ? 1 : 0 });
                if (fault == "output-type") ((OceanBaseParameter)command.Parameters[0]).Direction = ParameterDirection.Output;
                if (fault == "fetch") command.FetchSize = 1;
                if (fault == "stale") connection.ClearComplexTypeCache(transaction);
                using var canceled = new System.Threading.CancellationTokenSource(); if (fault == "pre-cancel") canceled.Cancel();
                await Assert.ThrowsAnyAsync<Exception>(() => command.ExecuteArrayAsync(fault == "count" ? 1 : 2, canceled.Token));
                Assert.Null(command.ArrayBindingResult); Assert.False(command.HasSentSql);
                using var after = connection.CreateCommand(); after.Transaction = transaction; after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
                command.Dispose(); await transaction.RollbackAsync();
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task OracleArrayRequiresPositiveCountAndExplicitTransaction()
        {
            using var unconnected = new OceanBaseCommand();
            Assert.Throws<ArgumentOutOfRangeException>(() => unconnected.ExecuteArray(0));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => unconnected.ExecuteArrayAsync(65536));
            var server = Server(async channel => { await Query(channel, "after"); await channel.WritePacketAsync(Ok(1)); });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); using var command = (OceanBaseCommand)connection.CreateCommand(); command.CommandText = "update t set x=:p";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", Value = connection.CreateArrayType("NUMBER").CreateValue(1) });
                Assert.Throws<NotSupportedException>(() => command.ExecuteArray(1));
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [MemberData(nameof(AnonymousOutputCases))]
        public async Task OracleArrayPlOutputsUseArrayFlagsAndDecodeEachScalarDirection(bool asynchronous, ParameterDirection direction, string name)
        {
            var code = name == "NUMBER" ? (byte)0 : name == "VARCHAR2" ? (byte)253 : name == "CHAR" ? (byte)254 : name == "RAW" ? (byte)203 : name == "DATE" ? (byte)12 : (byte)202;
            var body = Convert.FromHexString(name == "NUMBER" ? "023432" : name == "VARCHAR2" ? "0AE682A3E88085F09F9880" : name == "CHAR" ? "0161" : name == "RAW" ? "0200FF"
                : name == "DATE" ? "07E8070203040506" : "0C1418020304050615CD5B0709");
            var server = Server(async channel =>
            {
                await TransactionBegin(channel); await OracleVersionReply(channel, "4.3.5.0");
                var request = new ByteCursor(await StatementCommand(channel, 0xa1)); request.ReadBytes(10);
                Assert.Equal(direction == ParameterDirection.ReturnValue ? "BEGIN ? := pkg.p(); END;" : "BEGIN pkg.p(?); END;", ResultColumn.Utf8.GetString(request.ReadLengthEncodedBytes()));
                await channel.WritePacketAsync(OracleExecuteHeader(1, 4, direction == ParameterDirection.ReturnValue ? 7u : 6u));
                await channel.WritePacketAsync(Column("p", 8)); await channel.WritePacketAsync(Eof(5));
                await channel.WritePacketAsync(Column("sql_no", 8)); await channel.WritePacketAsync(Column("error_code", 8));
                var column = Column("out", code); if (code == 202) column[column.Length - 3] = 9;
                await channel.WritePacketAsync(column); await channel.WritePacketAsync(Column("error_message", 253)); await channel.WritePacketAsync(Eof(5));
                await channel.WritePacketAsync(new byte[] { 0, 32 }.Concat(new byte[16]).Concat(body).ToArray());
                await channel.WritePacketAsync(new byte[] { 0, 48 }.Concat(BitConverter.GetBytes(1L)).Concat(new byte[8]).ToArray());
                await channel.WritePacketAsync(Eof(5)); await channel.WritePacketAsync(Ok(0, 5));
                await StatementCommand(channel, 25); channel.BeginCommand(); await TransactionEnd(channel, false);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); using var transaction = (OceanBaseTransaction)await connection.BeginTransactionAsync();
                var type = connection.CreateArrayType(name, transaction);
                object item = name == "NUMBER" ? 42 : name == "VARCHAR2" ? "患者😀" : name == "CHAR" ? "a" : name == "RAW" ? new byte[] { 0, 255 }
                    : name == "DATE" ? new DateTime(2024, 2, 3, 4, 5, 6) : new OceanBaseTimestamp(new DateTime(2024, 2, 3, 4, 5, 6), 123456789, 9);
                using var command = (OceanBaseCommand)connection.CreateCommand(); command.Transaction = transaction; command.CommandType = CommandType.StoredProcedure; command.CommandText = "pkg.p";
                var output = new OceanBaseParameter { ParameterName = "p", Direction = direction, Value = direction == ParameterDirection.InputOutput ? type.CreateValue(item, null) : (object)"old" };
                if (direction != ParameterDirection.InputOutput) output.ComplexType = type;
                command.Parameters.Add(output); var result = asynchronous ? await command.ExecuteArrayAsync(2) : command.ExecuteArray(2);
                Assert.True(result.IsComplete); Assert.All(result.Items, entry => { Assert.Equal(OceanBaseArrayBindingStatus.Succeeded, entry.Status); Assert.Null(entry.RecordsAffected); });
                var groups = Assert.IsType<object[][]>(output.Value); var value = Assert.Single(groups[0]);
                if (name == "NUMBER") Assert.Equal("42", Assert.IsType<OceanBaseDecimal>(value).ToString()); else Assert.Equal(item, value);
                Assert.Equal(DBNull.Value, Assert.Single(groups[1])); command.Dispose(); await transaction.RollbackAsync();
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
        public async Task OracleArrayNamedOutputElementsRetainTheirDictionaryIdentity(bool asynchronous, bool collection)
        {
            var server = Server(async channel =>
            {
                await TransactionBegin(channel); await ComplexScalarType(channel, collection: collection);
                channel.BeginCommand(); await OracleVersionReply(channel, "4.3.5.0"); await StatementCommand(channel, 0xa1);
                await channel.WritePacketAsync(OracleExecuteHeader(1, 4, 6)); await channel.WritePacketAsync(Column("p", 8)); await channel.WritePacketAsync(Eof(5));
                await channel.WritePacketAsync(Column("sql_no", 8)); await channel.WritePacketAsync(Column("error_code", 8));
                await channel.WritePacketAsync(ComplexColumn()); await channel.WritePacketAsync(Column("error_message", 253)); await channel.WritePacketAsync(Eof(5));
                await channel.WritePacketAsync(new byte[] { 0, 32 }.Concat(new byte[16]).Concat(collection ? new byte[] { 1, 0, 1, 42 } : new byte[] { 0, 1, 42 }).ToArray());
                await channel.WritePacketAsync(Eof(5)); await channel.WritePacketAsync(Ok(0, 5));
                await StatementCommand(channel, 25); channel.BeginCommand(); await TransactionEnd(channel, false);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); using var transaction = (OceanBaseTransaction)await connection.BeginTransactionAsync();
                var named = await connection.GetComplexTypeAsync("Owner", "Thing", transaction); var type = connection.CreateArrayType(named, transaction);
                using var command = (OceanBaseCommand)connection.CreateCommand(); command.Transaction = transaction; command.CommandType = CommandType.StoredProcedure; command.CommandText = "pkg.p";
                var output = new OceanBaseParameter { ParameterName = "p", ComplexType = type, Direction = ParameterDirection.InputOutput, Value = type.CreateValue(named.CreateValue(new byte[] { 7 })) };
                command.Parameters.Add(output); var result = asynchronous ? await command.ExecuteArrayAsync(1) : command.ExecuteArray(1);
                var value = Assert.IsType<OceanBaseComplexValue>(Assert.Single(Assert.Single(result.Items[0].OutputRows)));
                Assert.Same(named, value.Type); Assert.Equal(new byte[] { 42 }, Assert.Single(value.GetValues()));
                command.Dispose(); await transaction.RollbackAsync();
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
