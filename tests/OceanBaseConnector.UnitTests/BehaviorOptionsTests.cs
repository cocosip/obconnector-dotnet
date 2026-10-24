using System;
using System.Data;
using System.Data.Common;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Fact]
        public void BehaviorOptionsRoundTripAndFreezeForEveryEndpoint()
        {
            var defaults = new OceanBaseConnectionStringBuilder("Server=one;User ID=u");
            Assert.True(defaults.TreatTinyAsBoolean);
            Assert.False(defaults.UseAffectedRows);
            Assert.True(defaults.Build().TreatTinyAsBoolean);
            Assert.False(defaults.Build().UseAffectedRows);
            var builder = new OceanBaseConnectionStringBuilder("Server=one,two;User ID=u;Password=secret;Treat Tiny As Boolean=false;Use Affected Rows=true");
            var frozen = builder.Build();
            Assert.Contains("Treat Tiny As Boolean=false", frozen.ToString());
            Assert.Contains("Use Affected Rows=true", frozen.ToString());
            Assert.DoesNotContain("secret", frozen.ToString());
            builder.TreatTinyAsBoolean = true;
            builder.UseAffectedRows = false;
            foreach (var endpoint in frozen.Endpoints)
            {
                var selected = frozen.ForEndpoint(endpoint);
                Assert.False(selected.TreatTinyAsBoolean);
                Assert.True(selected.UseAffectedRows);
                var serialized = new OceanBaseConnectionStringBuilder(selected.ToString());
                Assert.Equal("false", serialized["Treat Tiny As Boolean"]);
                Assert.Equal("true", serialized["Use Affected Rows"]);
                Assert.False(ClientCapabilities.Build(0, selected).HasFlag(CapabilityFlags.ClientFoundRows));
            }
            Assert.True(ClientCapabilities.Build(0, builder.Build()).HasFlag(CapabilityFlags.ClientFoundRows));
        }

        [Fact]
        public void MultiStatementsAreOptInAndNegotiatedOnlyWhenEnabled()
        {
            var defaults = new OceanBaseConnectionStringBuilder("Server=one;User ID=u").Build();
            Assert.False(defaults.AllowMultiStatements);
            Assert.False(ClientCapabilities.Build(CapabilityFlags.ClientMultiStatements, defaults)
                .HasFlag(CapabilityFlags.ClientMultiStatements));

            var enabled = new OceanBaseConnectionStringBuilder(
                "Server=one;User ID=u;Allow Multi Statements=true").Build();
            Assert.True(enabled.AllowMultiStatements);
            Assert.True(ClientCapabilities.Build(CapabilityFlags.ClientMultiStatements, enabled)
                .HasFlag(CapabilityFlags.ClientMultiStatements));
        }

        public static IEnumerable<object[]> TinyBooleanReaderCases()
        {
            foreach (var binary in new[] { false, true })
            foreach (var asynchronous in new[] { false, true })
            foreach (var sequential in new[] { false, true })
            foreach (var boolean in new[] { false, true })
                yield return new object[] { binary, asynchronous, sequential, boolean };
        }

        private static LoopbackServer MySqlBehaviorServer(Func<PacketChannel, Task> exchange, bool foundRows = true) => LoopbackServer.Start(async client =>
        {
            var channel = new PacketChannel(client.GetStream());
            await channel.WritePacketAsync(ProtocolFixture.Load("initial-handshake-oceanbase").Payload);
            var authentication = await channel.ReadPacketAsync();
            Assert.Equal(foundRows, (authentication[0] & 2) != 0);
            await channel.WritePacketAsync(Ok(status: 2));
            await ServerIdentity.ReplyAsync(channel, oracle: false);
            channel.BeginCommand();
            await exchange(channel);
        });

        [Theory]
        [MemberData(nameof(TinyBooleanReaderCases))]
        public async Task TinyBooleanReadersRespectOptionAndPreserveNullSchema(bool binary, bool asynchronous, bool sequential, bool boolean)
        {
            var server = MySqlBehaviorServer(async channel =>
            {
                if (binary)
                {
                    await StatementCommand(channel, 22);
                    await channel.WritePacketAsync(new byte[] { 0, 91, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
                    await channel.WritePacketAsync(SchemaColumn("tiny", 1, 1));
                    await channel.WritePacketAsync(Eof(status: 2));
                    await StatementCommand(channel, 23);
                }
                else await Query(channel, "select tiny");
                await channel.WritePacketAsync(new byte[] { 1 });
                await channel.WritePacketAsync(SchemaColumn("tiny", 1, 1));
                await channel.WritePacketAsync(Eof(status: 2));
                foreach (var value in new sbyte[] { 0, 1, -1, 2 })
                    await channel.WritePacketAsync(binary ? new byte[] { 0, 0, unchecked((byte)value) }
                        : WithLength(Encoding.ASCII.GetBytes(value.ToString(System.Globalization.CultureInfo.InvariantCulture))));
                await channel.WritePacketAsync(binary ? new byte[] { 0, 4 } : new byte[] { 251 });
                await channel.WritePacketAsync(Eof(status: 2));
                if (binary) await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port) + (boolean ? "" : ";Treat Tiny As Boolean=false"));
            try
            {
                if (asynchronous) await connection.OpenAsync(); else connection.Open();
                Assert.Equal(OceanBaseCompatibilityMode.MySql, connection.CompatibilityMode);
                using var command = connection.CreateCommand(); command.CommandText = "select tiny";
                var type = boolean ? typeof(bool) : typeof(sbyte);
                if (binary) { if (asynchronous) await command.PrepareAsync(); else command.Prepare(); }
                if (binary)
                {
                    var prepared = (PreparedStatement)typeof(OceanBaseCommand).GetField("_prepared", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(command)!;
                    Assert.Equal(type, Assert.Single(prepared.Columns).FieldType);
                }
                using var reader = asynchronous ? await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default)
                    : command.ExecuteReader(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                Assert.Equal(type, reader.GetFieldType(0));
                Assert.Equal(type, reader.GetProviderSpecificFieldType(0));
                Assert.Equal("TINYINT", reader.GetDataTypeName(0));
                var column = Assert.Single(reader.GetColumnSchema());
                Assert.Equal(type, column.DataType);
                Assert.Equal(type, column["ProviderSpecificDataType"]);
                Assert.Equal((int)(boolean ? OceanBaseDbType.Boolean : OceanBaseDbType.SByte), column["ProviderType"]);
                foreach (var value in new sbyte[] { 0, 1, -1, 2 })
                {
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                    Assert.Equal(boolean ? (object)(value != 0) : value, reader.GetValue(0));
                    Assert.Equal(boolean ? (object)(value != 0) : value, reader.GetProviderSpecificValue(0));
                    if (boolean) Assert.Equal(value != 0, asynchronous ? await reader.GetFieldValueAsync<bool>(0) : reader.GetBoolean(0));
                }
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                Assert.True(reader.IsDBNull(0));
                Assert.Equal(DBNull.Value, reader.GetValue(0));
                Assert.Equal(type, reader.GetSchemaTable()!.Rows[0][System.Data.Common.SchemaTableColumn.DataType]);
                Assert.Equal(type, reader.GetColumnSchema()[0].DataType);
                Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task AffectedRowOptionNegotiatesFoundRowsAndPreservesServerCounts(bool affected, bool asynchronous)
        {
            var count = affected ? (byte)0 : (byte)3;
            var server = MySqlBehaviorServer(async channel =>
            {
                foreach (var sql in new[] { "unchanged", "one", "two" })
                {
                    await Query(channel, sql); await channel.WritePacketAsync(Ok(count, 2)); channel.BeginCommand();
                }
            }, foundRows: !affected);
            using var connection = new OceanBaseConnection(Configuration(server.Port) + (affected ? ";Use Affected Rows=true" : ""));
            try
            {
                if (asynchronous) await connection.OpenAsync(); else connection.Open();
                using var command = connection.CreateCommand(); command.CommandText = "unchanged";
                Assert.Equal(count, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
                using var batch = connection.CreateBatch();
                var one = BatchItem(batch, "one"); var two = BatchItem(batch, "two");
                Assert.Equal(count * 2, asynchronous ? await batch.ExecuteNonQueryAsync() : batch.ExecuteNonQuery());
                Assert.Equal(count, one.RecordsAffected); Assert.Equal(count, two.RecordsAffected);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData("Treat Tiny As Boolean")]
        [InlineData("Use Affected Rows")]
        [InlineData("Allow Multi Statements")]
        public void BehaviorOptionsRejectInvalidBooleans(string keyword)
        {
            var error = Assert.Throws<ArgumentException>(() => new OceanBaseConnectionStringBuilder($"{keyword}=private-value"));
            Assert.Contains("'true' or 'false'", error.Message);
        }

        [Theory]
        [InlineData(OceanBaseCompatibilityMode.MySql, 1U, 0, 0, true)]
        [InlineData(OceanBaseCompatibilityMode.MySql, 1U, 32, 0, false)]
        [InlineData(OceanBaseCompatibilityMode.MySql, 4U, 0, 0, false)]
        [InlineData(OceanBaseCompatibilityMode.MySql, 1U, 0, 1, false)]
        [InlineData(OceanBaseCompatibilityMode.Oracle, 1U, 0, 0, false)]
        public void TinyBooleanMappingRequiresSignedMySqlWidthOneZeroScale(OceanBaseCompatibilityMode mode, uint length, ushort flags, byte scale, bool boolean)
        {
            var column = ResultColumn.Parse(SchemaColumn("tiny", 1, length, scale: scale, flags: flags), mode);
            Assert.Equal(boolean ? typeof(bool) : flags == 32 ? typeof(byte) : typeof(sbyte), column.FieldType);
            Assert.Equal(boolean ? OceanBaseDbType.Boolean : flags == 32 ? OceanBaseDbType.Byte : OceanBaseDbType.SByte, column.ProviderType);
            Assert.Equal("TINYINT", column.DataTypeName);
            if (boolean)
            {
                foreach (var value in new sbyte[] { 0, 1, -1, 2, sbyte.MinValue, sbyte.MaxValue })
                {
                    Assert.Equal(value != 0, column.Decode(Encoding.ASCII.GetBytes(value.ToString(System.Globalization.CultureInfo.InvariantCulture))));
                    Assert.Equal(value != 0, column.DecodeBinary(new[] { unchecked((byte)value) }));
                }
                Assert.Equal(DBNull.Value, column.Decode(null));
                Assert.Equal(DBNull.Value, column.DecodeBinary(null));
            }
        }
    }
}
