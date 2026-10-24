using System;
using System.Data;
using System.Linq;
using Xunit;

namespace OceanBaseConnector.UnitTests.TestInfrastructure
{
    internal static class CatalogSamples
    {
        public static readonly string[] Names = { "Views", "Indexes", "IndexColumns", "Keys", "Constraints", "ConstraintColumns", "ForeignKeys", "ForeignKeyColumns", "Types", "Sequences", "Users", "Synonyms" };
        public static string Source(string name, bool mysql) => name switch
        {
            "Views" => mysql ? "INFORMATION_SCHEMA.VIEWS" : "ALL_VIEWS",
            "Indexes" => mysql ? "INFORMATION_SCHEMA.STATISTICS" : "ALL_INDEXES",
            "IndexColumns" => mysql ? "INFORMATION_SCHEMA.STATISTICS" : "ALL_IND_COLUMNS",
            "Keys" or "Constraints" => mysql ? "INFORMATION_SCHEMA.TABLE_CONSTRAINTS" : "ALL_CONSTRAINTS",
            "ConstraintColumns" => mysql ? "INFORMATION_SCHEMA.KEY_COLUMN_USAGE" : "ALL_CONS_COLUMNS",
            "ForeignKeys" or "ForeignKeyColumns" => mysql ? "INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS" : "ALL_CONSTRAINTS",
            "Types" => "ALL_TYPES",
            "Sequences" => mysql ? "oceanbase.DBA_SEQUENCES" : "ALL_SEQUENCES",
            "Users" => mysql ? "mysql.user" : "ALL_USERS",
            "Synonyms" => "ALL_SYNONYMS",
            _ => throw new ArgumentException(name)
        };

        public static (string[] Names, string?[] Values) Row(string name, bool mysql)
        {
            var common = new[] { "TABLE_CATALOG", "TABLE_SCHEMA", "TABLE_NAME" };
            string?[] identity = { mysql ? "Db目标" : null, mysql ? null : "Owner目标", "Table目标" };
            var owner = mysql ? null : "Owner目标";
            var extra = name switch
            {
                "Views" => (new[] { "TABLE_TYPE" }, new string?[] { "VIEW" }),
                "Indexes" => (new[] { "INDEX_SCHEMA", "INDEX_NAME", "INDEX_TYPE", "IS_UNIQUE" }, new[] { owner, "Index目标", "NORMAL", "YES" }),
                "IndexColumns" => (new[] { "INDEX_SCHEMA", "INDEX_NAME", "COLUMN_NAME", "ORDINAL_POSITION", "SORT_ORDER", "PREFIX_LENGTH" }, new[] { owner, "Index目标", "Column目标", "2", "ASC", null }),
                "Keys" or "Constraints" => (new[] { "CONSTRAINT_SCHEMA", "CONSTRAINT_NAME", "CONSTRAINT_TYPE" }, new[] { owner, "Key目标", "UNIQUE" }),
                "ConstraintColumns" => (new[] { "CONSTRAINT_SCHEMA", "CONSTRAINT_NAME", "COLUMN_NAME", "ORDINAL_POSITION" }, new[] { owner, "Key目标", "Column目标", "2" }),
                "ForeignKeys" => (new[] { "CONSTRAINT_SCHEMA", "CONSTRAINT_NAME", "REFERENCED_TABLE_CATALOG", "REFERENCED_TABLE_SCHEMA", "REFERENCED_TABLE_NAME", "REFERENCED_CONSTRAINT_SCHEMA", "REFERENCED_CONSTRAINT_NAME", "UPDATE_RULE", "DELETE_RULE" }, new[] { owner, "FK目标", mysql ? "OtherDb" : null, mysql ? null : "OtherOwner", "Parent目标", mysql ? null : "OtherOwner", "PK目标", mysql ? "CASCADE" : "NO ACTION", "CASCADE" }),
                "ForeignKeyColumns" => (new[] { "CONSTRAINT_SCHEMA", "CONSTRAINT_NAME", "COLUMN_NAME", "ORDINAL_POSITION", "REFERENCED_TABLE_CATALOG", "REFERENCED_TABLE_SCHEMA", "REFERENCED_TABLE_NAME", "REFERENCED_COLUMN_NAME" }, new[] { owner, "FK目标", "Column目标", "2", mysql ? "OtherDb" : null, mysql ? null : "OtherOwner", "Parent目标", "ParentColumn目标" }),
                _ => (Array.Empty<string>(), Array.Empty<string?>())
            };
            if (extra.Item1.Length > 0) return (common.Concat(extra.Item1).ToArray(), identity.Concat(extra.Item2).ToArray());
            return name switch
            {
                "Types" => (new[] { "TYPE_CATALOG", "TYPE_SCHEMA", "TYPE_NAME", "TYPE_CODE", "ATTRIBUTE_COUNT", "METHOD_COUNT", "SUPER_TYPE_SCHEMA", "SUPER_TYPE_NAME" }, new string?[] { null, owner, "Type目标", "OBJECT", "2", "0", null, null }),
                "Sequences" => (new[] { "SEQUENCE_CATALOG", "SEQUENCE_SCHEMA", "SEQUENCE_NAME", "MINIMUM_VALUE", "MAXIMUM_VALUE", "INCREMENT", "CACHE_SIZE", "LAST_NUMBER", "CYCLE_FLAG", "ORDER_FLAG" }, new[] { mysql ? "Db目标" : null, owner, "Sequence目标", "-9999999999999999999999999999", "99999999999999999999999999999999999999", "-1", "20", "12345678901234567890123456789012345678", "N", "Y" }),
                "Users" => (new[] { "USER_NAME", "HOST" }, new[] { "User目标", mysql ? "localhost" : null }),
                "Synonyms" => (new[] { "SYNONYM_CATALOG", "SYNONYM_SCHEMA", "SYNONYM_NAME", "OBJECT_CATALOG", "OBJECT_SCHEMA", "OBJECT_NAME", "DB_LINK" }, new string?[] { null, owner, "Syn目标", null, "OtherOwner", "Object目标", null }),
                _ => throw new ArgumentException(name)
            };
        }

        public static void AssertRow(DataTable table, string collection, bool mysql)
        {
            var (names, values) = Row(collection, mysql);
            Assert.Equal(collection, table.TableName); Assert.Equal(names, table.Columns.Cast<DataColumn>().Select(x => x.ColumnName));
            var row = Assert.Single(table.Rows.Cast<DataRow>());
            for (var i = 0; i < values.Length; i++)
            {
                if (values[i] == null) Assert.Equal(DBNull.Value, row[i]);
                else if (collection == "Sequences" && i >= 3 && i <= 7) { Assert.Equal("OceanBaseDecimal", table.Columns[i].DataType.Name); Assert.Equal(values[i], row[i].ToString()); }
                else if (names[i] == "ORDINAL_POSITION" || names[i] == "ATTRIBUTE_COUNT" || names[i] == "METHOD_COUNT") Assert.Equal(int.Parse(values[i]!), row[i]);
                else Assert.Equal(values[i], row[i]);
            }
        }
    }
}
