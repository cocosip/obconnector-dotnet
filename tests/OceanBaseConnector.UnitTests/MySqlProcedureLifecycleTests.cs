using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Sql;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        private static (OceanBaseParameter Output, OceanBaseParameter InOut) AddMySqlProcedureParameters(OceanBaseCommand command)
        {
            command.CommandType = CommandType.StoredProcedure; command.CommandText = "p";
            command.Parameters.Add(new OceanBaseParameter { Value = 7L });
            var output = new OceanBaseParameter { Direction = ParameterDirection.Output, Value = new object() };
            var inout = new OceanBaseParameter { Direction = ParameterDirection.InputOutput, Value = "中文'input" };
            command.Parameters.Add(output); command.Parameters.Add(inout); return (output, inout);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MySqlProcedureRejectionsNeverTransmitAndKeepSessionReusable(bool async)
        {
            var server = MySqlProcedureServer(async channel => { await Query(channel, "after rejected"); await channel.WritePacketAsync(Ok(1, 2)); });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = (OceanBaseCommand)connection.CreateCommand(); command.CommandType = CommandType.StoredProcedure;
                foreach (var name in new[] { "CALL p()", "p; DROP TABLE t", "p(?)", "p--comment", "a.b.c", "`unclosed", "p\0", "db..p" })
                {
                    command.CommandText = name;
                    await Assert.ThrowsAsync<NotSupportedException>(async () => { if (async) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery(); });
                }
                command.CommandText = "p";
                command.Parameters.Add(new OceanBaseParameter { Direction = ParameterDirection.ReturnValue });
                Assert.Throws<NotSupportedException>(() => command.Prepare()); command.Parameters.Clear();
                command.FetchSize = 1; Assert.Throws<NotSupportedException>(() => command.ExecuteNonQuery()); command.FetchSize = 0;
                Assert.Throws<NotSupportedException>(() => command.ExecuteReader(CommandBehavior.SchemaOnly));
                Assert.Throws<NotSupportedException>(() => command.ExecuteReader(CommandBehavior.KeyInfo));
                command.Parameters.Add(new OceanBaseParameter { Direction = ParameterDirection.InputOutput, Value = new MemoryStream() });
                Assert.Throws<NotSupportedException>(() => command.ExecuteNonQuery()); command.Parameters.Clear();
                command.Parameters.Add(new OceanBaseParameter { Value = Guid.Empty });
                Assert.Throws<NotSupportedException>(() => command.ExecuteNonQuery()); command.Parameters.Clear();
                Assert.False(connection.GetOpenSession().RequiresDiscardOnReturn);
                command.CommandType = CommandType.Text; command.CommandText = "after rejected";
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public static IEnumerable<object[]> MySqlProcedureFailureCases()
        {
            foreach (var async in new[] { false, true })
                for (var stage = 0; stage < 5; stage++) yield return new object[] { async, stage };
        }

        [Theory]
        [MemberData(nameof(MySqlProcedureFailureCases))]
        public async Task MySqlProcedureFailureDoesNotPublishPartialOutputsOrReplay(bool async, int stage)
        {
            var server = MySqlProcedureServer(async channel =>
            {
                var variables = await MySqlProcedureInitialize(channel, false);
                await StatementCommand(channel, 22); await Prepared(channel, 81, 1);
                await StatementCommand(channel, 23);
                if (stage == 0) await channel.WritePacketAsync(Error());
                else
                {
                    await channel.WritePacketAsync(Ok(3, 2));
                    channel.BeginCommand(); await Query(channel, "SELECT " + string.Join(", ", variables));
                    if (stage == 1) await channel.WritePacketAsync(Error());
                    else
                    {
                        await channel.WritePacketAsync(new byte[] { 2 });
                        await channel.WritePacketAsync(Column("out", 8)); await channel.WritePacketAsync(Column("inout", 253));
                        await channel.WritePacketAsync(Eof(2));
                        if (stage != 2) await channel.WritePacketAsync(new byte[] { 1, 55, 3, 110, 101, 119 });
                        if (stage == 3)
                        {
                            await channel.WritePacketAsync(new byte[] { 1, 56, 3, 110, 101, 119 });
                            await AssertOracleExecuteTransportClosed(channel); return;
                        }
                        if (stage == 4) { channel.Close(); return; }
                        await channel.WritePacketAsync(Eof(2));
                    }
                }
                Assert.Equal(new byte[] { 25, 81, 0, 0, 0 }, await StatementCommand(channel, 25));
                channel.BeginCommand(); await Query(channel, "after failed procedure"); await channel.WritePacketAsync(Ok(1, 2));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var command = (OceanBaseCommand)connection.CreateCommand(); var parameters = AddMySqlProcedureParameters(command);
                var old = parameters.Output.Value;
                var error = await Record.ExceptionAsync(async () => { if (async) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery(); });
                Assert.NotNull(error); Assert.Same(old, parameters.Output.Value); Assert.Equal("中文'input", parameters.InOut.Value);
                if (stage >= 3)
                {
                    Assert.True(Assert.IsType<OceanBaseException>(error).IsExecutionOutcomeUnknown);
                    Assert.Equal(ConnectionState.Broken, connection.State);
                }
                else
                {
                    Assert.Equal(ConnectionState.Open, connection.State);
                    if (stage <= 1) Assert.False(Assert.IsType<OceanBaseException>(error).IsExecutionOutcomeUnknown);
                    else Assert.IsType<ProtocolException>(error);
                }
                command.Dispose();
                if (stage < 3)
                {
                    using var after = connection.CreateCommand(); after.CommandText = "after failed procedure";
                    Assert.Equal(1, await after.ExecuteNonQueryAsync());
                }
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MySqlProcedureOutputCancellationKeepsExactTokenAndRetiresSession(bool readerToken)
        {
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = MySqlProcedureServer(async channel =>
            {
                var variables = await MySqlProcedureInitialize(channel, false);
                await StatementCommand(channel, 22); await Prepared(channel, 81, 1); await StatementCommand(channel, 23);
                await channel.WritePacketAsync(Ok(3, 2));
                channel.BeginCommand(); await Query(channel, "SELECT " + string.Join(", ", variables));
                waiting.TrySetResult(); await AssertOracleExecuteTransportClosed(channel);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port)); using var cancellation = new CancellationTokenSource();
            try
            {
                await connection.OpenAsync(); using var command = (OceanBaseCommand)connection.CreateCommand(); var parameters = AddMySqlProcedureParameters(command);
                var old = parameters.Output.Value;
                using var reader = await command.ExecuteReaderAsync(readerToken ? default : cancellation.Token);
                var reading = reader.NextResultAsync(readerToken ? cancellation.Token : default);
                await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancellation.Cancel();
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => reading);
                Assert.True(error.IsExecutionOutcomeUnknown);
                Assert.Equal(cancellation.Token, Assert.IsType<OperationCanceledException>(error.InnerException).CancellationToken);
                Assert.Equal(ConnectionState.Broken, connection.State); Assert.Same(old, parameters.Output.Value); Assert.Equal("中文'input", parameters.InOut.Value);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MySqlProcedureExplicitPreparationAndInputStreamAreOwnedByCommand(bool async)
        {
            var server = MySqlProcedureServer(async channel =>
            {
                var request = await StatementCommand(channel, 22); Assert.Equal("CALL `p`(?)", Encoding.UTF8.GetString(request, 1, request.Length - 1));
                await Prepared(channel, 81, 1);
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    var data = await StatementCommand(channel, 24); Assert.Equal(new byte[] { 24, 81, 0, 0, 0, 0, 0, 7 }, data);
                    await StatementCommand(channel, 23); await channel.WritePacketAsync(Ok(2, 2));
                }
                Assert.Equal(new byte[] { 25, 81, 0, 0, 0 }, await StatementCommand(channel, 25));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = (OceanBaseCommand)connection.CreateCommand(); command.CommandText = "p"; command.CommandType = CommandType.StoredProcedure;
                using var stream = new MemoryStream(new byte[] { 7 }); command.Parameters.Add(new OceanBaseParameter { DbType = DbType.Binary, Value = stream });
                if (async) await command.PrepareAsync(); else command.Prepare();
                Assert.False(connection.GetOpenSession().RequiresDiscardOnReturn);
                for (var i = 0; i < 2; i++)
                {
                    stream.Position = 0; Assert.Equal(2, async ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                    Assert.True(stream.CanRead); Assert.True(connection.GetOpenSession().RequiresDiscardOnReturn);
                }
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task MySqlProcedureBatchPublishesCallerOutputsAndKeepsDrainedCountWithoutExecutingClosedSuffix(bool async, bool close)
        {
            var server = MySqlProcedureServer(async channel =>
            {
                var variables = await MySqlProcedureInitialize(channel, false);
                await StatementCommand(channel, 22); await Prepared(channel, 81, 1); await StatementCommand(channel, 23);
                await MySqlProcedureReply(channel, false, 9, more: true); await channel.WritePacketAsync(Ok(3, 2));
                channel.BeginCommand(); await Query(channel, "SELECT " + string.Join(", ", variables));
                await channel.WritePacketAsync(new byte[] { 2 }); await channel.WritePacketAsync(Column("out", 8));
                await channel.WritePacketAsync(Column("inout", 253)); await channel.WritePacketAsync(Eof(2));
                await channel.WritePacketAsync(new byte[] { 1, 55, 3, 110, 101, 119 }); await channel.WritePacketAsync(Eof(2));
                if (!close)
                { channel.BeginCommand(); await Query(channel, "suffix"); await channel.WritePacketAsync(Ok(4, 2)); }
                Assert.Equal(new byte[] { 25, 81, 0, 0, 0 }, await StatementCommand(channel, 25));
                channel.BeginCommand(); await Query(channel, "after batch"); await channel.WritePacketAsync(Ok(1, 2));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var batch = connection.CreateBatch(); var item = BatchItem(batch, "p"); item.CommandType = CommandType.StoredProcedure;
                item.Parameters.Add(new OceanBaseParameter { Value = 7L });
                var output = new OceanBaseParameter { Direction = ParameterDirection.Output, Value = new object() };
                var inout = new OceanBaseParameter { Direction = ParameterDirection.InputOutput, Value = "中文'input" };
                item.Parameters.Add(output); item.Parameters.Add(inout); var suffix = BatchItem(batch, "suffix");
                using var reader = async ? await batch.ExecuteReaderAsync() : batch.ExecuteReader();
                Assert.True(async ? await reader.ReadAsync() : reader.Read()); Assert.Equal(9L, reader.GetInt64(0));
                if (!close)
                    do { while (async ? await reader.ReadAsync() : reader.Read()) { } } while (async ? await reader.NextResultAsync() : reader.NextResult());
                if (async) await reader.CloseAsync(); else reader.Close();
                Assert.Equal(7L, output.Value); Assert.Equal("new", inout.Value);
                Assert.Equal(3, item.RecordsAffected); Assert.Equal(close ? -1 : 4, suffix.RecordsAffected);
                Assert.Equal(close ? 3 : 7, reader.RecordsAffected);
                if (async) await batch.DisposeAsync(); else batch.Dispose();
                using var after = connection.CreateCommand(); after.CommandText = "after batch"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task MySqlProcedureInitializationFailureClosesTransientStatementWithoutCallingRoutine(bool async, bool wrongCount)
        {
            var server = MySqlProcedureServer(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 80, wrongCount ? (ushort)1 : (ushort)2);
                if (!wrongCount) { await StatementCommand(channel, 23); await channel.WritePacketAsync(Error()); }
                Assert.Equal(new byte[] { 25, 80, 0, 0, 0 }, await StatementCommand(channel, 25));
                channel.BeginCommand(); await Query(channel, "after initialization failure"); await channel.WritePacketAsync(Ok(1, 2));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = (OceanBaseCommand)connection.CreateCommand(); var parameters = AddMySqlProcedureParameters(command);
                var old = parameters.Output.Value;
                var error = await Record.ExceptionAsync(async () => { if (async) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery(); });
                if (wrongCount) Assert.IsType<ProtocolException>(error);
                else Assert.False(Assert.IsType<OceanBaseException>(error).IsExecutionOutcomeUnknown);
                Assert.Same(old, parameters.Output.Value); Assert.Equal("中文'input", parameters.InOut.Value);
                Assert.Equal(!wrongCount, connection.GetOpenSession().RequiresDiscardOnReturn);
                using var after = connection.CreateCommand(); after.CommandText = "after initialization failure"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task MySqlProcedureMalformedPrivateOutputNeverPublishesOrReplays(bool async, bool extraResults)
        {
            var server = MySqlProcedureServer(async channel =>
            {
                var variables = await MySqlProcedureInitialize(channel, false);
                await StatementCommand(channel, 22); await Prepared(channel, 81, 1); await StatementCommand(channel, 23);
                await channel.WritePacketAsync(Ok(3, 2)); channel.BeginCommand(); await Query(channel, "SELECT " + string.Join(", ", variables));
                await channel.WritePacketAsync(new byte[] { extraResults ? (byte)2 : (byte)1 });
                await channel.WritePacketAsync(Column("out", 8));
                if (extraResults) await channel.WritePacketAsync(Column("inout", 253));
                await channel.WritePacketAsync(Eof(2));
                await channel.WritePacketAsync(extraResults ? new byte[] { 1, 55, 3, 110, 101, 119 } : new byte[] { 1, 55 });
                if (extraResults) await channel.WritePacketAsync(Eof(10));
                await AssertOracleExecuteTransportClosed(channel);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = (OceanBaseCommand)connection.CreateCommand(); var parameters = AddMySqlProcedureParameters(command);
                var old = parameters.Output.Value;
                var error = await Assert.ThrowsAsync<OceanBaseException>(async () => { if (async) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery(); });
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.IsType<ProtocolException>(error.InnerException);
                Assert.Same(old, parameters.Output.Value); Assert.Equal("中文'input", parameters.InOut.Value); Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, 0)]
        [InlineData(true, 0)]
        [InlineData(false, 1)]
        [InlineData(true, 1)]
        [InlineData(false, 2)]
        [InlineData(true, 2)]
        public async Task MySqlProcedureInitializationInterruptionNeverCallsRoutineOrPublishes(bool async, int interruption)
        {
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = MySqlProcedureServer(async channel =>
            {
                var prepare = await StatementCommand(channel, 22); Assert.StartsWith("SET @", Encoding.UTF8.GetString(prepare, 1, prepare.Length - 1));
                await Prepared(channel, 80, 2); await StatementCommand(channel, 23); waiting.TrySetResult();
                if (interruption == 1) channel.Close();
                else await AssertOracleExecuteTransportClosed(channel);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port)); using var cancellation = new CancellationTokenSource();
            try
            {
                await connection.OpenAsync(); var session = connection.GetOpenSession(); using var command = (OceanBaseCommand)connection.CreateCommand();
                var parameters = AddMySqlProcedureParameters(command); var old = parameters.Output.Value;
                command.CommandTimeout = interruption == 2 ? 1 : 10;
                var executing = Task.Run(async () => { if (async) await command.ExecuteNonQueryAsync(cancellation.Token); else command.ExecuteNonQuery(); });
                await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
                if (interruption == 0) { if (async) cancellation.Cancel(); else command.Cancel(); }
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => executing.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.Equal(ConnectionState.Broken, connection.State); Assert.True(session.RequiresDiscardOnReturn);
                Assert.Same(old, parameters.Output.Value); Assert.Equal("中文'input", parameters.InOut.Value);
                if (interruption == 0)
                {
                    var cause = Assert.IsType<OperationCanceledException>(error.InnerException);
                    if (async) Assert.Equal(cancellation.Token, cause.CancellationToken);
                }
                if (interruption == 2) Assert.IsType<TimeoutException>(error.InnerException);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MySqlProcedureRejectsMissingCapabilityAndNeverFallsBackToInterpolatedSql(bool missingCapability)
        {
            var server = MySqlProcedureServer(async channel =>
            {
                if (!missingCapability)
                {
                    var request = await StatementCommand(channel, 22); Assert.Equal(OceanBaseCommand.SqlPacket("CALL `p`(?)", 22), request);
                    await channel.WritePacketAsync(new byte[] { 255, 15, 5, 35, 72, 89, 48, 48, 48 });
                }
                channel.BeginCommand(); await Query(channel, "after no fallback"); await channel.WritePacketAsync(Ok(1, 2));
            }, psMultiResults: !missingCapability);
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = (OceanBaseCommand)connection.CreateCommand(); command.CommandType = CommandType.StoredProcedure; command.CommandText = "p";
                command.AllowClientSidePrepareFallback = true; command.Parameters.Add(new OceanBaseParameter { Value = "'secret" });
                if (missingCapability) await Assert.ThrowsAsync<NotSupportedException>(() => command.ExecuteNonQueryAsync());
                else
                {
                    var error = await Assert.ThrowsAsync<OceanBaseException>(() => command.ExecuteNonQueryAsync());
                    Assert.Equal(1295, error.ServerErrorCode); Assert.False(error.IsExecutionOutcomeUnknown);
                }
                Assert.False(connection.GetOpenSession().RequiresDiscardOnReturn);
                using var after = connection.CreateCommand(); after.CommandText = "after no fallback"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, CommandBehavior.SingleRow)]
        [InlineData(true, CommandBehavior.SingleRow)]
        [InlineData(false, CommandBehavior.SingleResult)]
        [InlineData(true, CommandBehavior.SingleResult)]
        public async Task MySqlProcedureLimitedReadersDrainOutputAndRetirePooledSession(bool async, CommandBehavior behavior)
        {
            var server = MySqlProcedureServer(async channel =>
            {
                var variables = await MySqlProcedureInitialize(channel, false);
                await StatementCommand(channel, 22); await Prepared(channel, 81, 1); await StatementCommand(channel, 23);
                await MySqlProcedureReply(channel, false, 9, more: true); await MySqlProcedureReply(channel, false, 10, more: true);
                await channel.WritePacketAsync(Ok(3, 2)); channel.BeginCommand(); await Query(channel, "SELECT " + string.Join(", ", variables));
                await channel.WritePacketAsync(new byte[] { 2 }); await channel.WritePacketAsync(Column("out", 8)); await channel.WritePacketAsync(Column("inout", 253));
                await channel.WritePacketAsync(Eof(2)); await channel.WritePacketAsync(new byte[] { 1, 55, 3, 110, 101, 119 }); await channel.WritePacketAsync(Eof(2));
                Assert.Equal(new byte[] { 25, 81, 0, 0, 0 }, await StatementCommand(channel, 25));
                await AssertOracleExecuteTransportClosed(channel);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Pooling=true;Max Pool Size=1");
            try
            {
                await connection.OpenAsync(); var session = connection.GetOpenSession();
                using (var command = (OceanBaseCommand)connection.CreateCommand())
                {
                    var parameters = AddMySqlProcedureParameters(command);
                    using var reader = async ? await command.ExecuteReaderAsync(behavior) : command.ExecuteReader(behavior);
                    Assert.True(async ? await reader.ReadAsync() : reader.Read()); Assert.Equal(9L, reader.GetInt64(0));
                    Assert.False(async ? await reader.NextResultAsync() : reader.NextResult());
                    Assert.Equal(7L, parameters.Output.Value); Assert.Equal("new", parameters.InOut.Value); Assert.Equal(3, reader.RecordsAffected);
                    if (async) await reader.CloseAsync(); else reader.Close();
                    Assert.Equal(7L, parameters.Output.Value); Assert.Equal("new", parameters.InOut.Value); Assert.Equal(3, reader.RecordsAffected);
                    Assert.True(session.RequiresDiscardOnReturn);
                }
                if (async) await connection.CloseAsync(); else connection.Close();
                Assert.Equal(OceanBaseConnector.Internal.Session.SessionState.Closed, session.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
