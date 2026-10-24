using System;
using System.Data;
using System.Globalization;
using System.Text;

namespace OceanBaseConnector
{
    public sealed partial class OceanBaseConnection
    {
        private static DataTable CreateCatalogDirectoryTable(string name)
        {
            var table = new DataTable(name) { Locale = CultureInfo.InvariantCulture };
            string columns;
            switch (name)
            {
                case "Views": columns = "TABLE_CATALOG TABLE_SCHEMA TABLE_NAME TABLE_TYPE"; break;
                case "Indexes": columns = "TABLE_CATALOG TABLE_SCHEMA TABLE_NAME INDEX_SCHEMA INDEX_NAME INDEX_TYPE IS_UNIQUE"; break;
                case "IndexColumns": columns = "TABLE_CATALOG TABLE_SCHEMA TABLE_NAME INDEX_SCHEMA INDEX_NAME COLUMN_NAME ORDINAL_POSITION SORT_ORDER PREFIX_LENGTH"; break;
                case "Keys":
                case "Constraints": columns = "TABLE_CATALOG TABLE_SCHEMA TABLE_NAME CONSTRAINT_SCHEMA CONSTRAINT_NAME CONSTRAINT_TYPE"; break;
                case "ConstraintColumns": columns = "TABLE_CATALOG TABLE_SCHEMA TABLE_NAME CONSTRAINT_SCHEMA CONSTRAINT_NAME COLUMN_NAME ORDINAL_POSITION"; break;
                case "ForeignKeys": columns = "TABLE_CATALOG TABLE_SCHEMA TABLE_NAME CONSTRAINT_SCHEMA CONSTRAINT_NAME REFERENCED_TABLE_CATALOG REFERENCED_TABLE_SCHEMA REFERENCED_TABLE_NAME REFERENCED_CONSTRAINT_SCHEMA REFERENCED_CONSTRAINT_NAME UPDATE_RULE DELETE_RULE"; break;
                case "ForeignKeyColumns": columns = "TABLE_CATALOG TABLE_SCHEMA TABLE_NAME CONSTRAINT_SCHEMA CONSTRAINT_NAME COLUMN_NAME ORDINAL_POSITION REFERENCED_TABLE_CATALOG REFERENCED_TABLE_SCHEMA REFERENCED_TABLE_NAME REFERENCED_COLUMN_NAME"; break;
                case "Types": columns = "TYPE_CATALOG TYPE_SCHEMA TYPE_NAME TYPE_CODE ATTRIBUTE_COUNT METHOD_COUNT SUPER_TYPE_SCHEMA SUPER_TYPE_NAME"; break;
                case "Sequences": columns = "SEQUENCE_CATALOG SEQUENCE_SCHEMA SEQUENCE_NAME MINIMUM_VALUE MAXIMUM_VALUE INCREMENT CACHE_SIZE LAST_NUMBER CYCLE_FLAG ORDER_FLAG"; break;
                case "Users": columns = "USER_NAME HOST"; break;
                case "Synonyms": columns = "SYNONYM_CATALOG SYNONYM_SCHEMA SYNONYM_NAME OBJECT_CATALOG OBJECT_SCHEMA OBJECT_NAME DB_LINK"; break;
                default: throw new ArgumentException("Unknown catalog collection.", nameof(name));
            }
            foreach (var column in columns.Split(' '))
                table.Columns.Add(column, column == "ORDINAL_POSITION" || column == "ATTRIBUTE_COUNT" || column == "METHOD_COUNT" ? typeof(int)
                    : column == "PREFIX_LENGTH" ? typeof(long)
                    : name == "Sequences" && column != "SEQUENCE_CATALOG" && column != "SEQUENCE_SCHEMA" && column != "SEQUENCE_NAME" && column != "CYCLE_FLAG" && column != "ORDER_FLAG" ? typeof(OceanBaseDecimal) : typeof(string));
            return table;
        }

        private static string CatalogDirectorySql(string name, bool mysql, string?[] restrictions, OceanBaseCommand command)
        {
            var sql = new StringBuilder();
            string?[] filters;
            var order = "1, 2, 3";
            switch (name)
            {
                case "Views":
                    sql.Append(mysql
                        ? "SELECT TABLE_SCHEMA AS TABLE_CATALOG, NULL AS TABLE_SCHEMA, TABLE_NAME, 'VIEW' AS TABLE_TYPE FROM INFORMATION_SCHEMA.VIEWS WHERE 1=1"
                        : "SELECT NULL AS TABLE_CATALOG, OWNER AS TABLE_SCHEMA, VIEW_NAME AS TABLE_NAME, 'VIEW' AS TABLE_TYPE FROM ALL_VIEWS WHERE OWNER <> '__recyclebin'");
                    filters = mysql ? new[] { "TABLE_SCHEMA", null, "TABLE_NAME" } : new[] { null, "OWNER", "VIEW_NAME" };
                    break;
                case "Indexes":
                    sql.Append(mysql
                        ? "SELECT DISTINCT TABLE_SCHEMA AS TABLE_CATALOG, NULL AS TABLE_SCHEMA, TABLE_NAME, NULL AS INDEX_SCHEMA, INDEX_NAME, INDEX_TYPE, CASE WHEN NON_UNIQUE = 0 THEN 'YES' ELSE 'NO' END AS IS_UNIQUE FROM INFORMATION_SCHEMA.STATISTICS WHERE 1=1"
                        : "SELECT NULL AS TABLE_CATALOG, TABLE_OWNER AS TABLE_SCHEMA, TABLE_NAME, OWNER AS INDEX_SCHEMA, INDEX_NAME, INDEX_TYPE, CASE WHEN UNIQUENESS = 'UNIQUE' THEN 'YES' ELSE 'NO' END AS IS_UNIQUE FROM ALL_INDEXES WHERE TABLE_OWNER <> '__recyclebin'");
                    filters = mysql ? new[] { "TABLE_SCHEMA", null, "TABLE_NAME", "INDEX_NAME" } : new[] { null, "TABLE_OWNER", "TABLE_NAME", "INDEX_NAME" };
                    order += ", 4, 5";
                    break;
                case "IndexColumns":
                    sql.Append(mysql
                        ? "SELECT TABLE_SCHEMA AS TABLE_CATALOG, NULL AS TABLE_SCHEMA, TABLE_NAME, NULL AS INDEX_SCHEMA, INDEX_NAME, COLUMN_NAME, SEQ_IN_INDEX AS ORDINAL_POSITION, CASE WHEN COLLATION = 'A' THEN 'ASC' WHEN COLLATION = 'D' THEN 'DESC' ELSE NULL END AS SORT_ORDER, SUB_PART AS PREFIX_LENGTH FROM INFORMATION_SCHEMA.STATISTICS WHERE 1=1"
                        : "SELECT NULL AS TABLE_CATALOG, TABLE_OWNER AS TABLE_SCHEMA, TABLE_NAME, INDEX_OWNER AS INDEX_SCHEMA, INDEX_NAME, COLUMN_NAME, COLUMN_POSITION AS ORDINAL_POSITION, DESCEND AS SORT_ORDER, NULL AS PREFIX_LENGTH FROM ALL_IND_COLUMNS WHERE TABLE_OWNER <> '__recyclebin'");
                    filters = mysql ? new[] { "TABLE_SCHEMA", null, "TABLE_NAME", "INDEX_NAME", "COLUMN_NAME" } : new[] { null, "TABLE_OWNER", "TABLE_NAME", "INDEX_NAME", "COLUMN_NAME" };
                    order += ", 4, 5, 7";
                    break;
                case "Keys":
                case "Constraints":
                    const string constraintType = "CASE CONSTRAINT_TYPE WHEN 'P' THEN 'PRIMARY KEY' WHEN 'U' THEN 'UNIQUE' WHEN 'R' THEN 'FOREIGN KEY' WHEN 'C' THEN 'CHECK' ELSE CONSTRAINT_TYPE END";
                    sql.Append(mysql
                        ? "SELECT TABLE_SCHEMA AS TABLE_CATALOG, NULL AS TABLE_SCHEMA, TABLE_NAME, NULL AS CONSTRAINT_SCHEMA, CONSTRAINT_NAME, CONSTRAINT_TYPE FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS WHERE 1=1"
                        : "SELECT NULL AS TABLE_CATALOG, OWNER AS TABLE_SCHEMA, TABLE_NAME, OWNER AS CONSTRAINT_SCHEMA, CONSTRAINT_NAME, " + constraintType + " AS CONSTRAINT_TYPE FROM ALL_CONSTRAINTS WHERE OWNER <> '__recyclebin'");
                    if (name == "Keys") sql.Append(mysql ? " AND CONSTRAINT_TYPE IN ('PRIMARY KEY', 'UNIQUE')" : " AND CONSTRAINT_TYPE IN ('P', 'U')");
                    filters = mysql ? new[] { "TABLE_SCHEMA", null, "TABLE_NAME", "CONSTRAINT_NAME", "CONSTRAINT_TYPE" } : new[] { null, "OWNER", "TABLE_NAME", "CONSTRAINT_NAME", constraintType };
                    order += ", 4, 5";
                    break;
                case "ConstraintColumns":
                    sql.Append(mysql
                        ? "SELECT TABLE_SCHEMA AS TABLE_CATALOG, NULL AS TABLE_SCHEMA, TABLE_NAME, NULL AS CONSTRAINT_SCHEMA, CONSTRAINT_NAME, COLUMN_NAME, ORDINAL_POSITION FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE WHERE 1=1"
                        : "SELECT NULL AS TABLE_CATALOG, OWNER AS TABLE_SCHEMA, TABLE_NAME, OWNER AS CONSTRAINT_SCHEMA, CONSTRAINT_NAME, COLUMN_NAME, POSITION AS ORDINAL_POSITION FROM ALL_CONS_COLUMNS WHERE OWNER <> '__recyclebin'");
                    filters = mysql ? new[] { "TABLE_SCHEMA", null, "TABLE_NAME", "CONSTRAINT_NAME", "COLUMN_NAME" } : new[] { null, "OWNER", "TABLE_NAME", "CONSTRAINT_NAME", "COLUMN_NAME" };
                    order += ", 4, 5, 7";
                    break;
                case "ForeignKeys":
                    sql.Append(mysql
                        ? "SELECT r.CONSTRAINT_SCHEMA AS TABLE_CATALOG, NULL AS TABLE_SCHEMA, r.TABLE_NAME, NULL AS CONSTRAINT_SCHEMA, r.CONSTRAINT_NAME, r.UNIQUE_CONSTRAINT_SCHEMA AS REFERENCED_TABLE_CATALOG, NULL AS REFERENCED_TABLE_SCHEMA, r.REFERENCED_TABLE_NAME, NULL AS REFERENCED_CONSTRAINT_SCHEMA, r.UNIQUE_CONSTRAINT_NAME AS REFERENCED_CONSTRAINT_NAME, r.UPDATE_RULE, r.DELETE_RULE FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS r WHERE 1=1"
                        : "SELECT NULL AS TABLE_CATALOG, c.OWNER AS TABLE_SCHEMA, c.TABLE_NAME, c.OWNER AS CONSTRAINT_SCHEMA, c.CONSTRAINT_NAME, NULL AS REFERENCED_TABLE_CATALOG, p.OWNER AS REFERENCED_TABLE_SCHEMA, p.TABLE_NAME AS REFERENCED_TABLE_NAME, p.OWNER AS REFERENCED_CONSTRAINT_SCHEMA, p.CONSTRAINT_NAME AS REFERENCED_CONSTRAINT_NAME, 'NO ACTION' AS UPDATE_RULE, c.DELETE_RULE FROM ALL_CONSTRAINTS c JOIN ALL_CONSTRAINTS p ON p.OWNER = c.R_OWNER AND p.CONSTRAINT_NAME = c.R_CONSTRAINT_NAME WHERE c.CONSTRAINT_TYPE = 'R' AND c.OWNER <> '__recyclebin'");
                    filters = mysql ? new[] { "r.CONSTRAINT_SCHEMA", null, "r.TABLE_NAME", "r.CONSTRAINT_NAME" } : new[] { null, "c.OWNER", "c.TABLE_NAME", "c.CONSTRAINT_NAME" };
                    order += ", 4, 5";
                    break;
                case "ForeignKeyColumns":
                    sql.Append(mysql
                        ? "SELECT k.TABLE_SCHEMA AS TABLE_CATALOG, NULL AS TABLE_SCHEMA, k.TABLE_NAME, NULL AS CONSTRAINT_SCHEMA, k.CONSTRAINT_NAME, k.COLUMN_NAME, k.ORDINAL_POSITION, k.REFERENCED_TABLE_SCHEMA AS REFERENCED_TABLE_CATALOG, NULL AS REFERENCED_TABLE_SCHEMA, k.REFERENCED_TABLE_NAME, k.REFERENCED_COLUMN_NAME FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE k JOIN INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS r ON r.CONSTRAINT_SCHEMA = k.CONSTRAINT_SCHEMA AND r.CONSTRAINT_NAME = k.CONSTRAINT_NAME AND r.TABLE_NAME = k.TABLE_NAME WHERE k.REFERENCED_TABLE_NAME IS NOT NULL"
                        : "SELECT NULL AS TABLE_CATALOG, c.OWNER AS TABLE_SCHEMA, c.TABLE_NAME, c.OWNER AS CONSTRAINT_SCHEMA, c.CONSTRAINT_NAME, fk.COLUMN_NAME, fk.POSITION AS ORDINAL_POSITION, NULL AS REFERENCED_TABLE_CATALOG, p.OWNER AS REFERENCED_TABLE_SCHEMA, p.TABLE_NAME AS REFERENCED_TABLE_NAME, pk.COLUMN_NAME AS REFERENCED_COLUMN_NAME FROM ALL_CONSTRAINTS c JOIN ALL_CONS_COLUMNS fk ON fk.OWNER = c.OWNER AND fk.CONSTRAINT_NAME = c.CONSTRAINT_NAME AND fk.TABLE_NAME = c.TABLE_NAME JOIN ALL_CONSTRAINTS p ON p.OWNER = c.R_OWNER AND p.CONSTRAINT_NAME = c.R_CONSTRAINT_NAME JOIN ALL_CONS_COLUMNS pk ON pk.OWNER = p.OWNER AND pk.CONSTRAINT_NAME = p.CONSTRAINT_NAME AND pk.TABLE_NAME = p.TABLE_NAME AND fk.POSITION = pk.POSITION WHERE c.CONSTRAINT_TYPE = 'R' AND c.OWNER <> '__recyclebin'");
                    filters = mysql ? new[] { "k.TABLE_SCHEMA", null, "k.TABLE_NAME", "k.CONSTRAINT_NAME", "k.COLUMN_NAME" } : new[] { null, "c.OWNER", "c.TABLE_NAME", "c.CONSTRAINT_NAME", "fk.COLUMN_NAME" };
                    order += ", 4, 5, 7";
                    break;
                case "Types":
                    sql.Append("SELECT NULL AS TYPE_CATALOG, OWNER AS TYPE_SCHEMA, TYPE_NAME, TYPECODE AS TYPE_CODE, ATTRIBUTES AS ATTRIBUTE_COUNT, METHODS AS METHOD_COUNT, SUPERTYPE_OWNER AS SUPER_TYPE_SCHEMA, SUPERTYPE_NAME AS SUPER_TYPE_NAME FROM ALL_TYPES WHERE OWNER <> '__recyclebin'");
                    filters = new[] { null, "OWNER", "TYPE_NAME", "TYPECODE" };
                    break;
                case "Sequences":
                    // Sequence values can exceed CLR decimal; text transport preserves the server's exact precision.
                    var cast = mysql ? "CHAR" : "VARCHAR2(80)";
                    sql.Append(mysql ? "SELECT SEQUENCE_OWNER AS SEQUENCE_CATALOG, NULL AS SEQUENCE_SCHEMA, " : "SELECT NULL AS SEQUENCE_CATALOG, SEQUENCE_OWNER AS SEQUENCE_SCHEMA, ");
                    sql.Append("SEQUENCE_NAME, CAST(MIN_VALUE AS ").Append(cast).Append(") AS MINIMUM_VALUE, CAST(MAX_VALUE AS ").Append(cast).Append(") AS MAXIMUM_VALUE, CAST(INCREMENT_BY AS ").Append(cast).Append(mysql ? ") AS INCREMENT, CAST(CACHE_SIZE AS " : ") AS \"INCREMENT\", CAST(CACHE_SIZE AS ").Append(cast).Append(") AS CACHE_SIZE, CAST(LAST_NUMBER AS ").Append(cast).Append(") AS LAST_NUMBER, CYCLE_FLAG, ORDER_FLAG FROM ")
                        .Append(mysql ? "oceanbase.DBA_SEQUENCES" : "ALL_SEQUENCES").Append(" WHERE SEQUENCE_OWNER <> '__recyclebin'");
                    filters = mysql ? new[] { "SEQUENCE_OWNER", null, "SEQUENCE_NAME" } : new[] { null, "SEQUENCE_OWNER", "SEQUENCE_NAME" };
                    break;
                case "Users":
                    sql.Append(mysql ? "SELECT User AS USER_NAME, Host AS HOST FROM mysql.user WHERE 1=1" : "SELECT USERNAME AS USER_NAME, NULL AS HOST FROM ALL_USERS WHERE 1=1");
                    filters = mysql ? new[] { "User", "Host" } : new[] { "USERNAME", null };
                    order = "1, 2";
                    break;
                case "Synonyms":
                    sql.Append("SELECT NULL AS SYNONYM_CATALOG, OWNER AS SYNONYM_SCHEMA, SYNONYM_NAME, NULL AS OBJECT_CATALOG, TABLE_OWNER AS OBJECT_SCHEMA, TABLE_NAME AS OBJECT_NAME, DB_LINK FROM ALL_SYNONYMS WHERE OWNER <> '__recyclebin'");
                    filters = new[] { null, "OWNER", "SYNONYM_NAME" };
                    break;
                default: throw new ArgumentException("Unknown catalog collection.", nameof(name));
            }
            AppendSchemaRestrictions(sql, mysql, restrictions, filters, command);
            return sql.Append(" ORDER BY ").Append(order).ToString();
        }
    }
}
