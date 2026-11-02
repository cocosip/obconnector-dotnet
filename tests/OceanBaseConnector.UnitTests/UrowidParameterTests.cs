using System;
using System.Data;
using System.Linq;
using System.Text;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Sql;
using Xunit;

namespace OceanBaseConnector.UnitTests;

public sealed class UrowidParameterTests
{
    [Fact]
    public void UrowidParameterUsesOracleType209AndLengthEncodedText()
    {
        var parameter = new OceanBaseParameter
        {
            OceanBaseDbType = OceanBaseDbType.Urowid,
            Value = "AAAVhPAAEAAAACXAAA"
        };

        Assert.Equal(DbType.String, parameter.DbType);
        Assert.Equal(new byte[] { 0, 1, 209, 0, 18, 65, 65, 65, 86, 104, 80, 65, 65, 69, 65, 65, 65, 65, 67, 88, 65, 65, 65 },
            PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
    }

    [Fact]
    public void UrowidParameterRejectsMySqlAndNonStringValues()
    {
        var parameter = new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Urowid, Value = "AAAVhPAAEAAAACXAAA" };
        Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { parameter }));
        parameter.Value = 1;
        Assert.Throws<ArgumentException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
    }

    [Fact]
    public void UrowidNullRetainsModeAndDeclarationValidation()
    {
        var parameter = new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Urowid };
        foreach (var value in new object?[] { null, DBNull.Value })
        {
            parameter.Value = value;
            Assert.Equal(new byte[] { 1, 1, 6, 0 }, PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
            Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { parameter }));
            parameter.Precision = 1;
            Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
            parameter.Precision = 0; parameter.Scale = 1;
            Assert.Throws<NotSupportedException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
            parameter.Scale = 0; parameter.DecimalScale = 0;
            Assert.Throws<ArgumentException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
            parameter.DecimalScale = null;
        }
    }

    [Fact]
    public void UrowidPreservesTextAndUsesStrictEncodingWithoutTruncation()
    {
        var text = new string('A', 251);
        var parameter = new OceanBaseParameter { OceanBaseDbType = OceanBaseDbType.Urowid, Value = text, Size = 1 };
        Assert.Equal(new byte[] { 0, 1, 209, 0, 252, 251, 0 }.Concat(Encoding.UTF8.GetBytes(text)),
            PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
        parameter.Value = "\uD800";
        Assert.Throws<EncoderFallbackException>(() => PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle));
        parameter.ResetDbType(); parameter.Value = "AAAVhPAAEAAAACXAAA";
        Assert.Null(parameter.OceanBaseDbType);
        Assert.Equal(253, PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle)[2]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UrowidClientTextPreservesItsTypeAndEscapesValues(bool multiStatement)
    {
        var parameters = new OceanBaseParameterCollection();
        var parameter = new OceanBaseParameter { ParameterName = "p", OceanBaseDbType = OceanBaseDbType.Urowid, Value = "A'); DELETE FROM t; --\\\0", Size = 1 };
        parameters.Add(parameter);
        var sql = multiStatement ? "SELECT :p FROM DUAL; SELECT :p FROM DUAL" : "SELECT :p FROM DUAL";
        string Build() => ClientPreparedSql.Build(ParameterBindingPlan.Create(sql, OceanBaseCompatibilityMode.Oracle, parameters),
            OceanBaseCompatibilityMode.Oracle, multiStatement: multiStatement);
        var expected = "SELECT (CHARTOROWID(REPLACE( 'A''); DELETE FROM t; --\\\0',NULL,NULL))) FROM DUAL";
        Assert.Equal(multiStatement ? expected + "; " + expected : expected, Build());
        foreach (var value in new object?[] { null, DBNull.Value })
        {
            parameter.Value = value;
            const string empty = "SELECT (NULL) FROM DUAL";
            Assert.Equal(multiStatement ? empty + "; " + empty : empty, Build());
        }
        parameter.Value = ""; Assert.Throws<ArgumentException>(() => Build());
        parameter.Value = "\ud800"; Assert.Throws<EncoderFallbackException>(() => Build());
        parameter.Value = 1; Assert.Throws<ArgumentException>(() => Build());
        parameter.Value = "A"; parameter.Precision = 1; Assert.Throws<NotSupportedException>(() => Build());
        parameter.Precision = 0; parameter.Direction = ParameterDirection.Output; Assert.Throws<NotSupportedException>(() => Build());
    }

    [Theory]
    [InlineData("ROWID")]
    [InlineData("UROWID")]
    public void DerivedRowidArgumentsUseTheExplicitParameterWire(string type)
    {
        using var table = DeriveParametersMetadataTests.Arguments();
        var row = DeriveParametersMetadataTests.Argument(table, type);
        var parameter = RoutineParameterDerivation.Map(row, false, false);
        Assert.Equal(OceanBaseDbType.Urowid, parameter.OceanBaseDbType);
        parameter.Value = "AAAVhPAAEAAAACXAAA";
        Assert.Equal(209, PreparedParameters.Encode(new[] { parameter }, OceanBaseCompatibilityMode.Oracle)[2]);
        Assert.Throws<NotSupportedException>(() => RoutineParameterDerivation.Map(row, true, false));
    }
}
