using System;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ConnectionSchemaNeverRoundsTinyFractionsBeforeIntegerValidation(bool floating)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                var packets = DirectoryPackets(true, false, ordinal: floating ? "1.0000000000000002" : "1.00000000000000000000000000001");
                if (floating) packets[5] = Column("ORDINAL_POSITION", 5);
                using var input = new MemoryStream(); byte sequence = 1;
                foreach (var packet in packets) input.Write(ResponseFrame(packet, sequence++));
                InstallFaultStream(connection, new ProtocolFaultStream(input.ToArray()));
                var error = await Record.ExceptionAsync(() => connection.GetSchemaAsync("Columns"));
                Assert.NotNull(error); Assert.True(error is InvalidCastException || error is OverflowException);
                Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ConnectionSchemaOracleNumberMetadataConvertsExactly(bool asynchronous)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var input = new MemoryStream(); byte sequence = 1;
                foreach (var packet in DirectoryPackets(true, false, nativeNumbers: true)) input.Write(ResponseFrame(packet, sequence++));
                var transport = new ProtocolFaultStream(input.ToArray()); InstallFaultStream(connection, transport);
                var table = asynchronous ? await connection.GetSchemaAsync("Columns") : connection.GetSchema("Columns");
                var row = Assert.Single(table.Rows.Cast<DataRow>());
                Assert.Equal(1, row["ORDINAL_POSITION"]); Assert.Equal(4294967295L, row["CHARACTER_MAXIMUM_LENGTH"]); Assert.Equal(-2, row["NUMERIC_SCALE"]);
                Assert.Equal(DBNull.Value, row["DATETIME_PRECISION"]); Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "name")]
        [InlineData(true, "name")]
        [InlineData(false, "fractional")]
        [InlineData(true, "fractional")]
        [InlineData(false, "overflow")]
        [InlineData(true, "overflow")]
        [InlineData(false, "multiple")]
        [InlineData(true, "multiple")]
        public async Task ConnectionSchemaInvalidResultsNeverReturnUnreadSessionToService(bool asynchronous, string fault)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                var columns = fault == "fractional" || fault == "overflow";
                var packets = DirectoryPackets(columns, false, ordinal: fault == "fractional" ? "1.5" : fault == "overflow" ? "2147483648" : "1", nativeNumbers: columns);
                if (fault == "name") packets[1] = Column("WRONG", 253);
                if (fault == "multiple") { packets[packets.Length - 1] = Eof(14); packets = packets.Concat(DirectoryPackets(false, false)).ToArray(); }
                using var input = new MemoryStream(); byte sequence = 1;
                foreach (var packet in packets) input.Write(ResponseFrame(packet, sequence++));
                var transport = new ProtocolFaultStream(input.ToArray()); InstallFaultStream(connection, transport);
                var error = await Record.ExceptionAsync(() => asynchronous ? connection.GetSchemaAsync(columns ? "Columns" : "Tables") : Task.FromResult(connection.GetSchema(columns ? "Columns" : "Tables")));
                if (fault == "fractional") Assert.IsType<InvalidCastException>(error);
                else if (fault == "overflow") Assert.IsType<OverflowException>(error);
                else Assert.IsType<NotSupportedException>(error);
                Assert.Equal(ConnectionState.Broken, connection.State); Assert.True(transport.Disposed);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task ConnectionSchemaSnapshotsRestrictionsBeforeAwaitingPrepare()
        {
            var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); prepared.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Prepared(channel, 72, 1);
                var execute = await StatementCommand(channel, 23);
                Assert.Equal((byte)'A', execute[execute.Length - 1]);
                foreach (var packet in DirectoryPackets(false, false, true)) await channel.WritePacketAsync(packet);
                await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var restrictions = new[] { null, null, "A" };
                var pending = connection.GetSchemaAsync("Tables", restrictions);
                await prepared.Task.WaitAsync(TimeSpan.FromSeconds(5)); restrictions[2] = "B"; release.TrySetResult();
                Assert.Single((await pending).Rows.Cast<DataRow>());
            }
            finally { release.TrySetResult(); connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ConnectionSchemaPreCanceledDirectoryNeverSendsOrAcquiresSession(bool mysql)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); if (mysql) UseInternalMySqlFixture(connection);
                var transport = new ProtocolFaultStream(Array.Empty<byte>()); InstallFaultStream(connection, transport);
                using var canceled = new CancellationTokenSource(); canceled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.GetSchemaAsync("Tables", canceled.Token));
                Assert.Empty(transport.Written); Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task ConnectionSchemaRequiredSessionCannotExecuteAfterConnectionReopens()
        {
            var first = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            var second = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(first.Port));
            try
            {
                await connection.OpenAsync(); using var command = new OceanBaseCommand { Connection = connection, RequiredSession = connection.GetOpenSession(), CommandText = "old dictionary query" };
                connection.Close(); connection.ConnectionString = Configuration(second.Port); await connection.OpenAsync();
                await Assert.ThrowsAsync<InvalidOperationException>(() => command.ExecuteReaderAsync()); Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); first.Stop(); second.Stop(); }
            await Task.WhenAll(first.Completion, second.Completion).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
