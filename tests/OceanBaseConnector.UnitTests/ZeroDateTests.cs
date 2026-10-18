using System;
using System.Data;
using System.Data.Common;
using System.Reflection;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Session;
using System.Text;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(10, "OceanBaseDate")]
        [InlineData(12, "OceanBaseDateTime")]
        [InlineData(7, "OceanBaseDateTime")]
        public void ZeroDateProviderMetadataIsStableForEveryRowAndNull(byte code, string name)
        {
            var column = ValueColumn(code, OceanBaseCompatibilityMode.MySql);
            Assert.Equal(typeof(DateTime), column.FieldType);
            Assert.Equal("OceanBaseConnector." + name, column.ProviderSpecificFieldType.FullName);
            Assert.Equal(column.ProviderSpecificFieldType, new ResultDbColumn(column, 0)["ProviderSpecificDataType"]);
            Assert.Equal(DBNull.Value, column.Decode(null));
            Assert.Equal(DBNull.Value, column.DecodeBinary(null));
        }

        [Theory]
        [InlineData("OceanBaseDate")]
        [InlineData("OceanBaseDateTime")]
        public void ZeroDatePublicValuesHaveSharedTargetIdentity(string name)
            => Assert.NotNull(typeof(OceanBaseConnection).Assembly.GetType("OceanBaseConnector." + name));

        [Theory]
        [InlineData(10, "0000-00-00")]
        [InlineData(10, "2024-00-12")]
        [InlineData(12, "2024-02-00 01:02:03")]
        [InlineData(7, "0000-00-00 00:00:00")]
        public void ZeroDateStandardAccessThrowsConversionException(byte code, string text)
            => Assert.Throws<InvalidCastException>(() => ValueColumn(code, OceanBaseCompatibilityMode.MySql).Decode(Encoding.UTF8.GetBytes(text)));

        [Theory]
        [InlineData(0, 0, 0)]
        [InlineData(2024, 0, 12)]
        [InlineData(2024, 2, 0)]
        [InlineData(0, 2, 29)]
        [InlineData(2024, 2, 29)]
        public void ZeroDateValuesRetainAllComponentsWithoutNormalization(int year, int month, int day)
        {
            var date = new OceanBaseDate(year, month, day);
            Assert.Equal(year, date.Year); Assert.Equal(month, date.Month); Assert.Equal(day, date.Day);
            Assert.Equal($"{year:D4}-{month:D2}-{day:D2}", date.ToString());
            var valid = year != 0 && month != 0 && day != 0;
            Assert.Equal(valid, date.IsValidDate);
            var value = new OceanBaseDateTime(date, 23, 59, 58, 123456);
            Assert.Equal(valid, value.IsValidDateTime);
            Assert.Equal(date + " 23:59:58.123456", value.ToString());
            if (valid)
            {
                Assert.Equal(DateTimeKind.Unspecified, value.ToDateTime().Kind);
                Assert.Equal(new DateTime(year, month, day, 23, 59, 58).AddTicks(1234560), value.ToDateTime());
                Assert.Equal(date, new OceanBaseDate(date.ToDateTime()));
                Assert.Equal(value, new OceanBaseDateTime(value.ToDateTime()));
            }
            else { Assert.Throws<InvalidCastException>(() => date.ToDateTime()); Assert.Throws<InvalidCastException>(() => value.ToDateTime()); }
            Assert.False(default(OceanBaseDate).IsValidDate); Assert.False(default(OceanBaseDateTime).IsValidDateTime);
        }

        [Fact]
        public void ZeroDateValuesRejectInvalidComponentsZonesAndPrecisionLoss()
        {
            foreach (var tuple in new[] { (-1, 1, 1), (10000, 1, 1), (2024, -1, 1), (2024, 13, 1), (2024, 1, -1), (2024, 0, 32), (2023, 2, 29), (2024, 2, 30), (0, 2, 30) })
                Assert.ThrowsAny<ArgumentException>(() => new OceanBaseDate(tuple.Item1, tuple.Item2, tuple.Item3));
            Assert.Throws<ArgumentOutOfRangeException>(() => new OceanBaseDateTime(default, hour: 24));
            Assert.Throws<ArgumentOutOfRangeException>(() => new OceanBaseDateTime(default, minute: 60));
            Assert.Throws<ArgumentOutOfRangeException>(() => new OceanBaseDateTime(default, second: 60));
            Assert.Throws<ArgumentOutOfRangeException>(() => new OceanBaseDateTime(default, microsecond: 1000000));
            Assert.Throws<ArgumentOutOfRangeException>(() => new OceanBaseDateTime(default, scale: 7));
            Assert.Throws<ArgumentException>(() => new OceanBaseDateTime(default, microsecond: 1, scale: 5));
            Assert.Throws<ArgumentException>(() => new OceanBaseDate(new DateTime(2024, 1, 1, 1, 0, 0)));
            foreach (var kind in new[] { DateTimeKind.Local, DateTimeKind.Utc })
            {
                var zoned = DateTime.SpecifyKind(new DateTime(2024, 1, 1), kind);
                Assert.Throws<ArgumentException>(() => new OceanBaseDate(zoned));
                Assert.Throws<ArgumentException>(() => new OceanBaseDateTime(zoned));
            }
            Assert.Throws<ArgumentException>(() => new OceanBaseDateTime(new DateTime(2024, 1, 1).AddTicks(1)));
        }

        [Theory]
        [InlineData(10, "0000-00-00", new byte[] { 0 }, 0)]
        [InlineData(10, "2024-00-12", new byte[] { 4, 232, 7, 0, 12 }, 0)]
        [InlineData(10, "0000-02-29", new byte[] { 4, 0, 0, 2, 29 }, 0)]
        [InlineData(10, "2024-02-29", new byte[] { 4, 232, 7, 2, 29 }, 0)]
        [InlineData(12, "0000-00-00 00:00:00", new byte[] { 0 }, 6)]
        [InlineData(12, "0000-00-00 01:02:03.123456", new byte[] { 11, 0, 0, 0, 0, 1, 2, 3, 64, 226, 1, 0 }, 6)]
        [InlineData(12, "2024-02-00 01:02:03", new byte[] { 7, 232, 7, 2, 0, 1, 2, 3 }, 3)]
        [InlineData(12, "2024-02-29 01:02:03.123", new byte[] { 11, 232, 7, 2, 29, 1, 2, 3, 120, 224, 1, 0 }, 3)]
        [InlineData(12, "2024-02-29 01:02:03.123", new byte[] { 11, 232, 7, 2, 29, 1, 2, 3, 120, 224, 1, 0 }, 6)]
        [InlineData(7, "0000-00-00 00:00:00", new byte[] { 0 }, 0)]
        public void ZeroDateCodecAndParameterWriteBackKeepExactFields(byte code, string text, byte[] wire, byte scale)
        {
            var column = ValueColumn(code, OceanBaseCompatibilityMode.MySql, scale: scale);
            var a = column.DecodeProviderDate(Encoding.ASCII.GetBytes(text), false);
            var b = column.DecodeProviderDate(column.ReadBinaryField(new ByteCursor(wire)), true);
            Assert.Equal(a, b);
            if (code != 10) Assert.Equal(scale, ((OceanBaseDateTime)a).Scale);
            var parameter = new OceanBaseParameter { Value = a };
            Assert.Equal(code == 10 ? DbType.Date : DbType.DateTime, parameter.DbType);
            var encoded = PreparedParameters.Encode(new[] { parameter });
            Assert.Equal(new byte[] { 0, 1, code == 10 ? (byte)10 : (byte)12, 0 }, encoded.AsSpan(0, 4).ToArray());
            Assert.Equal(wire, encoded.AsSpan(4).ToArray());
            Assert.Throws<ArgumentException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
            parameter.DbType = code == 10 ? DbType.DateTime : DbType.Date;
            Assert.Throws<ArgumentException>(() => PreparedParameters.Encode(new[] { parameter }));
        }

        [Theory]
        [InlineData(10, "2023-02-29")]
        [InlineData(10, "2024-02-29 ")]
        [InlineData(10, "2024-02-29T00:00:00Z")]
        [InlineData(10, "0000-13-00")]
        [InlineData(12, "0000-00-00 24:00:00")]
        [InlineData(12, "0000-00-00 00:00:00.")]
        [InlineData(12, "0000-00-00 00:00:00.1234567")]
        [InlineData(12, "0000-00-00 00:00:00.123456 ")]
        [InlineData(12, "0000-00-00T00:00:00")]
        [InlineData(12, "0000-00-00 00:00:00Z")]
        public void ZeroDateMalformedTextIsNeverTreatedAsNull(byte code, string text)
        {
            var column = ValueColumn(code, OceanBaseCompatibilityMode.MySql, scale: code == 10 ? (byte)0 : (byte)6);
            Assert.ThrowsAny<FormatException>(() => column.DecodeProviderDate(Encoding.UTF8.GetBytes(text), false));
        }

        [Fact]
        public void ZeroDateMetadataAndFractionsCannotBeTruncated()
        {
            Assert.Throws<NotSupportedException>(() => ValueColumn(10, OceanBaseCompatibilityMode.MySql, scale: 1).Decode(null));
            var column = ValueColumn(12, OceanBaseCompatibilityMode.MySql, scale: 3);
            Assert.Throws<FormatException>(() => column.DecodeProviderDate(Encoding.ASCII.GetBytes("2024-01-01 00:00:00.1234"), false));
            Assert.Throws<FormatException>(() => column.DecodeProviderDate(new byte[] { 232, 7, 1, 1, 0, 0, 0, 1, 0, 0, 0 }, true));
            Assert.Throws<ProtocolException>(() => column.DecodeProviderDate(new byte[] { 232, 7, 1 }, true));
            Assert.Throws<ProtocolException>(() => ValueColumn(10, OceanBaseCompatibilityMode.MySql).DecodeProviderDate(new byte[7], true));
            var parameter = new OceanBaseParameter { Value = new OceanBaseDateTime(default, microsecond: 1), Scale = 3 };
            Assert.Throws<ArgumentException>(() => PreparedParameters.Encode(new[] { parameter }));
            parameter.Value = default(OceanBaseDateTime); parameter.Scale = 7;
            Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { parameter }));
        }

        private static void UseInternalMySqlFixture(OceanBaseConnection connection)
        {
            // A05 still rejects untrusted MySQL authentication; this isolates post-auth mode behavior.
            var session = connection.GetOpenSession(); var info = session.Info!;
            typeof(PhysicalSession).GetField("_info", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session,
                new ConnectResult(info.ServerVersion, info.ConnectionId, OceanBaseCompatibilityMode.MySql,
                    info.Capabilities, info.ServerStatus, info.ObCapabilityFlag, info.SessionVariables));
        }

        [Theory]
        [InlineData(false, false, 10)]
        [InlineData(true, false, 10)]
        [InlineData(false, true, 10)]
        [InlineData(true, true, 10)]
        [InlineData(false, false, 12)]
        [InlineData(true, false, 12)]
        [InlineData(false, true, 12)]
        [InlineData(true, true, 12)]
        [InlineData(false, false, 7)]
        [InlineData(true, false, 7)]
        [InlineData(false, true, 7)]
        [InlineData(true, true, 7)]
        public async Task ZeroDateInternalMySqlReaderKeepsMixedRowsAndWriteBack(bool asynchronous, bool binary, byte code)
            => await VerifyZeroDateReader(asynchronous, binary, code, false);

        [Theory]
        [InlineData(false, false, 10)]
        [InlineData(true, false, 10)]
        [InlineData(false, true, 10)]
        [InlineData(true, true, 10)]
        [InlineData(false, false, 12)]
        [InlineData(true, false, 12)]
        [InlineData(false, true, 12)]
        [InlineData(true, true, 12)]
        [InlineData(false, false, 7)]
        [InlineData(true, false, 7)]
        [InlineData(false, true, 7)]
        [InlineData(true, true, 7)]
        public async Task SequentialZeroDateReaderKeepsProviderValuesAndExactWriteBack(bool asynchronous, bool binary, byte code)
            => await VerifyZeroDateReader(asynchronous, binary, code, true);

        private static async Task VerifyZeroDateReader(bool asynchronous, bool binary, byte code, bool sequential)
        {
            var dateOnly = code == 10; var normal = dateOnly ? "2024-02-29" : "2024-02-29 01:02:03.123456";
            var partial = dateOnly ? "2024-00-12" : "2024-00-12 01:02:03.123456";
            var zero = dateOnly ? "0000-00-00" : "0000-00-00 00:00:00.000000";
            var normalWire = dateOnly ? new byte[] { 4, 232, 7, 2, 29 } : new byte[] { 11, 232, 7, 2, 29, 1, 2, 3, 64, 226, 1, 0 };
            var partialWire = (byte[])normalWire.Clone(); partialWire[3] = 0; partialWire[4] = 12;
            var server = Server(async channel =>
            {
                if (binary) { await StatementCommand(channel, 22); await Prepared(channel, 109, 0); await StatementCommand(channel, 23); }
                else await Query(channel, "mixed");
                await channel.WritePacketAsync(new byte[] { 1 });
                await channel.WritePacketAsync(ValueColumnPacket(code, scale: dateOnly ? (byte)0 : (byte)6)); await channel.WritePacketAsync(Eof());
                var texts = new[] { normal, partial, zero };
                var binaries = new[] { normalWire, partialWire, new byte[] { 0 } };
                for (var i = 0; i < texts.Length; i++)
                {
                    var field = binary ? binaries[i] : WithLength(Encoding.UTF8.GetBytes(texts[i]));
                    if (binary) { var row = new byte[field.Length + 2]; field.CopyTo(row, 2); field = row; }
                    await channel.WritePacketAsync(field);
                }
                await channel.WritePacketAsync(binary ? new byte[] { 0, 4 } : new byte[] { 251 }); await channel.WritePacketAsync(Eof());
                if (binary) await StatementCommand(channel, 25);
                await StatementCommand(channel, 22); await Prepared(channel, 110, 1);
                var execute = await StatementCommand(channel, 23);
                Assert.Equal(new byte[] { 0, 1, dateOnly ? (byte)10 : (byte)12, 0, 0 }, execute.AsSpan(10).ToArray());
                await channel.WritePacketAsync(Ok(1)); await StatementCommand(channel, 25);
            });
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); UseInternalMySqlFixture(connection); object zeroValue;
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "mixed";
                    if (binary) { if (asynchronous) await command.PrepareAsync(); else command.Prepare(); }
                    using var reader = asynchronous ? await command.ExecuteReaderAsync(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default) : command.ExecuteReader(sequential ? CommandBehavior.SequentialAccess : CommandBehavior.Default);
                    var providerType = dateOnly ? typeof(OceanBaseDate) : typeof(OceanBaseDateTime);
                    Assert.Equal(typeof(DateTime), reader.GetFieldType(0)); Assert.Equal(providerType, reader.GetProviderSpecificFieldType(0));
                    Assert.Equal(providerType, Assert.Single(reader.GetColumnSchema())["ProviderSpecificDataType"]);
                    Assert.Equal(providerType, reader.GetSchemaTable()!.Rows[0]["ProviderSpecificDataType"]);
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.IsType<DateTime>(reader.GetValue(0));
                    Assert.IsType(providerType, reader.GetProviderSpecificValue(0));
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.False(reader.IsDBNull(0));
                    Assert.Throws<InvalidCastException>(() => reader.GetValue(0)); Assert.Throws<InvalidCastException>(() => reader.GetDateTime(0));
                    Assert.IsType(providerType, reader.GetProviderSpecificValue(0));
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); zeroValue = reader.GetProviderSpecificValue(0);
                    if (dateOnly) Assert.Equal(default(OceanBaseDate), reader.GetFieldValue<OceanBaseDate>(0));
                    else Assert.Equal(new OceanBaseDateTime(default(OceanBaseDate)), await reader.GetFieldValueAsync<OceanBaseDateTime>(0));
                    var values = new object[1]; Assert.Equal(1, reader.GetProviderSpecificValues(values)); Assert.Equal(zeroValue, values[0]);
                    Assert.Throws<InvalidCastException>(() => reader.GetFieldValue<DateTime>(0)); Assert.Throws<InvalidCastException>(() => reader.GetValues(values));
                    Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read()); Assert.True(reader.IsDBNull(0));
                    Assert.Equal(DBNull.Value, reader.GetProviderSpecificValue(0)); Assert.Equal(DBNull.Value, reader.GetValue(0));
                    Assert.Equal(providerType, reader.GetProviderSpecificFieldType(0)); Assert.Equal(typeof(DateTime), reader.GetFieldType(0));
                    Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
                }
                using var insert = connection.CreateCommand(); insert.CommandText = "select :t";
                insert.Parameters.Add(new OceanBaseParameter { ParameterName = "t", Value = zeroValue });
                Assert.Equal(1, asynchronous ? await insert.ExecuteNonQueryAsync() : insert.ExecuteNonQuery());
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public void ZeroDateBinaryRangesAndBoundaryDatesStayExact()
        {
            var column = ValueColumn(12, OceanBaseCompatibilityMode.MySql, scale: 6);
            foreach (var wire in new[] { new byte[] { 255, 255, 1, 1 }, new byte[] { 232, 7, 2, 30 }, new byte[] { 232, 7, 1, 1, 24, 0, 0 } })
                Assert.Throws<FormatException>(() => column.DecodeProviderDate(wire, true));
            Assert.Throws<ProtocolException>(() => column.DecodeProviderDate(new byte[] { 232, 7, 1, 1, 0, 0, 0, 255, 255, 255, 255 }, true));
            Assert.Throws<FormatException>(() => column.DecodeProviderDate(new byte[] { 255 }, false));
            var minimum = new OceanBaseDateTime(DateTime.MinValue);
            Assert.True(minimum.IsValidDateTime); Assert.Equal(DateTime.MinValue, minimum.ToDateTime());
            var maximum = new OceanBaseDateTime(DateTime.MaxValue.AddTicks(-9));
            Assert.Equal(DateTime.MaxValue.Ticks - 9, maximum.ToDateTime().Ticks);
            Assert.Equal("9999-12-31 23:59:59.999999", maximum.ToString());
            Assert.Equal("0000-00-00 00:00:00", default(OceanBaseDateTime).ToString());
        }
    }
}
