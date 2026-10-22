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
        internal static string Build(ParameterBindingPlan bindings, OceanBaseCompatibilityMode mode)
        {
            var positions = bindings.BindingPositions ?? MySqlPositions(bindings.CommandText);
            if (positions.Length != bindings.Parameters.Length) throw new ArgumentException("Client preparation parameter count mismatch.");
            // Reuse all declared-type, exact conversion and packet-budget validation.
            _ = PreparedParameters.Encode(bindings.Parameters, mode, validateOnly: true);
            var sql = new StringBuilder();
            var copied = 0;
            long bytes = ResultColumn.Utf8.GetByteCount(bindings.CommandText) + 1L;
            for (var i = 0; i < positions.Length; i++)
            {
                var literal = Literal(bindings.Parameters[i], mode);
                bytes += ResultColumn.Utf8.GetByteCount(literal) + 1L;
                if (bytes > PacketChannel.DefaultMaxPacketSize) throw new ArgumentException("Client prepared SQL exceeds the packet size limit.");
                sql.Append(bindings.CommandText, copied, positions[i] - copied).Append('(').Append(literal).Append(')');
                copied = positions[i] + 1;
            }
            if (bytes > PacketChannel.DefaultMaxPacketSize) throw new ArgumentException("Client prepared SQL exceeds the packet size limit.");
            return sql.Append(bindings.CommandText, copied, bindings.CommandText.Length - copied).ToString();
        }

        private static string Literal(OceanBaseParameter parameter, OceanBaseCompatibilityMode mode)
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
                // shortcut: no trusted MySQL connection collation, enable strings after session tracking exists.
                if (mode == OceanBaseCompatibilityMode.MySql) throw new NotSupportedException("MySQL client string preparation requires a trusted session collation.");
                if (text.IndexOf('\0') >= 0) throw new ArgumentException("Oracle client text literals cannot contain NUL.");
                text = text.Replace("'", "''");
                if (ResultColumn.Utf8.GetByteCount(text) > 4000) throw new ArgumentException("Oracle client text literal exceeds 4000 bytes.");
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
                var scale = parameter.DeclaredScale ?? number.Scale;
                if (mode == OceanBaseCompatibilityMode.MySql) scale = Math.Max(0, scale);
                if (parameter.Precision != 0 || parameter.DeclaredScale.HasValue) number = DecimalCodec.Constrain(number, parameter.Precision, scale);
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

        private static string Hex(byte[] bytes)
        {
            if (bytes.Length > (PacketChannel.DefaultMaxPacketSize - 64) / 2) throw new ArgumentException("Client literal exceeds the packet size limit.");
            const string digits = "0123456789ABCDEF";
            var text = new char[bytes.Length * 2];
            for (var i = 0; i < bytes.Length; i++) { text[i * 2] = digits[bytes[i] >> 4]; text[i * 2 + 1] = digits[bytes[i] & 15]; }
            return new string(text);
        }

        private static int[] MySqlPositions(string sql)
        {
            // Accept only SQL whose placeholder locations agree with and without backslash escapes.
            var escaped = ScanMySql(sql, true);
            var plain = ScanMySql(sql, false);
            var ansi = ScanMySql(sql, true, ansiQuotes: true);
            if (escaped.Count != plain.Count || escaped.Count != ansi.Count) throw Ambiguous();
            for (var i = 0; i < escaped.Count; i++) if (escaped[i] != plain[i] || escaped[i] != ansi[i]) throw Ambiguous();
            return escaped.ToArray();
        }

        private static List<int> ScanMySql(string sql, bool backslash, bool ansiQuotes = false)
        {
            var positions = new List<int>();
            for (var i = 0; i < sql.Length; i++)
            {
                var c = sql[i];
                if (c == '\0') throw Ambiguous();
                if (c == '\'' || c == '"' || c == '`')
                {
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
                else if (c == '#' || c == '-' && i + 2 < sql.Length && sql[i + 1] == '-' && (sql[i + 2] <= ' ' || char.IsControl(sql[i + 2])))
                { while (i + 1 < sql.Length && sql[i + 1] != '\r' && sql[i + 1] != '\n') i++; }
                else if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
                {
                    if (i + 2 < sql.Length && (sql[i + 2] == '!' || sql[i + 2] == 'M')) throw Ambiguous();
                    var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (end < 0) throw Ambiguous();
                    i = end + 1;
                }
                else if (c == '?') positions.Add(i);
            }
            return positions;
        }

        private static ArgumentException Ambiguous() => new ArgumentException("SQL quoting or comments cannot be safely client prepared.");
        private static NotSupportedException Unsupported() => new NotSupportedException("Client preparation supports only ordinary scalar input numbers, strings, binary and NULL.");
    }
}
