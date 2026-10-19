using System;
using System.Collections.Generic;
using System.Data;
using System.Text;

namespace OceanBaseConnector.Internal.Sql
{
    internal sealed class ParameterBindingPlan
    {
        internal ParameterBindingPlan(string commandText, OceanBaseParameter[] parameters, int[]? bindingPositions = null)
        { CommandText = commandText; Parameters = parameters; BindingPositions = bindingPositions; }

        internal string CommandText { get; }
        internal OceanBaseParameter[] Parameters { get; }
        internal int[]? BindingPositions { get; }

        internal static ParameterBindingPlan Create(string sql, OceanBaseCompatibilityMode mode, OceanBaseParameterCollection parameters, bool allowOutputs = false)
        {
            var supplied = new OceanBaseParameter[parameters.Count];
            for (var i = 0; i < supplied.Length; i++)
            {
                supplied[i] = (OceanBaseParameter)parameters[i];
                if (supplied[i].Direction != ParameterDirection.Input && (!allowOutputs || mode != OceanBaseCompatibilityMode.Oracle))
                    throw new NotSupportedException("Only input parameters are supported at this stage.");
            }
            // MySQL SQL_MODE can change quoting; only the server counts positional parameters.
            if (mode == OceanBaseCompatibilityMode.MySql) return new ParameterBindingPlan(sql, supplied);
            if (mode != OceanBaseCompatibilityMode.Oracle) throw new ArgumentOutOfRangeException(nameof(mode));

            var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < supplied.Length; i++)
            {
                var name = NormalizeName(supplied[i].ParameterName);
                if (names.ContainsKey(name)) throw new ArgumentException("Oracle parameter names must be unique after normalization.");
                names.Add(name, i);
            }

            var ordered = new List<OceanBaseParameter>();
            var positions = new List<int>();
            var used = new bool[supplied.Length];
            var usedCount = 0;
            StringBuilder? rewritten = null;
            var copiedThrough = 0;
            var position = 0;
            while (position < sql.Length)
            {
                var c = sql[position];
                if (c == '\'' || c == '"') { position = SkipQuote(sql, position, c); continue; }
                var alternative = (c == 'q' || c == 'Q') ? position :
                    (c == 'n' || c == 'N') && position + 1 < sql.Length && (sql[position + 1] == 'q' || sql[position + 1] == 'Q') ? position + 1 : -1;
                if (alternative >= 0 && alternative + 1 < sql.Length && sql[alternative + 1] == '\'')
                {
                    if (position > 0 && IsIdentifierPart(sql[position - 1])) throw UnsupportedSyntax();
                    position = SkipAlternativeQuote(sql, alternative);
                    continue;
                }
                if (c == '-' && position + 1 < sql.Length && sql[position + 1] == '-')
                {
                    position += 2;
                    while (position < sql.Length && sql[position] != '\r' && sql[position] != '\n') position++;
                    continue;
                }
                if (c == '/' && position + 1 < sql.Length && sql[position + 1] == '*')
                { position = SkipComment(sql, position + 2); continue; }
                if (c == '?') throw UnsupportedSyntax();
                if (c != ':') { position++; continue; }
                if (position + 1 < sql.Length && sql[position + 1] == '=') { position += 2; continue; }

                var start = position++;
                if (position == sql.Length || !IsNameStart(sql[position])) throw UnsupportedSyntax();
                position++;
                while (position < sql.Length && IsNamePart(sql[position])) position++;
                if (position < sql.Length && IsIdentifierPart(sql[position])) throw UnsupportedSyntax();
                var name = sql.Substring(start + 1, position - start - 1);
                if (!names.TryGetValue(name, out var index)) throw new ArgumentException("A required Oracle parameter is missing.");
                if (used[index] && supplied[index].Direction != ParameterDirection.Input)
                    throw new ArgumentException("An Oracle output parameter cannot occupy multiple binding positions.");
                if (!used[index]) { used[index] = true; usedCount++; }
                ordered.Add(supplied[index]);
                rewritten ??= new StringBuilder();
                rewritten.Append(sql, copiedThrough, start - copiedThrough).Append('?');
                positions.Add(rewritten.Length - 1);
                copiedThrough = position;
            }
            if (usedCount != supplied.Length) throw new ArgumentException("One or more Oracle parameters are not referenced by the command.");
            var text = rewritten == null ? sql : rewritten.Append(sql, copiedThrough, sql.Length - copiedThrough).ToString();
            return new ParameterBindingPlan(text, ordered.ToArray(), positions.ToArray());
        }

        private static string NormalizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) throw InvalidName();
            var start = name[0] == ':' ? 1 : 0;
            if (start == name.Length || !IsNameStart(name[start])) throw InvalidName();
            for (var i = start + 1; i < name.Length; i++) if (!IsNamePart(name[i])) throw InvalidName();
            return start == 0 ? name : name.Substring(1);
        }

        private static int SkipQuote(string sql, int position, char quote)
        {
            for (var i = position + 1; i < sql.Length; i++)
            {
                if (sql[i] != quote) continue;
                if (i + 1 < sql.Length && sql[i + 1] == quote) { i++; continue; }
                return i + 1;
            }
            throw Unterminated();
        }

        private static int SkipAlternativeQuote(string sql, int position)
        {
            if (position + 2 >= sql.Length) throw Unterminated();
            var delimiter = sql[position + 2];
            if (char.IsWhiteSpace(delimiter) || char.IsControl(delimiter) || char.IsSurrogate(delimiter)) throw UnsupportedSyntax();
            var close = delimiter == '[' ? ']' : delimiter == '{' ? '}' : delimiter == '(' ? ')' : delimiter == '<' ? '>' : delimiter;
            for (var i = position + 3; i + 1 < sql.Length; i++)
                if (sql[i] == close && sql[i + 1] == '\'') return i + 2;
            throw Unterminated();
        }

        private static int SkipComment(string sql, int position)
        {
            for (var i = position; i + 1 < sql.Length; i++)
            {
                if (sql[i] == '*' && sql[i + 1] == '/') return i + 2;
            }
            throw Unterminated();
        }

        private static bool IsNameStart(char c) => c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c == '_';
        private static bool IsNamePart(char c) => IsNameStart(c) || c >= '0' && c <= '9' || c == '$' || c == '#';
        private static bool IsIdentifierPart(char c) => IsNamePart(c) || c >= 128 && !char.IsWhiteSpace(c);
        private static ArgumentException InvalidName() => new ArgumentException("Oracle parameter names must use the supported unquoted name syntax.");
        private static ArgumentException Unterminated() => new ArgumentException("CommandText contains an unterminated quote or comment.");
        private static NotSupportedException UnsupportedSyntax() => new NotSupportedException("The Oracle literal or binding syntax is not supported.");
    }
}
