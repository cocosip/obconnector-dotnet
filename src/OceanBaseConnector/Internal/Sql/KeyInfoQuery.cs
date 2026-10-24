using System;
using System.Collections.Generic;
using System.Text;
using OceanBaseConnector.Internal.Protocol;

namespace OceanBaseConnector.Internal.Sql
{
    // Only rewrite queries whose projection cannot change the source table's row cardinality.
    internal sealed class KeyInfoQuery
    {
        private KeyInfoQuery(string text, int insertion, string? schema, string table, string qualifier, bool mysql)
        { Text = text; Insertion = insertion; Schema = schema; Table = table; Qualifier = qualifier; MySql = mysql; }
        internal string Text { get; }
        internal int Insertion { get; }
        internal string? Schema { get; }
        internal string Table { get; }
        internal string Qualifier { get; }
        private bool MySql { get; }

        internal static KeyInfoQuery? Parse(string sql, OceanBaseCompatibilityMode mode)
        {
            var mysql = mode == OceanBaseCompatibilityMode.MySql;
            var tokens = Tokens(sql, mysql);
            if (tokens == null || tokens.Count < 4 || tokens[0].Value != "SELECT") return null;
            var from = -1;
            for (var i = 1; i < tokens.Count; i++)
            {
                var value = tokens[i].Value;
                if (value == "FROM") { from = i; break; }
                if (value == "(" || value == "DISTINCT" || value == "DISTINCTROW" || value == "UNIQUE" || value == "INTO") return null;
            }
            if (from < 2 || from + 1 >= tokens.Count || !tokens[from + 1].Identifier) return null;
            var position = from + 1;
            var table = tokens[position++]; string? schema = null;
            if (position < tokens.Count && tokens[position].Value == ".")
            {
                if (++position >= tokens.Count || !tokens[position].Identifier) return null;
                schema = table.Name; table = tokens[position++];
            }
            var qualifier = sql.Substring(table.Start, table.End - table.Start);
            if (position < tokens.Count && tokens[position].Value == "AS") position++;
            if (position < tokens.Count && tokens[position].Identifier && !IsTail(tokens[position].Value))
            { var alias = tokens[position++]; qualifier = sql.Substring(alias.Start, alias.End - alias.Start); }
            if (position < tokens.Count && !IsTail(tokens[position].Value) && tokens[position].Value != ";") return null;
            var depth = 0; var ordering = false;
            for (; position < tokens.Count; position++)
            {
                var value = tokens[position].Value;
                if (ordering && tokens[position].Identifier && position + 1 < tokens.Count && tokens[position + 1].Value == "(") return null;
                if (value == "(") { depth++; continue; }
                if (value == ")") { if (--depth < 0) return null; continue; }
                if (depth != 0) continue;
                if (value == "ORDER") ordering = true;
                if (value == "JOIN" || value == "GROUP" || value == "HAVING" || value == "UNION" || value == "INTERSECT" ||
                    value == "EXCEPT" || value == "MINUS" || value == "CONNECT" || value == "START" || value == "MODEL" ||
                    value == "QUALIFY" || value == "INTO" || value == "RETURNING" || value == "PROCEDURE") return null;
                if (value == ";" && position != tokens.Count - 1) return null;
            }
            return depth == 0 ? new KeyInfoQuery(sql, tokens[from].Start, schema, table.Name, qualifier, mysql) : null;
        }

        internal string Append(IReadOnlyList<string> columns, ResultColumn[] original)
        {
            if (columns.Count > 4096 - original.Length) throw new NotSupportedException("KeyInfo result column limit exceeded.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in original) names.Add(column.Name);
            var extra = new StringBuilder(); var suffix = 0;
            foreach (var column in columns)
            {
                string name;
                do { name = "__ob_key_" + suffix++; } while (!names.Add(name));
                extra.Append(", ").Append(Qualifier).Append('.').Append(Quote(column)).Append(" AS ").Append(Quote(name));
            }
            var text = Text.Insert(Insertion, extra.Append(' ').ToString());
            if (ResultColumn.Utf8.GetByteCount(text) >= PacketChannel.DefaultMaxPacketSize) throw new NotSupportedException("KeyInfo query exceeds the packet size limit.");
            return text;
        }

        private string Quote(string name)
        {
            var quote = MySql ? "`" : "\"";
            if (name.Length == 0 || name.IndexOf('\0') >= 0 || name.IndexOf('\\') >= 0 || ResultColumn.Utf8.GetByteCount(name) > 512)
                throw new NotSupportedException("KeyInfo requires an unambiguous source identifier.");
            return quote + name.Replace(quote, quote + quote) + quote;
        }

        private static bool IsTail(string value) => value == "WHERE" || value == "ORDER" || value == "LIMIT" || value == "OFFSET" || value == "FETCH" || value == "FOR";
        private sealed class Token
        {
            internal int Start, End;
            internal string Value = "", Name = "";
            internal bool Identifier;
        }
        private static List<Token>? Tokens(string sql, bool mysql)
        {
            var tokens = new List<Token>();
            for (var i = 0; i < sql.Length;)
            {
                if (tokens.Count >= 65536) return null;
                if (char.IsWhiteSpace(sql[i])) { i++; continue; }
                if (sql[i] == '#' && mysql || i + 1 < sql.Length && sql[i] == '-' && sql[i + 1] == '-' &&
                    (!mysql || i + 2 == sql.Length || char.IsWhiteSpace(sql[i + 2])))
                { while (i < sql.Length && sql[i] != '\r' && sql[i] != '\n') i++; continue; }
                if (i + 1 < sql.Length && sql[i] == '/' && sql[i + 1] == '*')
                {
                    if (mysql && i + 2 < sql.Length && (sql[i + 2] == '!' || sql[i + 2] == 'M')) return null;
                    var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal); if (end < 0) return null;
                    i = end + 2; continue;
                }
                var start = i;
                var alternative = sql[i] == 'q' || sql[i] == 'Q' ? i :
                    (sql[i] == 'n' || sql[i] == 'N') && i + 1 < sql.Length && (sql[i + 1] == 'q' || sql[i + 1] == 'Q') ? i + 1 : -1;
                if (!mysql && alternative >= 0 && alternative + 1 < sql.Length && sql[alternative + 1] == '\'')
                {
                    try { i = ParameterBindingPlan.SkipAlternativeQuote(sql, alternative); }
                    catch (ArgumentException) { return null; }
                    tokens.Add(new Token { Start = start, End = i, Value = "literal" }); continue;
                }
                var quote = sql[i];
                if (quote == '\'' || quote == '"' || mysql && quote == '`')
                {
                    try { i = ParameterBindingPlan.SkipQuote(sql, i, quote); }
                    catch (ArgumentException) { return null; }
                    var raw = sql.Substring(start + 1, i - start - 2);
                    // MySQL SQL_MODE changes backslash and double-quote interpretation.
                    if (mysql && (raw.IndexOf('\\') >= 0 || quote == '"')) return null;
                    var identifier = quote != '\'';
                    tokens.Add(new Token { Start = start, End = i, Identifier = identifier, Value = "quoted", Name = raw.Replace(new string(quote, 2), quote.ToString()) }); continue;
                }
                if (char.IsLetter(sql[i]) || sql[i] == '_' || sql[i] == '$' || sql[i] == '#')
                {
                    while (++i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_' || sql[i] == '$' || sql[i] == '#')) { }
                    var name = sql.Substring(start, i - start);
                    tokens.Add(new Token { Start = start, End = i, Value = name.ToUpperInvariant(), Name = mysql ? name : name.ToUpperInvariant(), Identifier = true }); continue;
                }
                tokens.Add(new Token { Start = i, End = i + 1, Value = sql[i++].ToString() });
            }
            return tokens;
        }
    }
}
