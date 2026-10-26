using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Values;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        private static byte[] ComplexColumn(string schema = "Owner", string name = "Thing", long version = 0, byte element = 203)
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(Column("complex", 160));
            PreparedParameters.WriteBytes(writer, Encoding.UTF8.GetBytes(schema));
            PreparedParameters.WriteBytes(writer, Encoding.UTF8.GetBytes(name));
            if (version < 251) writer.Write((byte)version);
            else { writer.Write((byte)254); writer.Write((ulong)version); }
            if (name.Length == 0) writer.Write(element);
            return stream.ToArray();
        }

        private static OceanBaseComplexType ComplexResultType(bool collection, params OceanBaseComplexMember[] members)
            => new OceanBaseComplexType(null!, "Owner", "Thing", collection, members);
        private static OceanBaseComplexMember ComplexResultScalar(string name, byte type)
            => new OceanBaseComplexMember(name, null, "RAW", null, new ComplexType(type));

        [Theory]
        [InlineData("Thing")]
        [InlineData("")]
        public void ComplexColumnMetadataKeepsNullVersionDistinctFromZero(string name)
        {
            var packet = ComplexColumn(name: name);
            packet[packet.Length - (name.Length == 0 ? 2 : 1)] = 251;
            var column = ResultColumn.Parse(packet, OceanBaseCompatibilityMode.Oracle);
            Assert.Null(column.ComplexMetadata!.Version);
            Assert.Equal(name.Length == 0 ? (byte?)203 : null, column.ComplexMetadata.ElementType);
            var zero = ResultColumn.Parse(ComplexColumn(name: name), OceanBaseCompatibilityMode.Oracle);
            Assert.False(column.ComplexMetadata.Matches(zero.ComplexMetadata));
            Assert.True(column.ComplexMetadata.Matches(ResultColumn.Parse(packet, OceanBaseCompatibilityMode.Oracle).ComplexMetadata));
        }

        [Theory]
        [InlineData(0)] [InlineData(250)] [InlineData(251)] [InlineData(4294967296)] [InlineData(long.MaxValue)]
        public void ComplexColumnMetadataPreservesIdentityVersionAndFieldBoundaries(long version)
        {
            var type = ComplexResultType(true, ComplexResultScalar("A", 203));
            var types = new Dictionary<(string, string), OceanBaseComplexType> { [("Owner", "Thing")] = type };
            var column = ResultColumn.Parse(ComplexColumn(version: version), OceanBaseCompatibilityMode.Oracle, complexTypes: types);
            Assert.Equal(version, column.ComplexMetadata!.Version); Assert.Same(type, column.ComplexType);
            Assert.Equal(typeof(OceanBaseComplexValue), column.FieldType); Assert.Equal("Thing", column.DataTypeName);
            Assert.Equal(OceanBaseDbType.Array, column.ProviderType);
            Assert.Null(new ResultDbColumn(column, 0).ColumnSize);
            var cursor = new ByteCursor(new byte[] { 3, 8, 2, 1, 2, 0, 99 });
            var bytes = column.ReadBinaryField(cursor); Assert.Equal(99, cursor.ReadUInt8()); Assert.False(cursor.HasRemaining);
            var value = Assert.IsType<OceanBaseComplexValue>(column.DecodeBinary(bytes)); Assert.Same(type, value.Type);
            var values = value.GetValues(); Assert.Equal(new byte[] { 1, 2 }, values[0]); Assert.Null(values[1]); Assert.Empty(Assert.IsType<byte[]>(values[2]));
            ((byte[])values[0]!)[0] = 9; Assert.Equal(new byte[] { 1, 2 }, value.GetValues()[0]);
            Assert.Throws<NotSupportedException>(() => column.Decode(null));
        }

        [Theory]
        [InlineData((byte)210, "BLOB")]
        [InlineData((byte)211, "CLOB")]
        public void AnonymousComplexLobMetadataExposesTheDeclaredElement(byte element, string name)
        {
            var column = ResultColumn.Parse(ComplexColumn(name: "", element: element), OceanBaseCompatibilityMode.Oracle);
            Assert.Equal("ARRAY", column.DataTypeName);
            Assert.Equal(name, column.ComplexType!.Members[0].TypeName);
            Assert.Throws<NotSupportedException>(() => column.FieldType);
        }

        [Fact]
        public void ComplexLobMetadataRequiresNegotiatedLocators()
        {
            var type = ComplexResultType(false, ComplexResultScalar("A", 210));
            var column = ResultColumn.Parse(ComplexColumn(), OceanBaseCompatibilityMode.Oracle,
                complexTypes: new Dictionary<(string, string), OceanBaseComplexType> { [("Owner", "Thing")] = type });
            Assert.Throws<NotSupportedException>(() => column.FieldType);
        }

        [Theory]
        [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
        public async Task ComplexResultsReadOrSkipExactlyOneFieldAndKeepSessionReusable(bool async, bool sequential)
        {
            var server = Server(async channel =>
            {
                await ComplexScalarType(channel);
                await StatementCommand(channel, 22); await Prepared(channel, 181, 0); await StatementCommand(channel, 23);
                await channel.WritePacketAsync(new byte[] { 3 });
                await channel.WritePacketAsync(ComplexColumn()); await channel.WritePacketAsync(Column("after", 8)); await channel.WritePacketAsync(ComplexColumn());
                await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(new byte[] { 0, 16, 3, 8, 2, 1, 2, 0 }.Concat(BitConverter.GetBytes(42L)).ToArray());
                await channel.WritePacketAsync(new byte[] { 0, 16, 0, 0 }.Concat(BitConverter.GetBytes(43L)).ToArray());
                await channel.WritePacketAsync(Eof());
                await StatementCommand(channel, 3); await channel.WritePacketAsync(Ok(7)); await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var type = await connection.GetComplexTypeAsync("Owner", "Thing");
                using var command = connection.CreateCommand(); command.CommandText = "complex";
                if (async) await command.PrepareAsync(); else command.Prepare();
                OceanBaseComplexValue value;
                using (var reader = async ? await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default)
                    : command.ExecuteReader(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default))
                {
                    Assert.Equal(typeof(OceanBaseComplexValue), reader.GetFieldType(0)); Assert.Equal("Thing", reader.GetDataTypeName(0));
                    Assert.True(async ? await reader.ReadAsync() : reader.Read());
                    value = async ? await reader.GetFieldValueAsync<OceanBaseComplexValue>(0) : reader.GetFieldValue<OceanBaseComplexValue>(0);
                    Assert.Same(type, value.Type); Assert.Equal(new byte[] { 1, 2 }, value.GetValues()[0]); Assert.Null(value.GetValues()[1]);
                    Assert.Equal(42L, async ? await reader.GetFieldValueAsync<long>(1) : reader.GetInt64(1));
                    Assert.True(async ? await reader.IsDBNullAsync(2) : reader.IsDBNull(2)); Assert.Equal(DBNull.Value, reader.GetValue(2));
                    Assert.True(async ? await reader.ReadAsync() : reader.Read());
                    // Skip the empty collection without treating its count as an outer byte length.
                    Assert.Equal(43L, async ? await reader.GetFieldValueAsync<long>(1) : reader.GetInt64(1));
                    Assert.False(async ? await reader.ReadAsync() : reader.Read());
                }
                connection.ClearComplexTypeCache(); Assert.Equal(new byte[] { 1, 2 }, value.GetValues()[0]);
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(7, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("missing")] [InlineData("truncated")] [InlineData("null-name")]
        [InlineData("null-schema")] [InlineData("missing-version")]
        [InlineData("overflow-version")] [InlineData("long-name")] [InlineData("nul-name")]
        [InlineData("utf8-name")] [InlineData("missing-element")] [InlineData("trailing")]
        public void ComplexMetadataRejectsMalformedExtensions(string kind)
        {
            var valid = ComplexColumn(); var header = Column("complex", 160); byte[] extension;
            switch (kind)
            {
                case "missing": extension = Array.Empty<byte>(); break;
                case "truncated": extension = new byte[] { 2, 65 }; break;
                case "null-name": extension = new byte[] { 0, 251, 0 }; break;
                case "null-schema": extension = new byte[] { 251, 1, 84, 0 }; break;
                case "missing-version": extension = new byte[] { 0, 1, 84 }; break;
                case "overflow-version": extension = new byte[] { 0, 1, 84, 254, 0, 0, 0, 0, 0, 0, 0, 128 }; break;
                case "long-name": extension = new byte[] { 252, 1, 2 }; break;
                case "nul-name": extension = new byte[] { 1, 0, 1, 84, 0 }; break;
                case "utf8-name": extension = new byte[] { 1, 255, 1, 84, 0 }; break;
                case "missing-element": extension = new byte[] { 0, 0, 0 }; break;
                default: extension = valid.Skip(header.Length).Concat(new byte[] { 7 }).ToArray(); break;
            }
            void Parse() => ResultColumn.Parse(header.Concat(extension).ToArray(), OceanBaseCompatibilityMode.Oracle);
            if (kind == "utf8-name") Assert.Throws<DecoderFallbackException>(Parse);
            else if (kind == "trailing") Assert.Throws<NotSupportedException>(Parse);
            else Assert.Throws<ProtocolException>(Parse);
        }

        [Fact]
        public void ComplexMetadataRequiresOracleExactPreloadedIdentityAndNamedLayout()
        {
            Assert.Throws<NotSupportedException>(() => ResultColumn.Parse(ComplexColumn(), OceanBaseCompatibilityMode.MySql));
            var type = ComplexResultType(false, ComplexResultScalar("A", 203));
            var types = new Dictionary<(string, string), OceanBaseComplexType> { [("Owner", "Thing")] = type };
            foreach (var bytes in new[] { ComplexColumn("owner"), ComplexColumn(name: "thing") })
            {
                var column = ResultColumn.Parse(bytes, OceanBaseCompatibilityMode.Oracle, complexTypes: types);
                Assert.Throws<NotSupportedException>(() => column.RequireComplexResult(true));
            }
            var named = ResultColumn.Parse(ComplexColumn(), OceanBaseCompatibilityMode.Oracle, complexTypes: types);
            Assert.Equal(OceanBaseDbType.Struct, named.ProviderType);
            Assert.Throws<NotSupportedException>(() => named.RequireComplexResult(false));
        }

        [Fact]
        public void ComplexNestedResultKeepsDescriptorsAndCopiesRawAtEveryLevel()
        {
            var child = new OceanBaseComplexType(null!, "Child", "Items", true, new[] { ComplexResultScalar("A", 203) });
            var root = ComplexResultType(false, new OceanBaseComplexMember("CHILD", "Child", "Items", child, child.Layout), ComplexResultScalar("A", 0));
            var column = ResultColumn.Parse(ComplexColumn(), OceanBaseCompatibilityMode.Oracle,
                complexTypes: new Dictionary<(string, string), OceanBaseComplexType> { [("Owner", "Thing")] = root });
            var cursor = new ByteCursor(new byte[] { 9, 0, 2, 0, 1, 42, 0, 2, 52, 50, 88 }, 1, 10);
            var field = column.ReadBinaryField(cursor); Assert.Equal(88, cursor.ReadUInt8());
            var value = Assert.IsType<OceanBaseComplexValue>(column.DecodeBinary(field));
            var nested = Assert.IsType<OceanBaseComplexValue>(value.GetValues()[0]); Assert.Same(child, nested.Type);
            Assert.Equal(new byte[] { 42 }, nested.GetValues()[0]); Assert.Empty(Assert.IsType<byte[]>(nested.GetValues()[1]));
            Assert.Equal(new OceanBaseDecimal(42), value.GetValues()[1]);
            field[4] = 99; var raw = (byte[])nested.GetValues()[0]!; raw[0] = 99;
            Assert.Equal(new byte[] { 42 }, nested.GetValues()[0]);
            Assert.Throws<ProtocolException>(() => column.DecodeBinary(field.Concat(new byte[] { 0 }).ToArray()));
        }

        [Theory]
        [InlineData(false, "missing")] [InlineData(true, "missing")]
        [InlineData(false, "clear")] [InlineData(true, "clear")]
        [InlineData(false, "text")] [InlineData(true, "text")]
        [InlineData(false, "transaction")] [InlineData(true, "transaction")]
        public async Task ComplexResultRejectsMissingOrInvalidatedDictionaryAndTextProtocol(bool async, string kind)
        {
            var server = Server(async channel =>
            {
                if (kind != "missing") await ComplexScalarType(channel);
                if (kind == "transaction")
                {
                    for (var i = 0; i < 2; i++) { await StatementCommand(channel, 3); await channel.WritePacketAsync(Ok(status: i == 0 ? (byte)6 : (byte)4)); }
                }
                if (kind == "text") await StatementCommand(channel, 3);
                else { await StatementCommand(channel, 22); await Prepared(channel, 183, 0); await StatementCommand(channel, 23); }
                await channel.WritePacketAsync(new byte[] { 1 }); await channel.WritePacketAsync(ComplexColumn());
                // No body: reject the metadata before trying to interpret even a NULL complex value.
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                if (kind != "missing") await connection.GetComplexTypeAsync("Owner", "Thing");
                if (kind == "clear") connection.ClearComplexTypeCache();
                using var transaction = kind == "transaction" ? connection.BeginTransaction() : null;
                using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "complex";
                if (kind != "text") { if (async) await command.PrepareAsync(); else command.Prepare(); }
                var error = await Assert.ThrowsAsync<OceanBaseException>(async () => { using var reader = async ? await command.ExecuteReaderAsync() : command.ExecuteReader(); });
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.IsType<NotSupportedException>(error.InnerException);
                Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
        public async Task ComplexMalformedFramingDiscardsSessionInBufferedAndSequentialReads(bool async, bool sequential)
        {
            var server = Server(async channel =>
            {
                await ComplexScalarType(channel); await StatementCommand(channel, 22); await Prepared(channel, 184, 0); await StatementCommand(channel, 23);
                await channel.WritePacketAsync(new byte[] { 1 }); await channel.WritePacketAsync(ComplexColumn()); await channel.WritePacketAsync(Eof());
                // 65536 collection members; reject before allocating or waiting for its bitmap/body.
                await channel.WritePacketAsync(new byte[] { 0, 0, 253, 0, 0, 1 });
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); await connection.GetComplexTypeAsync("Owner", "Thing");
                using var command = connection.CreateCommand(); command.CommandText = "complex"; await command.PrepareAsync();
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

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public void ComplexOracleOutputRequiresAnExplicitTypeContract(bool isNull)
        {
            using var command = new OceanBaseCommand { CommandText = "pkg.p", CommandType = CommandType.StoredProcedure };
            command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", Direction = ParameterDirection.Output, Value = "old" });
            var plan = Internal.Sql.OracleCommandPlan.Create(command.CommandText, command.CommandType, (OceanBaseParameterCollection)command.Parameters);
            var column = ResultColumn.Parse(ComplexColumn(), OceanBaseCompatibilityMode.Oracle,
                complexTypes: new Dictionary<(string, string), OceanBaseComplexType> { [("Owner", "Thing")] = ComplexResultType(true, ComplexResultScalar("A", 203)) });
            Assert.Throws<NotSupportedException>(() => plan.CaptureOutput(new[] { column }, new[] { isNull ? null : new byte[] { 0, 0 } }));
            Assert.Equal("old", command.Parameters[0].Value);
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public async Task ComplexSequentialGetterCompletesBeforeFollowingLargeFieldArrives(bool async)
        {
            const int length = 4 * 1024 * 1024;
            var tail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Server(async channel =>
            {
                await ComplexScalarType(channel); await StatementCommand(channel, 22); await Prepared(channel, 185, 0); await StatementCommand(channel, 23);
                await channel.WritePacketAsync(new byte[] { 2 }); await channel.WritePacketAsync(ComplexColumn());
                await channel.WritePacketAsync(Column("large", 252, 63)); await channel.WritePacketAsync(Eof());
                var wire = (Stream)typeof(PacketChannel).GetField("_stream", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(channel)!;
                await wire.WriteAsync(FrameHeader(length + 15, 5)); await wire.WriteAsync(new byte[] { 0, 0, 1, 0, 1, 42 });
                await tail.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await wire.WriteAsync(new byte[] { 254 }.Concat(BitConverter.GetBytes((long)length)).ToArray());
                var chunk = new byte[8192]; Array.Fill(chunk, (byte)7);
                for (var i = 0; i < length; i += chunk.Length) await wire.WriteAsync(chunk);
                await wire.WriteAsync(ResponseFrame(Eof(), 6));
                await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); await connection.GetComplexTypeAsync("Owner", "Thing");
                using var command = connection.CreateCommand(); command.CommandText = "complex"; await command.PrepareAsync();
                using var reader = async ? await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess) : command.ExecuteReader(CommandBehavior.SequentialAccess);
                Assert.True(async ? await reader.ReadAsync() : reader.Read());
                var pending = async ? reader.GetFieldValueAsync<OceanBaseComplexValue>(0) : Task.Run(() => reader.GetFieldValue<OceanBaseComplexValue>(0));
                var value = await pending.WaitAsync(TimeSpan.FromSeconds(3)); Assert.Equal(new byte[] { 42 }, value.GetValues()[0]);
                tail.SetResult(); using var stream = reader.GetStream(1); var buffer = new byte[8192]; var total = 0; int read;
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                while ((read = stream.Read(buffer, 0, buffer.Length)) != 0) total += read;
                allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
                Assert.Equal(length, total); Assert.InRange(allocated, 0, 1024 * 1024); Assert.Equal(7, buffer[0]);
                Assert.False(async ? await reader.ReadAsync() : reader.Read());
            }
            finally { tail.TrySetResult(); connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
        public async Task ComplexSequentialMaterializationHonorsCancellationOrConnectionClose(bool async, bool close)
        {
            var server = Server(async channel =>
            {
                await ComplexScalarType(channel); await StatementCommand(channel, 22); await Prepared(channel, 186, 0); await StatementCommand(channel, 23);
                await channel.WritePacketAsync(new byte[] { 1 }); await channel.WritePacketAsync(ComplexColumn()); await channel.WritePacketAsync(Eof());
                var wire = (Stream)typeof(PacketChannel).GetField("_stream", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(channel)!;
                // The RAW advertises four bytes, but only its first byte is available.
                await wire.WriteAsync(FrameHeader(9, 4)); await wire.WriteAsync(new byte[] { 0, 0, 1, 0, 4, 42 });
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); await connection.GetComplexTypeAsync("Owner", "Thing");
                using var command = connection.CreateCommand(); command.CommandText = "complex"; await command.PrepareAsync();
                using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess); Assert.True(await reader.ReadAsync());
                using var caller = new CancellationTokenSource();
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var pending = async ? reader.GetFieldValueAsync<OceanBaseComplexValue>(0, caller.Token) : Task.Run(() => { entered.SetResult(); return reader.GetFieldValue<OceanBaseComplexValue>(0); });
                if (!async) await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.False(pending.IsCompleted);
                if (close) connection.Close(); else if (async) caller.Cancel(); else command.Cancel();
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => pending.WaitAsync(TimeSpan.FromSeconds(3)));
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.Equal(close ? ConnectionState.Closed : ConnectionState.Broken, connection.State);
                if (async && !close)
                {
                    Exception? cause = error;
                    while (cause != null && !(cause is OperationCanceledException)) cause = cause.InnerException;
                    Assert.Equal(caller.Token, Assert.IsAssignableFrom<OperationCanceledException>(cause).CancellationToken);
                }
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
        public async Task ComplexNestedColumnsUseThePreloadedGraphForReadingAndSkipping(bool async, bool sequential)
        {
            var server = Server(async channel =>
            {
                await ComplexDictionaryReply(channel, 191, "Owner", "Thing", true, new[] { "Owner", "Thing", "OBJECT", "2" }); await StatementCommand(channel, 25);
                await ComplexDictionaryReply(channel, 192, "Owner", "Thing", false,
                    new[] { "CHILD", "1", "Child", "Items", "COLLECTION" }, new[] { "ID", "2", null, "NUMBER", "NUMBER" }); await StatementCommand(channel, 25);
                await ComplexDictionaryReply(channel, 193, "Child", "Items", true, new[] { "Child", "Items", "COLLECTION", null }); await StatementCommand(channel, 25);
                await StatementCommand(channel, 22); await Prepared(channel, 194, 2); await StatementCommand(channel, 23);
                await ComplexDictionaryRows(channel, ComplexMemberNames, new[] { null, "1", null, "RAW", "RAW" }); await StatementCommand(channel, 25);
                await StatementCommand(channel, 22); await Prepared(channel, 195, 0); await StatementCommand(channel, 23);
                await channel.WritePacketAsync(new byte[] { 2 }); await channel.WritePacketAsync(ComplexColumn());
                await channel.WritePacketAsync(Column("after", 8)); await channel.WritePacketAsync(Eof());
                // Root { CHILD = [ RAW(42), empty RAW ], ID = 42 }, then a fixed-width column.
                var row = new byte[] { 0, 0, 0, 2, 0, 1, 42, 0, 2, 52, 50 }.Concat(BitConverter.GetBytes(9L)).ToArray();
                await channel.WritePacketAsync(row); await channel.WritePacketAsync(row); await channel.WritePacketAsync(Eof());
                await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var root = await connection.GetComplexTypeAsync("Owner", "Thing");
                using var command = connection.CreateCommand(); command.CommandText = "complex"; await command.PrepareAsync();
                using var reader = async ? await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default)
                    : command.ExecuteReader(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                Assert.True(async ? await reader.ReadAsync() : reader.Read());
                var value = async ? await reader.GetFieldValueAsync<OceanBaseComplexValue>(0) : reader.GetFieldValue<OceanBaseComplexValue>(0);
                Assert.Same(root, value.Type); var child = Assert.IsType<OceanBaseComplexValue>(value.GetValues()[0]);
                Assert.Same(root.Members[0].ComplexType, child.Type); Assert.Equal(new byte[] { 42 }, child.GetValues()[0]);
                Assert.Empty(Assert.IsType<byte[]>(child.GetValues()[1])); Assert.Equal(new OceanBaseDecimal(42), value.GetValues()[1]);
                Assert.Equal(9L, reader.GetInt64(1));
                Assert.True(async ? await reader.ReadAsync() : reader.Read()); Assert.Equal(9L, reader.GetInt64(1));
                Assert.False(async ? await reader.ReadAsync() : reader.Read());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("BLOB", false)] [InlineData("BLOB", true)]
        [InlineData("CLOB", false)] [InlineData("CLOB", true)]
        public async Task ComplexLobMembersPublishBoundHandlesFromBinaryResults(string scalar, bool async)
        {
            var locator = InlineLob(Encoding.UTF8.GetBytes(scalar == "CLOB" ? "患者" : "blob"));
            var server = Server(async channel =>
            {
                await ComplexScalarType(channel, scalar: scalar, collection: false);
                await StatementCommand(channel, 22); await Prepared(channel, 241, 0); await StatementCommand(channel, 23);
                await channel.WritePacketAsync(new byte[] { 1 }); await channel.WritePacketAsync(ComplexColumn()); await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(new byte[] { 0, 0, 0 }.Concat(WithLength(locator)).ToArray()); await channel.WritePacketAsync(Eof());
                await StatementCommand(channel, 25);
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port));
            try
            {
                await connection.OpenAsync(); await connection.GetComplexTypeAsync("Owner", "Thing");
                using var command = connection.CreateCommand(); command.CommandText = "complex"; await command.PrepareAsync();
                using (var reader = async ? await command.ExecuteReaderAsync() : command.ExecuteReader())
                {
                    Assert.True(async ? await reader.ReadAsync() : reader.Read());
                    var value = Assert.IsType<OceanBaseComplexValue>(reader.GetValue(0));
                    using var lob = Assert.IsType<OceanBaseLob>(Assert.Single(value.GetValues()));
                    Assert.Equal(scalar == "CLOB", lob.IsCharacter); Assert.Equal(scalar == "CLOB" ? 6 : 4, lob.ByteLength);
                    Assert.False(async ? await reader.ReadAsync() : reader.Read());
                }
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("BLOB", false)] [InlineData("BLOB", true)]
        [InlineData("CLOB", false)] [InlineData("CLOB", true)]
        public async Task AnonymousComplexLobElementsPublishBoundHandlesFromBinaryResults(string scalar, bool async)
        {
            var locator = InlineLob(Encoding.UTF8.GetBytes(scalar == "CLOB" ? "患者" : "blob"));
            var element = scalar == "CLOB" ? (byte)211 : (byte)210;
            var server = Server(async channel =>
            {
                await StatementCommand(channel, 22); await Prepared(channel, 242, 0); await StatementCommand(channel, 23);
                await channel.WritePacketAsync(new byte[] { 1 }); await channel.WritePacketAsync(ComplexColumn("", "", element: element)); await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(new byte[] { 0, 0, 1, 0 }.Concat(WithLength(locator)).ToArray()); await channel.WritePacketAsync(Eof());
                await StatementCommand(channel, 25);
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand(); command.CommandText = "anonymous"; await command.PrepareAsync();
                using (var reader = async ? await command.ExecuteReaderAsync() : command.ExecuteReader())
                {
                    Assert.True(async ? await reader.ReadAsync() : reader.Read());
                    var value = Assert.IsType<OceanBaseComplexValue>(reader.GetValue(0));
                    using var lob = Assert.IsType<OceanBaseLob>(Assert.Single(value.GetValues()));
                    Assert.Equal(scalar == "CLOB", lob.IsCharacter); Assert.Equal(scalar == "CLOB" ? 6 : 4, lob.ByteLength);
                    Assert.False(async ? await reader.ReadAsync() : reader.Read());
                }
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
        public async Task ComplexCursorPagesRejectAChangedTypeVersion(bool async, bool sequential)
        {
            var server = Server(async channel =>
            {
                await ComplexScalarType(channel); await StatementCommand(channel, 22); await Prepared(channel, 196, 0, columns: 1); await StatementCommand(channel, 23);
                await channel.WritePacketAsync(new byte[] { 1 }); await channel.WritePacketAsync(ComplexColumn(version: 17)); await channel.WritePacketAsync(Eof(66));
                await StatementCommand(channel, 28);
                await channel.WritePacketAsync(new byte[] { 1 }); await channel.WritePacketAsync(ComplexColumn(version: 17)); await channel.WritePacketAsync(Eof());
                await channel.WritePacketAsync(new byte[] { 0, 0, 0, 0 }); await channel.WritePacketAsync(Eof(66));
                await StatementCommand(channel, 28);
                await channel.WritePacketAsync(new byte[] { 1 }); await channel.WritePacketAsync(ComplexColumn(version: 18));
                Assert.Equal(0, await channelStreamRead(channel));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); await connection.GetComplexTypeAsync("Owner", "Thing");
                using var command = connection.CreateCommand(); command.CommandText = "complex"; SetFetchSize(command, 1);
                using var reader = async ? await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default)
                    : command.ExecuteReader(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                Assert.True(async ? await reader.ReadAsync() : reader.Read()); Assert.Empty(reader.GetFieldValue<OceanBaseComplexValue>(0).GetValues());
                var error = await Assert.ThrowsAsync<OceanBaseException>(() => async ? reader.ReadAsync() : Task.FromResult(reader.Read()));
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
