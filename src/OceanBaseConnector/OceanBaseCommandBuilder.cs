using System;
using System.Data;
using System.Data.Common;
using System.Globalization;

namespace OceanBaseConnector
{
    /// <summary>Generates single-table parameterized commands from KeyInfo metadata.</summary>
    public sealed class OceanBaseCommandBuilder : DbCommandBuilder
    {
        private OceanBaseCompatibilityMode? _mode;
        public OceanBaseCommandBuilder() { }
        public OceanBaseCommandBuilder(OceanBaseDataAdapter adapter) => DataAdapter = adapter;
        public new OceanBaseDataAdapter? DataAdapter { get => (OceanBaseDataAdapter?)base.DataAdapter; set => base.DataAdapter = value; }
        public new OceanBaseCommand GetInsertCommand() => (OceanBaseCommand)base.GetInsertCommand();
        public new OceanBaseCommand GetInsertCommand(bool useColumnsForParameterNames) => (OceanBaseCommand)base.GetInsertCommand(useColumnsForParameterNames);
        public new OceanBaseCommand GetUpdateCommand() => (OceanBaseCommand)base.GetUpdateCommand();
        public new OceanBaseCommand GetUpdateCommand(bool useColumnsForParameterNames) => (OceanBaseCommand)base.GetUpdateCommand(useColumnsForParameterNames);
        public new OceanBaseCommand GetDeleteCommand() => (OceanBaseCommand)base.GetDeleteCommand();
        public new OceanBaseCommand GetDeleteCommand(bool useColumnsForParameterNames) => (OceanBaseCommand)base.GetDeleteCommand(useColumnsForParameterNames);

        public override string QuotePrefix
        {
            get => base.QuotePrefix.Length != 0 ? base.QuotePrefix : ModeQuote();
            set { ValidateQuote(value); base.QuotePrefix = value; }
        }
        public override string QuoteSuffix
        {
            get => base.QuoteSuffix.Length != 0 ? base.QuoteSuffix : ModeQuote();
            set { ValidateQuote(value); base.QuoteSuffix = value; }
        }
        private static void ValidateQuote(string value)
        {
            if (value != "" && value != "`" && value != "\"") throw new ArgumentException("Use an empty, backtick or double-quote delimiter.", nameof(value));
        }
        private OceanBaseCompatibilityMode Mode()
        {
            var connection = DataAdapter?.SelectCommand?.Connection as OceanBaseConnection;
            return _mode ?? connection?.CompatibilityMode ?? (connection == null ? null : new OceanBaseConnectionStringBuilder(connection.ConnectionString).ExpectedMode)
                ?? throw new InvalidOperationException("An open connection or ExpectedMode is required to determine SQL syntax.");
        }
        private string ModeQuote() => Mode() == OceanBaseCompatibilityMode.MySql ? "`" : "\"";
        private string IdentifierQuote()
        {
            var prefix = QuotePrefix;
            if (prefix != QuoteSuffix) throw new InvalidOperationException("Identifier quote delimiters must match.");
            return prefix;
        }
        public override string QuoteIdentifier(string unquotedIdentifier)
        {
            if (unquotedIdentifier == null) throw new ArgumentNullException(nameof(unquotedIdentifier));
            var quote = IdentifierQuote();
            return quote + unquotedIdentifier.Replace(quote, quote + quote) + quote;
        }
        public override string UnquoteIdentifier(string quotedIdentifier)
        {
            if (quotedIdentifier == null) throw new ArgumentNullException(nameof(quotedIdentifier));
            var quote = IdentifierQuote();
            return quotedIdentifier.Length >= 2 && quotedIdentifier.StartsWith(quote, StringComparison.Ordinal) && quotedIdentifier.EndsWith(quote, StringComparison.Ordinal)
                ? quotedIdentifier.Substring(1, quotedIdentifier.Length - 2).Replace(quote + quote, quote) : quotedIdentifier;
        }
        public override void RefreshSchema() { base.RefreshSchema(); _mode = null; }
        protected override DataTable? GetSchemaTable(DbCommand sourceCommand)
        {
            var connection = sourceCommand.Connection as OceanBaseConnection ?? throw new InvalidOperationException("An OceanBaseConnection is required.");
            _mode = connection.CompatibilityMode ?? throw new InvalidOperationException("Open the source connection before obtaining metadata.");
            var quote = ModeQuote();
            if (QuotePrefix != quote || QuoteSuffix != quote) throw new InvalidOperationException("Identifier delimiters must match the server compatibility mode.");
            var schema = base.GetSchemaTable(sourceCommand);
            if (schema == null) return null;
            var known = false;
            foreach (DataRow row in schema.Rows)
            {
                if (row[SchemaTableColumn.BaseTableName] is string table && table.Length != 0 &&
                    row[SchemaTableColumn.BaseColumnName] is string column && column.Length != 0)
                {
                    if (row.IsNull(SchemaTableColumn.IsKey)) throw new NotSupportedException("Command generation requires proven single-base-table KeyInfo metadata.");
                    known = true;
                    // The framework treats nullable UNIQUE as a row key; only the selected complete row key is usable.
                    row[SchemaTableColumn.IsUnique] = false;
                    // DbCommandBuilder drops catalog qualification when schema is empty.
                    if (_mode == OceanBaseCompatibilityMode.MySql)
                    {
                        row[SchemaTableColumn.BaseSchemaName] = row[SchemaTableOptionalColumn.BaseCatalogName];
                        row[SchemaTableOptionalColumn.BaseCatalogName] = DBNull.Value;
                    }
                }
                else
                {
                    row[SchemaTableColumn.BaseTableName] = DBNull.Value;
                    row[SchemaTableColumn.IsExpression] = true;
                    row[SchemaTableOptionalColumn.IsReadOnly] = true;
                }
            }
            if (!known) throw new NotSupportedException("Command generation requires visible base-column metadata.");
            return schema;
        }
        protected override void ApplyParameterInfo(DbParameter parameter, DataRow row, StatementType statementType, bool whereClause)
        {
            var target = (OceanBaseParameter)parameter;
            target.Precision = 0; target.ClearScaleDeclaration();
            target.IsNullable = row[SchemaTableColumn.AllowDBNull] is bool nullable && nullable;
            if (target.SourceColumnNullMapping) { target.DbType = DbType.Int32; return; }
            var type = (OceanBaseDbType)Convert.ToInt32(row[SchemaTableColumn.ProviderType], CultureInfo.InvariantCulture);
            if (type == OceanBaseDbType.Struct || type == OceanBaseDbType.Array || type == OceanBaseDbType.RefCursor || type == OceanBaseDbType.Blob || type == OceanBaseDbType.Clob)
                throw new NotSupportedException("This column requires an explicit command and binding context.");
            target.OceanBaseDbType = type;
            // FLOAT schema precision is binary digits; bind the exact value as an unconstrained NUMBER.
            if (type == OceanBaseDbType.Decimal && row["DataTypeName"] as string == "FLOAT") return;
            if (type == OceanBaseDbType.Decimal)
            {
                if (!row.IsNull(SchemaTableColumn.NumericPrecision)) target.Precision = Convert.ToByte(row[SchemaTableColumn.NumericPrecision], CultureInfo.InvariantCulture);
                if (!row.IsNull(SchemaTableColumn.NumericScale)) target.DecimalScale = Convert.ToInt32(row[SchemaTableColumn.NumericScale], CultureInfo.InvariantCulture);
            }
        }
        protected override string GetParameterName(int parameterOrdinal) => "p" + parameterOrdinal.ToString(CultureInfo.InvariantCulture);
        protected override string GetParameterName(string parameterName) => parameterName;
        protected override string GetParameterPlaceholder(int parameterOrdinal) => Mode() == OceanBaseCompatibilityMode.MySql ? "?" : ":" + GetParameterName(parameterOrdinal);
        protected override void SetRowUpdatingHandler(DbDataAdapter adapter)
        {
            if (!(adapter is OceanBaseDataAdapter source)) throw new ArgumentException("An OceanBaseDataAdapter is required.", nameof(adapter));
            if (ReferenceEquals(adapter, base.DataAdapter)) source.RowUpdating -= HandleRowUpdating;
            else source.RowUpdating += HandleRowUpdating;
        }
        private void HandleRowUpdating(object? sender, RowUpdatingEventArgs args) => RowUpdatingHandler(args);
    }
}
