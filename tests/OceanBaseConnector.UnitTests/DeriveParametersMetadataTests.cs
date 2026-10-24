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
        public void MySqlDirectoryNamesDoNotInheritOracleAsciiBindRestrictions()
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
        public void ReturnAndCaseInsensitiveArgumentNameCollisionsCannotProduceAmbiguousBindings(bool function)
        {
            using var table = Arguments(); var first = Argument(table, "NUMBER"); first["ROUTINE_TYPE"] = function ? "FUNCTION" : "PROCEDURE";
            first["PARAMETER_NAME"] = function ? "return_value" : "VALUE";
            var second = table.NewRow(); second.ItemArray = first.ItemArray;
            second["ORDINAL_POSITION"] = function ? 0 : 2; second["PARAMETER_MODE"] = function ? "RETURN" : "IN";
            second["PARAMETER_NAME"] = function ? DBNull.Value : "value"; table.Rows.Add(second);
            Assert.Throws<NotSupportedException>(() => RoutineParameterDerivation.OrderParameters(table, first, false));
        }

        [Theory]
        [InlineData("owner.pkg.p", false, "OWNER", "PKG", "P")]
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
        public void RoutineNamesCannotBeSql(string text, bool mysql)
            => Assert.Throws<NotSupportedException>(() => RoutineParameterDerivation.ParseName(text, mysql));

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
