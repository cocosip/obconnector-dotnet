using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        public static IEnumerable<object[]> LimitedCursorPageCases()
        {
            foreach (var behavior in new[] { CommandBehavior.SingleRow, CommandBehavior.SingleResult, CommandBehavior.SingleRow | CommandBehavior.SingleResult })
                foreach (var asynchronous in new[] { false, true })
                    foreach (var deprecated in new[] { false, true })
                        foreach (var sequential in new[] { false, true })
                            yield return new object[] { behavior, asynchronous, deprecated, sequential };
        }

        [Theory]
        [MemberData(nameof(LimitedCursorPageCases))]
        public Task RefCursorResultLimitsDrainPagesAndKeepParentOwnership(CommandBehavior behavior, bool asynchronous, bool deprecated, bool sequential)
            => ReadRefCursorPagesAndRelease(asynchronous, deprecated, sequential, behavior: behavior);

        [Theory]
        [MemberData(nameof(LimitedCursorPageCases))]
        public Task RefCursorResultLimitsAdvanceWithoutReadingAndDrainEveryPage(CommandBehavior behavior, bool asynchronous, bool deprecated, bool sequential)
            => ReadRefCursorPagesAndRelease(asynchronous, deprecated, sequential, behavior: behavior, advanceWithoutRead: true);

        public static IEnumerable<object[]> LimitedCursorEmptyCases()
        {
            foreach (var behavior in new[] { CommandBehavior.SingleRow, CommandBehavior.SingleResult, CommandBehavior.SingleRow | CommandBehavior.SingleResult })
                foreach (var asynchronous in new[] { false, true })
                    foreach (var deprecated in new[] { false, true })
                        yield return new object[] { behavior, asynchronous, deprecated };
        }

        [Theory]
        [MemberData(nameof(LimitedCursorEmptyCases))]
        public async Task RefCursorResultLimitsAllowEmptyCursorAndCloseConnection(CommandBehavior behavior, bool asynchronous, bool deprecated)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 0xa1, deprecated);
                await channel.WritePacketAsync(OracleExecuteHeader(1, 1, 4));
                await channel.WritePacketAsync(Column("c", 163));
                if (!deprecated) await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(Column("c", 163));
                if (!deprecated) await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(new byte[] { 0, 0, 91, 0, 0, 0 });
                await channel.WritePacketAsync(CursorTerminator(2, deprecated)); await channel.WritePacketAsync(Ok());
                Assert.Equal(new byte[] { 28, 91, 0, 0, 0, 2, 0, 0, 0 }, await StatementCommand(channel, 28, deprecated));
                await CursorMetadata(channel, 2, deprecated, omitTerminator: deprecated);
                await channel.WritePacketAsync(CursorTerminator(130, deprecated));
                await AssertOracleExecuteTransportClosed(channel);
            }, deprecated, serverVersion: "4.3.5.0");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); using var command = RefCursorCall(connection); await command.ExecuteNonQueryAsync();
                using var cursor = Assert.IsType<OceanBaseRefCursor>(command.Parameters[0].Value);
                foreach (var invalid in new[] { CommandBehavior.SchemaOnly, CommandBehavior.KeyInfo, (CommandBehavior)64 })
                {
                    Assert.Throws<NotSupportedException>(() => cursor.GetDataReader(2, behavior | invalid));
                    await Assert.ThrowsAsync<NotSupportedException>(() => cursor.GetDataReaderAsync(2, behavior | invalid));
                }
                using var reader = asynchronous ? await cursor.GetDataReaderAsync(2, behavior | CommandBehavior.CloseConnection)
                    : cursor.GetDataReader(2, behavior | CommandBehavior.CloseConnection);
                Assert.False(reader.HasRows); Assert.Equal(1, reader.FieldCount); Assert.Equal(-1, reader.RecordsAffected);
                Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                if (asynchronous) await reader.CloseAsync(); else reader.Close();
                Assert.Equal(ConnectionState.Closed, connection.State);
                Assert.Throws<InvalidOperationException>(() => cursor.GetDataReader());
                await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { connection.Close(); server.Stop(); }
        }

        public static IEnumerable<object[]> LimitedCursorFaultCases()
        {
            foreach (var behavior in new[] { CommandBehavior.SingleRow, CommandBehavior.SingleResult, CommandBehavior.SingleRow | CommandBehavior.SingleResult })
                foreach (var kind in new[] { "error", "no-progress", "extra-row", "changed-metadata" })
                    yield return new object[] { behavior, kind };
        }

        [Theory]
        [MemberData(nameof(LimitedCursorFaultCases))]
        public Task RefCursorResultLimitsSurfaceHiddenFetchFailures(CommandBehavior behavior, string kind)
            => RefCursorBadFetchBreaksSessionWithoutClaimingSqlOutcomeUnknownCore(kind, behavior);

        public static IEnumerable<object[]> LimitedCursorInterruptCases()
        {
            foreach (var behavior in new[] { CommandBehavior.SingleRow, CommandBehavior.SingleResult, CommandBehavior.SingleRow | CommandBehavior.SingleResult })
                foreach (var condition in new[] { false, true })
                    yield return new object[] { behavior, condition };
        }

        [Theory]
        [MemberData(nameof(LimitedCursorInterruptCases))]
        public Task RefCursorResultLimitsCancelHiddenPageWithOriginalTokenOrTimeout(CommandBehavior behavior, bool timeout)
            => RefCursorFetchCancellationOrTimeoutDoesNotReclassifyCompletedSqlCore(timeout, nextPage: true, behavior);

        [Theory]
        [MemberData(nameof(LimitedCursorInterruptCases))]
        public Task RefCursorResultLimitsEarlyCloseStillDestroysUnreadSession(CommandBehavior behavior, bool disposeCursor)
            => RefCursorEarlyReaderOrCursorCloseDestroysUnreadSessionCore(disposeCursor, behavior);
    }
}
