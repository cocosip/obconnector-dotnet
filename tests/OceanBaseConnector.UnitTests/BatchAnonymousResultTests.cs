using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        public static IEnumerable<object[]> BatchAnonymousScalarCases()
        {
            foreach (var asynchronous in new[] { false, true })
                foreach (var element in new byte[] { 0, 203, 253, 12, 202 })
                    foreach (var api in new[] { "reader", "scalar", "nonquery" })
                        yield return new object[] { asynchronous, element, api };
        }

        [Theory]
        [InlineData((byte)0)] [InlineData((byte)203)] [InlineData((byte)253)] [InlineData((byte)12)] [InlineData((byte)202)]
        public void BatchMultiSendAnonymousBinaryResultReusesScalarLayout(byte element)
        {
            var column = ResultColumn.Parse(ComplexColumn("", "", element: element), OceanBaseCompatibilityMode.Oracle);
            column.RequireBatchMultiSendResult(binary: true);
            Assert.Equal(typeof(OceanBaseComplexValue), column.FieldType); Assert.Equal(OceanBaseDbType.Array, column.ProviderType);
            Assert.True(column.ComplexType!.IsAnonymous);
            Assert.Throws<NotSupportedException>(() => column.RequireBatchMultiSendResult(binary: false));
        }

        private static async Task PrepareAnonymousBatchResult(PacketChannel channel, byte id, byte element)
        {
            await StatementCommand(channel, 22);
            await channel.WritePacketAsync(new byte[] { 0, id, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
            await channel.WritePacketAsync(ComplexColumn("", "", element: element)); await channel.WritePacketAsync(Eof());
        }

        private static async Task ReplyAnonymousBatchResult(PacketChannel channel, byte element, params byte[][] rows)
        {
            channel.BeginResponse(); await channel.WritePacketAsync(new byte[] { 1 });
            await channel.WritePacketAsync(ComplexColumn("", "", element: element)); await channel.WritePacketAsync(Eof());
            foreach (var row in rows) await channel.WritePacketAsync(row);
            await channel.WritePacketAsync(Eof());
        }

        private static (byte[] Wire, object Value) AnonymousBatchScalar(byte element)
        {
            switch (element)
            {
                case 0: return (WithLength(Encoding.UTF8.GetBytes("123456789012345678901234567890.12345678")), new OceanBaseDecimal(System.Numerics.BigInteger.Parse("12345678901234567890123456789012345678"), 8));
                case 203: return (new byte[] { 3, 0, 39, 255 }, new byte[] { 0, 39, 255 });
                case 253: return (WithLength(Encoding.UTF8.GetBytes("患者🙂")), "患者🙂");
                case 12: return (Convert.FromHexString("07E8070203040506"), new DateTime(2024, 2, 3, 4, 5, 6));
                default: return (Convert.FromHexString("0C1418020304050615CD5B0709"), new OceanBaseTimestamp(new DateTime(2024, 2, 3, 4, 5, 6), 123456789, 9));
            }
        }

        [Theory]
        [MemberData(nameof(BatchAnonymousScalarCases))]
        public async Task BatchMultiSendAnonymousBinaryResultsReadAndDrainAcrossGroups(bool asynchronous, byte element, string api)
        {
            var scalar = AnonymousBatchScalar(element);
            var valueRow = new byte[] { 0, 0, 3, 8 }.Concat(scalar.Wire).Concat(scalar.Wire).ToArray();
            var server = Server(async channel =>
            {
                for (byte id = 90; id < 93; id++) await PrepareAnonymousBatchResult(channel, id, element);
                Assert.Equal(90, (await StatementCommand(channel, 23))[1]); Assert.Equal(91, (await StatementCommand(channel, 23))[1]);
                await ReplyAnonymousBatchResult(channel, element, valueRow, new byte[] { 0, 4 });
                await ReplyAnonymousBatchResult(channel, element, new byte[] { 0, 0, 0, 0 });
                Assert.Equal(92, (await StatementCommand(channel, 23))[1]);
                await ReplyAnonymousBatchResult(channel, element, valueRow);
                for (var i = 0; i < 3; i++) await StatementCommand(channel, 25);
                channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(7));
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Batch Multi Send=true;Batch Multi Send Number=2");
            try
            {
                await connection.OpenAsync(); OceanBaseComplexValue? retained = null;
                using (var batch = connection.CreateBatch())
                {
                    for (var i = 0; i < 3; i++) BatchItem(batch, "select value from t" + i);
                    if (api == "reader")
                    {
                        using var reader = asynchronous ? await batch.ExecuteReaderAsync() : batch.ExecuteReader();
                        for (var i = 0; i < 3; i++)
                        {
                            Assert.Equal("ARRAY", reader.GetDataTypeName(0)); Assert.Equal(typeof(OceanBaseComplexValue), reader.GetFieldType(0));
                            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                            var value = reader.GetFieldValue<OceanBaseComplexValue>(0); Assert.True(value.Type.IsAnonymous);
                            if (i == 1) Assert.Empty(value.GetValues()); else { CheckAnonymousBatchScalar(value, scalar.Value); retained = value; }
                            if (i == 0) { Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.True(reader.IsDBNull(0)); }
                            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                            Assert.Equal(i != 2, asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                        }
                    }
                    else if (api == "scalar")
                    {
                        retained = Assert.IsType<OceanBaseComplexValue>(asynchronous ? await batch.ExecuteScalarAsync() : batch.ExecuteScalar());
                        CheckAnonymousBatchScalar(retained, scalar.Value);
                    }
                    else Assert.Equal(-1, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
                }
                connection.ClearComplexTypeCache();
                if (retained != null)
                {
                    CheckAnonymousBatchScalar(retained, scalar.Value);
                    Assert.Throws<NotSupportedException>(() => retained.Type.CreateValue());
                    Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { new OceanBaseParameter { Value = retained } }, OceanBaseCompatibilityMode.Oracle));
                }
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(7, await after.ExecuteNonQueryAsync());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private static void CheckAnonymousBatchScalar(OceanBaseComplexValue value, object expected)
        {
            var members = value.GetValues(); Assert.Equal(3, members.Length); Assert.Null(members[1]);
            for (var i = 0; i < 3; i += 2)
                if (expected is byte[] bytes) Assert.Equal(bytes, Assert.IsType<byte[]>(members[i])); else Assert.Equal(expected, members[i]);
        }

        [Theory]
        [InlineData((byte)4)] [InlineData((byte)160)] [InlineData((byte)163)] [InlineData((byte)255)]
        [InlineData((byte)210)] [InlineData((byte)211)]
        public void BatchMultiSendAnonymousResultKeepsUnsupportedAndResourceGuards(byte element)
        {
            var column = ResultColumn.Parse(ComplexColumn("", "", element: element), OceanBaseCompatibilityMode.Oracle);
            Assert.Throws<NotSupportedException>(() => column.RequireBatchMultiSendResult(true));
            Assert.Throws<NotSupportedException>(() => column.RequireBatchMultiSendResult(true, allowLobResults: true));
        }

        [Theory]
        [InlineData((byte)207)] [InlineData((byte)208)]
        public void BatchMultiSendAnonymousNationalResultStillRequiresTrustedEncoding(byte element)
        {
            var column = ResultColumn.Parse(ComplexColumn("", "", element: element), OceanBaseCompatibilityMode.Oracle);
            column.RequireBatchMultiSendResult(true);
            Assert.Empty(Assert.IsType<OceanBaseComplexValue>(column.DecodeBinary(new byte[] { 0, 0 })).GetValues());
            Assert.Throws<NotSupportedException>(() => column.DecodeBinary(new byte[] { 1, 0, 1, 65 }));
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public async Task BatchMultiSendAnonymousDeclaredOutputsRemainUnsupported(bool inputOutput)
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                using var command = new OceanBaseCommand { CommandText = "update t set id=1 returning value into :p" };
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "p", ComplexType = connection.CreateArrayType("NUMBER"),
                    Direction = inputOutput ? ParameterDirection.InputOutput : ParameterDirection.Output, Value = DBNull.Value });
                var plan = Internal.Sql.OracleCommandPlan.Create(command.CommandText, command.CommandType, (OceanBaseParameterCollection)command.Parameters);
                Assert.Throws<NotSupportedException>(() => plan.RequireBatchOutputs(allowLobOutputs: true));
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public async Task BatchMultiSendAnonymousInvalidLobReleasesEarlierMembers(bool asynchronous)
        {
            var row = new byte[] { 0, 0, 2, 0 }.Concat(WithLength(InlineLob(new byte[] { 42 }))).Concat(WithLength(new byte[] { 1, 2, 3 })).ToArray();
            var server = Server(async channel =>
            {
                await PrepareAnonymousBatchResult(channel, 90, 210); await StatementCommand(channel, 23);
                await ReplyAnonymousBatchResult(channel, 210, row);
                try { Assert.Equal(0, await channelStreamRead(channel)); } catch (IOException) { }
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port) + ";Use Batch Multi Send=true");
            try
            {
                await connection.OpenAsync(); var session = connection.GetOpenSession(); using var batch = connection.CreateBatch(); BatchItem(batch, "select value from t");
                using var reader = asynchronous ? await batch.ExecuteReaderAsync() : batch.ExecuteReader();
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                var error = Assert.Throws<OceanBaseException>(() => reader.GetFieldValue<OceanBaseComplexValue>(0));
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.Equal(ConnectionState.Broken, connection.State); Assert.False(session.RequiresDiscardOnReturn);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public async Task BatchMultiSendAnonymousMalformedCountDiscardsPipeline(bool asynchronous)
        {
            var server = Server(async channel =>
            {
                await PrepareAnonymousBatchResult(channel, 90, 203); await PrepareAnonymousBatchResult(channel, 91, 203);
                await StatementCommand(channel, 23); await StatementCommand(channel, 23);
                await ReplyAnonymousBatchResult(channel, 203, new byte[] { 0, 0, 253, 0, 0, 1 });
                try { Assert.Equal(0, await channelStreamRead(channel)); } catch (IOException) { }
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + ";Use Batch Multi Send=true;Batch Multi Send Number=2");
            try
            {
                await connection.OpenAsync(); using var batch = connection.CreateBatch();
                BatchItem(batch, "select first"); BatchItem(batch, "select second");
                var error = await Assert.ThrowsAsync<OceanBaseException>(async () =>
                {
                    using var reader = asynchronous ? await batch.ExecuteReaderAsync() : batch.ExecuteReader();
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                    _ = reader.GetFieldValue<OceanBaseComplexValue>(0);
                });
                Assert.True(error.IsExecutionOutcomeUnknown); Assert.Equal(ConnectionState.Broken, connection.State);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public static IEnumerable<object[]> BatchAnonymousLobCases()
        {
            foreach (var asynchronous in new[] { false, true })
                foreach (var character in new[] { false, true })
                    foreach (var api in new[] { "reader", "scalar", "nonquery" })
                        yield return new object[] { asynchronous, character, api };
        }

        [Theory]
        [MemberData(nameof(BatchAnonymousLobCases))]
        public async Task BatchMultiSendAnonymousLobsKeepOwnershipAndReleaseSkippedValues(bool asynchronous, bool character, string api)
        {
            var element = character ? (byte)211 : (byte)210; var data = Encoding.UTF8.GetBytes("患者🙂");
            var row = new byte[] { 0, 0, 3, 8 }.Concat(WithLength(InlineLob(data))).Concat(WithLength(InlineLob(Array.Empty<byte>()))).ToArray();
            var server = Server(async channel =>
            {
                for (byte id = 90; id < 93; id++) await PrepareAnonymousBatchResult(channel, id, element);
                await StatementCommand(channel, 23); await StatementCommand(channel, 23);
                await ReplyAnonymousBatchResult(channel, element, row); await ReplyAnonymousBatchResult(channel, element, new byte[] { 0, 4 });
                await StatementCommand(channel, 23); await ReplyAnonymousBatchResult(channel, element, new byte[] { 0, 0, 1, 4 });
                for (var i = 0; i < 3; i++) await StatementCommand(channel, 25);
                channel.BeginCommand(); await Query(channel, "after"); await channel.WritePacketAsync(Ok(7));
            }, lobLocators: true);
            using var connection = new OceanBaseConnection(LobConfiguration(server.Port) + ";Use Batch Multi Send=true;Batch Multi Send Number=2");
            var handles = new List<OceanBaseLob>();
            try
            {
                await connection.OpenAsync(); var session = connection.GetOpenSession();
                using (var batch = connection.CreateBatch())
                {
                    for (var i = 0; i < 3; i++) BatchItem(batch, "select value from t" + i);
                    if (api == "reader")
                    {
                        using var reader = asynchronous ? await batch.ExecuteReaderAsync() : batch.ExecuteReader();
                        for (var i = 0; i < 3; i++)
                        {
                            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                            if (i == 1) Assert.True(reader.IsDBNull(0));
                            else
                            {
                                var value = reader.GetFieldValue<OceanBaseComplexValue>(0); Assert.True(value.Type.IsAnonymous);
                                handles.AddRange(ComplexResultLobs(value));
                                if (i == 0) foreach (var lob in handles)
                                    Assert.Throws<InvalidOperationException>(() => { if (character) lob.OpenTextReader(); else lob.OpenRead(); });
                                else Assert.Null(Assert.Single(value.GetValues()));
                            }
                            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                            Assert.Equal(i < 2, asynchronous ? await reader.NextResultAsync() : reader.NextResult());
                        }
                    }
                    else if (api == "scalar")
                        handles.AddRange(ComplexResultLobs(Assert.IsType<OceanBaseComplexValue>(asynchronous ? await batch.ExecuteScalarAsync() : batch.ExecuteScalar())));
                    else Assert.Equal(-1, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
                }
                connection.ClearComplexTypeCache(); Assert.Equal(api != "nonquery", session.RequiresDiscardOnReturn);
                for (var i = 0; i < handles.Count; i++)
                {
                    using var lob = handles[i]; var expected = i == 0 ? data : Array.Empty<byte>();
                    if (character) { using var source = lob.OpenTextReader(); Assert.Equal(Encoding.UTF8.GetString(expected), await source.ReadToEndAsync()); }
                    else { using var source = lob.OpenRead(); using var target = new MemoryStream(); await source.CopyToAsync(target); Assert.Equal(expected, target.ToArray()); }
                }
                Assert.False(session.RequiresDiscardOnReturn);
                using var after = connection.CreateCommand(); after.CommandText = "after"; Assert.Equal(7, await after.ExecuteNonQueryAsync());
            }
            finally { foreach (var lob in handles) lob.Dispose(); connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
