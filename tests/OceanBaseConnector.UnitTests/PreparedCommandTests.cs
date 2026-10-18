using System;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        private static async Task<byte[]> StatementCommand(PacketChannel channel, byte code)
        {
            channel.BeginCommand();
            var request = await channel.ReadPacketAsync();
            Assert.Equal(code, request[0]);
            return request;
        }

        private static async Task Prepared(PacketChannel channel, byte id, ushort parameters, ushort columns = 0, bool deprecated = false)
        {
            await channel.WritePacketAsync(new byte[] { 0, id, 0, 0, 0, (byte)columns, (byte)(columns >> 8), (byte)parameters, (byte)(parameters >> 8), 0, 0, 0 });
            for (var i = 0; i < parameters; i++) await channel.WritePacketAsync(Column("p", 253));
            if (parameters > 0 && !deprecated) await channel.WritePacketAsync(Eof());
            for (var i = 0; i < columns; i++) await channel.WritePacketAsync(Column("n", 8));
            if (columns > 0 && !deprecated) await channel.WritePacketAsync(Eof());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task PreparedBindingsRepeatInSqlOrderAndReuseIdUntilCommandDisposal(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                var prepare = await StatementCommand(channel, 22);
                Assert.Equal("update t set a=?, b=?", System.Text.Encoding.UTF8.GetString(prepare, 1, prepare.Length - 1));
                await Prepared(channel, 42, 2);
                for (var i = 0; i < 2; i++)
                {
                    var execute = await StatementCommand(channel, 23);
                    var value = (byte)('a' + i);
                    Assert.Equal(new byte[] { 23, 42, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 253, 0, 253, 0, 1, value, 1, value }, execute);
                    await channel.WritePacketAsync(Ok((byte)(i + 1)));
                }
                Assert.Equal(new byte[] { 25, 42, 0, 0, 0 }, await StatementCommand(channel, 25));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "update t set a=:value, b=:VALUE";
                var parameter = command.CreateParameter();
                parameter.ParameterName = ":Value";
                command.Parameters.Add(parameter);
                if (asynchronous) await command.PrepareAsync(); else command.Prepare();
                for (var i = 0; i < 2; i++)
                {
                    parameter.Value = ((char)('a' + i)).ToString();
                    Assert.Equal(i + 1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                }
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AutomaticPreparationStreamsBinaryRowsAndMultipleResults(bool deprecated)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22);
                await Prepared(channel, 7, 1, 2, deprecated);
                Assert.Equal(new byte[] { 23, 7, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 8, 0, 42, 0, 0, 0, 0, 0, 0, 0 }, await StatementCommand(channel, 23));
                await channel.WritePacketAsync(new byte[] { 2 });
                await channel.WritePacketAsync(Column("number", 8));
                await channel.WritePacketAsync(Column("nullable", 253));
                if (!deprecated) await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(new byte[] { 0, 8, 42, 0, 0, 0, 0, 0, 0, 0 });
                if (deprecated)
                {
                    var terminator = Ok(status: 14);
                    terminator[0] = 254;
                    await channel.WritePacketAsync(terminator);
                }
                else await channel.WritePacketAsync(Eof(14));
                await channel.WritePacketAsync(Ok(3));
                await StatementCommand(channel, 25);
            }, deprecated);
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "select :id";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "id", DbType = DbType.Int64, Value = 42L });
                using var reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(42L, reader.GetInt64(0));
                Assert.True(reader.IsDBNull(1));
                Assert.False(await reader.ReadAsync());
                Assert.True(await reader.NextResultAsync());
                Assert.Equal(3, reader.RecordsAffected);
                Assert.False(await reader.NextResultAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("netstandard2.1")]
        [InlineData("net10.0")]
        public async Task PrepareAsyncWaitsAsynchronouslyForMetadata(string target)
        {
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22);
                waiting.SetResult();
                await release.Task;
                await Prepared(channel, 9, 0);
                await StatementCommand(channel, 23);
                await channel.WritePacketAsync(Ok(2));
                await StatementCommand(channel, 25);
            });
            var context = new AssemblyLoadContext("prepare-" + target, isCollectible: true);
            var path = Path.GetFullPath("../../../../../src/OceanBaseConnector/bin/Release/" + target + "/OceanBaseConnector.dll", AppContext.BaseDirectory);
            var assembly = context.LoadFromAssemblyPath(path);
            using var connection = (DbConnection)Activator.CreateInstance(assembly.GetType("OceanBaseConnector.OceanBaseConnection")!, Configuration(server.Port))!;
            try
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "select 1";
                var preparing = command.PrepareAsync();
                await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.False(preparing.IsCompleted);
                release.SetResult();
                await preparing.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(2, await command.ExecuteNonQueryAsync());
            }
            finally { release.TrySetResult(); connection.Close(); server.Stop(); context.Unload(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task PrepareErrorIsTerminalAndNeverFallsBackToTextOrExecution()
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22);
                await channel.WritePacketAsync(Error());
                channel.BeginCommand();
                await Query(channel, "allowed");
                await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "unsupported prepared SQL";
                    var error = await Assert.ThrowsAsync<OceanBaseException>(() => command.PrepareAsync());
                    Assert.False(error.IsExecutionOutcomeUnknown);
                    Assert.Equal(1064, error.ServerErrorCode);
                }
                using var allowed = connection.CreateCommand();
                allowed.CommandText = "allowed";
                Assert.Equal(1, await allowed.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task PreparationMetadataErrorClosesKnownStatementAndKeepsSessionUsable()
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22);
                await channel.WritePacketAsync(new byte[] { 0, 51, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0 });
                await channel.WritePacketAsync(Error());
                Assert.Equal(new byte[] { 25, 51, 0, 0, 0 }, await StatementCommand(channel, 25));
                channel.BeginCommand();
                await Query(channel, "allowed");
                await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "select :id";
                    command.Parameters.Add(new OceanBaseParameter { ParameterName = "id", Value = "a" });
                    var error = await Assert.ThrowsAsync<OceanBaseException>(() => command.PrepareAsync());
                    Assert.False(error.IsExecutionOutcomeUnknown);
                    Assert.Equal(1064, error.ServerErrorCode);
                }
                using var allowed = connection.CreateCommand();
                allowed.CommandText = "allowed";
                Assert.Equal(1, await allowed.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task ServerParameterCountMismatchClosesIdWithoutExecutingAndKeepsSessionUsable()
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22);
                await Prepared(channel, 17, 2);
                Assert.Equal(new byte[] { 25, 17, 0, 0, 0 }, await StatementCommand(channel, 25));
                channel.BeginCommand();
                await Query(channel, "allowed");
                await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "select :id";
                    command.Parameters.Add(new OceanBaseParameter { ParameterName = "id", Value = "a" });
                    await Assert.ThrowsAsync<ArgumentException>(() => command.ExecuteScalarAsync());
                }
                using var allowed = connection.CreateCommand();
                allowed.CommandText = "allowed";
                Assert.Equal(1, await allowed.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task ChangedSqlClosesPreviousIdBeforePreparingReplacement()
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22);
                await Prepared(channel, 20, 0);
                Assert.Equal(new byte[] { 25, 20, 0, 0, 0 }, await StatementCommand(channel, 25));
                var replacement = await StatementCommand(channel, 22);
                Assert.Equal("second", System.Text.Encoding.UTF8.GetString(replacement, 1, replacement.Length - 1));
                await Prepared(channel, 21, 0);
                Assert.Equal(new byte[] { 23, 21, 0, 0, 0, 0, 1, 0, 0, 0 }, await StatementCommand(channel, 23));
                await channel.WritePacketAsync(Ok(4));
                Assert.Equal(new byte[] { 25, 21, 0, 0, 0 }, await StatementCommand(channel, 25));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "first";
                await command.PrepareAsync();
                command.CommandText = "second";
                Assert.Equal(4, await command.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CancellationDuringPreparationDiffersFromCancellationAfterExecutionSend(bool execute)
        {
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22);
                if (execute) { await Prepared(channel, 10, 0); await StatementCommand(channel, 23); }
                waiting.SetResult();
                await Assert.ThrowsAsync<ProtocolException>(() => channel.ReadPacketAsync());
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "pending";
                if (execute) await command.PrepareAsync();
                using var cancellation = new CancellationTokenSource();
                Task operation = execute ? command.ExecuteNonQueryAsync(cancellation.Token) : command.PrepareAsync(cancellation.Token);
                await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
                cancellation.Cancel();
                if (execute)
                {
                    var error = await Assert.ThrowsAsync<OceanBaseException>(() => operation.WaitAsync(TimeSpan.FromSeconds(10)));
                    Assert.True(error.IsExecutionOutcomeUnknown);
                    Assert.IsType<OperationCanceledException>(error.InnerException);
                }
                else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task ReopeningLogicalConnectionNeverReusesTheOldPhysicalStatementId()
        {
            var first = Server(async channel =>
            {
                await StatementCommand(channel, 22);
                await Prepared(channel, 31, 1);
                await StatementCommand(channel, 23); // Disconnect without a terminal execution response.
            });
            var second = Server(async channel =>
            {
                await StatementCommand(channel, 22);
                await Prepared(channel, 32, 1);
                var execute = await StatementCommand(channel, 23);
                Assert.Equal(32, execute[1]);
                await channel.WritePacketAsync(Ok(1));
                Assert.Equal(new byte[] { 25, 32, 0, 0, 0 }, await StatementCommand(channel, 25));
            });
            using var connection = new OceanBaseConnection(Configuration(first.Port));
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "update t set a=:id";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "id", Value = "a" });
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => command.ExecuteNonQueryAsync());
                Assert.True(error.IsExecutionOutcomeUnknown);
                connection.Close();
                connection.ConnectionString = Configuration(second.Port);
                await connection.OpenAsync();
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); first.Stop(); second.Stop(); }
            await Task.WhenAll(first.Completion, second.Completion).WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(new byte[] { 0, 1 })]
        [InlineData(new byte[] { 0, 1, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0 })]
        [InlineData(new byte[] { 0, 1, 0, 0, 0, 1, 16, 0, 0, 0, 0, 0 })]
        public async Task MalformedPreparationDestroysSessionWithoutUnknownSqlOutcome(byte[] header)
        {
            var server = Server(async channel => { await StatementCommand(channel, 22); await channel.WritePacketAsync(header); });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "prepare only";
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => command.PrepareAsync());
                Assert.False(error.IsExecutionOutcomeUnknown);
                Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task ExecuteErrorIsTerminalAndPreparedIdCanBeReusedWithChangedType()
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22);
                await Prepared(channel, 60, 1);
                var first = await StatementCommand(channel, 23);
                Assert.Equal(new byte[] { 1, 1, 6, 0 }, first.AsSpan(10).ToArray());
                await channel.WritePacketAsync(Error());
                var next = await StatementCommand(channel, 23);
                Assert.Equal(new byte[] { 0, 1, 253, 0, 1, 120 }, next.AsSpan(10).ToArray());
                Assert.Equal(60, next[1]);
                await channel.WritePacketAsync(Ok(1));
                await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "select :id";
                var parameter = new OceanBaseParameter { ParameterName = "id", Value = DBNull.Value };
                command.Parameters.Add(parameter);
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => command.ExecuteNonQueryAsync());
                Assert.False(error.IsExecutionOutcomeUnknown);
                Assert.Equal(ConnectionState.Open, connection.State);
                parameter.Value = "x";
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(new byte[] { 1, 0 })]
        [InlineData(new byte[] { 0, 1 })]
        [InlineData(new byte[] { 0 })]
        [InlineData(new byte[] { 0, 0, 42 })]
        [InlineData(new byte[] { 0, 4, 42 })]
        public async Task MalformedBinaryRowBreaksSessionAndReportsUnknownOutcome(byte[] row)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22);
                await Prepared(channel, 61, 0);
                await StatementCommand(channel, 23);
                await channel.WritePacketAsync(new byte[] { 1 });
                await channel.WritePacketAsync(Column("n", 8));
                await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(row);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "select row";
                await command.PrepareAsync();
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => command.ExecuteReaderAsync());
                Assert.True(error.IsExecutionOutcomeUnknown);
                Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task UnreadBinaryReaderDestroysItsPreparedSession()
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22);
                await Prepared(channel, 62, 0);
                await StatementCommand(channel, 23);
                await channel.WritePacketAsync(new byte[] { 1 });
                await channel.WritePacketAsync(Column("n", 8));
                await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(new byte[] { 0, 0, 42, 0, 0, 0, 0, 0, 0, 0 });
                await Assert.ThrowsAsync<ProtocolException>(() => channel.ReadPacketAsync());
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "select unread";
                await command.PrepareAsync();
                using (var reader = await command.ExecuteReaderAsync()) { Assert.True(reader.HasRows); }
                Assert.Equal(ConnectionState.Broken, connection.State);
                await Assert.ThrowsAsync<InvalidOperationException>(() => command.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task DisposingPreparedCommandWhileAnotherReaderIsActiveDefersCloseUntilNextCommand()
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22);
                await Prepared(channel, 41, 0);
                channel.BeginCommand();
                await Query(channel, "rows");
                await channel.WritePacketAsync(new byte[] { 1 });
                await channel.WritePacketAsync(Column("n"));
                await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(new byte[] { 1, 49 });
                await channel.WritePacketAsync(Eof());
                Assert.Equal(new byte[] { 25, 41, 0, 0, 0 }, await StatementCommand(channel, 25));
                channel.BeginCommand();
                await Query(channel, "next");
                await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var prepared = connection.CreateCommand();
                prepared.CommandText = "prepare only";
                await prepared.PrepareAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "rows";
                using (var reader = await command.ExecuteReaderAsync())
                {
                    prepared.Dispose();
                    Assert.True(await reader.ReadAsync());
                    Assert.Equal(1, reader.GetInt32(0));
                    Assert.False(await reader.ReadAsync());
                }
                command.CommandText = "next";
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
