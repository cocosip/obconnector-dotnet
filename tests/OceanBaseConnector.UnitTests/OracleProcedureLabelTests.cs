using System;
using System.Data;
using OceanBaseConnector.Internal.Sql;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class OracleProcedureLabelTests
    {
        [Theory]
        [InlineData(false)] [InlineData(true)]
        public void ProcedureLabelsNeverBecomeSqlAndPreserveOutputIdentity(bool function)
        {
            var parameters = new OceanBaseParameterCollection();
            var input = new OceanBaseParameter { ParameterName = "in put); DELETE FROM t; --", Value = new byte[] { 42 } };
            var output = new OceanBaseParameter { ParameterName = "Result", Direction = function ? ParameterDirection.ReturnValue : ParameterDirection.Output, Value = "old" };
            var inout = new OceanBaseParameter { ParameterName = "result", Direction = ParameterDirection.InputOutput, Value = 7 };
            parameters.Add(input); parameters.Add(inout); parameters.Add(output);
            var plan = OracleCommandPlan.Create("pkg.proc", CommandType.StoredProcedure, parameters);
            Assert.Equal(function ? "BEGIN ? := pkg.proc(?, ?); END;" : "BEGIN pkg.proc(?, ?, ?); END;", plan.Bindings.CommandText);
            var offset = function ? 1 : 0;
            Assert.Equal(input.ParameterName, plan.Bindings.Parameters[offset].ParameterName);
            Assert.Equal(inout.ParameterName, plan.Bindings.Parameters[offset + 1].ParameterName);
            Assert.Equal(function ? new[] { output, inout } : new[] { inout, output }, plan.Outputs);
            Assert.Equal("old", output.Value); Assert.Equal(7, inout.Value);
            ((byte[])input.Value!)[0] = 1;
            Assert.Equal(new byte[] { 42 }, plan.Bindings.Parameters[offset].Value);
        }

        [Theory]
        [InlineData("")] [InlineData("a\0b")] [InlineData("a\nb")] [InlineData("a\u007fb")]
        public void InvalidProcedureLabelsAreRejectedBeforeBuildingSql(string name)
        {
            var parameters = new OceanBaseParameterCollection(); parameters.Add(new OceanBaseParameter { ParameterName = name, Value = 1 });
            Assert.Throws<ArgumentException>(() => OracleCommandPlan.Create("p", CommandType.StoredProcedure, parameters));
        }

        [Fact]
        public void ProcedureLabelsRetainUtf8BudgetAndExactDuplicateGuard()
        {
            var parameters = new OceanBaseParameterCollection(); var parameter = new OceanBaseParameter { ParameterName = new string('中', 170) + "ab", Value = 1 }; parameters.Add(parameter);
            Assert.Single(OracleCommandPlan.Create("p", CommandType.StoredProcedure, parameters).Bindings.Parameters);
            parameter.ParameterName = new string('中', 171);
            Assert.Throws<ArgumentException>(() => OracleCommandPlan.Create("p", CommandType.StoredProcedure, parameters));
            parameter.ParameterName = "x y"; parameters.Add(new OceanBaseParameter { ParameterName = "x y", Value = 2 });
            Assert.Throws<ArgumentException>(() => OracleCommandPlan.Create("p", CommandType.StoredProcedure, parameters));
        }

        [Fact]
        public void TextCommandsStillRejectQuotedLabelsAndCaseCollisions()
        {
            var parameters = new OceanBaseParameterCollection(); parameters.Add(new OceanBaseParameter { ParameterName = "x y", Value = 1 });
            Assert.Throws<ArgumentException>(() => OracleCommandPlan.Create("BEGIN p(:x); END;", CommandType.Text, parameters));
            parameters[0].ParameterName = "x"; parameters.Add(new OceanBaseParameter { ParameterName = "X", Value = 2 });
            Assert.Throws<ArgumentException>(() => OracleCommandPlan.Create("BEGIN p(:x); END;", CommandType.Text, parameters));
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public void InvalidUtf16LabelsCannotReachTheWireOrDirectoryPublication(bool highSurrogate)
        {
            var name = "x" + (highSurrogate ? '\ud800' : '\udfff');
            var parameters = new OceanBaseParameterCollection(); parameters.Add(new OceanBaseParameter { ParameterName = name, Value = 1 });
            Assert.Throws<System.Text.EncoderFallbackException>(() => OracleCommandPlan.Create("p", CommandType.StoredProcedure, parameters));
            using var table = DeriveParametersMetadataTests.Arguments(); var row = DeriveParametersMetadataTests.Argument(table, "NUMBER"); row["PARAMETER_NAME"] = name;
            Assert.Throws<System.Text.EncoderFallbackException>(() => RoutineParameterDerivation.OrderParameters(table, row, false));
        }

        [Fact]
        public void MySqlDirectoryLabelsStillRejectCaseInsensitiveDuplicates()
        {
            using var table = DeriveParametersMetadataTests.Arguments(); var first = DeriveParametersMetadataTests.Argument(table, "INT"); first["PARAMETER_NAME"] = "value";
            var second = table.NewRow(); second.ItemArray = first.ItemArray; second["PARAMETER_NAME"] = "VALUE"; second["ORDINAL_POSITION"] = 2; table.Rows.Add(second);
            Assert.Throws<NotSupportedException>(() => RoutineParameterDerivation.OrderParameters(table, first, true));
        }
    }
}
