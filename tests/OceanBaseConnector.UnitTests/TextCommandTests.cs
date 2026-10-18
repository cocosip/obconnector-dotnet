using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
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
        private static string Configuration(int port) => $"Server=127.0.0.1;Port={port};User ID=u;Password=secret;TLS Mode=Disabled;Connection Timeout=5";

        private static LoopbackServer Server(Func<PacketChannel, Task> exchange, bool deprecateEof = false) => LoopbackServer.Start(async client =>
        {
            var channel = new PacketChannel(client.GetStream());
            var greeting = (byte[])ProtocolFixture.Load("initial-handshake-oceanbase").Payload.Clone();
            if (deprecateEof) greeting[Array.IndexOf(greeting, (byte)0, 1) + 20] |= 1;
            await channel.WritePacketAsync(greeting);
            var authentication = await channel.ReadPacketAsync();
            if (deprecateEof) Assert.Equal(1, authentication[3] & 1);
            await channel.WritePacketAsync(Ok());
            channel.BeginCommand();
            await exchange(channel);
        });

        private static byte[] Ok(byte affected = 0, byte status = 6) => new byte[] { 0, affected, 0, status, 0, 0, 0 };
        private static byte[] Eof(byte status = 6) => new byte[] { 254, 0, 0, status, 0 };
        private static byte[] Error() => new byte[] { 255, 40, 4, 35, 52, 50, 48, 48, 48, 115, 101, 99, 114, 101, 116 };

        private static byte[] Column(string name, byte type = 9, ushort charset = 45, ushort flags = 0)
        {
            using var bytes = new MemoryStream();
            foreach (var value in new[] { "def", "", "", "", name, name })
            {
                var encoded = Encoding.UTF8.GetBytes(value);
                bytes.WriteByte((byte)encoded.Length);
                bytes.Write(encoded, 0, encoded.Length);
            }
            bytes.Write(new byte[] { 12, (byte)charset, (byte)(charset >> 8), 255, 0, 0, 0,
                type, (byte)flags, (byte)(flags >> 8), 0, 0, 0 }, 0, 13);
            return bytes.ToArray();
        }

        private static async Task Query(PacketChannel channel, string expected)
        {
            var request = await channel.ReadPacketAsync();
            Assert.Equal(3, request[0]);
            Assert.Equal(expected, Encoding.UTF8.GetString(request, 1, request.Length - 1));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task OracleBindingsAreValidatedBeforeSendAndRejectedCommandsLeaveSessionUsable(bool asynchronous)
        {
            const string valid = "select ':hidden', q'[it's :hidden]'";
            var server = Server(async channel =>
            {
                await Query(channel, valid);
                await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                Assert.Equal(OceanBaseCompatibilityMode.Oracle, connection.CompatibilityMode);
                using var command = connection.CreateCommand();
                Func<Task> execute = () => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery());
                command.CommandText = "select :missingsecret";
                var missing = await Assert.ThrowsAsync<ArgumentException>(execute);
                Assert.DoesNotContain("missingsecret", missing.Message);
                command.CommandText = "select 'unfinished";
                await Assert.ThrowsAsync<ArgumentException>(execute);
                command.CommandText = "select ?";
                await Assert.ThrowsAsync<NotSupportedException>(execute);
                command.CommandText = "select :id";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = ":ID", DbType = DbType.Guid, Value = Guid.Empty });
                await Assert.ThrowsAsync<NotSupportedException>(execute); // Guid binding remains unsupported.
                command.CommandText = "select 1";
                await Assert.ThrowsAsync<ArgumentException>(execute);
                command.CommandText = "select :id";
                var second = new OceanBaseParameter { ParameterName = "other" };
                command.Parameters.Add(second);
                second.ParameterName = "id";
                await Assert.ThrowsAsync<ArgumentException>(execute);
                command.Parameters.Remove(second);
                command.Parameters[0].Direction = ParameterDirection.Output;
                await Assert.ThrowsAsync<NotSupportedException>(execute);
                command.Parameters.Clear();
                command.CommandText = valid;
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task TextRowsStreamWithNullsAndReuseTheSession(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await Query(channel, "select sample");
                await channel.WritePacketAsync(new byte[] { 2 });
                await channel.WritePacketAsync(Column("id"));
                await channel.WritePacketAsync(Column("label", 253));
                await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(new byte[] { 2, 52, 50, 0 });
                await channel.WritePacketAsync(new byte[] { 1, 55, 251 });
                await channel.WritePacketAsync(Eof());
                channel.BeginCommand();
                await Query(channel, "update unchanged");
                await channel.WritePacketAsync(Ok(5));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "select sample";
                using (var reader = asynchronous ? await command.ExecuteReaderAsync() : command.ExecuteReader())
                {
                    Assert.Equal(2, reader.FieldCount);
                    Assert.True(reader.HasRows);
                    Assert.Equal("id", reader.GetName(0));
                    Assert.Equal(typeof(int), reader.GetFieldType(0));
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                    Assert.Equal(42, reader.GetInt32(0));
                    Assert.Equal("", reader["label"]);
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                    Assert.Equal(7, reader.GetValue(0));
                    Assert.True(reader.IsDBNull(1));
                    Assert.Same(DBNull.Value, reader.GetValue(1));
                    Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                    Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                    Assert.Equal(-1, reader.RecordsAffected);
                }
                command.CommandText = "update unchanged";
                Assert.Equal(5, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ScalarConsumesAllResultsAndOnlyOkCountsContribute(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await Query(channel, "select mixed");
                await channel.WritePacketAsync(new byte[] { 1 });
                await channel.WritePacketAsync(Column("n"));
                await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(new byte[] { 1, 57 });
                await channel.WritePacketAsync(Eof(14));
                await channel.WritePacketAsync(Ok(3, 14));
                await channel.WritePacketAsync(new byte[] { 1 });
                await channel.WritePacketAsync(Column("empty"));
                await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(Eof(14));
                await channel.WritePacketAsync(Ok(2));
                channel.BeginCommand();
                await Query(channel, "update next");
                await channel.WritePacketAsync(Ok(0));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "select mixed";
                Assert.Equal(9, asynchronous ? await command.ExecuteScalarAsync() : command.ExecuteScalar());
                command.CommandText = "update next";
                Assert.Equal(0, await command.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ScalarSkipsInitialUpdateCountsToReachFirstResultSet(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await Query(channel, "update then select");
                await channel.WritePacketAsync(Ok(2, 14));
                await channel.WritePacketAsync(new byte[] { 1 });
                await channel.WritePacketAsync(Column("n"));
                await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(new byte[] { 1, 57 });
                await channel.WritePacketAsync(Eof());
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "update then select";
                Assert.Equal(9, asynchronous ? await command.ExecuteScalarAsync() : command.ExecuteScalar());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task NextResultSkipsUnreadRowsAndAccumulatesUpdateCounts()
        {
            var server = Server(async channel =>
            {
                await Query(channel, "mixed");
                await channel.WritePacketAsync(Ok(2, 14));
                await channel.WritePacketAsync(new byte[] { 1 });
                await channel.WritePacketAsync(Column("n"));
                await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(new byte[] { 1, 49 });
                await channel.WritePacketAsync(new byte[] { 1, 50 });
                await channel.WritePacketAsync(Eof(14));
                await channel.WritePacketAsync(Ok(4));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "mixed";
                using var reader = await command.ExecuteReaderAsync();
                Assert.Equal(0, reader.FieldCount);
                Assert.Equal(2, reader.RecordsAffected);
                Assert.True(await reader.NextResultAsync());
                Assert.True(await reader.ReadAsync());
                Assert.Equal(1, reader.GetInt32(0));
                Assert.True(await reader.NextResultAsync());
                Assert.Equal(0, reader.FieldCount);
                Assert.Equal(6, reader.RecordsAffected);
                Assert.False(await reader.NextResultAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task ServerErrorIsTerminalAndDoesNotLeakSqlIntoLogs()
        {
            var log = new RecordingLoggerFactory();
            var server = Server(async channel =>
            {
                await Query(channel, "secret SQL");
                await channel.WritePacketAsync(Error());
                channel.BeginCommand();
                await Query(channel, "next");
                await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port), log);
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "secret SQL";
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => command.ExecuteNonQueryAsync());
                Assert.Equal(1064, error.ServerErrorCode);
                Assert.Equal("42000", error.SqlState);
                Assert.False(error.IsExecutionOutcomeUnknown);
                Assert.Equal(OceanBaseCompatibilityMode.Oracle, connection.CompatibilityMode);
                command.CommandText = "next";
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
                Assert.Contains(log.Entries, e => e.EventId.Id == 1101);
                Assert.All(log.Entries, e => { Assert.Null(e.Error); Assert.DoesNotContain("secret", e.Message); });
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task UnreadReaderCloseDestroysSessionAndHonorsCloseConnection(bool closeConnection)
        {
            var server = Server(async channel =>
            {
                await Query(channel, "stream");
                await channel.WritePacketAsync(new byte[] { 1 });
                await channel.WritePacketAsync(Column("n"));
                await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(new byte[] { 1, 49 });
                await Assert.ThrowsAnyAsync<Exception>(() => channel.ReadPacketAsync());
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "stream";
                using var reader = await command.ExecuteReaderAsync(closeConnection ? CommandBehavior.CloseConnection : CommandBehavior.Default);
                using var second = connection.CreateCommand();
                second.CommandText = "must not send";
                await Assert.ThrowsAsync<InvalidOperationException>(() => second.ExecuteNonQueryAsync());
                reader.Close();
                Assert.Equal(closeConnection ? ConnectionState.Closed : ConnectionState.Broken, connection.State);
                Assert.True(reader.IsClosed);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("cancel")]
        [InlineData("timeout")]
        [InlineData("close")]
        public async Task InterruptedSentCommandReportsUnknownOutcome(string cause)
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel =>
            {
                await Query(channel, "write");
                ready.TrySetResult();
                await Assert.ThrowsAnyAsync<Exception>(() => channel.ReadPacketAsync());
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "write";
                command.CommandTimeout = cause == "timeout" ? 1 : 0;
                var executing = command.ExecuteNonQueryAsync();
                Assert.False(executing.IsCompleted);
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
                if (cause == "cancel") command.Cancel();
                if (cause == "close") connection.Close();
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => executing.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.True(error.IsExecutionOutcomeUnknown);
                Assert.IsAssignableFrom<Exception>(error.InnerException);
                Assert.Equal(cause == "close" ? ConnectionState.Closed : ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task SuccessfulTerminalReplyWinsOverCancellationFromCompletionLogger()
        {
            var server = Server(async channel =>
            {
                await Query(channel, "write");
                await channel.WritePacketAsync(Ok(3));
            });
            using var cancellation = new CancellationTokenSource();
            var log = new RecordingLoggerFactory { OnLog = id => { if (id.Id == 1100) cancellation.Cancel(); } };
            using var connection = new OceanBaseConnection(Configuration(server.Port), log);
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "write";
                Assert.Equal(3, await command.ExecuteNonQueryAsync(cancellation.Token));
                Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task ServerErrorWinsOverCancellationFromFailureLogger()
        {
            var server = Server(async channel =>
            {
                await Query(channel, "error");
                await channel.WritePacketAsync(Error());
            });
            using var cancellation = new CancellationTokenSource();
            var log = new RecordingLoggerFactory { OnLog = id => { if (id.Id == 1101) cancellation.Cancel(); } };
            using var connection = new OceanBaseConnection(Configuration(server.Port), log);
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "error";
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => command.ExecuteNonQueryAsync(cancellation.Token));
                Assert.Equal(1064, error.ServerErrorCode);
                Assert.False(error.IsExecutionOutcomeUnknown);
                Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task CancelWhileRowIsBufferedStillReportsUnreadCommandOutcome()
        {
            var server = Server(async channel =>
            {
                await Query(channel, "rows");
                await channel.WritePacketAsync(new byte[] { 1 });
                await channel.WritePacketAsync(Column("n"));
                await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(new byte[] { 1, 49 });
                await Assert.ThrowsAnyAsync<Exception>(() => channel.ReadPacketAsync());
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "rows";
                using var reader = await command.ExecuteReaderAsync();
                command.Cancel();
                var error = Assert.Throws<OceanBaseException>(() => reader.Read());
                Assert.True(error.IsExecutionOutcomeUnknown);
                Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task OracleNumberIsNotDecodedAsMysqlInt()
        {
            var server = Server(async channel =>
            {
                await Query(channel, "number");
                await channel.WritePacketAsync(new byte[] { 1 });
                await channel.WritePacketAsync(Column("n", 3));
                await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(new byte[] { 2, 52, 50 });
                await channel.WritePacketAsync(Eof());
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "number";
                using var reader = await command.ExecuteReaderAsync();
                Assert.True(reader.Read());
                Assert.Equal(typeof(decimal), reader.GetFieldType(0));
                Assert.Equal(42m, reader.GetValue(0));
                Assert.Equal("NUMBER", reader.GetDataTypeName(0));
                Assert.False(await reader.ReadAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("local")]
        [InlineData("columns")]
        [InlineData("truncated")]
        [InlineData("row")]
        public async Task MalformedResponsesDiscardSocketWithoutUploadingOrRetrying(string kind)
        {
            var server = Server(async channel =>
            {
                await Query(channel, "unsafe");
                if (kind == "local") await channel.WritePacketAsync(new byte[] { 251, 99, 58, 92, 115, 101, 99, 114, 101, 116 });
                else if (kind == "columns") await channel.WritePacketAsync(new byte[] { 252, 255, 255 });
                else
                {
                    await channel.WritePacketAsync(new byte[] { 1 });
                    if (kind == "truncated") await channel.WritePacketAsync(new byte[] { 3, 100 });
                    else
                    {
                        await channel.WritePacketAsync(Column("n"));
                        await channel.WritePacketAsync(Eof());
                        await channel.WritePacketAsync(new byte[] { 3, 49 });
                    }
                }
                await Assert.ThrowsAnyAsync<Exception>(() => channel.ReadPacketAsync());
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "unsafe";
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => command.ExecuteNonQueryAsync());
                Assert.True(error.IsExecutionOutcomeUnknown);
                Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task PreflightRejectionsSendNoPacketAndKeepConnectionUsable()
        {
            var server = Server(async channel =>
            {
                await Query(channel, "allowed");
                await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "allowed";
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteScalarAsync(cancellation.Token));
                Assert.Equal(cancellation.Token, canceled.CancellationToken);
                var parameter = command.CreateParameter();
                parameter.Value = "secret";
                command.Parameters.Add(parameter);
                Assert.Throws<ArgumentException>(() => command.ExecuteNonQuery());
                command.Parameters.Clear();
                command.CommandType = CommandType.StoredProcedure;
                Assert.Throws<NotSupportedException>(() => command.ExecuteNonQuery());
                command.CommandType = CommandType.Text;
                Assert.Throws<NotSupportedException>(() => command.ExecuteReader(CommandBehavior.SequentialAccess));
                using (var scope = new System.Transactions.TransactionScope(System.Transactions.TransactionScopeAsyncFlowOption.Enabled))
                    await Assert.ThrowsAsync<NotSupportedException>(() => command.ExecuteNonQueryAsync());
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task ReaderErrorEndsThatReaderWithoutCorruptingTheNextCommand()
        {
            var server = Server(async channel =>
            {
                await Query(channel, "rows then error");
                await channel.WritePacketAsync(new byte[] { 1 });
                await channel.WritePacketAsync(Column("n"));
                await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(new byte[] { 1, 49 });
                await channel.WritePacketAsync(Error());
                channel.BeginCommand();
                await Query(channel, "next");
                await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "rows then error";
                using var reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => reader.ReadAsync());
                Assert.False(error.IsExecutionOutcomeUnknown);
                command.CommandText = "next";
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
                Assert.Throws<InvalidOperationException>(() => reader.Read());
                Assert.Throws<InvalidOperationException>(() => reader.GetValue(0));
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task AsyncReadWaitsForNetworkAndPerReadCancellationKeepsItsCause()
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel =>
            {
                await Query(channel, "slow rows");
                await channel.WritePacketAsync(new byte[] { 1 });
                await channel.WritePacketAsync(Column("n"));
                await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(new byte[] { 1, 49 });
                ready.TrySetResult();
                await Assert.ThrowsAnyAsync<Exception>(() => channel.ReadPacketAsync());
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "slow rows";
                using var reader = await command.ExecuteReaderAsync();
                await ready.Task;
                Assert.True(await reader.ReadAsync());
                using var cancellation = new CancellationTokenSource();
                var reading = reader.ReadAsync(cancellation.Token);
                Assert.False(reading.IsCompleted);
                cancellation.Cancel();
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => reading.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.True(error.IsExecutionOutcomeUnknown);
                var cause = Assert.IsType<OperationCanceledException>(error.InnerException);
                Assert.Equal(cancellation.Token, cause.CancellationToken);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task UnrepresentableTimeoutDoesNotAcquireOrStrandTheSession()
        {
            var server = Server(async channel =>
            {
                await Query(channel, "valid");
                await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "invalid";
                command.CommandTimeout = int.MaxValue;
                Assert.Throws<ArgumentOutOfRangeException>(() => command.ExecuteNonQuery());
                command.CommandTimeout = 1;
                command.CommandText = "valid";
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task NegotiatedDeprecatedEofOmitsMetadataEofAndUsesOkTerminator(bool empty)
        {
            var server = Server(async channel =>
            {
                await Query(channel, "modern eof");
                await channel.WritePacketAsync(new byte[] { 1 });
                await channel.WritePacketAsync(Column("n"));
                if (!empty) await channel.WritePacketAsync(new byte[] { 1, 49 });
                var terminator = Ok();
                terminator[0] = 254;
                await channel.WritePacketAsync(terminator);
                channel.BeginCommand();
                await Query(channel, "next");
                await channel.WritePacketAsync(Ok(1));
            }, deprecateEof: true);
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "modern eof";
                using (var reader = await command.ExecuteReaderAsync())
                {
                    Assert.Equal(!empty, reader.HasRows);
                    Assert.Equal(!empty, await reader.ReadAsync());
                    if (!empty) Assert.Equal(1, reader.GetInt32(0));
                    Assert.False(await reader.ReadAsync());
                    Assert.Equal(-1, reader.RecordsAffected);
                }
                command.CommandText = "next";
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(1, false, "-128", typeof(sbyte), (sbyte)-128)]
        [InlineData(1, true, "255", typeof(byte), (byte)255)]
        [InlineData(2, false, "-32768", typeof(short), (short)-32768)]
        [InlineData(2, true, "65535", typeof(ushort), (ushort)65535)]
        [InlineData(8, false, "-9223372036854775808", typeof(long), long.MinValue)]
        [InlineData(8, true, "18446744073709551615", typeof(ulong), ulong.MaxValue)]
        [InlineData(9, false, "-8388608", typeof(int), -8388608)]
        [InlineData(9, true, "16777215", typeof(uint), 16777215U)]
        [InlineData(4, false, "1.25", typeof(float), 1.25f)]
        [InlineData(5, false, "-1.25e2", typeof(double), -125d)]
        public void CommonScalarTypesKeepSignednessAndCultureIndependentValues(byte type, bool unsigned, string text, Type fieldType, object expected)
        {
            foreach (var mode in new[] { OceanBaseCompatibilityMode.MySql, OceanBaseCompatibilityMode.Oracle })
            {
                var column = ResultColumn.Parse(Column("value", type, flags: unsigned ? (ushort)32 : (ushort)0), mode);
                Assert.Equal(fieldType, column.FieldType);
                Assert.Equal(expected, column.Decode(Encoding.UTF8.GetBytes(text)));
                Assert.Same(DBNull.Value, column.Decode(null));
            }
        }

        [Fact]
        public void MysqlIntAndBinaryColumnsRetainTheirOwnContracts()
        {
            var integer = ResultColumn.Parse(Column("value", 3, flags: 32), OceanBaseCompatibilityMode.MySql);
            Assert.Equal(typeof(uint), integer.FieldType);
            Assert.Equal(uint.MaxValue, integer.Decode(Encoding.UTF8.GetBytes("4294967295")));
            var binary = ResultColumn.Parse(Column("bytes", 253, charset: 63), OceanBaseCompatibilityMode.Oracle);
            var bytes = new byte[] { 255, 0, 128 };
            Assert.Equal(typeof(byte[]), binary.FieldType);
            var decoded = Assert.IsType<byte[]>(binary.Decode(bytes));
            Assert.Equal(bytes, decoded);
            Assert.NotSame(bytes, decoded);
            var text = ResultColumn.Parse(Column("text", 253), OceanBaseCompatibilityMode.Oracle);
            Assert.Equal("海洋", text.Decode(Encoding.UTF8.GetBytes("海洋")));
            Assert.Throws<DecoderFallbackException>(() => text.Decode(bytes));
            Assert.Throws<OverflowException>(() => integer.Decode(Encoding.UTF8.GetBytes("4294967296")));
        }

        [Theory]
        [InlineData(10, 45)]
        [InlineData(207, 45)]
        [InlineData(16, 63)]
        [InlineData(253, 8)]
        public void UnsupportedTypesOrCharsetsNeverBecomeGuessedStrings(byte type, ushort charset)
        {
            var column = ResultColumn.Parse(Column("unsupported", type, charset), OceanBaseCompatibilityMode.Oracle);
            Assert.Throws<NotSupportedException>(() => column.FieldType);
            Assert.Throws<NotSupportedException>(() => column.Decode(new byte[] { 49 }));
        }
    }
}
