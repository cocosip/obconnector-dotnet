using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Reflection;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        public static IEnumerable<object[]> OracleStandalonePrepareCases()
        {
            foreach (var asynchronous in new[] { false, true })
                foreach (var deprecated in new[] { false, true })
                    foreach (var shape in new[] { "text", "procedure", "function", "returning" })
                        yield return new object[] { asynchronous, deprecated, shape };
        }

        [Theory]
        [MemberData(nameof(OracleStandalonePrepareCases))]
        public async Task OracleStandalonePrepareDefersValuesAndTransfersStatementToExecution(bool asynchronous, bool deprecated, string shape)
            => await OracleStandalonePrepareDefersValuesAndTransfersStatementToExecutionCore(asynchronous, deprecated, shape);

        [Theory]
        [MemberData(nameof(OracleStandalonePrepareCases))]
        public async Task OracleOutputPositiveFetchHintPreservesPreparationAndStatementOwnership(bool asynchronous, bool deprecated, string shape)
            => await OracleStandalonePrepareDefersValuesAndTransfersStatementToExecutionCore(asynchronous, deprecated, shape, int.MaxValue);

        private static async Task OracleStandalonePrepareDefersValuesAndTransfersStatementToExecutionCore(bool asynchronous, bool deprecated, string shape, int fetchSize = 0)
        {
            var sql = shape == "returning" ? "insert into t values (?) returning id into ?" : shape == "text" ? "BEGIN ? := ?; END;" : shape == "function" ? "BEGIN ? := pkg.func(?); END;" : "BEGIN pkg.proc(?, ?); END;";
            var server = Server(async channel =>
            {
                var prepare = await StatementCommand(channel, 22, deprecated);
                Assert.Equal(sql, System.Text.Encoding.UTF8.GetString(prepare, 1, prepare.Length - 1));
                await Prepared(channel, 42, shape == "returning" ? (ushort)1 : (ushort)2, shape == "returning" ? (ushort)1 : (ushort)0, deprecated);
                for (var execution = 0; execution < 2; execution++)
                {
                    var bytes = new ByteCursor(await StatementCommand(channel, 0xa1, deprecated));
                    Assert.Equal(0xa1, bytes.ReadUInt8());
                    Assert.Equal(execution == 0 ? 42u : 0u, bytes.ReadUInt32());
                    Assert.Equal(0, bytes.ReadUInt8()); Assert.Equal(1u, bytes.ReadUInt32());
                    Assert.Equal(sql, System.Text.Encoding.UTF8.GetString(bytes.ReadLengthEncodedBytes()));
                    Assert.Equal(2u, bytes.ReadUInt32());
                    await OracleOutputReply(channel, deprecated);
                    await channel.WritePacketAsync(Ok(2));
                    Assert.Equal(new byte[] { 25, 42, 0, 0, 0 }, await StatementCommand(channel, 25, deprecated));
                }
                channel.BeginCommand(); await Query(channel, "after", deprecated); await channel.WritePacketAsync(Ok(1));
            }, deprecated, serverVersion: "4.3.5.0");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            Exception? failure = null;
            try
            {
                await connection.OpenAsync();
                using var command = (OceanBaseCommand)connection.CreateCommand();
                command.FetchSize = fetchSize;
                command.CommandType = shape == "text" || shape == "returning" ? CommandType.Text : CommandType.StoredProcedure;
                command.CommandText = shape == "returning" ? "insert into t values (:input) returning id into :result" : shape == "text" ? "BEGIN :result := :input; END;" : shape == "function" ? "pkg.func" : "pkg.proc";
                var input = new OceanBaseParameter { ParameterName = "input", DbType = DbType.Int64, Value = "not encoded during Prepare" };
                var output = new OceanBaseParameter { ParameterName = "result", Direction = shape == "function" ? ParameterDirection.ReturnValue : ParameterDirection.Output, Value = "unchanged" };
                command.Parameters.Add(input); command.Parameters.Add(output);
                for (var i = 0; i < 2; i++)
                {
                    if (asynchronous) await command.PrepareAsync(); else command.Prepare();
                    Assert.Equal("unchanged", output.Value);
                    Assert.False(command.HasSentSql);
                    Assert.False(connection.GetOpenSession().RequiresDiscardOnReturn);
                }
                input.Value = 7L;
                for (var i = 0; i < 2; i++)
                {
                    Assert.Equal(2, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                    Assert.Equal(fetchSize, command.FetchSize);
                    Assert.Equal(42L, output.Value); output.Value = "unchanged";
                }
                using var after = connection.CreateCommand(); after.CommandText = "after";
                Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                connection.Close(); server.Stop();
                try { await server.Completion.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch when (failure != null) { }
            }
        }

        private static OceanBaseCommand OracleStandaloneCall(OceanBaseConnection connection)
        {
            var command = (OceanBaseCommand)connection.CreateCommand();
            command.CommandType = CommandType.StoredProcedure; command.CommandText = "pkg.proc";
            command.Parameters.Add(new OceanBaseParameter { ParameterName = "input", DbType = DbType.Int64, Value = 7L });
            command.Parameters.Add(new OceanBaseParameter { ParameterName = "result", Direction = ParameterDirection.Output, Value = "old" });
            return command;
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task OracleStandalonePrepareDisposalClosesUnreadStreamStatementOnce(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 42, 2);
                Assert.Equal(new byte[] { 25, 42, 0, 0, 0 }, await StatementCommand(channel, 25));
                channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            Exception? failure = null;
            try
            {
                await connection.OpenAsync(); using var source = new LongDataSource(10, asynchronous);
                var command = OracleStandaloneCall(connection); command.Parameters[0].ResetDbType(); command.Parameters[0].Value = source;
                command.FetchSize = 1; // Preparing never requests cursor pages.
                if (asynchronous) await command.PrepareAsync(); else command.Prepare();
                Assert.Equal(0, source.BytesRead); Assert.False(source.WasDisposed);
                Assert.Equal("old", command.Parameters[1].Value); Assert.False(command.HasSentSql);
                command.Dispose(); command.Dispose();
                Assert.False(source.WasDisposed);
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                connection.Close(); server.Stop();
                try { await server.Completion.WaitAsync(TimeSpan.FromSeconds(10)); } catch when (failure != null) { }
            }
        }

        [Theory]
        [InlineData(false, "error")]
        [InlineData(true, "error")]
        [InlineData(false, "metadata-error")]
        [InlineData(true, "metadata-error")]
        [InlineData(false, "count")]
        [InlineData(true, "count")]
        [InlineData(false, "split-procedure")]
        [InlineData(true, "split-procedure")]
        public async Task OracleStandalonePrepareTrustedFailureNeverExecutesOrPublishesAndKeepsSession(bool asynchronous, string fault)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22);
                if (fault == "error") await channel.WritePacketAsync(Error());
                else
                {
                    if (fault == "count" || fault == "split-procedure") await Prepared(channel, 42, 1, fault == "split-procedure" ? (ushort)1 : (ushort)0);
                    else
                    {
                        await channel.WritePacketAsync(new byte[] { 0, 42, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0 });
                        await channel.WritePacketAsync(Error());
                    }
                    Assert.Equal(new byte[] { 25, 42, 0, 0, 0 }, await StatementCommand(channel, 25));
                }
                channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            Exception? failure = null;
            try
            {
                await connection.OpenAsync();
                using (var command = OracleStandaloneCall(connection))
                {
                    command.AllowClientSidePrepareFallback = true;
                    var error = await Record.ExceptionAsync(async () => { if (asynchronous) await command.PrepareAsync(); else command.Prepare(); });
                    if (fault == "count" || fault == "split-procedure") Assert.IsType<ArgumentException>(error);
                    else { var db = Assert.IsType<OceanBaseException>(error); Assert.Equal(1064, db.ServerErrorCode); Assert.False(db.IsExecutionOutcomeUnknown); }
                    Assert.Equal("old", command.Parameters[1].Value); Assert.False(command.HasSentSql);
                    Assert.False(connection.GetOpenSession().RequiresDiscardOnReturn);
                }
                Assert.Equal(ConnectionState.Open, connection.State);
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                connection.Close(); server.Stop();
                try { await server.Completion.WaitAsync(TimeSpan.FromSeconds(10)); } catch when (failure != null) { }
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task OracleStandalonePrepareMalformedMetadataBreaksWithoutUnknownExecution(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22);
                await channel.WritePacketAsync(new byte[] { 0, 42, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0 });
                await channel.WritePacketAsync(new byte[] { 1 });
                await AssertOracleExecuteTransportClosed(channel);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            Exception? failure = null;
            try
            {
                await connection.OpenAsync(); using var command = OracleStandaloneCall(connection);
                var error = await Assert.ThrowsAsync<OceanBaseException>(async () => { if (asynchronous) await command.PrepareAsync(); else command.Prepare(); });
                Assert.False(error.IsExecutionOutcomeUnknown); Assert.False(command.HasSentSql);
                Assert.Equal("old", command.Parameters[1].Value); Assert.Equal(ConnectionState.Broken, connection.State);
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                connection.Close(); server.Stop();
                try { await server.Completion.WaitAsync(TimeSpan.FromSeconds(10)); } catch when (failure != null) { }
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task OracleStandalonePrepareCancellationDuringMetadataNeverHasUnknownExecution(bool asynchronous)
        {
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22);
                await channel.WritePacketAsync(new byte[] { 0, 42, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0 });
                waiting.SetResult(); await AssertOracleExecuteTransportClosed(channel);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            Exception? failure = null;
            try
            {
                await connection.OpenAsync(); using var command = OracleStandaloneCall(connection);
                using var cancellation = new CancellationTokenSource();
                var preparing = asynchronous ? command.PrepareAsync(cancellation.Token) : Task.Run(command.Prepare);
                await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
                if (asynchronous) cancellation.Cancel(); else command.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparing.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Equal("old", command.Parameters[1].Value); Assert.False(command.HasSentSql); Assert.Equal(ConnectionState.Broken, connection.State);
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                connection.Close(); server.Stop();
                try { await server.Completion.WaitAsync(TimeSpan.FromSeconds(10)); } catch when (failure != null) { }
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task OracleStandalonePrepareChangedSqlClosesOldIdAndPreparesAgain(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 41, 2);
                Assert.Equal(new byte[] { 25, 41, 0, 0, 0 }, await StatementCommand(channel, 25));
                var prepare = await StatementCommand(channel, 22);
                Assert.Equal("BEGIN pkg.changed(?, ?); END;", System.Text.Encoding.UTF8.GetString(prepare, 1, prepare.Length - 1));
                await Prepared(channel, 42, 2);
                var request = new ByteCursor(await StatementCommand(channel, 0xa1)); request.ReadUInt8(); Assert.Equal(42u, request.ReadUInt32());
                await OracleOutputReply(channel, false); await channel.WritePacketAsync(Ok(2));
                Assert.Equal(new byte[] { 25, 42, 0, 0, 0 }, await StatementCommand(channel, 25));
            }, serverVersion: "4.3.5.0");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            Exception? failure = null;
            try
            {
                await connection.OpenAsync();
                using var command = OracleStandaloneCall(connection);
                if (asynchronous) await command.PrepareAsync(); else command.Prepare();
                command.CommandText = "pkg.changed";
                if (asynchronous) await command.PrepareAsync(); else command.Prepare();
                Assert.Equal(2, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                Assert.Equal(42L, command.Parameters[1].Value);
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                connection.Close(); server.Stop();
                try { await server.Completion.WaitAsync(TimeSpan.FromSeconds(10)); } catch when (failure != null) { }
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task OracleStandalonePrepareCursorOutlivesCommandAndClosesParentOnce(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 42, 1);
                var request = new ByteCursor(await StatementCommand(channel, 0xa1)); request.ReadUInt8(); Assert.Equal(42u, request.ReadUInt32());
                await RefCursorOutput(channel);
                Assert.Equal(new byte[] { 28, 91, 0, 0, 0, 1, 0, 0, 0 }, await StatementCommand(channel, 28));
                await CursorMetadata(channel, 2, false); await channel.WritePacketAsync(CursorRow(7)); await channel.WritePacketAsync(CursorTerminator(130, false));
                Assert.Equal(new byte[] { 25, 91, 0, 0, 0 }, await StatementCommand(channel, 25));
                Assert.Equal(new byte[] { 25, 42, 0, 0, 0 }, await StatementCommand(channel, 25));
                channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
            }, serverVersion: "4.3.5.0");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            Exception? failure = null;
            try
            {
                await connection.OpenAsync(); var command = RefCursorCall(connection);
                if (asynchronous) await command.PrepareAsync(); else command.Prepare();
                if (asynchronous) await command.ExecuteNonQueryAsync(); else command.ExecuteNonQuery();
                using var cursor = Assert.IsType<OceanBaseRefCursor>(command.Parameters[0].Value); command.Dispose();
                using (var reader = asynchronous ? await cursor.GetDataReaderAsync(1) : cursor.GetDataReader(1))
                {
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.Equal(7L, reader.GetInt64(0));
                    Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                }
                cursor.Dispose();
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                connection.Close(); server.Stop();
                try { await server.Completion.WaitAsync(TimeSpan.FromSeconds(10)); } catch when (failure != null) { }
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task OracleStandalonePreparePoolResetNeverReusesOldStatement(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 41, 2);
                Assert.Equal(new byte[] { 31 }, await StatementCommand(channel, 31)); await channel.WritePacketAsync(Ok());
                await TestInfrastructure.ServerIdentity.ReplyOracleAutocommitAsync(channel);
                Assert.Equal(new byte[] { 14 }, await StatementCommand(channel, 14)); await channel.WritePacketAsync(Ok());
                await StatementCommand(channel, 22); await Prepared(channel, 42, 2);
                var request = new ByteCursor(await StatementCommand(channel, 0xa1)); request.ReadUInt8(); Assert.Equal(42u, request.ReadUInt32());
                await OracleOutputReply(channel, false); await channel.WritePacketAsync(Ok(2));
                Assert.Equal(new byte[] { 25, 42, 0, 0, 0 }, await StatementCommand(channel, 25));
                await AssertOracleExecuteTransportClosed(channel);
            }, serverVersion: "4.3.5.0");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true;Pooling=true");
            Exception? failure = null;
            try
            {
                await connection.OpenAsync(); using var command = OracleStandaloneCall(connection);
                var session = connection.GetOpenSession(); var generation = session.Generation;
                if (asynchronous) await command.PrepareAsync(); else command.Prepare();
                connection.Close(); await connection.OpenAsync();
                Assert.Same(session, connection.GetOpenSession()); Assert.NotEqual(generation, session.Generation);
                if (asynchronous) await command.PrepareAsync(); else command.Prepare();
                Assert.Equal(2, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                Assert.Equal(42L, command.Parameters[1].Value); command.Dispose(); connection.Close();
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                connection.Close(); OceanBaseConnection.ClearPool(connection); server.Stop();
                try { await server.Completion.WaitAsync(TimeSpan.FromSeconds(10)); } catch when (failure != null) { }
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task OracleStandalonePrepareReadsStreamOnlyAtExecutionAndUsesTheSameId(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 42, 2);
                var piece = new ByteCursor(await StatementCommand(channel, 162));
                Assert.Equal(162, piece.ReadUInt8()); Assert.Equal(42u, piece.ReadUInt32()); Assert.Equal(0, piece.ReadUInt16());
                Assert.Equal(3, piece.ReadUInt8()); Assert.Equal(0, piece.ReadUInt8()); Assert.Equal(20uL, piece.ReadUInt64());
                Assert.Equal("00010203040506070809", System.Text.Encoding.ASCII.GetString(piece.ReadBytes(20)));
                Assert.False(piece.HasRemaining); await channel.WritePacketAsync(Ok());
                var request = new ByteCursor(await StatementCommand(channel, 0xa1)); request.ReadUInt8(); Assert.Equal(42u, request.ReadUInt32());
                await OracleOutputReply(channel, false); await channel.WritePacketAsync(Ok(2));
                Assert.Equal(new byte[] { 25, 42, 0, 0, 0 }, await StatementCommand(channel, 25));
            }, serverVersion: "4.3.5.0");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            Exception? failure = null;
            try
            {
                await connection.OpenAsync(); using var source = new LongDataSource(10, asynchronous);
                using var command = OracleStandaloneCall(connection); command.Parameters[0].ResetDbType(); command.Parameters[0].Value = source;
                if (asynchronous) await command.PrepareAsync(); else command.Prepare();
                Assert.Equal(0, source.BytesRead);
                Assert.Equal(2, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                Assert.Equal(10, source.BytesRead); Assert.False(source.WasDisposed); Assert.Equal(42L, command.Parameters[1].Value);
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                connection.Close(); server.Stop();
                try { await server.Completion.WaitAsync(TimeSpan.FromSeconds(10)); } catch when (failure != null) { }
            }
        }

        [Theory]
        [InlineData(ParameterDirection.InputOutput, 1, 1)]
        [InlineData(ParameterDirection.Output, 0, 2)]
        [InlineData(ParameterDirection.Output, 0, 1)]
        public async Task OracleStandalonePrepareDmlRejectsWrongSplitMetadata(ParameterDirection direction, ushort parameters, ushort columns)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 42, parameters, columns);
                Assert.Equal(new byte[] { 25, 42, 0, 0, 0 }, await StatementCommand(channel, 25));
                channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            Exception? failure = null;
            try
            {
                await connection.OpenAsync();
                using (var command = OracleStandaloneCall(connection))
                {
                    command.CommandType = CommandType.Text; command.CommandText = "/* prepare */ INSERT INTO t VALUES (:input) RETURNING id INTO :result";
                    command.Parameters[1].Direction = direction;
                    await Assert.ThrowsAsync<ArgumentException>(() => command.PrepareAsync());
                    Assert.Equal("old", command.Parameters[1].Value); Assert.False(command.HasSentSql);
                }
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                connection.Close(); server.Stop();
                try { await server.Completion.WaitAsync(TimeSpan.FromSeconds(10)); } catch when (failure != null) { }
            }
        }

        [Fact]
        public async Task OracleStandalonePrepareContextTransferAndDisposeAreAtomic()
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 42, 2);
                Assert.Equal(new byte[] { 25, 42, 0, 0, 0 }, await StatementCommand(channel, 25));
                channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            Exception? failure = null;
            try
            {
                await connection.OpenAsync(); using var command = OracleStandaloneCall(connection); await command.PrepareAsync();
                var session = connection.GetOpenSession();
                var sessionGate = session.GetType().GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
                var transfer = command.GetType().GetMethod("GetPrepared", BindingFlags.Instance | BindingFlags.NonPublic)!;
                Exception? transferFailure = null, disposalFailure = null;
                var preparing = new Thread(() =>
                {
                    try { transfer.Invoke(command, new object?[] { session, 2, null }); }
                    catch (Exception error) { transferFailure = error is TargetInvocationException invoked ? invoked.InnerException : error; }
                })
                { IsBackground = true };
                var disposing = new Thread(() => { try { command.Dispose(); } catch (Exception error) { disposalFailure = error; } }) { IsBackground = true };
                try
                {
                    lock (sessionGate)
                    {
                        preparing.Start();
                        Assert.True(SpinWait.SpinUntil(() => (preparing.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(5)));
                        disposing.Start();
                        Assert.True(SpinWait.SpinUntil(() => (disposing.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(5)));
                    }
                }
                finally
                {
                    if (preparing.IsAlive) Assert.True(preparing.Join(TimeSpan.FromSeconds(5)));
                    if (disposing.IsAlive) Assert.True(disposing.Join(TimeSpan.FromSeconds(5)));
                }
                Assert.Null(transferFailure); Assert.Null(disposalFailure);
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                connection.Close(); server.Stop();
                try { await server.Completion.WaitAsync(TimeSpan.FromSeconds(10)); } catch when (failure != null) { }
            }
        }
    }
}
