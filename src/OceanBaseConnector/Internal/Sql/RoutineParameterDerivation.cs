using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using OceanBaseConnector.Internal.Protocol;

namespace OceanBaseConnector.Internal.Sql
{
    internal static class RoutineParameterDerivation
    {
        internal const int MaximumParameters = 1024;
        private static readonly Regex Dimensions = new Regex(@"\([0-9]+(?:,\s*[0-9]+)?\)", RegexOptions.CultureInvariant);
        private static readonly Regex NumericDeclaration = new Regex(@"^([A-Z]+)(?:\(([0-9]+)(?:,\s*([0-9]+))?\))?((?: (?:UNSIGNED|ZEROFILL))*)$", RegexOptions.CultureInvariant);

        internal static string[] ParseName(string text, bool mysql)
        {
            if (string.IsNullOrEmpty(text) || ResultColumn.Utf8.GetByteCount(text) > 4096) throw Invalid("Invalid routine name.");
            var parts = new List<string>(); var position = 0;
            while (position < text.Length)
            {
                var value = new StringBuilder(); var quoted = text[position] == (mysql ? '`' : '"');
                if (quoted)
                {
                    var quote = text[position++]; var closed = false;
                    while (position < text.Length)
                    {
                        var c = text[position++];
                        if (char.IsControl(c)) throw Invalid("Invalid routine identifier.");
                        if (c != quote) { value.Append(c); continue; }
                        if (position < text.Length && text[position] == quote) { value.Append(quote); position++; continue; }
                        closed = true; break;
                    }
                    if (!closed) throw Invalid("Unterminated routine identifier.");
                }
                else
                {
                    var start = position;
                    while (position < text.Length && text[position] != '.')
                    {
                        var c = text[position++];
                        if (mysql ? !(char.IsLetterOrDigit(c) || c == '_' || c == '$')
                            : !(IsNamePart(c) && (position != start + 1 || IsNameStart(c)))) throw Invalid("Invalid routine identifier.");
                        value.Append(c);
                    }
                }
                if (value.Length == 0 || ResultColumn.Utf8.GetByteCount(value.ToString()) > 512) throw Invalid("Invalid routine identifier length.");
                parts.Add(quoted || mysql ? value.ToString() : value.ToString().ToUpperInvariant());
                if (parts.Count > (mysql ? 2 : 3)) throw Invalid("Too many routine identifier parts.");
                if (position == text.Length) return parts.ToArray();
                if (text[position++] != '.' || position == text.Length) throw Invalid("Invalid qualified routine name.");
            }
            throw Invalid("Invalid routine name.");
        }

        internal static DataRow SelectRoutine(DataTable routines, string? overload, bool mysql)
        {
            if (mysql && overload != null) throw Invalid("MySQL routines do not have an Oracle overload selector.");
            if (overload != null && (overload.Length == 0 || overload.Length > 40 || overload.IndexOf('\0') >= 0)) throw Invalid("Invalid overload selector.");
            DataRow? selected = null;
            foreach (DataRow row in routines.Rows)
            {
                RequiredText(row, mysql ? "ROUTINE_CATALOG" : "ROUTINE_SCHEMA"); RequiredText(row, "ROUTINE_NAME");
                var kind = Text(row, "ROUTINE_TYPE");
                if (kind != null && kind != "PROCEDURE" && kind != "FUNCTION" || mysql && kind == null) throw Invalid("Invalid routine kind.");
                if (!mysql && (!Integer(row, "SUBPROGRAM_ID").HasValue || Integer(row, "SUBPROGRAM_ID") < 0)) throw Invalid("Missing routine subprogram identity.");
                if (overload != null && !string.Equals(overload, Text(row, "OVERLOAD"), StringComparison.Ordinal)) continue;
                if (selected != null) throw Invalid("The routine identity is ambiguous; qualify the name or select an overload.");
                selected = row;
            }
            return selected ?? throw Invalid("No matching visible routine was found.");
        }

        internal static DataRow[] OrderParameters(DataTable arguments, DataRow routine, bool mysql)
        {
            if (arguments.Rows.Count > MaximumParameters) throw Invalid("The routine has too many parameters.");
            var rows = new List<DataRow>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DataRow row in arguments.Rows)
            {
                foreach (var identity in new[] { "ROUTINE_CATALOG", "ROUTINE_SCHEMA", "ROUTINE_NAME", "PACKAGE_NAME", "OVERLOAD", "SUBPROGRAM_ID" })
                    if (!Equals(row[identity], routine[identity])) throw Invalid("Argument metadata does not match the selected routine.");
                if (mysql && !Equals(row["ROUTINE_TYPE"], routine["ROUTINE_TYPE"])) throw Invalid("Argument routine kind changed.");
                var ordinal = Integer(row, "ORDINAL_POSITION") ?? throw Invalid("Missing argument position.");
                var direction = RequiredText(row, "PARAMETER_MODE");
                if (ordinal < 0 || ordinal > MaximumParameters || (ordinal == 0) != (direction == "RETURN")) throw Invalid("Invalid argument position or return direction.");
                var name = ordinal == 0 ? "RETURN_VALUE" : RequiredText(row, "PARAMETER_NAME");
                if (!ValidParameterName(name, mysql) || !names.Add(name)) throw Invalid("Argument names cannot be represented by the current binding syntax.");
                RequiredText(row, "DATA_TYPE"); rows.Add(row);
            }
            rows.Sort((a, b) => Integer(a, "ORDINAL_POSITION")!.Value.CompareTo(Integer(b, "ORDINAL_POSITION")!.Value));
            var returned = rows.Count > 0 && Integer(rows[0], "ORDINAL_POSITION") == 0;
            var expected = returned ? 0 : 1;
            foreach (var row in rows) if (Integer(row, "ORDINAL_POSITION") != expected++) throw Invalid("Argument positions are duplicated or incomplete.");
            var kind = Text(routine, "ROUTINE_TYPE");
            if (kind == "FUNCTION" && !returned || kind == "PROCEDURE" && returned) throw Invalid("Routine return metadata is incomplete or inconsistent.");
            return rows.ToArray();
        }

        internal static bool RequiresCharacterLength(DataRow row)
        {
            var raw = RequiredText(row, "DATA_TYPE").Trim().ToUpperInvariant();
            if (raw.Length > 512) throw Invalid("Invalid argument type length.");
            var type = Dimensions.Replace(raw, "");
            return type == "VARCHAR2" || type == "VARCHAR" || type == "CHAR" || type == "NCHAR" || type == "NVARCHAR2";
        }

        internal static OceanBaseParameter Map(DataRow row, bool mysql, bool tinyAsBoolean, OceanBaseComplexType? complex = null)
        {
            var rawType = RequiredText(row, "DATA_TYPE").Trim().ToUpperInvariant();
            if (rawType.Length > 512) throw Invalid("Invalid argument type length.");
            var type = Dimensions.Replace(rawType, "");
            var parameter = new OceanBaseParameter { ParameterName = Integer(row, "ORDINAL_POSITION") == 0 ? "RETURN_VALUE" : RequiredText(row, "PARAMETER_NAME"), IsNullable = true, Value = DBNull.Value };
            switch (RequiredText(row, "PARAMETER_MODE"))
            {
                case "IN": parameter.Direction = ParameterDirection.Input; break;
                case "OUT": parameter.Direction = ParameterDirection.Output; break;
                case "INOUT": parameter.Direction = ParameterDirection.InputOutput; break;
                case "RETURN": parameter.Direction = ParameterDirection.ReturnValue; break;
                default: throw Invalid("Unsupported argument direction.");
            }
            if (Text(row, "TYPE_SUBNAME") != null) throw Invalid("Package-local PL/SQL argument types are not supported.");
            if ((Text(row, "TYPE_OWNER") == null) != (Text(row, "TYPE_NAME") == null)) throw Invalid("Named argument type identity is incomplete.");
            if (complex != null)
            {
                if (mysql || (type != "OBJECT" && type != "TABLE" && type != "VARRAY") || complex.IsCollection != (type != "OBJECT")) throw Invalid("Named argument type kind does not match its descriptor.");
                parameter.ComplexType = complex; return parameter;
            }
            bool sized = false, binary = false;
            if (mysql)
            {
                switch (type)
                {
                    case "TINYINT":
                    case "SMALLINT":
                    case "MEDIUMINT":
                    case "INT":
                    case "INTEGER":
                    case "BIGINT":
                        var declaration = ParseNumericDeclaration(row, type);
                        if (declaration.Scale.HasValue) throw Invalid("Integer declarations cannot have a fractional dimension.");
                        parameter.OceanBaseDbType = type == "TINYINT" ? tinyAsBoolean && declaration.Width == 1 ? OceanBaseDbType.Boolean : declaration.Unsigned ? OceanBaseDbType.Byte : OceanBaseDbType.SByte
                            : type == "SMALLINT" ? declaration.Unsigned ? OceanBaseDbType.UInt16 : OceanBaseDbType.Int16
                            : type == "BIGINT" ? declaration.Unsigned ? OceanBaseDbType.UInt64 : OceanBaseDbType.Int64
                            : declaration.Unsigned ? OceanBaseDbType.UInt32 : OceanBaseDbType.Int32; break;
                    case "DECIMAL":
                    case "NUMERIC":
                        var numeric = ParseNumericDeclaration(row, type);
                        if (numeric.Unsigned || numeric.Width.HasValue && (numeric.Width != Integer(row, "NUMERIC_PRECISION") || (numeric.Scale ?? 0) != Integer(row, "NUMERIC_SCALE")))
                            throw Invalid("Unsigned or conflicting decimal metadata cannot be represented.");
                        parameter.OceanBaseDbType = OceanBaseDbType.Decimal; break;
                    case "FLOAT":
                    case "DOUBLE":
                    case "REAL":
                        var floating = ParseNumericDeclaration(row, type);
                        if (floating.Unsigned || floating.Width.HasValue || floating.Scale.HasValue) throw Invalid("Floating point declaration attributes cannot be represented.");
                        parameter.OceanBaseDbType = type == "FLOAT" ? OceanBaseDbType.Single : OceanBaseDbType.Double; break;
                    case "BIT":
                        var bits = ParseNumericDeclaration(row, type);
                        if (bits.Unsigned || !bits.Width.HasValue || bits.Width < 1 || bits.Width > 64 || bits.Scale.HasValue) throw Invalid("Invalid BIT declaration.");
                        parameter.OceanBaseDbType = OceanBaseDbType.Bit; parameter.Size = bits.Width.Value; break;
                    case "YEAR":
                        var year = ParseNumericDeclaration(row, type);
                        if (year.Unsigned || year.Width.HasValue && year.Width != 4 || year.Scale.HasValue) throw Invalid("The YEAR declaration is not supported.");
                        parameter.OceanBaseDbType = OceanBaseDbType.Year; break;
                    case "JSON": parameter.OceanBaseDbType = OceanBaseDbType.Json; break;
                    case "DATE": parameter.OceanBaseDbType = OceanBaseDbType.Date; break;
                    case "DATETIME": case "TIMESTAMP": parameter.OceanBaseDbType = OceanBaseDbType.DateTime; SetScale(parameter, Integer(row, "DATETIME_PRECISION"), 6); break;
                    case "TIME": parameter.OceanBaseDbType = OceanBaseDbType.Time; SetScale(parameter, Integer(row, "DATETIME_PRECISION"), 6); break;
                    case "CHAR": case "VARCHAR": case "TINYTEXT": case "TEXT": case "MEDIUMTEXT": case "LONGTEXT": case "ENUM": case "SET": parameter.OceanBaseDbType = OceanBaseDbType.String; sized = true; break;
                    case "BINARY": case "VARBINARY": case "TINYBLOB": case "BLOB": case "MEDIUMBLOB": case "LONGBLOB": parameter.OceanBaseDbType = OceanBaseDbType.Binary; sized = binary = true; break;
                    case "GEOMETRY":
                    case "POINT":
                    case "LINESTRING":
                    case "POLYGON":
                    case "MULTIPOINT":
                    case "MULTILINESTRING":
                    case "MULTIPOLYGON":
                    case "GEOMETRYCOLLECTION":
                    case "GEOMCOLLECTION":
                        // Spatial arguments carry SRID + WKB; catalogue length is not a fixed Size.
                        parameter.OceanBaseDbType = OceanBaseDbType.Binary; break;
                    default: throw Invalid("The MySQL argument type is not supported.");
                }
            }
            else
            {
                switch (type)
                {
                    case "NUMBER": case "DECIMAL": case "NUMERIC": case "INTEGER": parameter.OceanBaseDbType = OceanBaseDbType.Decimal; break;
                    case "FLOAT":
                    case "NUMBER_FLOAT":
                        var binaryPrecision = Integer(row, "NUMERIC_PRECISION"); var floatingScale = Integer(row, "NUMERIC_SCALE");
                        if (rawType != type || binaryPrecision.HasValue && binaryPrecision != -1 && (binaryPrecision < 1 || binaryPrecision > 126) ||
                            floatingScale.HasValue && floatingScale != -85 && floatingScale != -1)
                            throw Invalid("Oracle FLOAT requires binary precision 1 through 126 or unknown, and variable scale.");
                        // Binary precision is not a decimal parameter constraint; the server applies the FLOAT declaration.
                        parameter.OceanBaseDbType = OceanBaseDbType.Decimal; return parameter;
                    case "BINARY_FLOAT": parameter.OceanBaseDbType = OceanBaseDbType.Single; break;
                    case "BINARY_DOUBLE": parameter.OceanBaseDbType = OceanBaseDbType.Double; break;
                    case "PLS_INTEGER": case "BINARY_INTEGER": parameter.OceanBaseDbType = OceanBaseDbType.Int32; break;
                    case "VARCHAR2": case "VARCHAR": case "CHAR": parameter.OceanBaseDbType = OceanBaseDbType.String; sized = true; break;
                    case "NCHAR": parameter.OceanBaseDbType = OceanBaseDbType.NChar; sized = true; break;
                    case "NVARCHAR2": parameter.OceanBaseDbType = OceanBaseDbType.NVarchar2; sized = true; break;
                    case "RAW": parameter.OceanBaseDbType = OceanBaseDbType.Binary; sized = binary = true; break;
                    case "DATE": parameter.OceanBaseDbType = OceanBaseDbType.OracleDate; break;
                    case "TIMESTAMP":
                    case "TIMESTAMP WITH TIME ZONE":
                    case "TIMESTAMP WITH LOCAL TIME ZONE":
                        parameter.OceanBaseDbType = type == "TIMESTAMP" ? OceanBaseDbType.Timestamp : type == "TIMESTAMP WITH TIME ZONE" ? OceanBaseDbType.TimestampWithTimeZone : OceanBaseDbType.TimestampLocalTimeZone;
                        SetScale(parameter, Integer(row, "DATETIME_PRECISION"), 9); break;
                    case "INTERVAL YEAR TO MONTH":
                    case "INTERVAL DAY TO SECOND":
                        parameter.OceanBaseDbType = type == "INTERVAL YEAR TO MONTH" ? OceanBaseDbType.IntervalYearToMonth : OceanBaseDbType.IntervalDayToSecond;
                        parameter.Precision = Precision(Integer(row, "DECLARED_PRECISION"), 9);
                        SetScale(parameter, Integer(row, "DECLARED_SCALE"), type == "INTERVAL YEAR TO MONTH" ? 0 : 9); break;
                    case "REF CURSOR":
                        parameter.OceanBaseDbType = OceanBaseDbType.RefCursor;
                        if (parameter.Direction != ParameterDirection.Output && parameter.Direction != ParameterDirection.ReturnValue) throw Invalid("Input REF CURSOR parameters are not supported."); break;
                    case "ROWID":
                    case "UROWID":
                        parameter.OceanBaseDbType = OceanBaseDbType.Urowid; break;
                    case "BLOB": parameter.OceanBaseDbType = OceanBaseDbType.Blob; break;
                    case "CLOB": parameter.OceanBaseDbType = OceanBaseDbType.Clob; break;
                    default: throw Invalid("The Oracle argument type requires an unsupported or missing type descriptor.");
                }
            }
            if (parameter.DbType == DbType.Decimal)
            {
                var precision = Integer(row, "NUMERIC_PRECISION"); var scale = Integer(row, "NUMERIC_SCALE");
                parameter.Precision = Precision(precision, mysql ? 65 : 38);
                // Oracle ALL_ARGUMENTS uses -85 for an unconstrained NUMBER, not a declared scale.
                if (!mysql && !precision.HasValue && scale == -85) scale = null;
                if (scale.HasValue)
                {
                    if (scale < (mysql ? 0 : -84) || scale > (mysql ? 30 : 127) || mysql && parameter.Precision != 0 && scale > parameter.Precision) throw Invalid("Decimal argument scale cannot be represented.");
                    parameter.DecimalScale = scale;
                }
                if (mysql && (!precision.HasValue || !scale.HasValue)) throw Invalid("MySQL decimal metadata is incomplete.");
            }
            if (sized)
            {
                var length = row[binary ? "DATA_LENGTH" : "CHARACTER_LENGTH"];
                if (!(length is long count) || count < 0 || count > int.MaxValue) throw Invalid("Argument length cannot be represented by Size.");
                parameter.Size = (int)count;
            }
            return parameter;
        }

        private static (bool Unsigned, int? Width, int? Scale) ParseNumericDeclaration(DataRow row, string type)
        {
            var text = RequiredText(row, "TYPE_DECLARATION").Trim().ToUpperInvariant();
            if (text.Length > 1024) throw Invalid("Invalid numeric declaration length.");
            var match = NumericDeclaration.Match(text);
            if (!match.Success || match.Groups[1].Value != type) throw Invalid("Numeric declaration does not match DATA_TYPE.");
            int? width = null, scale = null;
            if (match.Groups[2].Success)
            {
                if (!int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var count)) throw Invalid("Invalid numeric declaration width.");
                width = count;
            }
            if (match.Groups[3].Success)
            {
                if (!int.TryParse(match.Groups[3].Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var count)) throw Invalid("Invalid numeric declaration scale.");
                scale = count;
            }
            var flags = match.Groups[4].Value;
            var tokens = flags.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (new HashSet<string>(tokens, StringComparer.Ordinal).Count != tokens.Length) throw Invalid("Repeated numeric declaration attributes.");
            return (flags.Length != 0, width, scale);
        }
        private static byte Precision(int? precision, int maximum)
        {
            if (!precision.HasValue) return 0;
            if (precision < 1 || precision > maximum) throw Invalid("Argument precision cannot be represented.");
            return (byte)precision.Value;
        }
        private static void SetScale(OceanBaseParameter parameter, int? scale, int maximum)
        {
            if (!scale.HasValue) return;
            if (scale < 0 || scale > maximum) throw Invalid("Argument scale cannot be represented.");
            parameter.Scale = (byte)scale.Value;
        }
        internal static string? Text(DataRow row, string column) => row.IsNull(column) ? null : row[column] as string ?? throw Invalid("Invalid directory text.");
        internal static string RequiredText(DataRow row, string column) => Text(row, column) is string text && text.Length != 0 ? text : throw Invalid("Missing directory text: " + column);
        internal static int? Integer(DataRow row, string column) => row.IsNull(column) ? (int?)null : row[column] is int value ? value : throw Invalid("Invalid directory integer.");
        private static bool ValidParameterName(string name, bool mysql)
        {
            if (name.Length == 0 || ResultColumn.Utf8.GetByteCount(name) > 512) return false;
            if (mysql)
            {
                foreach (var c in name) if (char.IsControl(c)) return false;
                return true;
            }
            if (!IsNameStart(name[0])) return false;
            for (var i = 1; i < name.Length; i++) if (!IsNamePart(name[i])) return false;
            return true;
        }
        private static bool IsNameStart(char c) => c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c == '_';
        private static bool IsNamePart(char c) => IsNameStart(c) || c >= '0' && c <= '9' || c == '$' || c == '#';
        private static NotSupportedException Invalid(string message) => new NotSupportedException(message);
    }
}
