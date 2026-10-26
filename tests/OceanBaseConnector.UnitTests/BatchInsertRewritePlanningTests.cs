using System;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        private static async Task CheckRewritePlan(Action<OceanBaseBatch> arrange, Action<OceanBaseBatch.Operation> check)
        {
            var server = MySqlProcedureServer(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var batch = (OceanBaseBatch)connection.CreateBatch(); arrange(batch);
                using var operation = (OceanBaseBatch.Operation)typeof(OceanBaseBatch).GetMethod("Start", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(batch, new object[] { CommandBehavior.Default, CancellationToken.None })!;
                check(operation);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
        private static OceanBaseCommand PlanRewrite(OceanBaseBatch.Operation operation, int index, out int rows)
        {
            var arguments = new object[] { operation, index, 0 };
            var command = (OceanBaseCommand)typeof(OceanBaseBatch).GetMethod("RewriteCommand", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, arguments)!;
            rows = (int)arguments[2]; return command;
        }

        [Fact]
        public async Task BatchInsertRewriteDisposeRetiresFallbackBeforeReleasingItsReservation()
        {
            OceanBaseBatch? batch = null; DbBatchCommand? firstItem = null;
            await CheckRewritePlan(value =>
            {
                batch = value; batch.Timeout = 1; firstItem = BatchItem(batch, "first"); BatchItem(batch, "must not execute");
            }, operation =>
            {
                var active = operation.Commands[1]; operation.Activate(active, false);
                var gate = typeof(OceanBaseCommand).GetField("_gate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(operation.Commands[0])!;
                var cached = typeof(OceanBaseBatchCommand).GetField("_command", BindingFlags.NonPublic | BindingFlags.Instance)!;
                using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
                var holder = Task.Run(() => { lock (gate) { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); } });
                Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
                var disposing = Task.Run(batch!.Dispose);
                try
                {
                    // First item Release is held after Operation.Dispose; the later active item is not released yet.
                    Assert.True(SpinWait.SpinUntil(() => cached.GetValue(firstItem) == null, TimeSpan.FromSeconds(10)));
                    Assert.Throws<ObjectDisposedException>(() => active.ExecuteNonQuery());
                }
                finally
                {
                    release.Set(); Task.WhenAll(holder, disposing).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                }
            });
        }

        [Fact]
        public async Task BatchInsertRewritePlanningCancellationKeepsOriginalCallerToken()
        {
            var server = MySqlProcedureServer(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port)); using var cancellation = new CancellationTokenSource();
            try
            {
                await connection.OpenAsync(); using var batch = (OceanBaseBatch)connection.CreateBatch(); batch.RewriteBatchedInserts = true;
                var sql = "insert into t values (1 /*" + new string('x', 16 * 1024 * 1024) + "*/)";
                BatchItem(batch, sql); BatchItem(batch, sql);
                cancellation.CancelAfter(TimeSpan.FromMilliseconds(1));
                var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => batch.ExecuteNonQueryAsync(cancellation.Token));
                Assert.Equal(cancellation.Token, error.CancellationToken); Assert.Equal(ConnectionState.Open, connection.State);
                batch.RewriteBatchedInserts = false;
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(32767, 2)]
        [InlineData(32768, 1)]
        public async Task BatchInsertRewriteSplitsAtProtocolParameterLimit(int columns, int expectedRows)
        {
            var sql = "insert into t values (" + string.Join(",", Enumerable.Repeat("?", columns)) + ")";
            await CheckRewritePlan(batch =>
            {
                for (var i = 0; i < 3; i++)
                {
                    var item = BatchItem(batch, sql);
                    for (var j = 0; j < columns; j++) item.Parameters.Add(new OceanBaseParameter { Value = DBNull.Value });
                }
            }, operation =>
            {
                using var planned = PlanRewrite(operation, 0, out var rows); Assert.Equal(expectedRows, rows);
                Assert.Equal(columns * rows, planned.Parameters.Count);
            });
        }

        [Fact]
        public async Task BatchInsertRewriteBoundsEvenParameterlessGroups()
        {
            await CheckRewritePlan(batch =>
            {
                for (var i = 0; i <= ushort.MaxValue; i++) BatchItem(batch, "insert into t values (1)");
            }, operation =>
            {
                using var planned = PlanRewrite(operation, 0, out var rows); Assert.Equal(ushort.MaxValue, rows);
                Assert.Equal(ushort.MaxValue, planned.CommandText.Count(c => c == '('));
                Assert.Same(operation.Commands[ushort.MaxValue], PlanRewrite(operation, ushort.MaxValue, out var tail)); Assert.Equal(1, tail);
            });
        }

        [Theory]
        [InlineData(0, 2)]
        [InlineData(1, 1)]
        public async Task BatchInsertRewriteBudgetsFullInlineRequestAtExactAndOverLimit(int extraBytes, int expectedRows)
        {
            // Two large strings: 10-byte header, bitmap, new-types byte, four type bytes, two 9-byte lengths.
            var value = new string('x', (int)((PacketChannel.DefaultMaxPacketSize - 34) / 2) + extraBytes);
            await CheckRewritePlan(batch =>
            {
                for (var i = 0; i < 2; i++) BatchItem(batch, "insert into t values (?)").Parameters.Add(new OceanBaseParameter { Value = value });
            }, operation =>
            {
                using var planned = PlanRewrite(operation, 0, out var rows); Assert.Equal(expectedRows, rows);
                Assert.Equal(rows, planned.Parameters.Count);
            });
        }

        [Theory]
        [InlineData(0, 2)]
        [InlineData(1, 1)]
        public async Task BatchInsertRewriteBudgetsPrepareSqlIncludingCommandByte(int extraBytes, int expectedRows)
        {
            const string prefix = "insert into t values  ";
            var padding = (int)((PacketChannel.DefaultMaxPacketSize - prefix.Length - 16) / 2) + extraBytes;
            var sql = prefix + "(/*" + new string('x', padding) + "*/1)";
            await CheckRewritePlan(batch => { BatchItem(batch, sql); BatchItem(batch, sql); }, operation =>
            {
                using var planned = PlanRewrite(operation, 0, out var rows); Assert.Equal(expectedRows, rows);
                if (rows == 2) Assert.Equal(PacketChannel.DefaultMaxPacketSize, Encoding.UTF8.GetByteCount(planned.CommandText) + 1L);
            });
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task BatchInsertRewriteBindsMixedTypesAcrossNullBitmapBoundary(bool asynchronous)
        {
            var values = new object[] { 7, DBNull.Value, "中文'\\?", new byte[] { 0, 255 }, 123.45m, new DateTime(2026, 10, 26), true, DBNull.Value, "tail" };
            var sql = "insert into t values (" + string.Join(",", values.Select(_ => "?")) + ")";
            var parameters = values.Select(value => new OceanBaseParameter { Value = value }).ToArray();
            var server = MySqlProcedureServer(async channel =>
            {
                var request = await StatementCommand(channel, 22); Assert.Equal(sql + "," + sql.Substring(sql.IndexOf('(')), Encoding.UTF8.GetString(request, 1, request.Length - 1));
                await Prepared(channel, 188, 18); request = await StatementCommand(channel, 23);
                Assert.Equal(PreparedParameters.Encode(parameters.Concat(parameters).ToArray()), request.AsSpan(10).ToArray());
                await channel.WritePacketAsync(Ok(2, 2)); await StatementCommand(channel, 25);
                channel.BeginCommand(); await Query(channel, "after mixed"); await channel.WritePacketAsync(Ok(1, 2));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var batch = (OceanBaseBatch)connection.CreateBatch(); batch.RewriteBatchedInserts = true;
                for (var i = 0; i < 2; i++) { var item = BatchItem(batch, sql); foreach (var parameter in parameters) item.Parameters.Add(parameter.Snapshot()); }
                Assert.Equal(2, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
                using var after = connection.CreateCommand(); after.CommandText = "after mixed"; Assert.Equal(1, await after.ExecuteNonQueryAsync());
            }
            catch (Exception error) when (server.Completion.IsFaulted) { throw new AggregateException(error, server.Completion.Exception!); }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
