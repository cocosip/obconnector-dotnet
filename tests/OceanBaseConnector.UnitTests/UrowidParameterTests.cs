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

    [Fact]
    public void UrowidDoesNotSilentlyBecomeAClientPreparedString()
    {
        var parameters = new OceanBaseParameterCollection();
        parameters.Add(new OceanBaseParameter { ParameterName = "p", OceanBaseDbType = OceanBaseDbType.Urowid, Value = "AAAVhPAAEAAAACXAAA" });
        var plan = ParameterBindingPlan.Create("SELECT :p FROM DUAL", OceanBaseCompatibilityMode.Oracle, parameters);
        Assert.Throws<NotSupportedException>(() => ClientPreparedSql.Build(plan, OceanBaseCompatibilityMode.Oracle));
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
