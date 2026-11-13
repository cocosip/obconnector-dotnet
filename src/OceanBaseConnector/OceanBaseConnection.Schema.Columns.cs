using System;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Values;

namespace OceanBaseConnector
{
    public sealed partial class OceanBaseConnection
    {
        private static readonly Regex ColumnOnUpdate = new Regex(@"(?:^|\s)on update (CURRENT_TIMESTAMP(?:\([0-6]\))?)(?=\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex ColumnKnownExtra = new Regex(@"(?:^|\s)(?:auto_increment|virtual generated|stored generated|invisible|default_generated)(?=\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static void AppendUnknownColumnMetadata(StringBuilder sql)
        {
            foreach (var name in SchemaColumnSourceColumns) sql.Append(", NULL AS ").Append(name);
        }

        private static DbDataReader ExecuteSchemaReader(OceanBaseCommand command, bool columns, bool mysql)
        {
            try { return command.ExecuteReader(); }
            catch (OceanBaseException error) when (columns && MissingOptionalColumnMetadata(error, mysql))
            {
                UseBasicColumnMetadata(command, mysql);
                return command.ExecuteReader();
            }
        }

#if NET8_0_OR_GREATER
        private static async Task<DbDataReader> ExecuteSchemaReaderAsync(OceanBaseCommand command, bool columns, bool mysql, CancellationToken token)
        {
            try { return await command.ExecuteReaderAsync(token).ConfigureAwait(false); }
            catch (OceanBaseException error) when (columns && MissingOptionalColumnMetadata(error, mysql))
            {
                token.ThrowIfCancellationRequested();
                UseBasicColumnMetadata(command, mysql);
                return await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            }
        }
#endif

        private static bool MissingOptionalColumnMetadata(OceanBaseException error, bool mysql)
        {
            // Server text is deliberately redacted; a successful legacy query establishes the boundary.
            return !error.IsExecutionOutcomeUnknown && error.ServerErrorCode == (mysql ? 1054 : 904);
        }

        private static void UseBasicColumnMetadata(OceanBaseCommand command, bool mysql)
        {
            var restrictions = new string?[4];
            foreach (OceanBaseParameter parameter in command.Parameters)
                restrictions[int.Parse(parameter.ParameterName.Substring(1), CultureInfo.InvariantCulture)] = (string)parameter.Value!;
            command.Parameters.Clear();
            command.CommandText = DirectorySql(true, mysql, restrictions, command, basicColumnMetadata: true);
        }

        private static string? ColumnSourceText(DbDataReader reader, int ordinal)
        {
            var value = reader.GetValue(ordinal);
            return value == DBNull.Value ? null : value is string text ? text
                : throw new InvalidCastException("A directory text column returned a non-text value.");
        }

        private static void CompleteColumnMetadata(object[] values, DbDataReader reader, bool mysql)
        {
            var type = values[7] as string;
            var flags = ColumnSourceText(reader, 15);
            var defaultValue = values[5] as string;
            values[21] = (object?)ColumnSourceText(reader, 18) ?? DBNull.Value;
            values[22] = (object?)ColumnSourceText(reader, 19) ?? DBNull.Value;
            if (mysql)
            {
                values[12] = (object?)Nonempty(ColumnSourceText(reader, 12)) ?? DBNull.Value;
                if (flags != null)
                {
                    var update = ColumnOnUpdate.Match(flags);
                    var rest = ColumnKnownExtra.Replace(ColumnOnUpdate.Replace(flags, " "), " ").Trim();
                    var known = rest.Length == 0;
                    var identity = Regex.IsMatch(flags, @"(?:^|\s)auto_increment(?=\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    var virtualColumn = Regex.IsMatch(flags, @"(?:^|\s)virtual generated(?=\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    var storedColumn = Regex.IsMatch(flags, @"(?:^|\s)stored generated(?=\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    if (identity || known) values[13] = identity;
                    if (virtualColumn || storedColumn || known) values[14] = virtualColumn || storedColumn;
                    if (virtualColumn != storedColumn) values[16] = storedColumn;
                    if (update.Success || known) values[19] = update.Success;
                    if (update.Success) values[20] = update.Groups[1].Value.ToUpperInvariant();
                }
                var expression = Nonempty(ColumnSourceText(reader, 16));
                if (expression != null) { values[14] = true; values[15] = expression; }
            }
            else
            {
                values[12] = (object?)OracleColumnDeclaration(values, reader) ?? DBNull.Value;
                var identity = ColumnSourceText(reader, 17);
                if (identity == "YES" || identity == "NO") values[13] = identity == "YES";
                // NO only rules out virtual generation; the view does not test the stored flag.
                if (flags == "YES")
                {
                    values[14] = true;
                    values[16] = false;
                    values[15] = (object?)Nonempty(defaultValue) ?? DBNull.Value;
                }
                // The inspected Oracle dictionary has no discriminator for ON UPDATE behavior.
            }
            if (values[14] is bool computed && computed)
            {
                values[17] = false;
            }
            else if (mysql && values[14] is bool)
            {
                if (defaultValue != null)
                {
                    values[17] = true;
                    values[18] = (object?)MySqlColumnDefault(type, defaultValue, values[11]) ?? DBNull.Value;
                }
            }
        }

        private static string? Nonempty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

        private static string? MySqlColumnDefault(string? type, string value, object temporalPrecision)
        {
            if (type == null) return null;
            type = type.ToUpperInvariant();
            // COLUMN_DEFAULT does not distinguish a string literal from an expression on this server.
            if ((type == "TIMESTAMP" || type == "DATETIME") && value.Equals("CURRENT_TIMESTAMP", StringComparison.OrdinalIgnoreCase))
                return temporalPrecision is int precision && precision >= 0 && precision <= 6
                    ? "CURRENT_TIMESTAMP" + (precision == 0 ? "" : "(" + precision.ToString(CultureInfo.InvariantCulture) + ")") : null;
            switch (type)
            {
                case "TINYINT": case "SMALLINT": case "MEDIUMINT": case "INT": case "INTEGER": case "BIGINT":
                case "DECIMAL": case "NUMERIC": case "FLOAT": case "DOUBLE": case "REAL": case "YEAR":
                    return Regex.IsMatch(value, @"\A[+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?\z", RegexOptions.CultureInvariant) ? value : null;
                default: return null;
            }
        }

        private static string? OracleColumnDeclaration(object[] values, DbDataReader reader)
        {
            if (!(values[7] is string type)) return null;
            var precision = values[9] is int p ? (int?)p : null;
            var scale = values[10] is int s ? (int?)s : null;
            var length = values[8] is long n ? (long?)n : null;
            var semantics = ColumnSourceText(reader, 14);
            string Dimension(long number) => number.ToString(CultureInfo.InvariantCulture);
            switch (type)
            {
                case "CHAR": case "VARCHAR": case "VARCHAR2":
                    if (semantics == "B") length = ColumnSourceLength(reader);
                    return length > 0 && (semantics == "B" || semantics == "C") ? type + "(" + Dimension(length.Value) + (semantics == "B" ? " BYTE)" : " CHAR)") : null;
                case "NCHAR": case "NVARCHAR2":
                    return length > 0 ? type + "(" + Dimension(length.Value) + ")" : null;
                case "RAW": case "UROWID":
                    length = ColumnSourceLength(reader);
                    return length > 0 ? type + "(" + Dimension(length.Value) + ")" : null;
                case "NUMBER":
                    if (precision.HasValue && (precision < 1 || precision > 38)) return null;
                    if (scale == -85 && !precision.HasValue) scale = null;
                    if (scale.HasValue && (scale < -84 || scale > 127)) return null;
                    if (precision.HasValue && !scale.HasValue) return null;
                    return !precision.HasValue && !scale.HasValue ? "NUMBER" : "NUMBER(" + (precision.HasValue ? Dimension(precision.Value) : "*") + "," + Dimension(scale!.Value) + ")";
                case "FLOAT":
                    return precision.HasValue ? precision >= 1 && precision <= 126 ? "FLOAT(" + Dimension(precision.Value) + ")" : null : "FLOAT";
                case "DATE": case "BINARY_FLOAT": case "BINARY_DOUBLE": case "BLOB": case "CLOB": case "NCLOB": case "JSON":
                    return type;
                default:
                    return Regex.IsMatch(type, @"\ATIMESTAMP\([0-9]\)(?: WITH (?:LOCAL )?TIME ZONE)?\z|\AINTERVAL YEAR\([1-9]\) TO MONTH\z|\AINTERVAL DAY\([1-9]\) TO SECOND\([0-9]\)\z", RegexOptions.CultureInvariant) ? type : null;
            }
        }

        private static long? ColumnSourceLength(DbDataReader reader)
        {
            var value = reader.GetValue(13);
            if (value == DBNull.Value) return null;
            var number = value is OceanBaseDecimal exact ? exact.ToDecimal()
                : value is string text ? DecimalCodec.Parse(text).ToDecimal()
                : value is decimal || value is sbyte || value is byte || value is short || value is ushort || value is int || value is uint || value is long || value is ulong
                    ? Convert.ToDecimal(value, CultureInfo.InvariantCulture)
                    : throw new InvalidCastException("A directory length column returned an unsupported numeric representation.");
            if (decimal.Truncate(number) != number) throw new InvalidCastException("A directory length column returned a fractional value.");
            return checked((long)number);
        }
    }
}
