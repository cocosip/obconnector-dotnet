using System;
using System.Data;
using System.Runtime.Serialization;
using OceanBaseConnector.Internal.Sql;
using Xunit;

#pragma warning disable SYSLIB0050

namespace OceanBaseConnector.UnitTests;

public sealed class NamedComplexLobBindingTests
{
    [Fact]
    public void RewritesNamedLobMemberToConstructorAndHiddenLocatorParameter()
    {
        var type = TypeWithBlobMember();
        var lob = (OceanBaseLob)FormatterServices.GetUninitializedObject(typeof(OceanBaseLob));
        var value = new OceanBaseComplexValue(type, new object?[] { lob, 7 });
        var parameter = new OceanBaseParameter { ParameterName = "p", ComplexType = type, Value = value };

        var plan = NamedComplexLobBinding.Rewrite("BEGIN consume(:p); END;", new[] { parameter });

        Assert.Equal("BEGIN consume(\"OWNER\".\"THING\"(?,?)); END;", plan.CommandText);
        Assert.Equal(2, plan.Parameters.Length);
        Assert.Same(lob, plan.Parameters[0].Value);
        Assert.Equal(OceanBaseDbType.Blob, plan.Parameters[0].OceanBaseDbType);
        Assert.Equal("7", Assert.IsType<OceanBaseDecimal>(plan.Parameters[1].Value).ToString());
        Assert.Equal(DbType.Decimal, plan.Parameters[1].DbType);
        Assert.DoesNotContain(parameter, plan.Parameters);
    }

    [Fact]
    public void RewritesNestedNamedLobAndKeepsNullNestedObjectLiteral()
    {
        var leaf = TypeWithBlobMember("LEAF");
        var root = new OceanBaseComplexType(null, "OWNER", "ROOT", false,
            new[] { new OceanBaseComplexMember("L", "OWNER", "LEAF", leaf, leaf.Layout),
                new OceanBaseComplexMember("N", null, "NUMBER", null, new Internal.Values.ComplexType(0)) });
        var lob = (OceanBaseLob)FormatterServices.GetUninitializedObject(typeof(OceanBaseLob));
        var value = new OceanBaseComplexValue(root, new object?[] { new OceanBaseComplexValue(leaf, new object?[] { lob, 9 }), null });
        var parameter = new OceanBaseParameter { ParameterName = "p", ComplexType = root, Value = value };

        var plan = NamedComplexLobBinding.Rewrite("INSERT INTO t VALUES (:p)", new[] { parameter });

        Assert.Equal("INSERT INTO t VALUES (\"OWNER\".\"ROOT\"(\"OWNER\".\"LEAF\"(?,?),NULL))", plan.CommandText);
        Assert.Equal(2, plan.Parameters.Length);
        Assert.Same(lob, plan.Parameters[0].Value);
    }

    [Fact]
    public void QuotesTypeNamesAndExpandsRepeatedPlaceholders()
    {
        var type = new OceanBaseComplexType(null, "SC\"HEMA", "T\"HING", false,
            new[]
            {
                new OceanBaseComplexMember("A", null, "BLOB", null, new Internal.Values.ComplexType(210))
            });
        var lob = (OceanBaseLob)FormatterServices.GetUninitializedObject(typeof(OceanBaseLob));
        var value = new OceanBaseComplexValue(type, new object?[] { lob });
        var parameter = new OceanBaseParameter { ParameterName = "p", ComplexType = type, Value = value };

        var plan = NamedComplexLobBinding.Rewrite("BEGIN consume(:p, :p); END;", new[] { parameter });

        Assert.Equal("BEGIN consume(\"SC\"\"HEMA\".\"T\"\"HING\"(?)" +
            ", \"SC\"\"HEMA\".\"T\"\"HING\"(?)); END;", plan.CommandText);
        Assert.Equal(2, plan.Parameters.Length);
        Assert.Same(lob, plan.Parameters[0].Value);
        Assert.Same(lob, plan.Parameters[1].Value);
    }

    [Fact]
    public void RejectsOutputAndAnonymousLocatorValues()
    {
        var named = TypeWithBlobMember();
        var lob = (OceanBaseLob)FormatterServices.GetUninitializedObject(typeof(OceanBaseLob));
        var output = new OceanBaseParameter
        {
            ParameterName = "p",
            ComplexType = named,
            Direction = ParameterDirection.InputOutput,
            Value = new OceanBaseComplexValue(named, new object?[] { lob, 1 })
        };
        Assert.Throws<NotSupportedException>(() => NamedComplexLobBinding.Rewrite("BEGIN p(:p); END;", new[] { output }));

        var anonymous = new OceanBaseComplexType(null, "", "", true,
            new[]
            {
                new OceanBaseComplexMember(null, null, "BLOB", null, new Internal.Values.ComplexType(210))
            });
        var input = new OceanBaseParameter
        {
            ParameterName = "p",
            ComplexType = anonymous,
            Value = new OceanBaseComplexValue(anonymous, new object?[] { lob })
        };
        Assert.Throws<NotSupportedException>(() => NamedComplexLobBinding.Rewrite("INSERT INTO t VALUES (:p)", new[] { input }));
    }

    private static OceanBaseComplexType TypeWithBlobMember(string name = "THING")
        => new OceanBaseComplexType(null, "OWNER", name, false,
            new[] { new OceanBaseComplexMember("A", null, "BLOB", null, new Internal.Values.ComplexType(210)),
                new OceanBaseComplexMember("N", null, "NUMBER", null, new Internal.Values.ComplexType(0)) });
}

#pragma warning restore SYSLIB0050
