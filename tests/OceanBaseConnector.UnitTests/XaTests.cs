using System;
using System.Data;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        private static readonly OceanBaseXid XaId = new OceanBaseXid(-2, new byte[] { 0, 39, 255 }, Array.Empty<byte>());
        private const string XaIdentity = "0x0027ff,X'',0xfffffffe";

        // A05 remains closed: these fixtures supply only the post-authentication MySQL fact.
        private static void SetXaMySql(OceanBaseConnection connection)
        {
            var session = connection.GetOpenSession(); var info = session.Info!;
            typeof(PhysicalSession).GetField("_info", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session,
                new ConnectResult(info.ServerVersion, info.ConnectionId, OceanBaseCompatibilityMode.MySql, info.Capabilities,
                    info.ServerStatus, info.ObCapabilityFlag, info.SessionVariables));
        }

        private static async Task XaReply(PacketChannel channel, string sql, byte[]? response = null)
        {
            await Query(channel, sql);
            await channel.WritePacketAsync(response ?? Ok(status: 2));
            channel.BeginCommand();
        }

        private static Task XaStart(OceanBaseXaResource resource, bool async, OceanBaseXaStartMode mode = OceanBaseXaStartMode.New)
        { if (async) return resource.StartAsync(XaId, mode); resource.Start(XaId, mode); return Task.CompletedTask; }
        private static Task XaEnd(OceanBaseXaResource resource, bool async, OceanBaseXaEndMode mode = OceanBaseXaEndMode.Success)
        { if (async) return resource.EndAsync(XaId, mode); resource.End(XaId, mode); return Task.CompletedTask; }
        private static Task XaCommit(OceanBaseXaResource resource, bool async, bool onePhase = false)
        { if (async) return resource.CommitAsync(XaId, onePhase); resource.Commit(XaId, onePhase); return Task.CompletedTask; }
        private static Task XaRollback(OceanBaseXaResource resource, bool async)
        { if (async) return resource.RollbackAsync(XaId); resource.Rollback(XaId); return Task.CompletedTask; }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task XaPinsTheSessionAndControlsOneAndTwoPhaseBranches(bool async, bool onePhase)
        {
            var server = Server(async channel =>
            {
                await XaReply(channel, "XA START " + XaIdentity);
                await XaReply(channel, "update sample", Ok(1, 3));
                await XaReply(channel, "XA END " + XaIdentity);
                if (!onePhase) await XaReply(channel, "XA PREPARE " + XaIdentity);
                await XaReply(channel, "XA COMMIT " + XaIdentity + (onePhase ? " ONE PHASE" : ""));
                await XaReply(channel, "XA START " + XaIdentity);
                await XaReply(channel, "XA END " + XaIdentity);
                await XaReply(channel, "XA ROLLBACK " + XaIdentity);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); SetXaMySql(connection);
                var resource = connection.GetXaResource(); Assert.Same(resource, connection.GetXaResource());
                using var command = connection.CreateCommand(); command.CommandText = "update sample";
                Assert.Throws<InvalidOperationException>(() => command.ExecuteNonQuery());
                Assert.Throws<InvalidOperationException>(() => command.Prepare());
                Assert.Throws<InvalidOperationException>(() => connection.BeginTransaction());
                Assert.Throws<InvalidOperationException>(() => connection.ChangeDatabase("other"));
                await XaStart(resource, async);
                Assert.Equal(1, async ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                Assert.Throws<InvalidOperationException>(() => resource.Prepare(XaId));
                Assert.Throws<InvalidOperationException>(() => resource.Rollback(XaId));
                await XaEnd(resource, async);
                Assert.Throws<InvalidOperationException>(() => command.ExecuteNonQuery());
                if (!onePhase) Assert.Equal(OceanBaseXaVote.Prepared, async ? await resource.PrepareAsync(XaId) : resource.Prepare(XaId));
                await XaCommit(resource, async, onePhase);
                Assert.Throws<InvalidOperationException>(() => command.ExecuteNonQuery());
                await XaStart(resource, async); await XaEnd(resource, async); await XaRollback(resource, async);
                Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task XaSuspendResumeJoinAndRollbackOnlyPreserveTheBranch(bool async)
        {
            var server = Server(async channel =>
            {
                await XaReply(channel, "XA START " + XaIdentity);
                await XaReply(channel, "XA END " + XaIdentity + " SUSPEND");
                await XaReply(channel, "XA START " + XaIdentity + " RESUME");
                await XaReply(channel, "XA END " + XaIdentity);
                await XaReply(channel, "XA START " + XaIdentity + " JOIN");
                await XaReply(channel, "XA END " + XaIdentity);
                await XaReply(channel, "XA ROLLBACK " + XaIdentity);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); SetXaMySql(connection); var resource = connection.GetXaResource();
                var other = new OceanBaseXid(1, new byte[] { 1 }, new byte[] { 2 });
                Assert.Throws<InvalidOperationException>(() => resource.Start(XaId, OceanBaseXaStartMode.Resume));
                Assert.Throws<ArgumentOutOfRangeException>(() => resource.Start(XaId, (OceanBaseXaStartMode)99));
                Assert.Throws<ArgumentNullException>(() => resource.Start(null!));
                await XaStart(resource, async);
                Assert.Throws<InvalidOperationException>(() => resource.End(other));
                Assert.Throws<ArgumentOutOfRangeException>(() => resource.End(XaId, (OceanBaseXaEndMode)99));
                Assert.Throws<InvalidOperationException>(() => resource.Start(other));
                await XaEnd(resource, async, OceanBaseXaEndMode.Suspend);
                Assert.Throws<InvalidOperationException>(() => resource.Start(other, OceanBaseXaStartMode.Resume));
                Assert.Throws<InvalidOperationException>(() => resource.Commit(XaId, true));
                await XaStart(resource, async, OceanBaseXaStartMode.Resume);
                await XaEnd(resource, async, OceanBaseXaEndMode.Fail);
                Assert.Throws<InvalidOperationException>(() => resource.Prepare(XaId));
                Assert.Throws<InvalidOperationException>(() => resource.Commit(XaId, true));
                await XaStart(resource, async, OceanBaseXaStartMode.Join); await XaEnd(resource, async);
                Assert.Throws<InvalidOperationException>(() => resource.Prepare(XaId));
                await XaRollback(resource, async);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private static async Task XaRecoveryReply(PacketChannel channel, bool terminate, params byte[][] rows)
        {
            await Query(channel, "XA RECOVER");
            await channel.WritePacketAsync(new byte[] { 4 });
            await channel.WritePacketAsync(Column("formatID", 8));
            await channel.WritePacketAsync(Column("gtrid_length", 3));
            await channel.WritePacketAsync(Column("bqual_length", 3));
            await channel.WritePacketAsync(Column("data", 252, charset: 63, flags: 128));
            await channel.WritePacketAsync(Eof(2));
            foreach (var row in rows) await channel.WritePacketAsync(row);
            if (terminate) { await channel.WritePacketAsync(Eof(2)); channel.BeginCommand(); }
        }

        private static byte[] XaRecoveryRow(string format, string globalLength, string branchLength, byte[] data)
        {
            var result = new System.Collections.Generic.List<byte>();
            foreach (var field in new[] { Encoding.ASCII.GetBytes(format), Encoding.ASCII.GetBytes(globalLength), Encoding.ASCII.GetBytes(branchLength), data })
            { result.Add((byte)field.Length); result.AddRange(field); }
            return result.ToArray();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task XaRecoveryReturnsAnExactIndependentSnapshotAndCanFinishRecoveredBranches(bool async)
        {
            var server = Server(async channel =>
            {
                await XaRecoveryReply(channel, true, XaRecoveryRow("4294967294", "3", "0", new byte[] { 0, 39, 255 }),
                    XaRecoveryRow("2147483648", "1", "2", new byte[] { 128, 0, 255 }));
                await XaReply(channel, "XA COMMIT " + XaIdentity);
                await XaReply(channel, "XA ROLLBACK " + XaIdentity);
                await XaRecoveryReply(channel, true);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); SetXaMySql(connection); var resource = connection.GetXaResource();
                var snapshot = async ? await resource.RecoverAsync() : resource.Recover();
                Assert.Equal(2, snapshot.Length); Assert.Equal(XaId, snapshot[0]);
                Assert.Equal(int.MinValue, snapshot[1].FormatId);
                Assert.Equal(new byte[] { 128 }, snapshot[1].GetGlobalTransactionId());
                Assert.Equal(new byte[] { 0, 255 }, snapshot[1].GetBranchQualifier());
                await XaCommit(resource, async); await XaRollback(resource, async);
                Assert.Empty(async ? await resource.RecoverAsync() : resource.Recover());
                Assert.Equal(2, snapshot.Length);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("4294967296", "1", "0", 1)]
        [InlineData("4294967295", "1", "0", 1)]
        [InlineData("-2", "1", "0", 1)]
        [InlineData("+1", "1", "0", 1)]
        [InlineData(" 1", "1", "0", 1)]
        [InlineData("1", "0", "0", 0)]
        [InlineData("1", "65", "0", 65)]
        [InlineData("1", "1", "65", 66)]
        [InlineData("1", "1", "0", 2)]
        [InlineData("1", "1", "1", 1)]
        public async Task XaRecoveryRejectsInvalidIdentitiesWithoutPartialSnapshots(string format, string global, string branch, int length)
        {
            var server = Server(async channel =>
            {
                await XaRecoveryReply(channel, false, XaRecoveryRow("0", "1", "0", new byte[] { 1 }),
                    XaRecoveryRow(format, global, branch, new byte[length]));
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); SetXaMySql(connection); var resource = connection.GetXaResource();
                await Assert.ThrowsAsync<OceanBaseException>(() => resource.RecoverAsync());
                Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task XaOrdinaryServerRejectionDoesNotAdvanceThePhase(bool async)
        {
            var server = Server(async channel =>
            {
                await XaReply(channel, "XA START " + XaIdentity, Error());
                await XaReply(channel, "XA START " + XaIdentity);
                await XaReply(channel, "XA END " + XaIdentity, Error());
                await XaReply(channel, "update still associated", Ok(1, 3));
                await XaReply(channel, "XA END " + XaIdentity);
                await XaReply(channel, "XA PREPARE " + XaIdentity, Error());
                await XaReply(channel, "XA PREPARE " + XaIdentity);
                await XaReply(channel, "XA COMMIT " + XaIdentity, Error());
                await XaReply(channel, "XA ROLLBACK " + XaIdentity);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); SetXaMySql(connection); var resource = connection.GetXaResource();
                Assert.False((await Assert.ThrowsAsync<OceanBaseException>(() => XaStart(resource, async))).IsExecutionOutcomeUnknown);
                await XaStart(resource, async);
                Assert.False((await Assert.ThrowsAsync<OceanBaseException>(() => XaEnd(resource, async))).IsExecutionOutcomeUnknown);
                using var command = connection.CreateCommand(); command.CommandText = "update still associated";
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
                await XaEnd(resource, async);
                await Assert.ThrowsAsync<OceanBaseException>(async () => { if (async) await resource.PrepareAsync(XaId); else resource.Prepare(XaId); });
                Assert.Equal(OceanBaseXaVote.Prepared, async ? await resource.PrepareAsync(XaId) : resource.Prepare(XaId));
                await Assert.ThrowsAsync<OceanBaseException>(() => XaCommit(resource, async));
                await XaRollback(resource, async); Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(1402, false)]
        [InlineData(1613, false)]
        [InlineData(1614, false)]
        [InlineData(1402, true)]
        [InlineData(1613, true)]
        [InlineData(1614, true)]
        public async Task XaRollbackErrorsDestroyTheSessionWithoutLosingTheTrustedServerError(int code, bool business)
        {
            var server = Server(async channel =>
            {
                await XaReply(channel, "XA START " + XaIdentity);
                if (!business) await XaReply(channel, "XA END " + XaIdentity);
                await Query(channel, business ? "update sample" : "XA PREPARE " + XaIdentity);
                var error = Error(); error[1] = (byte)code; error[2] = (byte)(code >> 8);
                await channel.WritePacketAsync(error); channel.BeginCommand();
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); SetXaMySql(connection); var resource = connection.GetXaResource();
                await XaStart(resource, true); using var command = connection.CreateCommand(); command.CommandText = "update sample";
                if (!business) await XaEnd(resource, true);
                var error = await Assert.ThrowsAsync<OceanBaseException>(async () =>
                { if (business) await command.ExecuteNonQueryAsync(); else await resource.PrepareAsync(XaId); });
                Assert.Equal(code, error.ServerErrorCode); Assert.False(error.IsExecutionOutcomeUnknown);
                Assert.Equal(ConnectionState.Broken, connection.State);
                await Assert.ThrowsAsync<InvalidOperationException>(() => resource.RollbackAsync(XaId));
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task XaCloseSendsNoImplicitCompletionAndOldResourcesNeverRebind(bool async)
        {
            var server = Server(async channel =>
            { await XaReply(channel, "XA START " + XaIdentity); Assert.Equal(0, await channelStreamRead(channel)); });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); SetXaMySql(connection); var old = connection.GetXaResource(); await XaStart(old, async);
                if (async) await connection.CloseAsync(); else connection.Close();
                Assert.Throws<InvalidOperationException>(() => old.End(XaId));
                await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
                var second = Server(async channel =>
                { await XaReply(channel, "XA ROLLBACK " + XaIdentity); Assert.Equal(0, await channelStreamRead(channel)); });
                try
                {
                    connection.ConnectionString = Configuration(second.Port); await connection.OpenAsync(); SetXaMySql(connection);
                    var current = connection.GetXaResource(); Assert.NotSame(old, current);
                    Assert.Throws<InvalidOperationException>(() => old.Start(XaId));
                    await Assert.ThrowsAsync<InvalidOperationException>(() => old.RecoverAsync());
                    await XaRollback(current, async); connection.Close();
                    await second.Completion.WaitAsync(TimeSpan.FromSeconds(10));
                }
                finally { connection.Close(); second.Stop(); }
            }
            finally { connection.Close(); server.Stop(); }
        }

        [Theory]
        [InlineData("disconnect")]
        [InlineData("cancel")]
        [InlineData("timeout")]
        [InlineData("close")]
        public async Task XaInterruptedPrepareIsUnknownAndNeverReplayed(string interruption)
        {
            var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel =>
            {
                await XaReply(channel, "XA START " + XaIdentity); await XaReply(channel, "XA END " + XaIdentity);
                await Query(channel, "XA PREPARE " + XaIdentity); requested.SetResult();
                if (interruption != "disconnect") Assert.Equal(0, await channelStreamRead(channel));
            });
            using var logger = new RecordingLoggerFactory();
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Command Timeout=1", logger);
            try
            {
                await connection.OpenAsync(); SetXaMySql(connection); var resource = connection.GetXaResource();
                using var canceled = new CancellationTokenSource(); canceled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resource.StartAsync(XaId, cancellationToken: canceled.Token));
                await XaStart(resource, true); await XaEnd(resource, true);
                using var cancellation = new CancellationTokenSource(); var preparing = resource.PrepareAsync(XaId, cancellation.Token);
                await requested.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(preparing.IsCompleted);
                await Assert.ThrowsAsync<InvalidOperationException>(() => resource.CommitAsync(XaId));
                if (interruption == "cancel") cancellation.Cancel();
                if (interruption == "close") connection.Close();
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => preparing);
                Assert.True(error.IsExecutionOutcomeUnknown);
                if (interruption == "cancel") Assert.Equal(cancellation.Token, Assert.IsType<OperationCanceledException>(error.InnerException).CancellationToken);
                if (interruption == "timeout") Assert.IsType<TimeoutException>(error.InnerException);
                Assert.Equal(interruption == "close" ? ConnectionState.Closed : ConnectionState.Broken, connection.State);
                Assert.Throws<InvalidOperationException>(() => resource.Prepare(XaId));
                Assert.All(logger.Entries, entry => { Assert.Null(entry.Error); Assert.DoesNotContain(XaIdentity, entry.Message); Assert.DoesNotContain("secret", entry.Message); });
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task XaRejectsOracleLocalAndAmbientTransactionsBeforeSending()
        {
            var server = Server(async channel =>
            {
                await TransactionBegin(channel); await TransactionEnd(channel, false);
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                Assert.Throws<InvalidOperationException>(() => connection.GetXaResource());
                await connection.OpenAsync(); Assert.Throws<NotSupportedException>(() => connection.GetXaResource());
                using (var local = connection.BeginTransaction())
                { Assert.Throws<InvalidOperationException>(() => connection.GetXaResource()); local.Rollback(); }
                SetXaMySql(connection); var resource = connection.GetXaResource();
                using (var scope = new System.Transactions.TransactionScope(System.Transactions.TransactionScopeAsyncFlowOption.Enabled))
                {
                    Assert.Throws<InvalidOperationException>(() => connection.GetXaResource());
                    Assert.Throws<InvalidOperationException>(() => resource.Start(XaId));
                    await Assert.ThrowsAsync<InvalidOperationException>(() => resource.RecoverAsync());
                    connection.Close();
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
        public async Task XaMalformedEmptyRecoveryUsesThePublicErrorContract(bool async, bool metadata)
        {
            var server = Server(async channel =>
            {
                await Query(channel, "XA RECOVER");
                if (metadata)
                {
                    await channel.WritePacketAsync(new byte[] { 1 });
                    await channel.WritePacketAsync(Column("unexpected"));
                    await channel.WritePacketAsync(Eof(2)); await channel.WritePacketAsync(Eof(2));
                }
                else await channel.WritePacketAsync(Ok(status: 2));
                channel.BeginCommand(); Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); SetXaMySql(connection); var resource = connection.GetXaResource();
                var error = await Assert.ThrowsAsync<OceanBaseException>(async () => { if (async) await resource.RecoverAsync(); else resource.Recover(); });
                Assert.False(error.IsExecutionOutcomeUnknown); Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task XaReaderAndBatchShareTheSamePhaseBoundary(bool async)
        {
            var server = Server(async channel =>
            {
                await XaReply(channel, "XA START " + XaIdentity);
                await Query(channel, "select row"); await BatchTextResult(channel, "sample", 3); channel.BeginCommand();
                await XaReply(channel, "update batch", Ok(1, 3));
                await XaReply(channel, "XA END " + XaIdentity); await XaReply(channel, "XA ROLLBACK " + XaIdentity);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); SetXaMySql(connection); var resource = connection.GetXaResource();
                using var batch = connection.CreateBatch(); BatchItem(batch, "update batch");
                await Assert.ThrowsAsync<InvalidOperationException>(() => batch.ExecuteNonQueryAsync());
                await XaStart(resource, async);
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "select row";
                    using var reader = async ? await command.ExecuteReaderAsync() : command.ExecuteReader();
                    Assert.Throws<InvalidOperationException>(() => resource.End(XaId));
                    await Assert.ThrowsAsync<InvalidOperationException>(() => resource.RecoverAsync());
                    Assert.True(await reader.ReadAsync()); Assert.Equal("sample", reader.GetString(0));
                    Assert.False(await reader.ReadAsync());
                }
                Assert.Equal(1, async ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
                await XaEnd(resource, async);
                Assert.Throws<InvalidOperationException>(() => batch.Prepare());
                await XaRollback(resource, async);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("null")]
        [InlineData("multiple")]
        public async Task XaRecoveryRejectsNullFieldsAndMultipleResults(string kind)
        {
            var server = Server(async channel =>
            {
                if (kind == "null") await XaRecoveryReply(channel, false, new byte[] { 251, 1, 49, 1, 48, 1, 1 });
                else
                {
                    await Query(channel, "XA RECOVER"); await channel.WritePacketAsync(Ok(status: 10));
                }
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); SetXaMySql(connection);
                await Assert.ThrowsAsync<OceanBaseException>(() => connection.GetXaResource().RecoverAsync());
                Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task XaRecoveryPreservesMaximumRawSegmentsAndAllSupportedFormatBits(bool async)
        {
            var global = new byte[64]; var branch = new byte[64];
            for (var i = 0; i < 64; i++) { global[i] = (byte)i; branch[i] = (byte)(255 - i); }
            var data = new byte[128]; global.CopyTo(data, 0); branch.CopyTo(data, 64);
            var server = Server(channel => XaRecoveryReply(channel, true,
                XaRecoveryRow("0", "64", "64", data), XaRecoveryRow("2147483647", "64", "64", data),
                XaRecoveryRow("2147483648", "64", "64", data), XaRecoveryRow("4294967294", "64", "64", data)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); SetXaMySql(connection); var resource = connection.GetXaResource();
                var recovered = async ? await resource.RecoverAsync() : resource.Recover();
                Assert.Equal(4, recovered.Length); var formats = new[] { 0, int.MaxValue, int.MinValue, -2 };
                for (var i = 0; i < recovered.Length; i++)
                { Assert.Equal(formats[i], recovered[i].FormatId); Assert.Equal(global, recovered[i].GetGlobalTransactionId()); Assert.Equal(branch, recovered[i].GetBranchQualifier()); }
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task XaRecoveryIsBoundedAndNeverTruncatesItsSnapshot(bool tooMany)
        {
            var rows = new byte[tooMany ? 65537 : 65536][];
            var row = XaRecoveryRow("1", "1", "0", new byte[] { 1 });
            for (var i = 0; i < rows.Length; i++) rows[i] = row;
            var server = Server(async channel =>
            {
                await XaRecoveryReply(channel, !tooMany, rows);
                if (tooMany) Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Command Timeout=15");
            try
            {
                await connection.OpenAsync(); SetXaMySql(connection); var resource = connection.GetXaResource();
                if (tooMany)
                {
                    await Assert.ThrowsAsync<OceanBaseException>(() => resource.RecoverAsync());
                    Assert.Equal(ConnectionState.Broken, connection.State);
                }
                else Assert.Equal(65536, (await resource.RecoverAsync()).Length);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(20));
        }

        [Theory]
        [InlineData("cancel")]
        [InlineData("disconnect")]
        [InlineData("close")]
        public async Task XaRecoveryInterruptedAfterAnIdentityNeverReturnsAPartialSnapshot(string interruption)
        {
            var rowSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel =>
            {
                await XaRecoveryReply(channel, false, XaRecoveryRow("1", "1", "0", new byte[] { 1 })); rowSent.SetResult();
                if (interruption != "disconnect") Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); SetXaMySql(connection); var resource = connection.GetXaResource();
                using var cancellation = new CancellationTokenSource(); var recovering = resource.RecoverAsync(cancellation.Token);
                await rowSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (interruption == "cancel") cancellation.Cancel();
                if (interruption == "close") connection.Close();
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => recovering);
                Assert.True(error.IsExecutionOutcomeUnknown);
                Assert.Equal(interruption == "close" ? ConnectionState.Closed : ConnectionState.Broken, connection.State);
                await Assert.ThrowsAsync<InvalidOperationException>(() => resource.RecoverAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(0, "00000000")]
        [InlineData(-2, "fffffffe")]
        [InlineData(int.MinValue, "80000000")]
        [InlineData(int.MaxValue, "7fffffff")]
        public async Task XaEmptyBranchUsesASyntacticallyValidHexString(int format, string encodedFormat)
        {
            // The fixed server lexer permits zero digits in X'', but requires one or more after 0x.
            var server = Server(channel => XaReply(channel, "XA ROLLBACK 0x01,X'',0x" + encodedFormat));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); SetXaMySql(connection);
                var error = await Record.ExceptionAsync(() => connection.GetXaResource().RollbackAsync(new OceanBaseXid(format, new byte[] { 1 }, Array.Empty<byte>())));
                await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Null(error);
                Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
