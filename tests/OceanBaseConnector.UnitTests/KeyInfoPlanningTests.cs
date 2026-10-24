using System;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Sql;
using Xunit;

namespace OceanBaseConnector.UnitTests
{
    public sealed class KeyInfoPlanningTests
    {
        [Theory]
        [InlineData("select UNIQUE value from t", false)]
        [InlineData("select value from t order by count(*)", true)]
        [InlineData("select value from t order by count(*)", false)]
        [InlineData("select value from t where x = 1 order by sum(value)", true)]
        [InlineData("select distinct value from t", true)]
        [InlineData("select value from t group by value", false)]
        [InlineData("select value from t union select value from u", true)]
        [InlineData("select sum(value) from t", false)]
        [InlineData("select value from t join u on t.id=u.id", true)]
        [InlineData("select value from (select * from t) v", false)]
        [InlineData("select value from t, u", true)]
        [InlineData("select \"value\" from t", true)]
        [InlineData("select 'a\\b' from t", true)]
        [InlineData("select value /*! FROM u */ from t", true)]
        [InlineData("with q as (select value from t) select * from q", false)]
        public void AmbiguousOrCardinalityChangingSqlCannotAcquireHiddenKeys(string sql, bool mysql)
            => Assert.Null(KeyInfoQuery.Parse(sql, mysql ? OceanBaseCompatibilityMode.MySql : OceanBaseCompatibilityMode.Oracle));

        [Theory]
        [InlineData("/* from false */ select l.id, 'FROM fake', l.value + 1 from `owner`.`table` AS l where l.x in (select x from u) order by l.id limit 4", true, "owner", "table")]
        [InlineData("select l.id, q'[FROM fake]' from \"Owner\".\"Table\" l WHERE l.x IN (SELECT x FROM u) ORDER BY l.id FETCH FIRST 1 ROW ONLY", false, "Owner", "Table")]
        [InlineData("select l.id, nq'[it's FROM fake]' from \"Owner\".\"Table\" l", false, "Owner", "Table")]
        [InlineData("select id from ledger", false, null, "LEDGER")]
        [InlineData("select id from ledger", true, null, "ledger")]
        [InlineData("select x from `a``b`.`t``x`", true, "a`b", "t`x")]
        [InlineData("select x from \"a\"\"b\".\"t\"\"x\"", false, "a\"b", "t\"x")]
        public void SourceRecognitionRespectsQuotesCommentsAndNestedFilters(string sql, bool mysql, string? schema, string table)
        {
            var query = KeyInfoQuery.Parse(sql, mysql ? OceanBaseCompatibilityMode.MySql : OceanBaseCompatibilityMode.Oracle);
            Assert.NotNull(query); Assert.Equal(schema, query.Schema); Assert.Equal(table, query.Table);
            var rewritten = query.Append(new[] { "missing" }, Array.Empty<ResultColumn>());
            Assert.StartsWith(sql.Substring(0, query.Insertion), rewritten);
            Assert.EndsWith(sql.Substring(query.Insertion), rewritten);
        }

        [Fact]
        public void FullCompositeKeysWinOverPartialNullableOrPrefixIndexes()
        {
            var catalog = new KeyInfoMetadata.Catalog();
            catalog.Add(Row("A", "U", "id", "1", "NO", "NO"));
            catalog.Add(Row("B", "U", "nullable", "1", "YES"));
            catalog.Add(Row("PK", "P", "id", "1", "NO"));
            catalog.Add(Row("PK", "P", "part", "2", "NO"));
            var key = catalog.SelectKey(); Assert.NotNull(key); Assert.Equal("PK", key.Name); Assert.Equal(new[] { "id", "part" }, key.Members);
            Assert.Equal(key.Members, catalog.Missing(key, Array.Empty<ResultColumn>()));
        }

        [Theory]
        [InlineData("P", "id", "2", "NO", "YES")]
        [InlineData("X", "id", "1", "NO", "YES")]
        [InlineData("P", "id", "0", "NO", "YES")]
        [InlineData("P", "id", "1", "MAYBE", "YES")]
        [InlineData("P", "id", "1", "NO", "MAYBE")]
        public void MalformedCatalogCannotManufactureAKey(string kind, string column, string ordinal, string nullable, string full)
        {
            var catalog = new KeyInfoMetadata.Catalog();
            Assert.Throws<ProtocolException>(() => catalog.Add(Row("PK", kind, column, ordinal, nullable, full)));
        }

        [Fact]
        public void CatalogRejectsMixedTablesConflictingNullabilityAndUnboundedRows()
        {
            var catalog = new KeyInfoMetadata.Catalog(); catalog.Add(Row("PK", "P", "id", "1", "NO"));
            var mixed = Row("UQ", "U", "other", "1", "NO"); mixed[1] = "other_table";
            Assert.Throws<ProtocolException>(() => catalog.Add(mixed));
            Assert.Throws<ProtocolException>(() => catalog.Add(Row("UQ", "U", "id", "1", "YES")));
            catalog = new KeyInfoMetadata.Catalog();
            for (var i = 0; i < 4096; i++) catalog.Add(Row("U" + i, "U", "c" + i, "1", "NO"));
            Assert.Throws<NotSupportedException>(() => catalog.Add(Row("overflow", "U", "last", "1", "NO")));
        }

        private static string?[] Row(string name, string kind, string column, string ordinal, string nullable, string full = "YES")
            => new string?[] { "owner", "table", name, kind, column, ordinal, nullable, full };
    }
}
