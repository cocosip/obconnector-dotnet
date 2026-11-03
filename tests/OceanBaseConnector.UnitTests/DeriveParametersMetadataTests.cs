using System;
using System.Data;
using System.Reflection;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Sql;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class DeriveParametersMetadataTests
    {
        internal static DataTable Arguments() => (DataTable)typeof(OceanBaseConnection)
            .GetMethod("CreateRoutineDirectoryTable", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { true, true })!;

        internal static DataRow Argument(DataTable table, string type, string? declaration = null)
        {
            var row = table.NewRow(); row["ROUTINE_SCHEMA"] = "OWNER"; row["ROUTINE_NAME"] = "P"; row["SUBPROGRAM_ID"] = 1;
            row["PARAMETER_NAME"] = "value"; row["ORDINAL_POSITION"] = 1; row["PARAMETER_MODE"] = "IN"; row["DATA_TYPE"] = type;
            if (declaration != null) row["TYPE_DECLARATION"] = declaration;
            table.Rows.Add(row); return row;
        }

        [Theory]
        [InlineData("BIT", "bit(1)", 1)]
        [InlineData("BIT", "bit(64)", 64)]
        public void DerivedBitPreservesItsActualWidth(string type, string declaration, int width)
        {
            using var table = Arguments(); var row = Argument(table, type, declaration);
            var parameter = RoutineParameterDerivation.Map(row, true, false);
            Assert.Equal(width, parameter.Size);
            if (width == 1)
            {
                parameter.Value = 2UL;
                Assert.Throws<ArgumentException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.MySql));
            }
        }

        [Theory]
        [InlineData("DECIMAL", "decimal(10,2) unsigned")]
        [InlineData("FLOAT", "float zerofill")]
        [InlineData("DOUBLE", "double unsigned")]
        public void DerivationCannotSilentlyDropUnrepresentableUnsignedDeclarations(string type, string declaration)
        {
            using var table = Arguments(); var row = Argument(table, type, declaration);
            row["NUMERIC_PRECISION"] = 10; row["NUMERIC_SCALE"] = 2;
            Assert.Throws<NotSupportedException>(() => RoutineParameterDerivation.Map(row, true, false));
        }

        [Theory]
        [InlineData("bigint(20) unsigned", OceanBaseDbType.UInt64)]
        [InlineData("bigint(20) zerofill", OceanBaseDbType.UInt64)]
        [InlineData("bigint", OceanBaseDbType.Int64)]
        public void IntegerDerivationUsesSignednessTokens(string declaration, OceanBaseDbType expected)
        {
            using var table = Arguments(); var row = Argument(table, "BIGINT", declaration);
            Assert.Equal(expected, RoutineParameterDerivation.Map(row, true, false).OceanBaseDbType);
        }

        [Theory]
        [InlineData(3, 0)]
        [InlineData(10, 0)]
        [InlineData(9, 2)]
        public void ConflictingDecimalDeclarationsAreNotGuessed(int precision, int scale)
        {
            using var table = Arguments(); var row = Argument(table, "DECIMAL", "decimal(10,2)");
            row["NUMERIC_PRECISION"] = precision; row["NUMERIC_SCALE"] = scale;
            Assert.Throws<NotSupportedException>(() => RoutineParameterDerivation.Map(row, true, false));
        }

        [Theory]
        [InlineData(true, OceanBaseDbType.Boolean)]
        [InlineData(false, OceanBaseDbType.SByte)]
        public void TinyIntFollowsTheSessionBooleanPolicy(bool tinyBoolean, OceanBaseDbType expected)
        {
            using var table = Arguments(); var row = Argument(table, "TINYINT", "tinyint(1)");
            Assert.Equal(expected, RoutineParameterDerivation.Map(row, true, tinyBoolean).OceanBaseDbType);
        }

        [Fact]
        public void NumberDerivationPreservesSignedScaleAndUnconstrainedNullMetadata()
        {
            using var table = Arguments(); var row = Argument(table, "NUMBER");
            var unconstrained = RoutineParameterDerivation.Map(row, false, false);
            Assert.Equal(0, unconstrained.Precision); Assert.Null(unconstrained.DecimalScale);
            row["NUMERIC_PRECISION"] = 3; row["NUMERIC_SCALE"] = -2;
            var parameter = RoutineParameterDerivation.Map(row, false, false);
            Assert.Equal(-2, parameter.DecimalScale); Assert.Equal(3, parameter.Precision);
            parameter.Value = 12345m;
            Assert.Throws<OverflowException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
        }

        [Fact]
        public void StringLengthUsesCharactersRatherThanOctets()
        {
            using var table = Arguments(); var row = Argument(table, "VARCHAR2"); row["DATA_LENGTH"] = 12L; row["CHARACTER_LENGTH"] = 4L;
            Assert.Equal(4, RoutineParameterDerivation.Map(row, false, false).Size);
            row["DATA_TYPE"] = "RAW";
            Assert.Equal(12, RoutineParameterDerivation.Map(row, false, false).Size);
        }

        [Fact]
        public void MySqlDirectoryNamesDoNotInheritOracleBindRestrictions()
        {
            using var table = Arguments(); var row = Argument(table, "INT", "int");
            row["PARAMETER_NAME"] = "目标"; row["ROUTINE_TYPE"] = "PROCEDURE";
            Assert.Single(RoutineParameterDerivation.OrderParameters(table, row, true));
            Assert.Equal("目标", RoutineParameterDerivation.Map(row, true, false).ParameterName);
        }

        [Fact]
        public void IntegerDeclarationsCannotContainFractionalDimensions()
        {
            using var table = Arguments(); var row = Argument(table, "INT", "int(10,2)");
            Assert.Throws<NotSupportedException>(() => RoutineParameterDerivation.Map(row, true, false));
        }

        [Theory]
        [InlineData("YEAR", "year", OceanBaseDbType.Year)]
        [InlineData("YEAR", "year(4)", OceanBaseDbType.Year)]
        [InlineData("FLOAT", "float", OceanBaseDbType.Single)]
        [InlineData("DOUBLE", "double", OceanBaseDbType.Double)]
        public void SupportedYearAndFloatingDeclarationsRetainTheirTypes(string type, string declaration, OceanBaseDbType expected)
        {
            using var table = Arguments(); var row = Argument(table, type, declaration);
            Assert.Equal(expected, RoutineParameterDerivation.Map(row, true, false).OceanBaseDbType);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void OracleRealArgumentNamesUseExactIdentityWithoutReservingTheSyntheticReturn(bool function)
        {
            using var table = Arguments(); var first = Argument(table, "NUMBER"); first["ROUTINE_TYPE"] = function ? "FUNCTION" : "PROCEDURE";
            first["PARAMETER_NAME"] = function ? "return_value" : "VALUE";
            var second = table.NewRow(); second.ItemArray = first.ItemArray;
            second["ORDINAL_POSITION"] = function ? 0 : 2; second["PARAMETER_MODE"] = function ? "RETURN" : "IN";
            second["PARAMETER_NAME"] = function ? DBNull.Value : "value"; table.Rows.Add(second);
            Assert.Equal(2, RoutineParameterDerivation.OrderParameters(table, first, false).Length);
            first["PARAMETER_NAME"] = function ? "RETURN_VALUE" : "value";
            if (function) Assert.Equal(2, RoutineParameterDerivation.OrderParameters(table, first, false).Length);
            else Assert.Throws<NotSupportedException>(() => RoutineParameterDerivation.OrderParameters(table, first, false));
        }

        [Theory]
        [InlineData("owner.pkg.p", false, "OWNER", "PKG", "P")]
        [InlineData("医院.诊断包.查询_2$#", false, "医院", "诊断包", "查询_2$#")]
        [InlineData("诊断包.查询", false, "诊断包", "查询", null)]
        [InlineData("hôpital.查询", false, "HÔPITAL", "查询", null)]
        [InlineData("\"Mixed.Owner\".查询", false, "Mixed.Owner", "查询", null)]
        [InlineData("\"Mixed.Owner\".\"P\"", false, "Mixed.Owner", "P", null)]
        [InlineData("`db.name`.`p``name`", true, "db.name", "p`name", null)]
        public void RoutineNamesPreserveQuotingAndModeCase(string text, bool mysql, string first, string second, string? third)
        {
            Assert.Equal(third == null ? new[] { first, second } : new[] { first, second, third }, RoutineParameterDerivation.ParseName(text, mysql));
        }

        [Theory]
        [InlineData("p;delete", false)]
        [InlineData("a..p", false)]
        [InlineData("p@remote", false)]
        [InlineData("`bad", true)]
        [InlineData("db.p.extra", true)]
        [InlineData(" p", false)]
        [InlineData("诊断;delete", false)]
        [InlineData("诊断包.1查询", false)]
        [InlineData("诊断--", false)]
        [InlineData("诊断/*x*/", false)]
        [InlineData("诊断。查询", false)]
        [InlineData("诊断\u200B查询", false)]
        [InlineData("诊断\u0301", false)]
        [InlineData("诊断🙂", false)]
        public void RoutineNamesCannotBeSql(string text, bool mysql)
            => Assert.Throws<NotSupportedException>(() => RoutineParameterDerivation.ParseName(text, mysql));

        [Theory]
        [InlineData("IN", ParameterDirection.Input)]
        [InlineData("OUT", ParameterDirection.Output)]
        [InlineData("INOUT", ParameterDirection.InputOutput)]
        public void UnicodeDerivedParameterNamesCanBeBoundWithoutRenaming(string mode, ParameterDirection direction)
        {
            using var table = Arguments(); var row = Argument(table, "NUMBER"); row["PARAMETER_NAME"] = "数值"; row["PARAMETER_MODE"] = mode;
            Assert.Same(row, Assert.Single(RoutineParameterDerivation.OrderParameters(table, row, false)));
            var parameter = RoutineParameterDerivation.Map(row, false, false);
            Assert.Equal("数值", parameter.ParameterName); Assert.Equal(direction, parameter.Direction);
            var parameters = new OceanBaseParameterCollection(); parameters.Add(parameter);
            var plan = OracleCommandPlan.Create("诊断", CommandType.StoredProcedure, parameters);
            Assert.Equal("BEGIN 诊断(?); END;", plan.Bindings.CommandText); Assert.Equal("数值", Assert.Single(plan.Bindings.Parameters).ParameterName);
        }

        [Theory]
        [InlineData("输 入")]
        [InlineData(":输入")]
        [InlineData("输入\u0301")]
        [InlineData("输入\u200B")]
        [InlineData("输入🙂")]
        [InlineData("输入。")]
        public void DerivedQuotedNamesRemainRawAndBindPositionally(string name)
        {
            using var table = Arguments(); var row = Argument(table, "NUMBER"); row["PARAMETER_NAME"] = name;
            Assert.Single(RoutineParameterDerivation.OrderParameters(table, row, false));
            var parameters = new OceanBaseParameterCollection(); parameters.Add(RoutineParameterDerivation.Map(row, false, false));
            var plan = OracleCommandPlan.Create("p", CommandType.StoredProcedure, parameters);
            Assert.Equal("BEGIN p(?); END;", plan.Bindings.CommandText); Assert.Equal(name, plan.Bindings.Parameters[0].ParameterName);
        }

        [Fact]
        public void DerivedUnicodeNamesRetainTheUtf8ByteLimitAndExactIdentity()
        {
            using var table = Arguments(); var row = Argument(table, "NUMBER");
            row["PARAMETER_NAME"] = new string('中', 170) + "ab";
            Assert.Single(RoutineParameterDerivation.OrderParameters(table, row, false));
            row["PARAMETER_NAME"] = new string('中', 171);
            Assert.Throws<NotSupportedException>(() => RoutineParameterDerivation.OrderParameters(table, row, false));
            row["PARAMETER_NAME"] = "état";
            var second = table.NewRow(); second.ItemArray = row.ItemArray;
            second["ORDINAL_POSITION"] = 2; second["PARAMETER_NAME"] = "ÉTAT"; table.Rows.Add(second);
            Assert.Equal(2, RoutineParameterDerivation.OrderParameters(table, row, false).Length);
            second["PARAMETER_NAME"] = "état";
            Assert.Throws<NotSupportedException>(() => RoutineParameterDerivation.OrderParameters(table, row, false));
        }

        [Fact]
        public void AtomicReplacementDetectsClearAndReaddAndRetainsOwnershipOnFailure()
        {
            var collection = new OceanBaseParameterCollection(); var original = new OceanBaseParameter(); collection.Add(original);
            var version = collection.Version; collection.Clear(); collection.Add(original);
            var fresh = new OceanBaseParameter();
            Assert.Throws<InvalidOperationException>(() => collection.ReplaceDerived(new[] { fresh }, version));
            Assert.Same(original, collection[0]); Assert.Same(collection, original.Owner); Assert.Null(fresh.Owner);
            collection.ReplaceDerived(new[] { fresh }, collection.Version);
            Assert.Same(fresh, collection[0]); Assert.Null(original.Owner); Assert.Same(collection, fresh.Owner);
        }
    }
}
