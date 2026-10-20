using System;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Values;

namespace OceanBaseConnector
{
    public sealed partial class OceanBaseConnection
    {
        private static readonly string[] SchemaCollections = { "MetaDataCollections", "DataSourceInformation", "Tables", "Columns", "Procedures", "ProcedureParameters" };
        private static readonly string[] SchemaTableColumns = { "TABLE_CATALOG", "TABLE_SCHEMA", "TABLE_NAME", "TABLE_TYPE" };
        private static readonly string[] SchemaColumnColumns = { "TABLE_CATALOG", "TABLE_SCHEMA", "TABLE_NAME", "COLUMN_NAME", "ORDINAL_POSITION", "COLUMN_DEFAULT", "IS_NULLABLE", "DATA_TYPE", "CHARACTER_MAXIMUM_LENGTH", "NUMERIC_PRECISION", "NUMERIC_SCALE", "DATETIME_PRECISION" };

        public override DataTable GetSchema() => GetSchema("MetaDataCollections");
        public override DataTable GetSchema(string collectionName) => GetSchema(collectionName, null);
        public override DataTable GetSchema(string collectionName, string?[]? restrictionValues)
        {
            var operation = CreateSchemaOperation(collectionName, restrictionValues, CancellationToken.None);
            try
            {
                using var command = operation.Command;
                if (command == null) return operation.Table;
                using var reader = command.ExecuteReader();
                ValidateSchemaResult(operation.Table, reader);
                while (reader.Read()) AddSchemaRow(operation.Table, reader);
                if (reader.NextResult()) throw new NotSupportedException("A directory query returned multiple results.");
                return operation.Table;
            }
            catch { operation.Table.Dispose(); throw; }
        }

#if NET8_0_OR_GREATER
        public override Task<DataTable> GetSchemaAsync(CancellationToken cancellationToken = default) => GetSchemaAsync("MetaDataCollections", null, cancellationToken);
        public override Task<DataTable> GetSchemaAsync(string collectionName, CancellationToken cancellationToken = default) => GetSchemaAsync(collectionName, null, cancellationToken);
        public override async Task<DataTable> GetSchemaAsync(string collectionName, string?[]? restrictionValues, CancellationToken cancellationToken = default)
        {
            var operation = CreateSchemaOperation(collectionName, restrictionValues, cancellationToken);
            try
            {
                await using var command = operation.Command;
                if (command == null) return operation.Table;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                ValidateSchemaResult(operation.Table, reader);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) AddSchemaRow(operation.Table, reader);
                if (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false)) throw new NotSupportedException("A directory query returned multiple results.");
                return operation.Table;
            }
            catch { operation.Table.Dispose(); throw; }
        }
#endif

        private (DataTable Table, OceanBaseCommand? Command) CreateSchemaOperation(string collectionName, string?[]? restrictions, CancellationToken token)
        {
            if (collectionName == null) throw new ArgumentNullException(nameof(collectionName));
            var index = Array.FindIndex(SchemaCollections, name => string.Equals(name, collectionName, StringComparison.OrdinalIgnoreCase));
            if (index < 0) throw new NotSupportedException("The requested schema collection is not supported.");
            if (restrictions != null && restrictions.Length > (index < 2 ? 0 : index < 4 ? 4 : 5)) throw new ArgumentException("Too many restrictions for this schema collection.", nameof(restrictions));
            var values = restrictions == null ? Array.Empty<string?>() : (string?[])restrictions.Clone();
            RejectAmbientTransaction(); token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var session = GetOpenSession();
                var mysql = session.Info!.Mode == OceanBaseCompatibilityMode.MySql;
                if (index == 0) return (CreateSchemaCollections(), null);
                if (index == 1) return (CreateDataSourceInformation(mysql, session.Info.ServerVersion), null);
                var table = index >= 4 ? CreateRoutineDirectoryTable(index == 5) : CreateDirectoryTable(index == 3);
                // This mode has no such identifier part; a non-null exact restriction cannot match.
                var absent = mysql ? 1 : 0;
                if (values.Length > absent && values[absent] != null || mysql && index >= 4 && values.Length > 4 && values[4] != null) return (table, null);
                var command = new OceanBaseCommand { Connection = this, Transaction = _transaction, CommandTimeout = _builder.CommandTimeout, RequiredSession = session };
                try { command.CommandText = index >= 4 ? RoutineDirectorySql(index == 5, mysql, values, command) : DirectorySql(index == 3, mysql, values, command); return (table, command); }
                catch { command.Dispose(); table.Dispose(); throw; }
            }
        }

        private static DataTable CreateSchemaCollections()
        {
            var table = new DataTable("MetaDataCollections") { Locale = CultureInfo.InvariantCulture };
            table.Columns.Add("CollectionName", typeof(string)); table.Columns.Add("NumberOfRestrictions", typeof(int)); table.Columns.Add("NumberOfIdentifierParts", typeof(int));
            for (var i = 0; i < SchemaCollections.Length; i++) table.Rows.Add(SchemaCollections[i], i < 2 ? 0 : i < 4 ? 4 : 5, i < 2 ? 0 : i == 2 ? 3 : i == 5 ? 5 : 4);
            return table;
        }

        private static DataTable CreateDataSourceInformation(bool mysql, string version)
        {
            var table = new DataTable("DataSourceInformation") { Locale = CultureInfo.InvariantCulture };
            foreach (var name in new[] { "CompositeIdentifierSeparatorPattern", "DataSourceProductName", "DataSourceProductVersion", "DataSourceProductVersionNormalized" }) table.Columns.Add(name, typeof(string));
            table.Columns.Add("GroupByBehavior", typeof(GroupByBehavior)); table.Columns.Add("IdentifierPattern", typeof(string)); table.Columns.Add("IdentifierCase", typeof(IdentifierCase));
            table.Columns.Add("OrderByColumnsInSelect", typeof(bool)); table.Columns.Add("ParameterMarkerFormat", typeof(string)); table.Columns.Add("ParameterMarkerPattern", typeof(string));
            table.Columns.Add("ParameterNameMaxLength", typeof(int)); table.Columns.Add("ParameterNamePattern", typeof(string)); table.Columns.Add("QuotedIdentifierPattern", typeof(string));
            table.Columns.Add("QuotedIdentifierCase", typeof(IdentifierCase)); table.Columns.Add("StatementSeparatorPattern", typeof(string)); table.Columns.Add("StringLiteralPattern", typeof(string));
            table.Columns.Add("SupportedJoinOperators", typeof(SupportedJoinOperators));
            var row = table.NewRow();
            row["CompositeIdentifierSeparatorPattern"] = @"\."; row["DataSourceProductName"] = "OceanBase";
            row["DataSourceProductVersion"] = version; row["GroupByBehavior"] = GroupByBehavior.Unknown;
            row["IdentifierCase"] = mysql ? IdentifierCase.Unknown : IdentifierCase.Insensitive;
            row["QuotedIdentifierCase"] = mysql ? IdentifierCase.Unknown : IdentifierCase.Sensitive;
            row["ParameterMarkerFormat"] = mysql ? "?" : ":{0}";
            row["ParameterMarkerPattern"] = mysql ? @"\?" : @":[A-Za-z_][A-Za-z0-9_$#]*";
            if (!mysql) row["ParameterNamePattern"] = @"[A-Za-z_][A-Za-z0-9_$#]*";
            table.Rows.Add(row); return table;
        }

        private static DataTable CreateDirectoryTable(bool columns)
        {
            var table = new DataTable(columns ? "Columns" : "Tables") { Locale = CultureInfo.InvariantCulture };
            var names = columns ? SchemaColumnColumns : SchemaTableColumns;
            for (var i = 0; i < names.Length; i++)
                table.Columns.Add(names[i], columns && (i == 4 || i >= 9) ? typeof(int) : columns && i == 8 ? typeof(long) : typeof(string));
            return table;
        }

        private static string DirectorySql(bool columns, bool mysql, string?[] restrictions, OceanBaseCommand command)
        {
            var sql = new StringBuilder();
            string?[] filters;
            if (mysql)
            {
                sql.Append("SELECT TABLE_SCHEMA AS TABLE_CATALOG, NULL AS TABLE_SCHEMA, TABLE_NAME, ");
                if (columns)
                    sql.Append("COLUMN_NAME, ORDINAL_POSITION, COLUMN_DEFAULT, IS_NULLABLE, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE, DATETIME_PRECISION FROM INFORMATION_SCHEMA.COLUMNS WHERE 1=1");
                else
                    sql.Append("CASE WHEN TABLE_TYPE = 'BASE TABLE' THEN 'TABLE' ELSE TABLE_TYPE END AS TABLE_TYPE FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE IN ('BASE TABLE', 'VIEW')");
                filters = new[] { "TABLE_SCHEMA", null, "TABLE_NAME", columns ? "COLUMN_NAME" : "CASE WHEN TABLE_TYPE = 'BASE TABLE' THEN 'TABLE' ELSE TABLE_TYPE END" };
            }
            else
            {
                sql.Append("SELECT NULL AS TABLE_CATALOG, OWNER AS TABLE_SCHEMA, ");
                if (columns)
                    sql.Append("TABLE_NAME, COLUMN_NAME, COLUMN_ID AS ORDINAL_POSITION, DATA_DEFAULT AS COLUMN_DEFAULT, CASE WHEN NULLABLE = 'Y' THEN 'YES' WHEN NULLABLE = 'N' THEN 'NO' ELSE NULL END AS IS_NULLABLE, DATA_TYPE, CASE WHEN DATA_TYPE IN ('CHAR', 'VARCHAR', 'VARCHAR2', 'NCHAR', 'NVARCHAR2') THEN CHAR_LENGTH ELSE NULL END AS CHARACTER_MAXIMUM_LENGTH, DATA_PRECISION AS NUMERIC_PRECISION, CASE WHEN DATA_TYPE IN ('NUMBER', 'FLOAT') THEN DATA_SCALE ELSE NULL END AS NUMERIC_SCALE, CASE WHEN DATA_TYPE LIKE 'TIMESTAMP%' THEN DATA_SCALE ELSE NULL END AS DATETIME_PRECISION FROM ALL_TAB_COLUMNS WHERE OWNER <> '__recyclebin'");
                else
                    sql.Append("OBJECT_NAME AS TABLE_NAME, OBJECT_TYPE AS TABLE_TYPE FROM ALL_OBJECTS WHERE OWNER <> '__recyclebin' AND OBJECT_TYPE IN ('TABLE', 'VIEW')");
                filters = new[] { null, "OWNER", columns ? "TABLE_NAME" : "OBJECT_NAME", columns ? "COLUMN_NAME" : "OBJECT_TYPE" };
            }
            AppendSchemaRestrictions(sql, mysql, restrictions, filters, command);
            sql.Append(" ORDER BY TABLE_CATALOG, TABLE_SCHEMA, TABLE_NAME");
            if (columns) sql.Append(", ORDINAL_POSITION");
            return sql.ToString();
        }

        private static void AppendSchemaRestrictions(StringBuilder sql, bool mysql, string?[] restrictions, string?[] filters, OceanBaseCommand command)
        {
            for (var i = 0; i < restrictions.Length; i++)
            {
                if (restrictions[i] == null || filters[i] == null) continue;
                var name = "p" + i.ToString(CultureInfo.InvariantCulture);
                sql.Append(" AND ").Append(filters[i]).Append(" = ").Append(mysql ? "?" : ":" + name);
                command.Parameters.Add(new OceanBaseParameter { ParameterName = name, DbType = DbType.String, Value = restrictions[i] });
            }
        }

        private static void ValidateSchemaResult(DataTable table, DbDataReader reader)
        {
            if (reader.FieldCount != table.Columns.Count) throw new NotSupportedException("Unexpected directory result column count.");
            for (var i = 0; i < reader.FieldCount; i++)
                if (!string.Equals(reader.GetName(i), table.Columns[i].ColumnName, StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException("Unexpected directory result column name.");
        }

        private static void AddSchemaRow(DataTable table, DbDataReader reader)
        {
            var values = new object[table.Columns.Count];
            for (var i = 0; i < values.Length; i++)
            {
                var value = reader.GetValue(i);
                if (value == DBNull.Value) { values[i] = value; continue; }
                var type = table.Columns[i].DataType;
                if (type == typeof(string)) values[i] = value is string text ? text : throw new InvalidCastException("A directory text column returned a non-text value.");
                else
                {
                    var number = value is OceanBaseDecimal exact ? exact.ToDecimal()
                        : value is string text ? DecimalCodec.Parse(text).ToDecimal()
                        : value is decimal || value is sbyte || value is byte || value is short || value is ushort || value is int || value is uint || value is long || value is ulong
                            ? Convert.ToDecimal(value, CultureInfo.InvariantCulture)
                            : throw new InvalidCastException("A directory integer column returned an unsupported numeric representation.");
                    if (decimal.Truncate(number) != number) throw new InvalidCastException("A directory integer column returned a fractional value.");
                    values[i] = type == typeof(int) ? (object)checked((int)number) : checked((long)number);
                }
            }
            table.Rows.Add(values);
        }
    }
}
