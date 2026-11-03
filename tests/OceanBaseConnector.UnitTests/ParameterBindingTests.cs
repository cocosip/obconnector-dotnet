using System;
using System.Data;
using OceanBaseConnector.Internal.Sql;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class ParameterBindingTests
    {
        private static OceanBaseParameterCollection Parameters(params string[] names)
        {
            var parameters = new OceanBaseParameterCollection();
            foreach (var name in names) parameters.Add(new OceanBaseParameter { ParameterName = name, Value = "value-secret" });
            return parameters;
        }

        [Theory]
        [InlineData("select :id", "select ?")]
        [InlineData("select ':hidden', :id", "select ':hidden', ?")]
        [InlineData("select 'it''s :hidden', :id", "select 'it''s :hidden', ?")]
        [InlineData("select 'backslash\\', :id", "select 'backslash\\', ?")]
        [InlineData("select \":hidden\", :id", "select \":hidden\", ?")]
        [InlineData("select \"a\"\":hidden\", :id", "select \"a\"\":hidden\", ?")]
        [InlineData("select q'[it's :hidden ?]', :id", "select q'[it's :hidden ?]', ?")]
        [InlineData("select q'{it's :hidden}', :id", "select q'{it's :hidden}', ?")]
        [InlineData("select q'(it's :hidden)', :id", "select q'(it's :hidden)', ?")]
        [InlineData("select q'<it's :hidden>', :id", "select q'<it's :hidden>', ?")]
        [InlineData("select q'!it's :hidden!', :id", "select q'!it's :hidden!', ?")]
        [InlineData("select Q'~it's :hidden~', :id", "select Q'~it's :hidden~', ?")]
        [InlineData("select nq'[it's :hidden]', :id", "select nq'[it's :hidden]', ?")]
        [InlineData("select NQ'!it's :hidden!', :id", "select NQ'!it's :hidden!', ?")]
        [InlineData("select N'it''s :hidden', :id", "select N'it''s :hidden', ?")]
        [InlineData("select q'[a ] :hidden ]', :id", "select q'[a ] :hidden ]', ?")]
        [InlineData("select /* :hidden ? ' */ :id", "select /* :hidden ? ' */ ?")]
        [InlineData("select /*+ :hidden */ :id", "select /*+ :hidden */ ?")]
        [InlineData("select /* :hidden /* text */ :id", "select /* :hidden /* text */ ?")]
        [InlineData("-- :hidden ? '\r\nselect :id", "-- :hidden ? '\r\nselect ?")]
        [InlineData("-- :hidden\rselect :id", "-- :hidden\rselect ?")]
        [InlineData("begin x := :id; end;", "begin x := ?; end;")]
        public void OracleRewritesOnlyBindings(string sql, string expected)
        {
            var parameters = Parameters(":ID");
            var plan = ParameterBindingPlan.Create(sql, OceanBaseCompatibilityMode.Oracle, parameters);
            Assert.Equal(expected, plan.CommandText);
            Assert.Same(parameters[0], Assert.Single(plan.Parameters));
            Assert.DoesNotContain("value-secret", plan.CommandText);
        }

        [Fact]
        public void RepeatedNamesFollowSqlOrderAndRetainParameterIdentity()
        {
            var parameters = Parameters("second", ":Id", "_third$#1");
            var plan = ParameterBindingPlan.Create("select :id, :SECOND, :Id, :_third$#1", OceanBaseCompatibilityMode.Oracle, parameters);
            Assert.Equal("select ?, ?, ?, ?", plan.CommandText);
            Assert.Equal(new[] { parameters[1], parameters[0], parameters[1], parameters[2] }, plan.Parameters);
            Assert.Equal(":Id", parameters[1].ParameterName);
        }

        [Fact]
        public void UnicodeBindingsKeepSqlOrderRepeatedIdentityAndLiteralPositions()
        {
            var parameters = Parameters("编号_2", ":输入");
            var plan = ParameterBindingPlan.Create("select :输入, :编号_2, :输入", OceanBaseCompatibilityMode.Oracle, parameters);
            Assert.Equal("select ?, ?, ?", plan.CommandText);
            Assert.Equal(new[] { 7, 10, 13 }, plan.BindingPositions);
            Assert.Equal(new[] { parameters[1], parameters[0], parameters[1] }, plan.Parameters);
            Assert.Equal(":输入", parameters[1].ParameterName);
        }

        [Theory]
        [InlineData("select :输入", "select ?")]
        [InlineData("select ':输入', :输入", "select ':输入', ?")]
        [InlineData("select \"列:输入\", :输入", "select \"列:输入\", ?")]
        [InlineData("select q'[:输入 ?]', :输入", "select q'[:输入 ?]', ?")]
        [InlineData("select nq'!:输入 ?!', :输入", "select nq'!:输入 ?!', ?")]
        [InlineData("select /* :输入 ? */ :输入 -- :输入", "select /* :输入 ? */ ? -- :输入")]
        public void OracleUnicodeBindingsSkipQuotedAndCommentedNames(string sql, string expected)
        {
            var parameters = Parameters("输入");
            var plan = ParameterBindingPlan.Create(sql, OceanBaseCompatibilityMode.Oracle, parameters);
            Assert.Equal(expected, plan.CommandText); Assert.Same(parameters[0], Assert.Single(plan.Parameters));
        }

        [Theory]
        [InlineData(ParameterDirection.Output)] [InlineData(ParameterDirection.InputOutput)]
        public void UnicodeOutputBindingsRemainUnique(ParameterDirection direction)
        {
            var parameters = Parameters("输出"); parameters[0].Direction = direction;
            var plan = ParameterBindingPlan.Create("begin :输出 := 1; end;", OceanBaseCompatibilityMode.Oracle, parameters, allowOutputs: true);
            Assert.Equal("begin ? := 1; end;", plan.CommandText); Assert.Same(parameters[0], Assert.Single(plan.Parameters));
            Assert.Throws<ArgumentException>(() => ParameterBindingPlan.Create("begin :输出 := :输出; end;", OceanBaseCompatibilityMode.Oracle, parameters, allowOutputs: true));
        }

        [Theory]
        [InlineData("\u0301")]
        [InlineData("🙂")]
        [InlineData("\u200B")]
        [InlineData("。")]
        [InlineData("２")]
        public void UnicodeBindingCannotConsumeOnlyAValidPrefix(string suffix)
            => Assert.Throws<NotSupportedException>(() => ParameterBindingPlan.Create("select :输入" + suffix,
                OceanBaseCompatibilityMode.Oracle, Parameters("输入")));

        [Fact]
        public void UnicodeBindingsMatchOrdinalCaseWithoutNormalizingNames()
        {
            var parameters = Parameters(":état");
            Assert.Same(parameters[0], Assert.Single(ParameterBindingPlan.Create("select :ÉTAT", OceanBaseCompatibilityMode.Oracle, parameters).Parameters));
            Assert.Throws<NotSupportedException>(() => ParameterBindingPlan.Create("select :e\u0301tat", OceanBaseCompatibilityMode.Oracle, parameters));
        }

        [Theory]
        [InlineData("select 1 -- :hidden")]
        [InlineData("select '?:hidden', q'[:hidden]' /* :hidden */")]
        [InlineData("begin x := 1; end;")]
        public void ParameterlessSqlIsUnchanged(string sql)
        {
            var plan = ParameterBindingPlan.Create(sql, OceanBaseCompatibilityMode.Oracle, Parameters());
            Assert.Equal(sql, plan.CommandText);
            Assert.Empty(plan.Parameters);
        }

        [Theory]
        [InlineData("select 'unfinished :secret")]
        [InlineData("select \"unfinished :secret")]
        [InlineData("select q'[unfinished :secret'")]
        [InlineData("select nq'!unfinished :secret'")]
        [InlineData("select /* unfinished :secret")]
        public void UnterminatedConstructsFailWithoutExposingSql(string sql)
        {
            var error = Assert.Throws<ArgumentException>(() => ParameterBindingPlan.Create(sql, OceanBaseCompatibilityMode.Oracle, Parameters()));
            Assert.DoesNotContain("secret", error.Message);
        }

        [Theory]
        [InlineData("select :1")]
        [InlineData("select :\"quoted\"")]
        [InlineData("select :中文\u0301")]
        [InlineData("select :id中文🙂")]
        [InlineData("select ::id")]
        [InlineData("select :")]
        [InlineData("select ?")]
        [InlineData("select q' invalid '")]
        public void UncertainSyntaxIsExplicitlyUnsupported(string sql)
        {
            Assert.Throws<NotSupportedException>(() => ParameterBindingPlan.Create(sql, OceanBaseCompatibilityMode.Oracle, Parameters()));
        }

        [Theory]
        [InlineData("id", ":ID")]
        [InlineData(":id", "ID")]
        [InlineData("输入", ":输入")]
        [InlineData("état", ":ÉTAT")]
        public void DuplicateNormalizedNamesAreRejectedEvenIfUnused(string first, string second)
        {
            Assert.Throws<ArgumentException>(() => ParameterBindingPlan.Create("select 1", OceanBaseCompatibilityMode.Oracle, Parameters(first, second)));
        }

        [Theory]
        [InlineData("")]
        [InlineData(":")]
        [InlineData("::id")]
        [InlineData("1")]
        [InlineData("a b")]
        [InlineData("中文。")]
        public void InvalidParameterNamesAreRejected(string name)
        {
            Assert.Throws<ArgumentException>(() => ParameterBindingPlan.Create("select 1", OceanBaseCompatibilityMode.Oracle, Parameters(name)));
        }

        [Fact]
        public void MissingExcessAndMutatedDuplicateParametersAreRejected()
        {
            var missing = Assert.Throws<ArgumentException>(() => ParameterBindingPlan.Create("select :sqlsecret", OceanBaseCompatibilityMode.Oracle, Parameters()));
            var excess = Assert.Throws<ArgumentException>(() => ParameterBindingPlan.Create("select 1", OceanBaseCompatibilityMode.Oracle, Parameters("namesecret")));
            Assert.DoesNotContain("sqlsecret", missing.Message);
            Assert.DoesNotContain("namesecret", excess.Message);
            var parameters = Parameters("a", "b");
            parameters[1].ParameterName = ":A";
            Assert.Throws<ArgumentException>(() => ParameterBindingPlan.Create("select :a", OceanBaseCompatibilityMode.Oracle, parameters));
        }

        [Fact]
        public void MysqlSqlAndPositionalParametersAreNeverRewrittenOrCountedLocally()
        {
            var sql = "select ?, ':name', `?`, 'backslash\\'?, :name /* unmatched";
            var parameters = Parameters("same", "same", "");
            var plan = ParameterBindingPlan.Create(sql, OceanBaseCompatibilityMode.MySql, parameters);
            Assert.Equal(sql, plan.CommandText);
            Assert.Equal(new[] { parameters[0], parameters[1], parameters[2] }, plan.Parameters);
        }

        [Theory]
        [InlineData(ParameterDirection.Output)]
        [InlineData(ParameterDirection.InputOutput)]
        [InlineData(ParameterDirection.ReturnValue)]
        public void NonInputDirectionsAreUnsupportedInBothModes(ParameterDirection direction)
        {
            foreach (var mode in new[] { OceanBaseCompatibilityMode.MySql, OceanBaseCompatibilityMode.Oracle })
            {
                var parameters = Parameters("id");
                parameters[0].Direction = direction;
                Assert.Throws<NotSupportedException>(() => ParameterBindingPlan.Create("select :id", mode, parameters));
            }
        }
    }
}
