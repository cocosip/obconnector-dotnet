using System;
using System.Data;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        private const string MySqlLiteralStateSql = "SELECT @@character_set_client, @@character_set_connection, @@collation_connection, @@sql_mode";

        private static async Task MySqlLiteralState(PacketChannel channel, string client = "utf8mb4", string connection = "utf8mb4",
            string collation = "utf8mb4_general_ci", string modes = "", bool deprecated = false)
        {
            channel.BeginCommand();
            await Query(channel, MySqlLiteralStateSql, deprecated);
            await channel.WritePacketAsync(new byte[] { 4 });
            for (var i = 0; i < 4; i++) await channel.WritePacketAsync(Column("s" + i, 253));
            if (!deprecated) await channel.WritePacketAsync(Eof(2));
            using var bytes = new MemoryStream();
            foreach (var value in new[] { client, connection, collation, modes })
            {
                var encoded = Encoding.UTF8.GetBytes(value);
                Assert.True(encoded.Length < 251);
                bytes.WriteByte((byte)encoded.Length); bytes.Write(encoded);
            }
            await channel.WritePacketAsync(bytes.ToArray());
            await channel.WritePacketAsync(MySqlProcedureEnd(deprecated));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task MySqlClientStringsUseFreshSessionStateForEachFallback(bool asynchronous, bool deprecated)
        {
            var server = MySqlProcedureServer(async channel =>
            {
                for (var i = 0; i < 2; i++)
                {
                    await StatementCommand(channel, 22); await channel.WritePacketAsync(UnsupportedPrepare());
                    await MySqlLiteralState(channel, modes: i == 0 ? "ANSI_QUOTES" : "ANSI_QUOTES,NO_BACKSLASH_ESCAPES", deprecated: deprecated);
                    channel.BeginCommand();
                    await Query(channel, i == 0 ? "update t set x=('a''\\\\中😀')" : "update t set x=('a''\\中😀')", deprecated);
                    await channel.WritePacketAsync(Ok(1, 2));
                }
                channel.BeginCommand(); await Query(channel, "after strings", deprecated); await channel.WritePacketAsync(Ok(1, 2));
            }, deprecated: deprecated);
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = (OceanBaseCommand)connection.CreateCommand();
                command.AllowClientSidePrepareFallback = true; command.CommandText = "update t set x=?";
                command.Parameters.Add(new OceanBaseParameter { Value = "a'\\中😀" });
                for (var i = 0; i < 2; i++) Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                command.Parameters.Clear(); command.CommandText = "after strings"; Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MySqlClientStringsRejectUnsupportedDrainedStateAndRequeryAfterRecovery(bool asynchronous)
        {
            var server = MySqlProcedureServer(async channel =>
            {
                await StatementCommand(channel, 22); await channel.WritePacketAsync(UnsupportedPrepare());
                await MySqlLiteralState(channel, client: "latin1");
                channel.BeginCommand(); await Query(channel, "SET NAMES utf8mb4"); await channel.WritePacketAsync(Ok(status: 2));
                await StatementCommand(channel, 22); await channel.WritePacketAsync(UnsupportedPrepare());
                await MySqlLiteralState(channel);
                channel.BeginCommand(); await Query(channel, "select ('中')"); await channel.WritePacketAsync(Ok(1, 2));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = (OceanBaseCommand)connection.CreateCommand();
                command.AllowClientSidePrepareFallback = true; command.CommandText = "select ?"; command.Parameters.Add(new OceanBaseParameter { Value = "中" });
                await Assert.ThrowsAsync<NotSupportedException>(() => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
                Assert.Equal(ConnectionState.Open, connection.State);
                using var change = connection.CreateCommand(); change.CommandText = "SET NAMES utf8mb4"; await change.ExecuteNonQueryAsync();
                Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task MySqlClientStringsDiscoveryCancellationNeverMakesTargetOutcomeUnknown(bool asynchronous, bool timeout)
        {
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = MySqlProcedureServer(async channel =>
            {
                await StatementCommand(channel, 22); await channel.WritePacketAsync(UnsupportedPrepare());
                channel.BeginCommand(); await Query(channel, MySqlLiteralStateSql); received.SetResult();
                channel.BeginCommand();
                var closed = await Record.ExceptionAsync(() => channel.ReadPacketAsync());
                Assert.True(closed is ProtocolException || closed is IOException);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            using var cancellation = new CancellationTokenSource();
            try
            {
                await connection.OpenAsync(); using var command = (OceanBaseCommand)connection.CreateCommand();
                command.AllowClientSidePrepareFallback = true; command.CommandText = "select ?"; command.CommandTimeout = timeout ? 1 : 0;
                command.Parameters.Add(new OceanBaseParameter { Value = "中" });
                var execution = asynchronous ? command.ExecuteNonQueryAsync(cancellation.Token) : Task.Run(() => command.ExecuteNonQuery());
                await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (!timeout) { if (asynchronous) cancellation.Cancel(); else command.Cancel(); }
                var error = await Record.ExceptionAsync(() => execution);
                if (timeout) Assert.IsType<TimeoutException>(error);
                else
                {
                    var canceled = Assert.IsAssignableFrom<OperationCanceledException>(error);
                    if (asynchronous) Assert.Equal(cancellation.Token, canceled.CancellationToken);
                }
                Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task MySqlClientStringsDiscoveryServerErrorsRemainKnown(bool asynchronous, bool connectionError)
        {
            var server = MySqlProcedureServer(async channel =>
            {
                await StatementCommand(channel, 22); await channel.WritePacketAsync(UnsupportedPrepare());
                channel.BeginCommand(); await Query(channel, MySqlLiteralStateSql);
                await channel.WritePacketAsync(UnsupportedPrepare(1231, connectionError ? "08006" : "42000"));
                if (!connectionError) { channel.BeginCommand(); await Query(channel, "after state error"); await channel.WritePacketAsync(Ok(1, 2)); }
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = (OceanBaseCommand)connection.CreateCommand();
                command.AllowClientSidePrepareFallback = true; command.CommandText = "select ?"; command.Parameters.Add(new OceanBaseParameter { Value = "中" });
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => asynchronous ? command.ExecuteNonQueryAsync() : Task.FromResult(command.ExecuteNonQuery()));
                Assert.Equal(1231, error.ServerErrorCode); Assert.False(error.IsExecutionOutcomeUnknown);
                Assert.Equal(connectionError ? ConnectionState.Broken : ConnectionState.Open, connection.State);
                if (!connectionError)
                {
                    command.Parameters.Clear(); command.CommandText = "after state error"; Assert.Equal(1, await command.ExecuteNonQueryAsync());
                }
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("columns")]
        [InlineData("null")]
        [InlineData("integer")]
        [InlineData("extra")]
        [InlineData("multiple")]
        [InlineData("invalid-utf8")]
        [InlineData("oversize")]
        [InlineData("disconnect")]
        [InlineData("empty")]
        public async Task MySqlClientStringsMalformedDiscoveryNeverSendsTarget(string kind)
        {
            var server = MySqlProcedureServer(async channel =>
            {
                await StatementCommand(channel, 22); await channel.WritePacketAsync(UnsupportedPrepare());
                channel.BeginCommand(); await Query(channel, MySqlLiteralStateSql);
                if (kind == "disconnect") return;
                var count = kind == "columns" ? 3 : 4;
                await channel.WritePacketAsync(new byte[] { (byte)count });
                for (var i = 0; i < count; i++) await channel.WritePacketAsync(Column("s", kind == "integer" ? (byte)3 : (byte)253));
                await channel.WritePacketAsync(Eof(2));
                if (kind != "empty")
                {
                    using var row = new MemoryStream();
                    var values = new[] { "utf8mb4", "utf8mb4", "utf8mb4_bin", kind == "oversize" ? new string('X', 4097) : "" };
                    for (var i = 0; i < count; i++) row.Write(kind == "null" && i == 0 ? new byte[] { 251 }
                        : WithLength(kind == "invalid-utf8" && i == 0 ? new byte[] { 255 } : Encoding.UTF8.GetBytes(values[i])));
                    await channel.WritePacketAsync(row.ToArray());
                    if (kind == "extra") await channel.WritePacketAsync(row.ToArray());
                }
                await channel.WritePacketAsync(Eof(kind == "multiple" ? (byte)10 : (byte)2));
                if (kind == "empty")
                {
                    channel.BeginCommand(); await Query(channel, "after empty state"); await channel.WritePacketAsync(Ok(1, 2));
                }
                else
                {
                    channel.BeginCommand();
                    var closed = await Record.ExceptionAsync(() => channel.ReadPacketAsync());
                    Assert.True(closed is ProtocolException || closed is IOException);
                }
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = (OceanBaseCommand)connection.CreateCommand();
                command.AllowClientSidePrepareFallback = true; command.CommandText = "select ?"; command.Parameters.Add(new OceanBaseParameter { Value = "中" });
                var error = await Record.ExceptionAsync(() => command.ExecuteNonQueryAsync()); Assert.NotNull(error);
                if (error is OceanBaseException database) Assert.False(database.IsExecutionOutcomeUnknown);
                Assert.Equal(kind == "empty" ? ConnectionState.Open : ConnectionState.Broken, connection.State);
                if (kind == "empty")
                {
                    command.Parameters.Clear(); command.CommandText = "after empty state"; Assert.Equal(1, await command.ExecuteNonQueryAsync());
                }
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MySqlClientStringsDiscoveryRetainsTransactionAndFrozenParameters(bool asynchronous)
        {
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = MySqlProcedureServer(async channel =>
            {
                await TransactionBegin(channel);
                await StatementCommand(channel, 22); received.SetResult(); await release.Task;
                await channel.WritePacketAsync(UnsupportedPrepare()); await MySqlLiteralState(channel);
                channel.BeginCommand(); await Query(channel, "update t set x=('original\\\\''中')"); await channel.WritePacketAsync(Ok(1, 1));
                channel.BeginCommand(); await TransactionEnd(channel, false);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
                using var command = (OceanBaseCommand)connection.CreateCommand(); command.CommandText = "update t set x=?"; command.AllowClientSidePrepareFallback = true;
                var parameter = new OceanBaseParameter { Value = "original\\'中" }; command.Parameters.Add(parameter);
                await Assert.ThrowsAsync<InvalidOperationException>(() => command.ExecuteNonQueryAsync());
                command.Transaction = transaction;
                var execution = asynchronous ? command.ExecuteNonQueryAsync() : Task.Run(() => command.ExecuteNonQuery());
                await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
                parameter.Value = "changed'; --"; parameter.DbType = DbType.Int32;
                command.CommandText = "changed"; command.AllowClientSidePrepareFallback = false;
                using var concurrent = connection.CreateCommand(); concurrent.Transaction = transaction; concurrent.CommandText = "must not run";
                await Assert.ThrowsAsync<InvalidOperationException>(() => concurrent.ExecuteNonQueryAsync());
                release.SetResult(); Assert.Equal(1, await execution); Assert.Same(connection, transaction.Connection);
                await transaction.RollbackAsync();
            }
            finally { release.TrySetResult(); connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
