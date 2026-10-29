using System;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(false, false, 0)]
        [InlineData(false, true, 0)]
        [InlineData(true, false, 0)]
        [InlineData(true, true, 0)]
        [InlineData(false, false, 20000)]
        [InlineData(false, true, 20000)]
        [InlineData(true, false, 20000)]
        [InlineData(true, true, 20000)]
        public async Task MultiStatementStreamsUseBoundedReadsSnapshotsAndTextOnly(bool oracle, bool asynchronous, int size)
        {
            var source = new LongDataSource(size, asynchronous) { ChunkSize = 17 };
            var expected = Convert.ToHexString(Enumerable.Range(0, size).Select(i => (byte)(i % 251)).ToArray());
            async Task Script(PacketChannel channel)
            {
                await Query(channel, "INSERT INTO t VALUES ((" + (oracle ? "'" : "X'") + expected + "'),(7)); UPDATE t SET v=(NULL)");
                await channel.WritePacketAsync(Ok(2, 10)); await channel.WritePacketAsync(Ok(3, 2));
                channel.BeginCommand(); await Query(channel, "after stream multi"); await channel.WritePacketAsync(Ok(1));
            }
            var server = oracle ? Server(Script, multiStatements: true) : MySqlProcedureServer(Script);
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Allow Multi Statements=true");
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand();
                command.CommandText = oracle ? "INSERT INTO t VALUES (:p,:n); UPDATE t SET v=:v" : "INSERT INTO t VALUES (?,?); UPDATE t SET v=?";
                var parameter = new OceanBaseParameter { ParameterName = "p", DbType = DbType.Binary, Value = source };
                var number = new OceanBaseParameter { ParameterName = "n", Value = 7 };
                command.Parameters.Add(parameter); command.Parameters.Add(number);
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "v", DbType = DbType.Binary, Value = DBNull.Value });
                source.BeforeRead = _ => { parameter.Value = new byte[] { 99 }; number.Value = 99; command.CommandText = "mutated"; };
                Assert.Equal(5, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                Assert.Equal(size, source.BytesRead); Assert.False(source.WasDisposed);
                command.Parameters.Clear(); command.CommandText = "after stream multi";
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task MultiStatementStreamsStartAtCurrentPositionAndReuseAtEof(bool oracle, bool asynchronous)
        {
            async Task Script(PacketChannel channel)
            {
                foreach (var hex in new[] { "00275CFF", "" })
                {
                    await Query(channel, "SELECT (" + (oracle ? "'" : "X'") + hex + "'); SELECT 1");
                    await channel.WritePacketAsync(Ok(2, 10)); await channel.WritePacketAsync(Ok(3, 2));
                    channel.BeginCommand();
                }
            }
            var server = oracle ? Server(Script, multiStatements: true) : MySqlProcedureServer(Script);
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Allow Multi Statements=true");
            using var source = new MemoryStream(new byte[] { 99, 0, 39, 92, 255 }); source.Position = 1;
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand();
                command.CommandText = oracle ? "SELECT :p; SELECT 1" : "SELECT ?; SELECT 1";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", DbType = DbType.Binary, Value = source });
                for (var i = 0; i < 2; i++) Assert.Equal(5, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                Assert.Equal(5, source.Position); Assert.True(source.CanRead);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task MultiStatementStreamFailuresSendNothingAndRetainTheSession(bool oracle, bool asynchronous)
        {
            async Task Script(PacketChannel channel) { await Query(channel, "after failed stream"); await channel.WritePacketAsync(Ok(1)); }
            var server = oracle ? Server(Script, multiStatements: true) : MySqlProcedureServer(Script);
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Allow Multi Statements=true");
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand();
                command.CommandText = oracle ? "SELECT :p; SELECT 1" : "SELECT ?; SELECT 1";
                var parameter = new OceanBaseParameter { ParameterName = "p", DbType = DbType.Binary }; command.Parameters.Add(parameter);
                var failed = new LongDataSource(20000, asynchronous) { BeforeRead = n => { if (n > 0) throw new IOException("Source failed."); } };
                parameter.Value = failed;
                Func<Task> execute = () => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery());
                await Assert.ThrowsAsync<IOException>(execute); Assert.True(failed.BytesRead > 0); Assert.False(failed.WasDisposed);
                foreach (var count in new[] { -1, 8193 })
                {
                    using var source = new InvalidMultiStream(count); parameter.Value = source;
                    await Assert.ThrowsAsync<IOException>(execute); Assert.True(source.CanRead);
                }
                Assert.Equal(ConnectionState.Open, connection.State);
                command.Parameters.Clear(); command.CommandText = "after failed stream"; Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task MultiStatementStreamValidationDoesNotReadSources(bool oracle, bool asynchronous)
        {
            async Task Script(PacketChannel channel) { await Query(channel, "after invalid stream"); await channel.WritePacketAsync(Ok(1)); }
            var server = oracle ? Server(Script, multiStatements: true) : MySqlProcedureServer(Script);
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Allow Multi Statements=true");
            var source = new LongDataSource(1, asynchronous);
            try
            {
                await connection.OpenAsync(); using var command = (OceanBaseCommand)connection.CreateCommand();
                var parameter = new OceanBaseParameter { ParameterName = "p", DbType = DbType.Binary, Value = source }; command.Parameters.Add(parameter);
                var second = new OceanBaseParameter { ParameterName = "q", Value = 1 }; command.Parameters.Add(second);
                Func<Task> execute = () => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery());
                foreach (var invalid in new Action[] { () => parameter.Size = 1, () => parameter.Precision = 1, () => parameter.Scale = 1,
                    () => parameter.DbType = DbType.String, () => parameter.Direction = ParameterDirection.Output,
                    () => command.FetchSize = 1, () => second.Value = source })
                {
                    command.CommandText = oracle ? "SELECT :p; SELECT :q" : "SELECT ?; SELECT ?";
                    parameter.Size = 0; parameter.Precision = parameter.Scale = 0; parameter.DbType = DbType.Binary;
                    parameter.Direction = ParameterDirection.Input; command.FetchSize = 0; second.Value = 1;
                    invalid(); await Assert.ThrowsAsync<NotSupportedException>(execute);
                }
                command.FetchSize = 0; second.Value = double.NaN; parameter.Direction = ParameterDirection.Input;
                await Assert.ThrowsAsync<ArgumentException>(execute);
                second.Value = 1; command.CommandText = oracle ? "SELECT :p; SELECT :missing" : "SELECT ?; SELECT ?,?";
                await Assert.ThrowsAsync<ArgumentException>(execute);
                Assert.Equal(0, source.BytesRead); Assert.False(source.WasDisposed);
                command.Parameters.Clear(); command.CommandText = "after invalid stream"; Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "caller", false)]
        [InlineData(true, "caller", false)]
        [InlineData(false, "command", false)]
        [InlineData(true, "command", false)]
        [InlineData(false, "timeout", false)]
        [InlineData(true, "timeout", false)]
        [InlineData(false, "caller", true)]
        [InlineData(true, "caller", true)]
        [InlineData(false, "timeout", true)]
        [InlineData(true, "timeout", true)]
        public async Task MultiStatementStreamCancellationRetainsLeaseUntilSourceExits(bool oracle, string cancellation, bool cooperative)
        {
            var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var caller = new CancellationTokenSource();
            var source = new LongDataSource(1, true);
            source.BeforeReadAsync = async token => { reading.TrySetResult(); if (cooperative) await Task.Delay(Timeout.Infinite, token); else await release.Task; };
            async Task Script(PacketChannel channel) { await Query(channel, "after canceled stream"); await channel.WritePacketAsync(Ok(1)); }
            var server = oracle ? Server(Script, multiStatements: true) : MySqlProcedureServer(Script);
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Allow Multi Statements=true");
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand();
                command.CommandText = oracle ? "SELECT :p; SELECT 1" : "SELECT ?; SELECT 1";
                command.CommandTimeout = cancellation == "timeout" ? 1 : 10;
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", Value = source });
                var pending = command.ExecuteNonQueryAsync(caller.Token);
                if (await Task.WhenAny(reading.Task, pending) == pending) await pending;
                await reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
                using var busy = connection.CreateCommand(); busy.CommandText = "after canceled stream";
                Assert.Throws<InvalidOperationException>(() => busy.ExecuteNonQuery());
                if (cancellation == "caller") caller.Cancel(); else if (cancellation == "command") command.Cancel();
                else await Task.Delay(1200);
                if (!cooperative)
                {
                    Assert.False(pending.IsCompleted); Assert.Throws<InvalidOperationException>(() => busy.ExecuteNonQuery()); release.SetResult();
                }
                if (cancellation == "timeout") await Assert.ThrowsAsync<TimeoutException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
                else
                {
                    var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
                    if (cancellation == "caller") Assert.Equal(caller.Token, error.CancellationToken);
                }
                Assert.False(source.WasDisposed); Assert.Equal(ConnectionState.Open, connection.State);
                Assert.Equal(1, await busy.ExecuteNonQueryAsync());
            }
            finally { release.TrySetResult(); connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MultiStatementStreamBudgetIncludesAllHexSourcesAndOtherLiterals(bool asynchronous)
        {
            var server = MySqlProcedureServer(async channel => { await Query(channel, "after oversized stream"); await channel.WritePacketAsync(Ok(1)); });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Allow Multi Statements=true");
            var first = new LongDataSource(16 * 1024 * 1024, asynchronous) { ChunkSize = 8192 };
            var second = new LongDataSource(16 * 1024 * 1024, asynchronous) { ChunkSize = 8192 };
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "SELECT ?, ?; SELECT ?";
                command.Parameters.Add(new OceanBaseParameter { Value = first }); command.Parameters.Add(new OceanBaseParameter { Value = second });
                command.Parameters.Add(new OceanBaseParameter { Value = new byte[] { 1, 2, 3 } });
                await Assert.ThrowsAsync<ArgumentException>(() => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
                Assert.Equal(16 * 1024 * 1024, first.BytesRead); Assert.True(second.BytesRead > 0);
                Assert.False(first.WasDisposed); Assert.False(second.WasDisposed); Assert.Equal(ConnectionState.Open, connection.State);
                command.Parameters.Clear(); command.CommandText = "after oversized stream"; Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task MultiStatementStreamIdentityUsesReferencesRatherThanUserEquality(bool oracle, bool asynchronous)
        {
            async Task Script(PacketChannel channel)
            {
                var prefix = oracle ? "'" : "X'";
                await Query(channel, "SELECT (" + prefix + "01'); SELECT (" + prefix + "02')");
                await channel.WritePacketAsync(Ok(2, 10)); await channel.WritePacketAsync(Ok(3, 2));
            }
            var server = oracle ? Server(Script, multiStatements: true) : MySqlProcedureServer(Script);
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Allow Multi Statements=true");
            using var first = new EqualMultiStream(1); using var second = new EqualMultiStream(2);
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand();
                command.CommandText = oracle ? "SELECT :p; SELECT :q" : "SELECT ?; SELECT ?";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", Value = first });
                var parameter = new OceanBaseParameter { ParameterName = "q", Value = first }; command.Parameters.Add(parameter);
                Func<Task<int>> execute = () => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery());
                await Assert.ThrowsAsync<NotSupportedException>(execute); Assert.Equal(0, first.Position);
                parameter.Value = second; Assert.Equal(5, await execute()); Assert.True(first.CanRead && second.CanRead);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MultiStatementStreamsRetainMySqlMixedStringPolicy(bool asynchronous)
        {
            var server = MySqlProcedureServer(async channel =>
            {
                await MySqlLiteralState(channel, modes: "NO_BACKSLASH_ESCAPES"); channel.BeginCommand();
                await Query(channel, "SET sql_mode=''; SELECT (X'00275CFF'), (REPLACE('x','x',_utf8mb4 X'61275C00')); SELECT 1");
                await channel.WritePacketAsync(Ok(2, 10)); await channel.WritePacketAsync(Ok(3, 2)); channel.BeginCommand();
                await MySqlLiteralState(channel); channel.BeginCommand();
                await Query(channel, "after mixed stream"); await channel.WritePacketAsync(Ok(1));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Allow Multi Statements=true");
            using var source = new MemoryStream(new byte[] { 0, 39, 92, 255 });
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand();
                command.CommandText = "SET sql_mode=''; SELECT ?, ?; SELECT 1";
                command.Parameters.Add(new OceanBaseParameter { Value = source }); command.Parameters.Add(new OceanBaseParameter { Value = "a'\\\0" });
                Func<Task<int>> execute = () => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery());
                Assert.Equal(5, await execute()); source.Position = 0;
                command.CommandText = "SET NAMES utf8mb4; SELECT ?, ?";
                await Assert.ThrowsAsync<NotSupportedException>(execute); Assert.Equal(0, source.Position);
                command.Parameters.Clear(); command.CommandText = "after mixed stream"; Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MultiStatementStreamsRetainOracleNationalLiteralPolicy(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await Query(channel, "SELECT ('00275CFF'), (u'\\4e2d') FROM DUAL; SELECT 1 FROM DUAL");
                await channel.WritePacketAsync(Ok(2, 10)); await channel.WritePacketAsync(Ok(3, 2));
            }, multiStatements: true, nationalCharacterSet: "unsupported");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Allow Multi Statements=true");
            using var source = new MemoryStream(new byte[] { 0, 39, 92, 255 });
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand();
                command.CommandText = "SELECT :p, :n FROM DUAL; SELECT 1 FROM DUAL";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", Value = source });
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "n", OceanBaseDbType = OceanBaseDbType.NVarchar2, Value = "中" });
                Assert.Equal(5, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                Assert.True(source.CanRead);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private sealed class EqualMultiStream : MemoryStream
        {
            internal EqualMultiStream(byte value) : base(new[] { value }) { }
            public override bool Equals(object? obj) => obj is EqualMultiStream;
            public override int GetHashCode() => 0;
        }

        private sealed class InvalidMultiStream : MemoryStream
        {
            private readonly int _count;
            internal InvalidMultiStream(int count) { _count = count; }
            public override int Read(byte[] buffer, int offset, int count) => _count;
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromResult(_count);
        }
    }
}
