using System;
using System.Data;
using OceanBaseConnector.Internal.Sql;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class OracleCommandPlanTests
    {
        [Theory]
        [InlineData("pkg.proc")] [InlineData("\"My Schema\".\"p\"\"q\"")]
        public void OracleProcedurePlanWrapsIdentifiersAndSnapshotsDirections(string name)
        {
            var parameters = new OceanBaseParameterCollection();
            var output = new OceanBaseParameter { ParameterName = "out", Direction = ParameterDirection.Output, Value = new object() };
            var input = new OceanBaseParameter { ParameterName = "in", Value = new byte[] { 42 } };
            parameters.Add(input); parameters.Add(output);
            var plan = OracleCommandPlan.Create(name, CommandType.StoredProcedure, parameters);
            Assert.Equal("BEGIN " + name + "(?, ?); END;", plan.Bindings.CommandText);
            Assert.NotSame(input, plan.Bindings.Parameters[0]);
            Assert.Equal(ParameterDirection.Input, plan.Bindings.Parameters[1].Direction);
            Assert.Null(plan.Bindings.Parameters[1].Value);
            ((byte[])input.Value!)[0] = 1;
            Assert.Equal(new byte[] { 42 }, plan.Bindings.Parameters[0].Value);
            Assert.Same(output, Assert.Single(plan.Outputs));
        }

        [Fact]
        public void OracleFunctionReturnIsFirstBindRegardlessOfCollectionOrder()
        {
            var parameters = new OceanBaseParameterCollection();
            parameters.Add(new OceanBaseParameter { ParameterName = "arg", Value = 42 });
            var returned = new OceanBaseParameter { ParameterName = "ret", Direction = ParameterDirection.ReturnValue };
            parameters.Add(returned);
            var plan = OracleCommandPlan.Create("pkg.func", CommandType.StoredProcedure, parameters);
            Assert.Equal("BEGIN ? := pkg.func(?); END;", plan.Bindings.CommandText);
            Assert.Equal("ret", plan.Bindings.Parameters[0].ParameterName);
            Assert.Same(returned, Assert.Single(plan.Outputs));
        }

        [Theory]
        [InlineData("p; delete from t")] [InlineData("p(--")] [InlineData("p..q")]
        [InlineData("p@remote")] [InlineData(" p ")] [InlineData("\"unclosed")]
        [InlineData("p/*comment*/")] [InlineData("p.1q")]
        public void OracleProcedureNamesCannotContainSql(string name)
            => Assert.Throws<ArgumentException>(() => OracleCommandPlan.Create(name, CommandType.StoredProcedure, new OceanBaseParameterCollection()));

        [Theory]
        [InlineData(ParameterDirection.Output)] [InlineData(ParameterDirection.InputOutput)]
        public void OracleRepeatedOutputPositionsAreRejectedBeforeSend(ParameterDirection direction)
        {
            var parameters = new OceanBaseParameterCollection();
            parameters.Add(new OceanBaseParameter { ParameterName = "p", Direction = direction, Value = 7 });
            Assert.Throws<ArgumentException>(() => OracleCommandPlan.Create("begin p(:p, :p); end;", CommandType.Text, parameters));
        }

        [Fact]
        public void OracleReturningPlanUsesSqlOrderAndDoesNotMutateInputOutputValues()
        {
            var parameters = new OceanBaseParameterCollection();
            var output = new OceanBaseParameter { ParameterName = "b", Direction = ParameterDirection.Output, Value = 5 };
            var inout = new OceanBaseParameter { ParameterName = "a", Direction = ParameterDirection.InputOutput, Value = 6 };
            parameters.Add(output); parameters.Add(inout);
            var plan = OracleCommandPlan.Create("begin p(:a, :b); end;", CommandType.Text, parameters);
            Assert.Equal(new[] { inout, output }, plan.Outputs);
            Assert.Equal(6, plan.Bindings.Parameters[0].Value); Assert.Null(plan.Bindings.Parameters[1].Value);
            Assert.Equal(5, output.Value); Assert.Equal(6, inout.Value);
        }

        [Fact]
        public void OraclePrepareExecuteOptionDefaultsOffAndFreezesAcrossEndpoints()
        {
            var builder = new OceanBaseConnectionStringBuilder("Server=one,two;User ID=u");
            Assert.False(builder.UseOraclePrepareExecute); Assert.False(builder.Build().UseOraclePrepareExecute);
            builder.UseOraclePrepareExecute = true;
            var frozen = builder.Build(); builder.UseOraclePrepareExecute = false;
            Assert.True(frozen.UseOraclePrepareExecute); Assert.True(frozen.ForEndpoint(frozen.Endpoints[1]).UseOraclePrepareExecute);
            Assert.True(new OceanBaseConnectionStringBuilder(frozen.SanitizedConnectionString).UseOraclePrepareExecute);
            Assert.Throws<ArgumentException>(() => new OceanBaseConnectionStringBuilder("Use Oracle Prepare Execute=maybe"));
        }

        [Fact]
        public void OracleReturnValueRequiresOneFunctionBinding()
        {
            var parameters = new OceanBaseParameterCollection();
            parameters.Add(new OceanBaseParameter { ParameterName = "a", Direction = ParameterDirection.ReturnValue });
            Assert.Throws<ArgumentException>(() => OracleCommandPlan.Create("begin :a := 1; end;", CommandType.Text, parameters));
            parameters.Add(new OceanBaseParameter { ParameterName = "b", Direction = ParameterDirection.ReturnValue });
            Assert.Throws<ArgumentException>(() => OracleCommandPlan.Create("p", CommandType.StoredProcedure, parameters));
        }
    }
}
