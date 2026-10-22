using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        private const string OracleXaRecoverySql = "declare x DBMS_XA_XID_ARRAY := DBMS_XA_XID_ARRAY(); BEGIN x := DBMS_XA.XA_RECOVER(); ? := x; END;";

        private static async Task OracleXaInitialize(PacketChannel channel)
        {
            await Query(channel, "set autocommit = 0");
            await channel.WritePacketAsync(Ok(status: 0));
        }

        private static byte[] XaBytes(ByteCursor cursor) => cursor.ReadBytes((int)cursor.ReadLengthEncodedInteger());

        private static async Task OracleXaRequest(PacketChannel channel, string operation, int? flag = null, bool previous = false,
            int format = -2, byte[]? global = null, byte[]? branch = null)
        {
            if (previous) Assert.Equal(new byte[] { 25, 42, 0, 0, 0 }, await StatementCommand(channel, 25));
            var cursor = new ByteCursor(await StatementCommand(channel, 0xa1));
            Assert.Equal(0xa1, cursor.ReadUInt8()); Assert.Equal(0u, cursor.ReadUInt32());
            Assert.Equal(0, cursor.ReadUInt8()); Assert.Equal(1u, cursor.ReadUInt32());
            var sql = Encoding.UTF8.GetString(XaBytes(cursor));
            Assert.Equal(operation == "RECOVER" ? OracleXaRecoverySql
                : "select DBMS_XA.XA_" + operation + (flag.HasValue ? "(?, ?) from dual" : "(?) from dual"), sql);
            Assert.Equal(flag.HasValue ? 2u : 1u, cursor.ReadUInt32());
            Assert.Equal(operation == "RECOVER" ? 1 : 0, cursor.ReadUInt8()); Assert.Equal(1, cursor.ReadUInt8());
            Assert.Equal(160, cursor.ReadUInt16());
            Assert.Equal("SYS", Encoding.UTF8.GetString(XaBytes(cursor)));
            Assert.Equal(operation == "RECOVER" ? "DBMS_XA_XID_ARRAY" : "DBMS_XA_XID", Encoding.UTF8.GetString(XaBytes(cursor)));
            Assert.Equal(0, cursor.ReadUInt8());
            if (flag.HasValue) Assert.Equal(operation == "COMMIT" ? 1 : 3, cursor.ReadUInt16());
            if (operation != "RECOVER")
            {
                Assert.Equal(0, cursor.ReadUInt8());
                Assert.Equal(format.ToString(CultureInfo.InvariantCulture), Encoding.ASCII.GetString(XaBytes(cursor)));
                Assert.Equal(global ?? new byte[] { 0, 39, 255 }, XaBytes(cursor));
                Assert.Equal(branch ?? Array.Empty<byte>(), XaBytes(cursor));
                if (flag.HasValue) Assert.Equal(unchecked((uint)flag.Value), operation == "COMMIT" ? cursor.ReadUInt8() : cursor.ReadUInt32());
            }
            Assert.Equal(0u, cursor.ReadUInt32()); Assert.Equal(0u, cursor.ReadUInt32());
            cursor.ReadUInt32(); Assert.Equal(0u, cursor.ReadUInt32()); Assert.False(cursor.HasRemaining);
        }

        private static async Task OracleXaScalarReply(PacketChannel channel, int parameterCount, long code = 0, bool tail = true, ushort tailStatus = 0, string? malformed = null, byte affected = 0)
        {
            await channel.WritePacketAsync(OracleExecuteHeader((ushort)parameterCount, 1, flags: 0));
            await channel.WritePacketAsync(ComplexColumn("SYS", "DBMS_XA_XID"));
            if (parameterCount == 2) await channel.WritePacketAsync(Column("flag", 3));
            await channel.WritePacketAsync(Eof(0));
            await channel.WritePacketAsync(Column("XA_RESULT", malformed == "text" ? (byte)253 : malformed == "fraction" ? (byte)246 : (byte)8)); await channel.WritePacketAsync(Eof(0));
            var row = malformed == "null" ? new byte[] { 0, 4 }
                : malformed == "fraction" ? new byte[] { 0, 0, 3, 49, 46, 53 }
                : malformed == "text" ? new byte[] { 0, 0, 1, 48 }
                : new byte[] { 0, 0 }.Concat(BitConverter.GetBytes(code)).ToArray();
            if (malformed != "missing") await channel.WritePacketAsync(row);
            if (malformed == "multiple") await channel.WritePacketAsync(row);
            if (malformed == "null" || malformed == "text" || malformed == "fraction" || malformed == "wide" || malformed == "multiple")
            { Assert.Equal(0, await channelStreamRead(channel)); return; }
            await channel.WritePacketAsync(Eof(0));
            if (tail) await channel.WritePacketAsync(new byte[] { 0, (byte)(malformed == "affected" ? 2 : affected), (byte)(malformed == "insert" ? 1 : 0), (byte)tailStatus, (byte)(tailStatus >> 8), 0, 0 });
        }

        private static async Task OracleXaRecoveryReply(PacketChannel channel, byte[] collection, bool tail = true, string? malformed = null, ushort tailStatus = 0, byte affected = 0)
        {
            await channel.WritePacketAsync(OracleExecuteHeader(1, 1, flags: 4));
            await channel.WritePacketAsync(ComplexColumn("SYS", "DBMS_XA_XID_ARRAY")); await channel.WritePacketAsync(Eof(0));
            await channel.WritePacketAsync(ComplexColumn("SYS", malformed == "type" ? "OTHER" : "DBMS_XA_XID_ARRAY"));
            if (malformed == "type") { Assert.Equal(0, await channelStreamRead(channel)); return; }
            await channel.WritePacketAsync(Eof(0));
            var row = malformed == "null" ? new byte[] { 0, 4 } : new byte[] { 0, 0 }.Concat(collection).ToArray();
            if (malformed != "missing") await channel.WritePacketAsync(row);
            if (malformed == "members") { Assert.Equal(0, await channelStreamRead(channel)); return; }
            if (malformed == "multiple") { await channel.WritePacketAsync(row); Assert.Equal(0, await channelStreamRead(channel)); return; }
            await channel.WritePacketAsync(Eof(0));
            if (tail) await channel.WritePacketAsync(new byte[] { 0, affected, 0, (byte)tailStatus, (byte)(tailStatus >> 8), 0, 0 });
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task OracleXaControlsBranchesWithStructuredIdentityAndNoImplicitCompletion(bool asynchronous, bool onePhase)
        {
            var server = Server(async channel =>
            {
                await OracleXaInitialize(channel);
                await OracleXaRequest(channel, "START", 0); await OracleXaScalarReply(channel, 2);
                await StatementCommand(channel, 25); await StatementCommand(channel, 3); await channel.WritePacketAsync(Ok(status: 1));
                await OracleXaRequest(channel, "END", 0x04000000); await OracleXaScalarReply(channel, 2);
                if (!onePhase) { await OracleXaRequest(channel, "PREPARE", previous: true); await OracleXaScalarReply(channel, 1); }
                await OracleXaRequest(channel, "COMMIT", onePhase ? 1 : 0, previous: true); await OracleXaScalarReply(channel, 2);
                await OracleXaRequest(channel, "START", 0, previous: true); await OracleXaScalarReply(channel, 2);
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); var resource = connection.GetXaResource(); Assert.Same(resource, connection.GetXaResource());
                using var command = connection.CreateCommand(); command.CommandText = "update sample";
                Assert.Throws<InvalidOperationException>(() => command.ExecuteNonQuery());
                await XaStart(resource, asynchronous); Assert.Throws<InvalidOperationException>(() => connection.BeginTransaction());
                Assert.Equal(0, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                await XaEnd(resource, asynchronous);
                if (!onePhase) Assert.Equal(OceanBaseXaVote.Prepared, asynchronous ? await resource.PrepareAsync(XaId) : resource.Prepare(XaId));
                await XaCommit(resource, asynchronous, onePhase);
                await XaStart(resource, asynchronous); connection.Close();
                await Assert.ThrowsAsync<InvalidOperationException>(() => resource.RecoverAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task OracleXaReadOnlyVoteEndsTheBranchAndAllowsRecovery(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await OracleXaInitialize(channel);
                await OracleXaRequest(channel, "START", 0); await OracleXaScalarReply(channel, 2);
                await OracleXaRequest(channel, "END", 0x04000000, previous: true); await OracleXaScalarReply(channel, 2);
                await OracleXaRequest(channel, "PREPARE", previous: true); await OracleXaScalarReply(channel, 1, 3);
                await OracleXaRequest(channel, "RECOVER", previous: true);
                // One collection element, then NUMBER/RAW/RAW, with an empty branch.
                await OracleXaRecoveryReply(channel, new byte[] { 1, 0, 0, 2, 45, 50, 3, 0, 39, 255, 0 });
                await OracleXaRequest(channel, "ROLLBACK", previous: true); await OracleXaScalarReply(channel, 1);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); var resource = connection.GetXaResource();
                await XaStart(resource, asynchronous); await XaEnd(resource, asynchronous);
                Assert.Equal(OceanBaseXaVote.ReadOnly, asynchronous ? await resource.PrepareAsync(XaId) : resource.Prepare(XaId));
                Assert.Throws<InvalidOperationException>(() => resource.Commit(XaId, true));
                var recovered = asynchronous ? await resource.RecoverAsync() : resource.Recover();
                Assert.Equal(XaId, Assert.Single(recovered));
                await XaRollback(resource, asynchronous); Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
        [Theory]
        [InlineData(-2)]
        [InlineData(-3)]
        [InlineData(-4)]
        [InlineData(-5)]
        [InlineData(-6)]
        [InlineData(-7)]
        [InlineData(-8)]
        [InlineData(-9)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(7)]
        [InlineData(8)]
        [InlineData(9)]
        [InlineData(100)]
        [InlineData(101)]
        [InlineData(102)]
        [InlineData(103)]
        [InlineData(104)]
        [InlineData(105)]
        [InlineData(106)]
        [InlineData(107)]
        [InlineData(int.MinValue)]
        [InlineData(int.MaxValue)]
        public async Task OracleXaNonzeroReturnCodeNeverBecomesSuccessOrAnErrPacketCode(int code)
        {
            var server = Server(async channel =>
            {
                await OracleXaInitialize(channel); await OracleXaRequest(channel, "START", 0);
                await OracleXaScalarReply(channel, 2, code);
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); var resource = connection.GetXaResource();
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => resource.StartAsync(XaId));
                Assert.Equal(code, error.XaErrorCode); Assert.Equal(0, error.ServerErrorCode); Assert.Null(error.SqlState);
                Assert.False(error.IsExecutionOutcomeUnknown); Assert.Equal(ConnectionState.Broken, connection.State);
                await Assert.ThrowsAsync<InvalidOperationException>(() => resource.StartAsync(XaId));
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(2)]
        [InlineData(8)]
        [InlineData(0x1000)]
        [InlineData(0x80)]
        public async Task OracleXaRejectsAutocommitAndNonControlTerminalFlags(ushort flags)
        {
            var server = Server(async channel =>
            {
                await OracleXaInitialize(channel); await OracleXaRequest(channel, "START", 0);
                await OracleXaScalarReply(channel, 2, tailStatus: flags);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync();
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => connection.GetXaResource().StartAsync(XaId));
                Assert.Null(error.XaErrorCode); Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, 0, 0)]
        [InlineData(true, 0, 0)]
        [InlineData(false, 1, 0x40)]
        [InlineData(true, 1, 0x40)]
        public async Task OracleXaAcceptsSingleRowCompletionForEveryPhase(bool asynchronous, byte affected, ushort flags)
        {
            var server = Server(async channel =>
            {
                await OracleXaInitialize(channel);
                await OracleXaRequest(channel, "START", 0);
                await OracleXaScalarReply(channel, 2, affected: affected, tailStatus: (ushort)(flags | 1));
                await OracleXaRequest(channel, "END", 0x04000000, previous: true);
                await OracleXaScalarReply(channel, 2, affected: affected, tailStatus: flags);
                await OracleXaRequest(channel, "PREPARE", previous: true);
                await OracleXaScalarReply(channel, 1, affected: affected, tailStatus: flags);
                await OracleXaRequest(channel, "COMMIT", 0, previous: true);
                await OracleXaScalarReply(channel, 2, affected: affected, tailStatus: flags);
                await OracleXaRequest(channel, "RECOVER", previous: true);
                await OracleXaRecoveryReply(channel, OracleXaCollection("-2", new byte[] { 0, 39, 255 }, Array.Empty<byte>()),
                    affected: affected, tailStatus: affected == 0 ? (ushort)0 : (ushort)0x1000);
                await OracleXaRequest(channel, "ROLLBACK", previous: true);
                await OracleXaScalarReply(channel, 1, affected: affected, tailStatus: flags);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); var resource = connection.GetXaResource();
                await XaStart(resource, asynchronous); await XaEnd(resource, asynchronous);
                Assert.Equal(OceanBaseXaVote.Prepared, asynchronous ? await resource.PrepareAsync(XaId) : resource.Prepare(XaId));
                await XaCommit(resource, asynchronous, false);
                var recovered = asynchronous ? await resource.RecoverAsync() : resource.Recover();
                Assert.Equal(XaId, Assert.Single(recovered));
                await XaRollback(resource, asynchronous); Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task OracleXaRecoveryAcceptsSingleOutputCompletion(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await OracleXaInitialize(channel); await OracleXaRequest(channel, "RECOVER");
                await OracleXaRecoveryReply(channel, OracleXaCollection(null, null, null, count: 0), affected: 1, tailStatus: 0x1000);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); var resource = connection.GetXaResource();
                Assert.Empty(asynchronous ? await resource.RecoverAsync() : resource.Recover());
                Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(2, 0)]
        [InlineData(0, 2)]
        [InlineData(0, 8)]
        [InlineData(0, 0x40)]
        [InlineData(0, 0x80)]
        public async Task OracleXaRecoveryRejectsNonOutputCompletion(byte affected, ushort flags)
        {
            var server = Server(async channel =>
            {
                await OracleXaInitialize(channel); await OracleXaRequest(channel, "RECOVER");
                await OracleXaRecoveryReply(channel, OracleXaCollection(null, null, null, count: 0), affected: affected, tailStatus: flags);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync();
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => connection.GetXaResource().RecoverAsync());
                Assert.Null(error.XaErrorCode); Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("affected")]
        [InlineData("insert")]
        [InlineData("cursor")]
        [InlineData("last-row")]
        [InlineData("output")]
        public async Task OracleXaMalformedInitializationNeverSendsAStage(string kind)
        {
            var server = Server(async channel =>
            {
                await Query(channel, "set autocommit = 0");
                var ok = Ok(status: 0);
                if (kind == "affected") ok[1] = 1;
                if (kind == "insert") ok[2] = 1;
                if (kind == "cursor") ok[3] = 0x40;
                if (kind == "last-row") ok[3] = 0x80;
                if (kind == "output") ok[4] = 0x10;
                await channel.WritePacketAsync(ok);
                channel.BeginCommand();
                Assert.Equal(0, await channelStreamRead(channel));
            }, serverVersion: "4.3.5.0");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync();
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => connection.GetXaResource().StartAsync(XaId));
                await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Null(error.XaErrorCode); Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task OracleXaSuspendResumeJoinAndFailKeepTheBranchRollbackOnly(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await OracleXaInitialize(channel);
                foreach (var step in new[] { ("START", 0), ("END", 0x02000000), ("START", 0x08000000),
                    ("END", 0x04000000), ("START", 0x00200000), ("END", 0x04000000) })
                { await OracleXaRequest(channel, step.Item1, step.Item2, previous: step != ("START", 0)); await OracleXaScalarReply(channel, 2); }
                await OracleXaRequest(channel, "ROLLBACK", previous: true); await OracleXaScalarReply(channel, 1);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); var resource = connection.GetXaResource(); await XaStart(resource, asynchronous);
                if (asynchronous) { await resource.EndAsync(XaId, OceanBaseXaEndMode.Suspend); await resource.StartAsync(XaId, OceanBaseXaStartMode.Resume); }
                else { resource.End(XaId, OceanBaseXaEndMode.Suspend); resource.Start(XaId, OceanBaseXaStartMode.Resume); }
                await XaEnd(resource, asynchronous);
                if (asynchronous) { await resource.StartAsync(XaId, OceanBaseXaStartMode.Join); await resource.EndAsync(XaId, OceanBaseXaEndMode.Fail); }
                else { resource.Start(XaId, OceanBaseXaStartMode.Join); resource.End(XaId, OceanBaseXaEndMode.Fail); }
                await Assert.ThrowsAsync<InvalidOperationException>(() => resource.PrepareAsync(XaId));
                Assert.Throws<InvalidOperationException>(() => resource.Commit(XaId, true)); await XaRollback(resource, asynchronous);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("2.2.75")]
        [InlineData("unknown")]
        public async Task OracleXaVersionGateAndPreSendCancellationSendNoInitialization(string version)
        {
            var server = Server(async channel => { Assert.Equal(0, await channelStreamRead(channel)); }, serverVersion: version);
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                if (version == "unknown")
                {
                    var error = await Assert.ThrowsAsync<OceanBaseException>(() => connection.OpenAsync());
                    Assert.False(error.IsExecutionOutcomeUnknown); Assert.Equal(ConnectionState.Closed, connection.State);
                    return;
                }
                await connection.OpenAsync(); var resource = connection.GetXaResource();
                using var canceled = new CancellationTokenSource(); canceled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resource.StartAsync(XaId, cancellationToken: canceled.Token));
                await Assert.ThrowsAsync<NotSupportedException>(() => resource.StartAsync(XaId));
                Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task OracleXaTrustedErrAllowsExplicitRetryWithoutLosingOriginalCode(bool initialization)
        {
            var server = Server(async channel =>
            {
                await Query(channel, "set autocommit = 0");
                if (initialization)
                {
                    await channel.WritePacketAsync(Error()); channel.BeginCommand(); await Query(channel, "set autocommit = 0");
                }
                await channel.WritePacketAsync(Ok(status: 0));
                await OracleXaRequest(channel, "START", 0);
                if (!initialization) { await channel.WritePacketAsync(Error()); await OracleXaRequest(channel, "START", 0); }
                await OracleXaScalarReply(channel, 2);
            }, serverVersion: "4.3.5.0");
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); var resource = connection.GetXaResource();
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => resource.StartAsync(XaId));
                Assert.Equal(1064, error.ServerErrorCode); Assert.Null(error.XaErrorCode); Assert.False(error.IsExecutionOutcomeUnknown);
                Assert.Equal(ConnectionState.Open, connection.State); await resource.StartAsync(XaId);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("null")]
        [InlineData("text")]
        [InlineData("fraction")]
        [InlineData("wide")]
        [InlineData("missing")]
        [InlineData("multiple")]
        [InlineData("affected")]
        [InlineData("insert")]
        public async Task OracleXaMalformedScalarCannotPublishSuccessOrAnXaCode(string kind)
        {
            var server = Server(async channel =>
            {
                await OracleXaInitialize(channel); await OracleXaRequest(channel, "START", 0);
                await OracleXaScalarReply(channel, 2, kind == "wide" ? long.MaxValue : 0, malformed: kind);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); var error = await Assert.ThrowsAsync<OceanBaseException>(() => connection.GetXaResource().StartAsync(XaId));
                Assert.True(error.GetBaseException() is ProtocolException || error.GetBaseException() is OverflowException);
                Assert.Null(error.XaErrorCode); Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, "cancel")]
        [InlineData(false, "timeout")]
        [InlineData(false, "disconnect")]
        [InlineData(false, "close")]
        [InlineData(true, "cancel")]
        [InlineData(true, "timeout")]
        [InlineData(true, "disconnect")]
        [InlineData(true, "close")]
        public async Task OracleXaNeverPublishesBeforeTheTailOrReplaysAnInterruptedExchange(bool recovery, string interruption)
        {
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel =>
            {
                await OracleXaInitialize(channel); await OracleXaRequest(channel, recovery ? "RECOVER" : "START", recovery ? null : 0);
                if (recovery) await OracleXaRecoveryReply(channel, OracleXaCollection("-2", new byte[] { 1 }, Array.Empty<byte>()), tail: false);
                else await OracleXaScalarReply(channel, 2, tail: false);
                waiting.SetResult(); if (interruption != "disconnect") Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true;Command Timeout=1");
            try
            {
                await connection.OpenAsync(); var resource = connection.GetXaResource(); using var cancellation = new CancellationTokenSource();
                Task pending = recovery ? resource.RecoverAsync(cancellation.Token) : resource.StartAsync(XaId, cancellationToken: cancellation.Token);
                await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (interruption != "disconnect") Assert.False(pending.IsCompleted);
                await Assert.ThrowsAsync<InvalidOperationException>(() => resource.RecoverAsync());
                if (interruption == "cancel") cancellation.Cancel(); if (interruption == "close") connection.Close();
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => pending);
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.Null(error.XaErrorCode);
                if (interruption == "cancel") Assert.Equal(cancellation.Token, Assert.IsType<OperationCanceledException>(error.InnerException).CancellationToken);
                if (interruption == "timeout") Assert.IsType<TimeoutException>(error.InnerException);
                Assert.Equal(interruption == "close" ? ConnectionState.Closed : ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        // Independent wire fixture: result bitmaps reserve two bits at both nesting levels.
        private static byte[] OracleXaCollection(string? format, byte[]? global, byte[]? branch, int count = 1, bool nullElement = false)
        {
            using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes);
            if (count < 251) writer.Write((byte)count); else { writer.Write((byte)252); writer.Write((ushort)count); }
            var bitmap = new byte[(count + 9) / 8]; if (nullElement) bitmap[0] = 4; writer.Write(bitmap);
            for (var i = 0; i < count; i++)
            {
                if (i == 0 && nullElement) continue;
                writer.Write((byte)((format == null ? 4 : 0) | (global == null ? 8 : 0) | (branch == null ? 16 : 0)));
                foreach (var field in new[] { format == null ? null : Encoding.ASCII.GetBytes(format), global, branch })
                    if (field != null) { writer.Write((byte)field.Length); writer.Write(field); }
            }
            return bytes.ToArray();
        }

        [Theory]
        [InlineData(int.MinValue, false)]
        [InlineData(-2, true)]
        [InlineData(0, false)]
        [InlineData(int.MaxValue, true)]
        public async Task OracleXaRecoveryPreservesSignedFormatAndAllRawBytes(int format, bool nullBranch)
        {
            var global = Enumerable.Range(0, 64).Select(i => (byte)(i * 4)).ToArray();
            var branch = nullBranch ? null : Enumerable.Reverse(global).ToArray();
            var server = Server(async channel =>
            {
                await OracleXaInitialize(channel); await OracleXaRequest(channel, "RECOVER");
                await OracleXaRecoveryReply(channel, OracleXaCollection(format.ToString(CultureInfo.InvariantCulture), global, branch));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); var item = Assert.Single(await connection.GetXaResource().RecoverAsync());
                Assert.Equal(format, item.FormatId); Assert.Equal(global, item.GetGlobalTransactionId());
                Assert.Equal(branch ?? Array.Empty<byte>(), item.GetBranchQualifier());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("reserved")]
        [InlineData("wide")]
        [InlineData("fraction")]
        [InlineData("format-null")]
        [InlineData("global-null")]
        [InlineData("global-empty")]
        [InlineData("global-long")]
        [InlineData("branch-long")]
        [InlineData("element-null")]
        [InlineData("null")]
        [InlineData("missing")]
        [InlineData("multiple")]
        [InlineData("type")]
        public async Task OracleXaMalformedRecoveryFailsTheWholeSnapshot(string kind)
        {
            var collection = OracleXaCollection(kind == "reserved" ? "-1" : kind == "wide" ? "2147483648" : kind == "fraction" ? "1.5" : kind == "format-null" ? null : "0",
                kind == "global-null" ? null : new byte[kind == "global-empty" ? 0 : kind == "global-long" ? 65 : 1],
                new byte[kind == "branch-long" ? 65 : 0], nullElement: kind == "element-null");
            var server = Server(async channel =>
            {
                await OracleXaInitialize(channel); await OracleXaRequest(channel, "RECOVER");
                await OracleXaRecoveryReply(channel, collection, malformed: kind);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); var error = await Assert.ThrowsAsync<OceanBaseException>(() => connection.GetXaResource().RecoverAsync());
                if (kind == "multiple") Assert.IsType<NotSupportedException>(error.GetBaseException());
                else Assert.True(error.GetBaseException() is ProtocolException || error.GetBaseException() is OverflowException || error.GetBaseException() is FormatException);
                Assert.Null(error.XaErrorCode); Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(16383)]
        [InlineData(16384)]
        public async Task OracleXaRecoveryHonorsTheExistingComplexMemberBudget(int count)
        {
            var server = Server(async channel =>
            {
                await OracleXaInitialize(channel); await OracleXaRequest(channel, "RECOVER");
                await OracleXaRecoveryReply(channel, OracleXaCollection("0", new byte[] { 1 }, Array.Empty<byte>(), count), malformed: count == 16384 ? "members" : null);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); var resource = connection.GetXaResource();
                if (count == 16384)
                {
                    var error = await Assert.ThrowsAsync<OceanBaseException>(() => resource.RecoverAsync());
                    Assert.IsType<ProtocolException>(error.GetBaseException()); Assert.Equal(ConnectionState.Broken, connection.State);
                }
                else Assert.Equal(count, (await resource.RecoverAsync()).Length);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task OracleXaBuiltInTypesDoNotBypassPublicDictionaryLookup()
        {
            var server = Server(async channel =>
            {
                await OracleXaInitialize(channel); await OracleXaRequest(channel, "START", 0); await OracleXaScalarReply(channel, 2);
                await StatementCommand(channel, 25);
                var prepare = await StatementCommand(channel, 22); Assert.Contains("ALL_TYPES", Encoding.UTF8.GetString(prepare, 1, prepare.Length - 1));
                await channel.WritePacketAsync(Error());
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); await connection.GetXaResource().StartAsync(XaId);
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => connection.GetComplexTypeAsync("SYS", "DBMS_XA_XID"));
                Assert.Equal(1064, error.ServerErrorCode); Assert.Equal(ConnectionState.Open, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task OracleXaReopenCreatesANewResourceAndInitializesTheNewSession(bool asynchronous)
        {
            var first = Server(async channel =>
            {
                await OracleXaInitialize(channel); await OracleXaRequest(channel, "START", 0); await OracleXaScalarReply(channel, 2);
                channel.BeginCommand(); Assert.Equal(0, await channelStreamRead(channel));
            });
            var second = Server(async channel =>
            {
                await OracleXaInitialize(channel); await OracleXaRequest(channel, "START", 0); await OracleXaScalarReply(channel, 2);
            });
            using var connection = new OceanBaseConnection(Configuration(first.Port) + ";Use Oracle Prepare Execute=true");
            try
            {
                await connection.OpenAsync(); var old = connection.GetXaResource(); await XaStart(old, asynchronous); connection.Close();
                connection.ConnectionString = Configuration(second.Port) + ";Use Oracle Prepare Execute=true"; await connection.OpenAsync();
                await Assert.ThrowsAsync<InvalidOperationException>(() => old.StartAsync(XaId));
                var current = connection.GetXaResource(); Assert.NotSame(old, current); await XaStart(current, asynchronous);
            }
            finally { connection.Close(); first.Stop(); second.Stop(); }
            await first.Completion.WaitAsync(TimeSpan.FromSeconds(10)); await second.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
