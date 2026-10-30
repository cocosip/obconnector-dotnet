using System;
using System.Data;
using System.Linq;
using OceanBaseConnector.Internal.Sql;
using OceanBaseConnector.Internal.Values;
using Xunit;

namespace OceanBaseConnector.UnitTests;

public sealed class ClientPreparedObjectTests
{
    private static OceanBaseComplexType ObjectType(params byte[] codes)
        => new(null, "Ow\"ner", "Ty\"pe", false, codes.Select(code =>
            new OceanBaseComplexMember("m", null, "scalar", null, new ComplexType(code))).ToArray());

    private static string Build(OceanBaseParameter parameter, bool multi = true, string sql = "select :p; select :P")
    {
        var parameters = new OceanBaseParameterCollection { parameter.Snapshot() };
        return ClientPreparedSql.Build(ParameterBindingPlan.Create(sql, OceanBaseCompatibilityMode.Oracle, parameters),
            OceanBaseCompatibilityMode.Oracle, multiStatement: multi);
    }

    [Fact]
    public void NamedObjectUsesQuotedIdentityAndExistingScalarLiterals()
    {
        var type = ObjectType(0, 253, 203, 207, 208, 254, 12, 202);
        var value = new OceanBaseComplexValue(type, new object?[] { 7, "x'); delete t; --", new byte[] { 0, 255 },
            "中", "文", "char", new DateTime(2026, 10, 30, 1, 2, 3),
            new OceanBaseTimestamp(new DateTime(2026, 10, 30, 1, 2, 3), 123456789, 9) });
        var literal = "\"Ow\"\"ner\".\"Ty\"\"pe\"(7,REPLACE( 'x''); delete t; --',NULL,NULL),HEXTORAW('00FF'),u'\\4e2d',u'\\6587',REPLACE( 'char',NULL,NULL),TO_DATE('2026-10-30 01:02:03','YYYY-MM-DD HH24:MI:SS'),TIMESTAMP '2026-10-30 01:02:03.123456789')";
        Assert.Equal("select (" + literal + "); select (" + literal + ")",
            Build(new OceanBaseParameter { ParameterName = "p", Value = value }));
    }

    [Fact]
    public void NullObjectDiffersFromObjectWithNullAttributes()
    {
        var type = ObjectType(0, 253);
        var parameter = new OceanBaseParameter { ParameterName = "p", ComplexType = type, Value = DBNull.Value };
        Assert.Equal("select (NULL); select (NULL)", Build(parameter));
        parameter.Value = new OceanBaseComplexValue(type, new object?[] { null, DBNull.Value });
        Assert.Equal("select (\"Ow\"\"ner\".\"Ty\"\"pe\"(NULL,NULL)); select (\"Ow\"\"ner\".\"Ty\"\"pe\"(NULL,NULL))", Build(parameter));
    }

    [Fact]
    public void NestedNamedObjectsPreserveQuotedIdentityAndNullShape()
    {
        var child = ObjectType(0, 207);
        var parent = new OceanBaseComplexType(null, "Parent\"Owner", "Parent\"Type", false,
            new[] { new OceanBaseComplexMember("child", child.SchemaName, child.TypeName, child, child.Layout) });
        var grandparent = new OceanBaseComplexType(null, "s", "root", false,
            new[] { new OceanBaseComplexMember("parent", parent.SchemaName, parent.TypeName, parent, parent.Layout) });
        var leaf = new OceanBaseComplexValue(child, new object?[] { 7, "中" });
        var branch = new OceanBaseComplexValue(parent, new object?[] { leaf });
        var parameter = new OceanBaseParameter { ParameterName = "p", Value = new OceanBaseComplexValue(grandparent, new object?[] { branch }) };
        var literal = "\"s\".\"root\"(\"Parent\"\"Owner\".\"Parent\"\"Type\"(\"Ow\"\"ner\".\"Ty\"\"pe\"(7,u'\\4e2d')))";
        Assert.Equal("select (" + literal + "); select (" + literal + ")", Build(parameter));
        parameter.Value = new OceanBaseComplexValue(parent, new object?[] { null });
        Assert.Equal("select (\"Parent\"\"Owner\".\"Parent\"\"Type\"(NULL)); select (\"Parent\"\"Owner\".\"Parent\"\"Type\"(NULL))", Build(parameter));
        parameter.Value = new OceanBaseComplexValue(parent, new object?[] { new OceanBaseComplexValue(child, new object?[] { null, null }) });
        literal = "\"Parent\"\"Owner\".\"Parent\"\"Type\"(\"Ow\"\"ner\".\"Ty\"\"pe\"(NULL,NULL))";
        Assert.Equal("select (" + literal + "); select (" + literal + ")", Build(parameter));
    }

    [Theory]
    [InlineData("collection")]
    [InlineData("anonymous")]
    [InlineData("lob")]
    [InlineData("cursor")]
    public void UnsupportedNestedLayoutsRemainRejectedEvenForNullParent(string shape)
    {
        var child = shape == "collection" ? new OceanBaseComplexType(null, "s", "c", true, ObjectType(0).Members.ToArray())
            : shape == "anonymous" ? new OceanBaseComplexType(null, "", "", false, ObjectType(0).Members.ToArray())
            : ObjectType(shape == "lob" ? (byte)210 : (byte)163);
        var parent = new OceanBaseComplexType(null, "s", "p", false,
            new[] { new OceanBaseComplexMember("child", child.SchemaName, child.TypeName, child, child.Layout) });
        Assert.Throws<NotSupportedException>(() => Build(new OceanBaseParameter { ParameterName = "p", ComplexType = parent, Value = DBNull.Value }));
    }

    [Fact]
    public void SharedNamedObjectDescriptorsAllowTypedNullWithoutExpandingValues()
    {
        var type = ObjectType(0);
        for (var depth = 1; depth < 32; depth++)
        {
            var member = new OceanBaseComplexMember("child", type.SchemaName, type.TypeName, type, type.Layout);
            type = new OceanBaseComplexType(null, "s", "t" + depth, false, new[] { member, member });
        }
        Assert.Equal("select (NULL); select (NULL)", Build(new OceanBaseParameter { ParameterName = "p", ComplexType = type, Value = DBNull.Value }));
    }

    [Theory]
    [InlineData(210)]
    [InlineData(211)]
    [InlineData(163)]
    public void UnsupportedMemberLayoutsRemainRejectedEvenForNullObject(int code)
    {
        var member = ObjectType((byte)code).Members[0];
        var type = new OceanBaseComplexType(null, "s", "t", false, new[] { member });
        Assert.Throws<NotSupportedException>(() => Build(new OceanBaseParameter { ParameterName = "p", ComplexType = type, Value = DBNull.Value }));
    }

    [Fact]
    public void CollectionsFallbackOutputAndDdlRemainRejected()
    {
        var type = ObjectType(0);
        var parameter = new OceanBaseParameter { ParameterName = "p", ComplexType = type, Value = DBNull.Value };
        Assert.Throws<NotSupportedException>(() => Build(parameter, multi: false, sql: "select :p"));
        Assert.Throws<NotSupportedException>(() => Build(parameter, sql: "insert into t values(:p); ALTER TYPE t ADD ATTRIBUTE n NUMBER"));
        parameter.Direction = ParameterDirection.InputOutput;
        Assert.Throws<NotSupportedException>(() => Build(parameter));
        parameter.Direction = ParameterDirection.Input;
        parameter.ComplexType = new OceanBaseComplexType(null, "s", "t", true, type.Members.ToArray());
        Assert.Throws<NotSupportedException>(() => Build(parameter));
        parameter.ComplexType = type;
        Assert.Throws<NotSupportedException>(() => ClientPreparedSql.Build(
            ParameterBindingPlan.Create("select ?; select ?", OceanBaseCompatibilityMode.MySql,
                new OceanBaseParameterCollection { parameter.Snapshot(), parameter.Snapshot() }), OceanBaseCompatibilityMode.MySql, multiStatement: true));
    }

    [Fact]
    public void ConstructorExpansionCountsEveryMemberAndRepeatedBindingAgainstPacketBudget()
    {
        var text = new string('x', 4 * 1024 * 1024);
        var type = ObjectType(207, 207);
        var parameter = new OceanBaseParameter { ParameterName = "p", Value = new OceanBaseComplexValue(type, new object?[] { text, text }) };
        Assert.Throws<ArgumentException>(() => Build(parameter));
        parameter.Value = new OceanBaseComplexValue(type, new object?[] { text + text, text + text });
        Assert.Throws<ArgumentException>(() => Build(parameter, sql: "select :p; select 1"));
        var parent = new OceanBaseComplexType(null, "s", "parent", false,
            new[] { new OceanBaseComplexMember("child", type.SchemaName, type.TypeName, type, type.Layout) });
        parameter.Value = new OceanBaseComplexValue(parent, new object?[] { new OceanBaseComplexValue(type, new object?[] { text, text }) });
        Assert.Throws<ArgumentException>(() => Build(parameter));
    }
}
