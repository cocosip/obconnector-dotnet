using System;
using System.Collections.Generic;
using System.Data;
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
        [Theory]
        [InlineData((byte)0, "NUMBER")]
        [InlineData((byte)3, "NUMBER")]
        [InlineData((byte)246, "NUMBER")]
        [InlineData((byte)253, "VARCHAR2")]
        [InlineData((byte)254, "CHAR")]
        [InlineData((byte)203, "RAW")]
        [InlineData((byte)12, "DATE")]
        [InlineData((byte)202, "TIMESTAMP")]
        [InlineData((byte)207, "NVARCHAR2")]
        [InlineData((byte)208, "NCHAR")]
        public void AnonymousCollectionMetadataResolvesScalarLayoutWithoutDictionary(byte element, string elementName)
        {
            var column = ResultColumn.Parse(ComplexColumn("Owner", "", 17, element), OceanBaseCompatibilityMode.Oracle);
            column.RequireComplexResult(true);
            Assert.Equal(typeof(OceanBaseComplexValue), column.FieldType); Assert.Equal(OceanBaseDbType.Array, column.ProviderType); Assert.Equal("ARRAY", column.DataTypeName);
            var type = column.ComplexType!; Assert.True(type.IsAnonymous); Assert.True(type.IsCollection); Assert.Equal("", type.TypeName); Assert.Equal("Owner", type.SchemaName);
            Assert.Equal(elementName, Assert.Single(type.Members).TypeName); Assert.Null(type.Members[0].Name); Assert.Null(type.Members[0].ComplexType);
            Assert.Equal(DBNull.Value, column.DecodeBinary(null));
            var empty = Assert.IsType<OceanBaseComplexValue>(column.DecodeBinary(new byte[] { 0, 0 })); Assert.Empty(empty.GetValues()); Assert.Same(type, empty.Type);
            Assert.Throws<NotSupportedException>(() => type.CreateValue());
            Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { new OceanBaseParameter { Value = empty } }, OceanBaseCompatibilityMode.Oracle));
            Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { new OceanBaseParameter { ComplexType = type } }, OceanBaseCompatibilityMode.Oracle));
            Assert.Throws<NotSupportedException>(() => column.RequireComplexResult(false));
        }

        [Theory]
        [InlineData((byte)0)]
        [InlineData((byte)3)]
        [InlineData((byte)246)]
        [InlineData((byte)253)]
        [InlineData((byte)254)]
        public void AnonymousCollectionScalarValueKeepsExactBytesAndFollowingFieldBoundary(byte element)
        {
            var column = ResultColumn.Parse(ComplexColumn("", "", element: element), OceanBaseCompatibilityMode.Oracle);
            var number = element == 0 || element == 3 || element == 246;
            var text = number ? "123456789012345678901234567890.12345678" : "患者😀";
            var body = Encoding.UTF8.GetBytes(text);
            var cursor = new ByteCursor(new byte[] { 3, 8, (byte)body.Length }.Concat(body).Concat(new[] { (byte)body.Length }).Concat(body).Concat(new byte[] { 99 }).ToArray());
            var field = column.ReadBinaryField(cursor); Assert.Equal(99, cursor.ReadUInt8()); Assert.False(cursor.HasRemaining);
            var value = Assert.IsType<OceanBaseComplexValue>(column.DecodeBinary(field)); var items = value.GetValues(); Assert.Equal(3, items.Length); Assert.Null(items[1]);
            if (number) { Assert.Equal(text, Assert.IsType<OceanBaseDecimal>(items[0]).ToString()); Assert.Equal(items[0], items[2]); }
            else { Assert.Equal(text, items[0]); Assert.Equal(text, items[2]); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AnonymousCollectionTemporalMembersKeepDateAndNanoseconds(bool timestamp)
        {
            var column = ResultColumn.Parse(ComplexColumn("", "", element: timestamp ? (byte)202 : (byte)12), OceanBaseCompatibilityMode.Oracle);
            var body = Convert.FromHexString(timestamp ? "0C1418020304050615CD5B0709" : "07E8070203040506");
            var cursor = new ByteCursor(new byte[] { 2, 8 }.Concat(body).Concat(new byte[] { 99 }).ToArray());
            var field = column.ReadBinaryField(cursor); Assert.Equal(99, cursor.ReadUInt8());
            var items = Assert.IsType<OceanBaseComplexValue>(column.DecodeBinary(field)).GetValues(); Assert.Null(items[1]);
            var date = new DateTime(2024, 2, 3, 4, 5, 6);
            if (timestamp) Assert.Equal(new OceanBaseTimestamp(date, 123456789, 9), items[0]); else Assert.Equal(date, items[0]);
        }

        [Theory]
        [InlineData((byte)160)]
        [InlineData((byte)200)]
        [InlineData((byte)201)]
        [InlineData((byte)4)]
        [InlineData((byte)255)]
        public void AnonymousCollectionUnsupportedElementsRejectEvenNullResults(byte element)
        {
            var column = ResultColumn.Parse(ComplexColumn("", "", element: element), OceanBaseCompatibilityMode.Oracle);
            Assert.Throws<NotSupportedException>(() => column.RequireComplexResult(true));
            Assert.Throws<NotSupportedException>(() => column.DecodeBinary(null));
        }

        [Fact]
        public void AnonymousCollectionLayoutCannotBeReplacedByAnEmptyNameDictionaryEntry()
        {
            var types = new Dictionary<(string, string), OceanBaseComplexType> { [("Owner", "")] = ComplexResultType(false) };
            var column = ResultColumn.Parse(ComplexColumn("Owner", "", element: 203), OceanBaseCompatibilityMode.Oracle, complexTypes: types);
            Assert.True(column.ComplexType!.IsAnonymous); Assert.True(column.ComplexType.IsCollection);
            Assert.Equal("RAW", Assert.Single(column.ComplexType.Members).TypeName);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AnonymousCollectionCannotBypassTheExplicitOutputContract(bool isNull)
        {
            using var command = new OceanBaseCommand { CommandText = "pkg.p", CommandType = CommandType.StoredProcedure };
            command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", Direction = ParameterDirection.Output, Value = "old" });
            var plan = Internal.Sql.OracleCommandPlan.Create(command.CommandText, command.CommandType, (OceanBaseParameterCollection)command.Parameters);
            var column = ResultColumn.Parse(ComplexColumn("", "", element: 203), OceanBaseCompatibilityMode.Oracle);
            Assert.Throws<NotSupportedException>(() => plan.CaptureOutput(new[] { column }, new[] { isNull ? null : new byte[] { 0, 0 } }));
            Assert.Equal("old", command.Parameters[0].Value);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task AnonymousCollectionOversizedMemberCountDiscardsBufferedAndSequentialSessions(bool async, bool sequential)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 201, 0); await StatementCommand(channel, 23);
                await channel.WritePacketAsync(new byte[] { 1 }); await channel.WritePacketAsync(ComplexColumn("", "", element: 203)); await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(new byte[] { 0, 0, 253, 0, 0, 1 });
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "anonymous"; await command.PrepareAsync();
                var error = await Assert.ThrowsAsync<OceanBaseException>(async () =>
                {
                    using var reader = async ? await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default)
                        : command.ExecuteReader(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                    Assert.True(async ? await reader.ReadAsync() : reader.Read());
                    _ = async ? await reader.GetFieldValueAsync<OceanBaseComplexValue>(0) : reader.GetFieldValue<OceanBaseComplexValue>(0);
                });
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task AnonymousCollectionPartialFieldHonorsCallerCancellation()
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 202, 0); await StatementCommand(channel, 23);
                await channel.WritePacketAsync(new byte[] { 1 }); await channel.WritePacketAsync(ComplexColumn("", "", element: 203)); await channel.WritePacketAsync(Eof());
                var wire = (Stream)typeof(PacketChannel).GetField("_stream", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(channel)!;
                await wire.WriteAsync(FrameHeader(9, 4)); await wire.WriteAsync(new byte[] { 0, 0, 1, 0, 4, 42 });
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "anonymous"; await command.PrepareAsync();
                using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess); Assert.True(await reader.ReadAsync());
                using var caller = new CancellationTokenSource();
                var pending = reader.GetFieldValueAsync<OceanBaseComplexValue>(0, caller.Token); Assert.False(pending.IsCompleted); caller.Cancel();
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => pending.WaitAsync(TimeSpan.FromSeconds(3)));
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.Equal(ConnectionState.Broken, connection.State);
                Exception? cause = error;
                while (cause != null && !(cause is OperationCanceledException)) cause = cause.InnerException;
                Assert.Equal(caller.Token, Assert.IsAssignableFrom<OperationCanceledException>(cause).CancellationToken);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task AnonymousCollectionUnsupportedLayoutOrTextProtocolFailsBeforeRows(bool async, bool text)
        {
            var server = Server(async channel =>
            {
                if (text) await StatementCommand(channel, 3);
                else { await StatementCommand(channel, 22); await Prepared(channel, 199, 0); await StatementCommand(channel, 23); }
                await channel.WritePacketAsync(new byte[] { 1 }); await channel.WritePacketAsync(ComplexColumn("", "", element: text ? (byte)203 : (byte)160));
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "anonymous";
                if (!text) { if (async) await command.PrepareAsync(); else command.Prepare(); }
                var error = await Assert.ThrowsAsync<OceanBaseException>(async () => { using var reader = async ? await command.ExecuteReaderAsync() : command.ExecuteReader(); });
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.IsType<NotSupportedException>(error.InnerException); Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task AnonymousCollectionCursorRejectsElementOrVersionChange(bool async, bool changeVersion)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 200, 0, columns: 1); await StatementCommand(channel, 23);
                await channel.WritePacketAsync(new byte[] { 1 }); await channel.WritePacketAsync(ComplexColumn("", "", 17, 203)); await channel.WritePacketAsync(Eof(66));
                await StatementCommand(channel, 28); await channel.WritePacketAsync(new byte[] { 1 });
                await channel.WritePacketAsync(ComplexColumn("", "", 17, 203)); await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(new byte[] { 0, 0, 1, 0, 1, 42 }); await channel.WritePacketAsync(Eof(66));
                await StatementCommand(channel, 28); await channel.WritePacketAsync(new byte[] { 1 });
                await channel.WritePacketAsync(ComplexColumn("", "", changeVersion ? 18 : 17, changeVersion ? (byte)203 : (byte)253));
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = "anonymous"; SetFetchSize(command, 1);
                using var reader = async ? await command.ExecuteReaderAsync() : command.ExecuteReader();
                Assert.True(async ? await reader.ReadAsync() : reader.Read());
                Assert.Equal(new byte[] { 42 }, reader.GetFieldValue<OceanBaseComplexValue>(0).GetValues()[0]);
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => async ? reader.ReadAsync() : Task.FromResult(reader.Read()));
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.IsType<ProtocolException>(error.InnerException); Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task AnonymousCollectionReadsAndSkipsWithoutPreloadingAnyDictionary(bool async, bool sequential)
        {
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 198, 0); await StatementCommand(channel, 23);
                await channel.WritePacketAsync(new byte[] { 3 }); await channel.WritePacketAsync(ComplexColumn("", "", element: 203));
                await channel.WritePacketAsync(Column("after", 8)); await channel.WritePacketAsync(ComplexColumn("", "", element: 203)); await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(new byte[] { 0, 16, 3, 8, 2, 1, 2, 0 }.Concat(BitConverter.GetBytes(42L)).ToArray());
                await channel.WritePacketAsync(new byte[] { 0, 16, 0, 0 }.Concat(BitConverter.GetBytes(43L)).ToArray());
                await channel.WritePacketAsync(Eof()); await StatementCommand(channel, 25); channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(7));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "anonymous"; if (async) await command.PrepareAsync(); else command.Prepare();
                    using var reader = async ? await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default)
                        : command.ExecuteReader(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                    Assert.Equal("ARRAY", reader.GetDataTypeName(0)); Assert.True(async ? await reader.ReadAsync() : reader.Read());
                    var value = async ? await reader.GetFieldValueAsync<OceanBaseComplexValue>(0) : reader.GetFieldValue<OceanBaseComplexValue>(0);
                    Assert.True(value.Type.IsAnonymous); var items = value.GetValues(); Assert.Equal(new byte[] { 1, 2 }, items[0]); Assert.Null(items[1]); Assert.Empty(Assert.IsType<byte[]>(items[2]));
                    ((byte[])items[0]!)[0] = 99; Assert.Equal(new byte[] { 1, 2 }, value.GetValues()[0]);
                    Assert.Equal(42L, reader.GetInt64(1)); Assert.True(reader.IsDBNull(2));
                    Assert.True(async ? await reader.ReadAsync() : reader.Read()); Assert.Equal(43L, reader.GetInt64(1));
                    Assert.False(async ? await reader.ReadAsync() : reader.Read());
                }
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(7, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
