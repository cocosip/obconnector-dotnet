using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Text;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Values;

namespace OceanBaseConnector.Internal.Sql
{
    internal static class ClientPreparedSql
    {
        internal static bool RequiresMySqlLiteralContext(ParameterBindingPlan bindings, OceanBaseCompatibilityMode mode)
        {
            if (mode == OceanBaseCompatibilityMode.MySql)
                foreach (var parameter in bindings.Parameters)
                    if (parameter.Value is string) return true;
            return false;
        }

        internal static string Build(ParameterBindingPlan bindings, OceanBaseCompatibilityMode mode, MySqlLiteralContext? context = null, Encoding? clientEncoding = null, bool multiStatement = false)
        {
            clientEncoding ??= ResultColumn.Utf8;
            // Reuse all declared-type, exact conversion and packet-budget validation.
            _ = PreparedParameters.Encode(bindings.Parameters, mode, validateOnly: true, clientEncoding: clientEncoding);
            var strings = RequiresMySqlLiteralContext(bindings, mode);
            if (strings && context == null) throw new NotSupportedException("MySQL client string preparation requires a trusted session collation.");
            if (strings) context!.ValidateText(bindings.CommandText);
            var positions = bindings.BindingPositions ?? MySqlPositions(bindings.CommandText, singleStatement: strings && !multiStatement,
                stableCharacterSet: strings && multiStatement);
            if (positions.Length != bindings.Parameters.Length) throw new ArgumentException("Client preparation parameter count mismatch.");
            var sql = new StringBuilder();
            var copied = 0;
            long bytes = clientEncoding.GetByteCount(bindings.CommandText) + 1L;
            for (var i = 0; i < positions.Length; i++)
            {
                var literal = Literal(bindings.Parameters[i], mode, context, clientEncoding, multiStatement);
                bytes += clientEncoding.GetByteCount(literal) + 1L;
                if (bytes > PacketChannel.DefaultMaxPacketSize) throw new ArgumentException("Client prepared SQL exceeds the packet size limit.");
                sql.Append(bindings.CommandText, copied, positions[i] - copied).Append('(').Append(literal).Append(')');
                copied = positions[i] + 1;
            }
            if (bytes > PacketChannel.DefaultMaxPacketSize) throw new ArgumentException("Client prepared SQL exceeds the packet size limit.");
            return sql.Append(bindings.CommandText, copied, bindings.CommandText.Length - copied).ToString();
        }

        private static string Literal(OceanBaseParameter parameter, OceanBaseCompatibilityMode mode, MySqlLiteralContext? context, Encoding clientEncoding, bool multiStatement)
        {
            if (parameter.Direction != ParameterDirection.Input) throw Unsupported();
            var provider = parameter.OceanBaseDbType;
            if (provider.HasValue && provider != OceanBaseDbType.Null && provider != OceanBaseDbType.Decimal &&
                provider != OceanBaseDbType.String && provider != OceanBaseDbType.Binary && provider != OceanBaseDbType.Boolean &&
                (provider < OceanBaseDbType.SByte || provider > OceanBaseDbType.Double)) throw Unsupported();
            var type = parameter.DbType;
            if (type != DbType.String && type != DbType.AnsiString && type != DbType.StringFixedLength && type != DbType.AnsiStringFixedLength &&
                type != DbType.Binary && type != DbType.Boolean && type != DbType.Decimal &&
                type != DbType.SByte && type != DbType.Byte && type != DbType.Int16 && type != DbType.UInt16 &&
                type != DbType.Int32 && type != DbType.UInt32 && type != DbType.Int64 && type != DbType.UInt64 &&
                type != DbType.Single && type != DbType.Double && provider != OceanBaseDbType.Null) throw Unsupported();
            var value = parameter.Value;
            if (value == null || value == DBNull.Value) return "NULL";
            if (value is string text)
            {
                if (mode == OceanBaseCompatibilityMode.MySql) return multiStatement ? context!.MultiStatementLiteral(text) : context!.Literal(text);
                if (text.IndexOf('\0') >= 0) throw new ArgumentException("Oracle client text literals cannot contain NUL.");
                text = text.Replace("'", "''");
                if (clientEncoding.GetByteCount(text) > 4000) throw new ArgumentException("Oracle client text literal exceeds 4000 bytes.");
                return "CAST('" + text + "' AS VARCHAR2(4000))";
            }
            if (value is byte[] binary)
            {
                if (mode == OceanBaseCompatibilityMode.Oracle && binary.Length > 2000) throw new ArgumentException("Oracle client RAW literal exceeds 2000 bytes.");
                var hex = Hex(binary);
                return mode == OceanBaseCompatibilityMode.Oracle ? "HEXTORAW('" + hex + "')" : "X'" + hex + "'";
            }
            if (value is bool boolean) return boolean ? "1" : "0";
            if (type == DbType.Decimal)
            {
                var number = value is OceanBaseDecimal exact ? exact : new OceanBaseDecimal((decimal)value);
                var scale = parameter.DeclaredDecimalScale ?? number.Scale;
                if (mode == OceanBaseCompatibilityMode.MySql) scale = Math.Max(0, scale);
                if (parameter.Precision != 0 || parameter.DeclaredDecimalScale.HasValue) number = DecimalCodec.Constrain(number, parameter.Precision, scale);
                return DecimalCodec.Format(number);
            }
            if (value is double d)
            {
                if (double.IsNaN(d) || double.IsInfinity(d)) throw new ArgumentException("Client numeric literals must be finite.");
                return Floating(d.ToString("R", CultureInfo.InvariantCulture), mode, "d");
            }
            if (value is float f)
            {
                if (float.IsNaN(f) || float.IsInfinity(f)) throw new ArgumentException("Client numeric literals must be finite.");
                return Floating(mode == OceanBaseCompatibilityMode.MySql ? ((double)f).ToString("R", CultureInfo.InvariantCulture)
                    : f.ToString("R", CultureInfo.InvariantCulture), mode, "f");
            }
            // Same exact CLR types as PreparedParameters.WriteValue; never inline a custom ToString.
            if (value is sbyte || value is byte || value is short || value is ushort ||
                value is int || value is uint || value is long || value is ulong)
                return ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture);
            throw new ArgumentException("The client literal value does not match its declared DbType.");
        }

        private static string Floating(string value, OceanBaseCompatibilityMode mode, string suffix)
            => mode == OceanBaseCompatibilityMode.Oracle ? value + suffix : value.IndexOf('E') >= 0 ? value : value + "E0";

        internal static string Hex(byte[] bytes)
        {
            if (bytes.Length > (PacketChannel.DefaultMaxPacketSize - 64) / 2) throw new ArgumentException("Client literal exceeds the packet size limit.");
            const string digits = "0123456789ABCDEF";
            var text = new char[bytes.Length * 2];
            for (var i = 0; i < bytes.Length; i++) { text[i * 2] = digits[bytes[i] >> 4]; text[i * 2 + 1] = digits[bytes[i] & 15]; }
            return new string(text);
        }

        private static int[] MySqlPositions(string sql, bool singleStatement, bool stableCharacterSet)
        {
            // Accept only SQL whose placeholder locations agree with and without backslash escapes.
            if (sql.IndexOf('\0') >= 0) throw Ambiguous();
            var escaped = ScanMySql(sql, true, false, singleStatement, stableCharacterSet);
            foreach (var positions in new[] { ScanMySql(sql, false, false, singleStatement, stableCharacterSet), ScanMySql(sql, true, true, singleStatement, stableCharacterSet), ScanMySql(sql, false, true, singleStatement, stableCharacterSet) })
            {
                if (escaped.Count != positions.Count) throw Ambiguous();
                for (var i = 0; i < escaped.Count; i++) if (escaped[i] != positions[i]) throw Ambiguous();
            }
            return escaped.ToArray();
        }

        private static List<int> ScanMySql(string sql, bool backslash, bool ansiQuotes, bool singleStatement, bool stableCharacterSet)
        {
            var positions = new List<int>();
            var terminated = false;
            var statementStart = true;
            var sessionSet = false;
            for (var i = 0; i < sql.Length; i++)
            {
                var c = sql[i];
                var lineComment = c == '#' || c == '-' && i + 2 < sql.Length && sql[i + 1] == '-' && (sql[i + 2] <= ' ' || char.IsControl(sql[i + 2]));
                var blockComment = c == '/' && i + 1 < sql.Length && sql[i + 1] == '*';
                if (terminated && !char.IsWhiteSpace(c) && !lineComment && !blockComment) throw Ambiguous();
                if (c == '\'' || c == '"' || c == '`')
                {
                    if (stableCharacterSet && sessionSet && c != '\'') throw UnstableCharacterSet();
                    var closed = false;
                    while (++i < sql.Length)
                    {
                        if (backslash && c != '`' && (!ansiQuotes || c != '"') && sql[i] == '\\') { i++; continue; }
                        if (sql[i] != c) continue;
                        if (i + 1 < sql.Length && sql[i + 1] == c) { i++; continue; }
                        closed = true; break;
                    }
                    if (!closed) throw Ambiguous();
                }
                else if (lineComment)
                { while (i + 1 < sql.Length && sql[i + 1] != '\r' && sql[i + 1] != '\n') i++; }
                else if (blockComment)
                {
                    if (i + 2 < sql.Length && (sql[i + 2] == '!' ||
                        (sql[i + 2] == 'M' || sql[i + 2] == 'm') && i + 3 < sql.Length && sql[i + 3] == '!')) throw Ambiguous();
                    var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (end < 0) throw Ambiguous();
                    i = end + 1;
                }
                else if (c == ';') { if (singleStatement) terminated = true; statementStart = true; sessionSet = false; }
                else if (c == '?') positions.Add(i);
                else if (stableCharacterSet && (char.IsLetter(c) || c == '_'))
                {
                    var start = i;
                    while (i + 1 < sql.Length && (char.IsLetterOrDigit(sql[i + 1]) || sql[i + 1] == '_' || sql[i + 1] == '$')) i++;
                    var word = sql.Substring(start, i - start + 1);
                    bool Is(string value) => string.Equals(word, value, StringComparison.OrdinalIgnoreCase);
                    if (statementStart)
                    {
                        sessionSet = Is("SET"); statementStart = false;
                        if (Is("CALL") || Is("DO") || Is("EXECUTE") || Is("PREPARE") || Is("DEALLOCATE") || Is("RESET")) throw UnstableCharacterSet();
                    }
                    // shortcut: SET reads of charset variables also fail closed; parse assignment targets if needed.
                    if (sessionSet && (Is("NAMES") || Is("CHARSET") || Is("CHARACTER") || Is("CHARACTER_SET_CLIENT") ||
                        Is("CHARACTER_SET_CONNECTION") || Is("COLLATION_CONNECTION"))) throw UnstableCharacterSet();
                }
            }
            return positions;
        }

        private static ArgumentException Ambiguous() => new ArgumentException("SQL quoting or comments cannot be safely client prepared.");
        private static NotSupportedException UnstableCharacterSet() => new NotSupportedException("String-bound multi-statements cannot change the parsing character set or invoke dynamic SQL/routines; SET identifiers must be unquoted.");
        private static NotSupportedException Unsupported() => new NotSupportedException("Client preparation supports only ordinary scalar input numbers, strings, binary and NULL.");
    }
}
