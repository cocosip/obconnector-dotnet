using System;
using System.Data;
using System.Globalization;
using System.Text;

namespace OceanBaseConnector
{
    public sealed partial class OceanBaseConnection
    {
        private static DataTable CreateRoutineDirectoryTable(bool parameters, bool derivation = false)
        {
            var table = new DataTable(parameters ? "ProcedureParameters" : "Procedures") { Locale = CultureInfo.InvariantCulture };
            foreach (var name in new[] { "ROUTINE_CATALOG", "ROUTINE_SCHEMA", "ROUTINE_NAME", "PACKAGE_NAME", "OVERLOAD" }) table.Columns.Add(name, typeof(string));
            table.Columns.Add("SUBPROGRAM_ID", typeof(int)); table.Columns.Add("ROUTINE_TYPE", typeof(string));
            if (!parameters) return table;
            table.Columns.Add("PARAMETER_NAME", typeof(string)); table.Columns.Add("ORDINAL_POSITION", typeof(int));
            table.Columns.Add("PARAMETER_MODE", typeof(string)); table.Columns.Add("DATA_TYPE", typeof(string)); table.Columns.Add("DATA_LENGTH", typeof(long));
            foreach (var name in new[] { "NUMERIC_PRECISION", "NUMERIC_SCALE", "DATETIME_PRECISION" }) table.Columns.Add(name, typeof(int));
            foreach (var name in new[] { "TYPE_OWNER", "TYPE_NAME", "TYPE_SUBNAME" }) table.Columns.Add(name, typeof(string));
            if (derivation)
            {
                table.Columns.Add("TYPE_DECLARATION", typeof(string)); table.Columns.Add("CHARACTER_LENGTH", typeof(long));
                table.Columns.Add("DECLARED_PRECISION", typeof(int)); table.Columns.Add("DECLARED_SCALE", typeof(int));
            }
            return table;
        }

        private static string RoutineDirectorySql(bool parameters, bool mysql, string?[] restrictions, OceanBaseCommand command, bool derivation = false, string? predicate = null)
        {
            var sql = new StringBuilder();
            string?[] filters;
            if (mysql)
            {
                sql.Append(parameters
                    ? "SELECT SPECIFIC_SCHEMA AS ROUTINE_CATALOG, NULL AS ROUTINE_SCHEMA, SPECIFIC_NAME AS ROUTINE_NAME, NULL AS PACKAGE_NAME, NULL AS OVERLOAD, NULL AS SUBPROGRAM_ID, ROUTINE_TYPE, PARAMETER_NAME, ORDINAL_POSITION, CASE WHEN ORDINAL_POSITION = 0 THEN 'RETURN' ELSE PARAMETER_MODE END AS PARAMETER_MODE, DATA_TYPE, CHARACTER_OCTET_LENGTH AS DATA_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE, DATETIME_PRECISION, NULL AS TYPE_OWNER, NULL AS TYPE_NAME, NULL AS TYPE_SUBNAME" + (derivation ? ", DTD_IDENTIFIER AS TYPE_DECLARATION, CHARACTER_MAXIMUM_LENGTH AS CHARACTER_LENGTH, NUMERIC_PRECISION AS DECLARED_PRECISION, NUMERIC_SCALE AS DECLARED_SCALE" : "") + " FROM INFORMATION_SCHEMA.PARAMETERS WHERE ROUTINE_TYPE IN ('PROCEDURE', 'FUNCTION')"
                    : "SELECT ROUTINE_SCHEMA AS ROUTINE_CATALOG, NULL AS ROUTINE_SCHEMA, ROUTINE_NAME, NULL AS PACKAGE_NAME, NULL AS OVERLOAD, NULL AS SUBPROGRAM_ID, ROUTINE_TYPE FROM INFORMATION_SCHEMA.ROUTINES WHERE ROUTINE_TYPE IN ('PROCEDURE', 'FUNCTION')");
                filters = new[] { parameters ? "SPECIFIC_SCHEMA" : "ROUTINE_SCHEMA", null, parameters ? "SPECIFIC_NAME" : "ROUTINE_NAME", parameters ? "PARAMETER_NAME" : "ROUTINE_TYPE", null };
            }
            else if (parameters)
            {
                sql.Append("SELECT NULL AS ROUTINE_CATALOG, OWNER AS ROUTINE_SCHEMA, OBJECT_NAME AS ROUTINE_NAME, PACKAGE_NAME, CAST(OVERLOAD AS VARCHAR2(40)) AS OVERLOAD, SUBPROGRAM_ID, NULL AS ROUTINE_TYPE, ARGUMENT_NAME AS PARAMETER_NAME, POSITION AS ORDINAL_POSITION, CASE WHEN POSITION = 0 THEN 'RETURN' WHEN IN_OUT = 'IN' THEN 'IN' WHEN IN_OUT = 'OUT' THEN 'OUT' WHEN IN_OUT IN ('IN/OUT', 'INOUT') THEN 'INOUT' ELSE NULL END AS PARAMETER_MODE, DATA_TYPE, DATA_LENGTH, CASE WHEN DATA_TYPE IN ('NUMBER', 'FLOAT', 'NUMBER_FLOAT') THEN DATA_PRECISION ELSE NULL END AS NUMERIC_PRECISION, CASE WHEN DATA_TYPE IN ('NUMBER', 'FLOAT', 'NUMBER_FLOAT') THEN DATA_SCALE ELSE NULL END AS NUMERIC_SCALE, CASE WHEN SUBSTR(DATA_TYPE, 1, 9) = 'TIMESTAMP' THEN DATA_SCALE ELSE NULL END AS DATETIME_PRECISION, TYPE_OWNER, TYPE_NAME, TYPE_SUBNAME" + (derivation ? ", NULL AS TYPE_DECLARATION, CHAR_LENGTH AS CHARACTER_LENGTH, DATA_PRECISION AS DECLARED_PRECISION, DATA_SCALE AS DECLARED_SCALE" : "") + " FROM ALL_ARGUMENTS WHERE OWNER <> '__recyclebin' AND DATA_LEVEL = 0" + (derivation ? "" : " AND DATA_TYPE IS NOT NULL"));
                filters = new[] { null, "OWNER", "OBJECT_NAME", "ARGUMENT_NAME", "PACKAGE_NAME" };
            }
            else
            {
                const string routineName = "CASE WHEN OBJECT_TYPE = 'PACKAGE' THEN PROCEDURE_NAME ELSE OBJECT_NAME END";
                const string packageName = "CASE WHEN OBJECT_TYPE = 'PACKAGE' THEN OBJECT_NAME ELSE NULL END";
                const string routineType = "CASE WHEN OBJECT_TYPE IN ('PROCEDURE', 'FUNCTION') THEN OBJECT_TYPE ELSE NULL END";
                sql.Append("SELECT NULL AS ROUTINE_CATALOG, OWNER AS ROUTINE_SCHEMA, ").Append(routineName).Append(" AS ROUTINE_NAME, ")
                    .Append(packageName).Append(" AS PACKAGE_NAME, CAST(OVERLOAD AS VARCHAR2(40)) AS OVERLOAD, SUBPROGRAM_ID, ").Append(routineType)
                    .Append(" AS ROUTINE_TYPE FROM ALL_PROCEDURES WHERE OWNER <> '__recyclebin' AND (OBJECT_TYPE IN ('PROCEDURE', 'FUNCTION') OR (OBJECT_TYPE = 'PACKAGE' AND PROCEDURE_NAME IS NOT NULL))");
                filters = new[] { null, "OWNER", routineName, routineType, packageName };
            }
            if (predicate != null) sql.Append(" AND (").Append(predicate).Append(')');
            AppendSchemaRestrictions(sql, mysql, restrictions, filters, command);
            sql.Append(" ORDER BY ROUTINE_CATALOG, ROUTINE_SCHEMA, PACKAGE_NAME, ROUTINE_NAME, ROUTINE_TYPE, SUBPROGRAM_ID, OVERLOAD");
            if (parameters) sql.Append(", ORDINAL_POSITION");
            return sql.ToString();
        }
    }
}
