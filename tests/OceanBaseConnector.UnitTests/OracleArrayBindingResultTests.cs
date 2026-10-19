using System;
using System.Linq;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Sql;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed partial class TextCommandTests
    {
        private static ResultColumn[] ArrayResultColumns() => new[]
        {
            ResultColumn.Parse(Column("sql_no", 8), OceanBaseCompatibilityMode.Oracle),
            ResultColumn.Parse(Column("error_code", 8), OceanBaseCompatibilityMode.Oracle),
            ResultColumn.Parse(Column("error_message", 253), OceanBaseCompatibilityMode.Oracle),
        };

        private static byte[]?[] ArrayResultRow(long positionOrCount, long code = 0, string? message = null)
            => new byte[]?[] { BitConverter.GetBytes(positionOrCount), BitConverter.GetBytes(code), message == null ? null : ResultColumn.Utf8.GetBytes(message) };

        [Fact]
        public void ArrayBindingResultPreservesDmlCountsAndFailuresWithoutExposingServerMessages()
        {
            var plan = new OracleArrayBindingPlan(3, false, Array.Empty<OceanBaseComplexType>());
            var columns = ArrayResultColumns();
            plan.Capture(columns, ArrayResultRow(2)); plan.Capture(columns, ArrayResultRow(1, 1, "private bind secret"));
            plan.Capture(columns, ArrayResultRow(0));
            var result = plan.Finish(2);
            Assert.True(result.IsComplete); Assert.Equal(2, result.RecordsAffected);
            Assert.Equal(new long?[] { 2, null, 0 }, result.Items.Select(i => i.RecordsAffected));
            Assert.Equal(new[] { OceanBaseArrayBindingStatus.Succeeded, OceanBaseArrayBindingStatus.Failed, OceanBaseArrayBindingStatus.Succeeded }, result.Items.Select(i => i.Status));
            Assert.Equal(1, result.Items[1].ServerErrorCode);
        }

        [Fact]
        public void ArrayBindingPlPositionsNeverBecomeAffectedCountsAndPartialSuffixRemainsUnknown()
        {
            var plan = new OracleArrayBindingPlan(3, true, Array.Empty<OceanBaseComplexType>());
            plan.Capture(ArrayResultColumns(), ArrayResultRow(0));
            plan.Capture(ArrayResultColumns(), ArrayResultRow(1, 1403, "secret"));
            var partial = plan.Snapshot();
            Assert.False(partial.IsComplete); Assert.Null(partial.RecordsAffected);
            Assert.Equal(OceanBaseArrayBindingStatus.Succeeded, partial.Items[0].Status);
            Assert.Null(partial.Items[0].RecordsAffected); Assert.Equal(1403, partial.Items[1].ServerErrorCode);
            Assert.Equal(OceanBaseArrayBindingStatus.Unknown, partial.Items[2].Status);
            Assert.Throws<ProtocolException>(() => plan.Finish(0));
        }

        [Theory]
        [InlineData(-1, 0)] [InlineData(1, 1)] [InlineData(0, -2147483649)] [InlineData(0, 65536)]
        public void ArrayBindingRejectsInvalidCountOrFailurePosition(long value, long error)
        {
            var plan = new OracleArrayBindingPlan(1, false, Array.Empty<OceanBaseComplexType>());
            Assert.Throws<ProtocolException>(() => plan.Capture(ArrayResultColumns(), ArrayResultRow(value, error, error == 0 ? null : "error")));
            Assert.Equal(OceanBaseArrayBindingStatus.Unknown, plan.Snapshot().Items[0].Status);
        }

        [Theory]
        [InlineData("short")] [InlineData("overflow")] [InlineData("tail")]
        [InlineData("success-message")] [InlineData("null-count")] [InlineData("null-code")]
        [InlineData("wrong-column")] [InlineData("extra-row")] [InlineData("pl-position")]
        public void ArrayBindingRejectsMalformedOrInconsistentResults(string fault)
        {
            var plan = new OracleArrayBindingPlan(1, fault == "pl-position", Array.Empty<OceanBaseComplexType>());
            var columns = ArrayResultColumns(); var row = ArrayResultRow(fault == "overflow" ? long.MaxValue : fault == "pl-position" ? 1 : 2);
            if (fault == "success-message") row[2] = new byte[] { 120 };
            if (fault == "null-count") row[0] = null;
            if (fault == "null-code") row[1] = null;
            if (fault == "wrong-column") columns[0] = ResultColumn.Parse(Column("sql_no", 253), OceanBaseCompatibilityMode.Oracle);
            if (fault == "short") { Assert.Throws<ProtocolException>(() => plan.Finish(0)); return; }
            if (fault == "tail") { plan.Capture(columns, row); Assert.Throws<ProtocolException>(() => plan.Finish(1)); return; }
            if (fault == "extra-row") plan.Capture(columns, row);
            if (fault == "overflow")
            {
                plan.Capture(columns, row); Assert.Throws<ProtocolException>(() => plan.Finish(0)); return;
            }
            Assert.Throws<ProtocolException>(() => plan.Capture(columns, row));
        }

        [Fact]
        public async System.Threading.Tasks.Task ArrayBindingOutputSnapshotsWaitForBoundaryAndRejectPlGapsOrInvalidRows()
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var type = connection.CreateArrayType("RAW");
                var columns = new[] { ArrayResultColumns()[0], ArrayResultColumns()[1], ResultColumn.Parse(Column("out", 203), OceanBaseCompatibilityMode.Oracle), ArrayResultColumns()[2] };
                byte[]?[] Row(long index, long error = 0) => new byte[]?[] { BitConverter.GetBytes(index), BitConverter.GetBytes(error), error == 0 ? new byte[] { 42 } : null, error == 0 ? null : new byte[] { 120 } };
                var plan = new OracleArrayBindingPlan(3, false, new[] { type });
                plan.Capture(columns, Row(0)); var early = plan.Snapshot();
                Assert.Equal(OceanBaseArrayBindingStatus.Unknown, early.Items[0].Status);
                plan.Finish(1); var interrupted = plan.Snapshot();
                Assert.False(interrupted.IsComplete); Assert.Equal(OceanBaseArrayBindingStatus.Unknown, interrupted.Items[2].Status);
                var bytes = Assert.IsType<byte[]>(early.Items[0].OutputRows[0][0]); bytes[0] = 9;
                Assert.Equal(new byte[] { 42 }, early.Items[0].OutputRows[0][0]);
                var pl = new OracleArrayBindingPlan(3, true, new[] { type });
                Assert.Throws<ProtocolException>(() => pl.Capture(columns, Row(1)));
                Assert.All(pl.Snapshot().Items, i => Assert.Equal(OceanBaseArrayBindingStatus.Unknown, i.Status));
                pl.Capture(columns, Row(0)); Assert.Throws<ProtocolException>(() => pl.Finish(0));
                var invalid = new OracleArrayBindingPlan(3, false, new[] { type });
                var wrong = Row(2, 1); wrong[2] = new byte[] { 9 };
                Assert.Throws<ProtocolException>(() => invalid.Capture(columns, wrong));
                Assert.All(invalid.Snapshot().Items, i => Assert.Equal(OceanBaseArrayBindingStatus.Unknown, i.Status));
                var negativeError = new OracleArrayBindingPlan(1, false, new[] { type });
                negativeError.Capture(columns, Row(0, -4016));
                Assert.Equal(-4016, negativeError.Finish(0).Items[0].ServerErrorCode);
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async System.Threading.Tasks.Task ArrayBindingOutputPublicationCopiesEachItemOnceAndMetadataIsValidatedWithoutRows()
        {
            var server = Server(async channel => Assert.Equal(0, await channelStreamRead(channel)));
            using var connection = new OceanBaseConnection(Configuration(server.Port));
            try
            {
                await connection.OpenAsync(); var raw = connection.CreateArrayType("RAW");
                var parameters = new OceanBaseParameterCollection();
                for (var i = 0; i < 200; i++) parameters.Add(new OceanBaseParameter { ParameterName = "p" + i, ComplexType = raw, Direction = System.Data.ParameterDirection.Output });
                var plan = OracleCommandPlan.Create("pkg.p", System.Data.CommandType.StoredProcedure, parameters, 1);
                var columns = new[] { ArrayResultColumns()[0], ArrayResultColumns()[1] }.Concat(Enumerable.Range(0, 200)
                    .Select(_ => ResultColumn.Parse(Column("out", 203), OceanBaseCompatibilityMode.Oracle))).Concat(new[] { ArrayResultColumns()[2] }).ToArray();
                var row = new byte[]?[203]; row[0] = BitConverter.GetBytes(0L); row[1] = BitConverter.GetBytes(0L); row[2] = new byte[256 * 1024];
                plan.CaptureOutput(columns, row); plan.ArrayBinding!.Finish(0);
                var before = GC.GetAllocatedBytesForCurrentThread(); plan.PublishOutputs();
                Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 8 * 1024 * 1024);
                var first = Assert.IsType<object[][]>(((OceanBaseParameter)parameters[0]).Value);
                Assert.Equal(256 * 1024, Assert.IsType<byte[]>(Assert.Single(Assert.Single(first))).Length);

                var strings = new OracleArrayBindingPlan(1, false, new[] { connection.CreateArrayType("VARCHAR2") });
                Assert.Throws<ProtocolException>(() => strings.ValidateColumn(2, ResultColumn.Parse(Column("out", 253, charset: 63), OceanBaseCompatibilityMode.Oracle)));
                var badNumber = Column("out", 0); badNumber[badNumber.Length - 2] = 39;
                var numbers = new OracleArrayBindingPlan(1, false, new[] { connection.CreateArrayType("NUMBER") });
                Assert.Throws<NotSupportedException>(() => numbers.ValidateColumn(2, ResultColumn.Parse(badNumber, OceanBaseCompatibilityMode.Oracle)));
                var bounded = new OracleArrayBindingPlan(1, false, new[] { raw });
                var rawColumns = new[] { columns[0], columns[1], columns[2], columns[columns.Length - 1] };
                var nullRow = new byte[]?[] { BitConverter.GetBytes(0L), BitConverter.GetBytes(0L), null, null };
                for (var i = 0; i < ushort.MaxValue; i++) bounded.Capture(rawColumns, nullRow);
                Assert.Throws<ProtocolException>(() => bounded.Capture(rawColumns, nullRow));
                Assert.Throws<ArgumentOutOfRangeException>(() => new OracleArrayBindingPlan(1, false, new OceanBaseComplexType[4094]));
            }
            finally { connection.Close(); server.Stop(); }
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
