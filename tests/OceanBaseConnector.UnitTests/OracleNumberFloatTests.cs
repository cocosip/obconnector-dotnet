using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Sql;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        [Theory]
        [InlineData(1, 171, "700", "700", 0)]
        [InlineData(24, 255, "-7.2500", "-72500", 4)]
        [InlineData(53, 171, "0.000000000000000000000000000001", "1", 30)]
        [InlineData(126, 171, "12345678901234567890123456789012345678", "12345678901234567890123456789012345678", 0)]
        [InlineData(126, 255, "1E125", "1", -125)]
        [InlineData(0, 171, "1E-130", "1", 130)]
        [InlineData(255, 255, "0", "0", 0)]
        public void OracleNumberFloatKeepsExactDecimalValuesAndBinaryPrecision(byte precision, byte scale, string text, string coefficient, int valueScale)
        {
            var column = ValueColumn(206, OceanBaseCompatibilityMode.Oracle, precision, scale);
            Assert.Equal("FLOAT", column.DataTypeName);
            Assert.Equal(typeof(OceanBaseDecimal), column.FieldType);
            Assert.Equal(typeof(OceanBaseDecimal), column.ProviderSpecificFieldType);
            Assert.Equal(OceanBaseDbType.Decimal, column.ProviderType);
            var schema = new ResultDbColumn(column, 0);
            Assert.Equal(precision == 0 || precision == 255 ? (int?)null : precision, schema.NumericPrecision);
            Assert.Null(schema.NumericScale);
            foreach (var binary in new[] { false, true })
            {
                var bytes = Encoding.ASCII.GetBytes(text);
                var value = Assert.IsType<OceanBaseDecimal>(binary ? column.DecodeBinary(bytes) : column.Decode(bytes));
                Assert.Equal(BigInteger.Parse(coefficient, CultureInfo.InvariantCulture), value.Coefficient);
                Assert.Equal(valueScale, value.Scale);
                Assert.Equal(value, column.DecodeDecimal(bytes, binary));
                Assert.Equal(DBNull.Value, binary ? column.DecodeBinary(null) : column.Decode(null));
                var wire = PreparedParameters.Encode(new[] { new OceanBaseParameter { Value = value } }, OceanBaseCompatibilityMode.Oracle);
                Assert.Equal(246, wire[2]);
                var expectedWire = text == "1E125" ? "1" + new string('0', 125)
                    : text == "1E-130" ? "0." + new string('0', 129) + "1" : text;
                Assert.Equal(expectedWire, Encoding.ASCII.GetString(new ByteCursor(wire.AsSpan(4).ToArray()).ReadLengthEncodedBytes()));
            }
        }

        [Theory]
        [InlineData(127, 171)]
        [InlineData(254, 255)]
        [InlineData(126, 0)]
        [InlineData(126, 2)]
        [InlineData(126, 172)]
        public void OracleNumberFloatRejectsUnsupportedMetadataIncludingNull(byte precision, byte scale)
        {
            var column = ValueColumn(206, OceanBaseCompatibilityMode.Oracle, precision, scale);
            Assert.Throws<NotSupportedException>(() => column.FieldType);
            Assert.Throws<NotSupportedException>(() => column.Decode(null));
            Assert.Throws<NotSupportedException>(() => column.DecodeBinary(null));
        }

        [Fact]
        public void NumberFloatDoesNotExpandMySql()
        {
            var column = ValueColumn(206, OceanBaseCompatibilityMode.MySql, 126, 171);
            Assert.Throws<NotSupportedException>(() => column.FieldType);
            Assert.Throws<NotSupportedException>(() => column.Decode(null));
        }

        [Theory]
        [InlineData("1E126")]
        [InlineData("1E-131")]
        [InlineData("123456789012345678901234567890123456789")]
        public void OracleNumberFloatStillEnforcesNumberRange(string text)
        {
            var column = ValueColumn(206, OceanBaseCompatibilityMode.Oracle, 126, 171);
            foreach (var binary in new[] { false, true })
                Assert.Throws<OverflowException>(() => column.DecodeDecimal(Encoding.ASCII.GetBytes(text), binary));
        }

        [Theory]
        [InlineData("NaN")]
        [InlineData("Inf")]
        [InlineData("1.2junk")]
        [InlineData("")]
        public void OracleNumberFloatRejectsNonDecimalTokens(string text)
        {
            var column = ValueColumn(206, OceanBaseCompatibilityMode.Oracle, 126, 171);
            foreach (var binary in new[] { false, true })
                Assert.Throws<FormatException>(() => column.DecodeDecimal(Encoding.ASCII.GetBytes(text), binary));
        }

        [Theory]
        [InlineData(126, 171)]
        [InlineData(0, 255)]
        public void OracleNumberFloatOutputsStayExactAndPublishAtomically(byte precision, byte scale)
        {
            var parameters = new OceanBaseParameterCollection();
            var first = new OceanBaseParameter { ParameterName = "a", OceanBaseDbType = OceanBaseDbType.Decimal, Direction = ParameterDirection.Output, Value = "unchanged" };
            var second = new OceanBaseParameter { ParameterName = "b", OceanBaseDbType = OceanBaseDbType.Decimal, Direction = ParameterDirection.Output, Value = "unchanged" };
            parameters.Add(first); parameters.Add(second);
            var plan = OracleCommandPlan.Create("pkg.p", CommandType.StoredProcedure, parameters);
            var column = ValueColumn(206, OceanBaseCompatibilityMode.Oracle, precision, scale);
            Assert.Throws<OverflowException>(() => plan.CaptureOutput(new[] { column, column }, new[] { Encoding.ASCII.GetBytes("7.25"), Encoding.ASCII.GetBytes("1E126") }));
            Assert.Equal("unchanged", first.Value); Assert.Equal("unchanged", second.Value);
            plan.CaptureOutput(new[] { column, column }, new byte[]?[] { Encoding.ASCII.GetBytes("7.2500"), null });
            Assert.Equal("unchanged", first.Value);
            plan.PublishOutputs();
            Assert.Equal("7.2500", Assert.IsType<OceanBaseDecimal>(first.Value).ToString());
            Assert.Equal(DBNull.Value, second.Value);
        }

        [Fact]
        public async Task OracleNumberFloatNumberArrayOutputsKeepValuesAndNulls()
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync();
                var plan = new OracleArrayBindingPlan(1, true, new[] { connection.CreateArrayType("NUMBER") });
                var header = ArrayResultColumns();
                var columns = new[] { header[0], header[1], ValueColumn(206, OceanBaseCompatibilityMode.Oracle, 126, 171), header[2] };
                foreach (var bad in new[] { ValueColumn(206, OceanBaseCompatibilityMode.Oracle, 127, 171), ValueColumn(206, OceanBaseCompatibilityMode.Oracle, 126, 0) })
                {
                    var malformed = new[] { header[0], header[1], bad, header[2] };
                    Assert.Throws<NotSupportedException>(() => plan.Capture(malformed, new byte[]?[] { BitConverter.GetBytes(0L), BitConverter.GetBytes(0L), null, null }));
                    Assert.Empty(plan.Snapshot().Items[0].OutputRows);
                    Assert.Equal(OceanBaseArrayBindingStatus.Unknown, plan.Snapshot().Items[0].Status);
                }
                foreach (var text in new[] { "7.2500", null })
                    plan.Capture(columns, new byte[]?[] { BitConverter.GetBytes(0L), BitConverter.GetBytes(0L), text == null ? null : Encoding.ASCII.GetBytes(text), null });
                var rows = plan.Finish(0).Items[0].OutputRows;
                Assert.Equal(2, rows.Count);
                Assert.Equal("7.2500", Assert.IsType<OceanBaseDecimal>(rows[0][0]).ToString());
                Assert.Equal(DBNull.Value, rows[1][0]);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(126, "7.25")]
        [InlineData(53, "7.25")]
        [InlineData(24, "7.25")]
        [InlineData(1, "700")]
        public async Task OracleNumberFloatBuilderBindsNumberWithoutCopyingBinaryPrecision(byte precision, string text)
        {
            var columns = new[] { SchemaColumn("id", 8, 20, flags: 1, originalName: "id"),
                SchemaColumn("amount", 206, 126, precision, 171, originalName: "amount") };
            await WithBuilderSchema(false, columns, new[] { BuilderKey("id") }, (_, builder) =>
            {
                var value = new OceanBaseDecimal(decimal.Parse(text, CultureInfo.InvariantCulture));
                foreach (var command in new[] { builder.GetInsertCommand(), builder.GetUpdateCommand(), builder.GetDeleteCommand() })
                    foreach (OceanBaseParameter parameter in command.Parameters)
                        if (parameter.SourceColumn == "amount" && !parameter.SourceColumnNullMapping)
                        {
                            Assert.Equal(OceanBaseDbType.Decimal, parameter.OceanBaseDbType);
                            Assert.Equal(0, parameter.Precision); Assert.Null(parameter.DeclaredDecimalScale);
                            parameter.Value = value;
                            Assert.Equal(PreparedParameters.Encode(new[] { new OceanBaseParameter { Value = value } }, OceanBaseCompatibilityMode.Oracle),
                                PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
                        }
            });
        }

        public static IEnumerable<object[]> OracleNumberFloatReaderCases()
        {
            foreach (var asynchronous in new[] { false, true })
                foreach (var binary in new[] { false, true })
                    foreach (var sequential in new[] { false, true })
                        yield return new object[] { asynchronous, binary, sequential };
        }

        [Theory]
        [MemberData(nameof(OracleNumberFloatReaderCases))]
        public Task OracleNumberFloatReadersKeepExactValuesNullOverflowAndReuse(bool asynchronous, bool binary, bool sequential)
            => NumberFloatFixture.Verify(typeof(OceanBaseConnection), ProtocolFixture.Load("initial-handshake-oceanbase").Payload,
                (channel, _) => ReadClientCommand(channel), asynchronous, binary, sequential);
    }
}
