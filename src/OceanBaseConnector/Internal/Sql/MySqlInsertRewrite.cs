#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using OceanBaseConnector.Internal.Protocol;

namespace OceanBaseConnector.Internal.Sql
{
    internal sealed class MySqlInsertRewrite
    {
        private readonly string _sql;
        private readonly int _start, _end, _semicolon;
        private readonly List<int> _parameters;
        private MySqlInsertRewrite(string sql, int start, int end, int semicolon, List<int> parameters)
        { _sql = sql; _start = start; _end = end; _semicolon = semicolon; _parameters = parameters; }
        internal int ParameterCount => _parameters.Count;
        internal int TupleBytes => ResultColumn.Utf8.GetByteCount(_sql.Substring(_start, _end - _start));
        internal int BaseBytes => ResultColumn.Utf8.GetByteCount(_sql) - (_semicolon >= 0 ? 1 : 0) + 1;
        internal string Build(int rows)
        {
            var builder = new StringBuilder(_sql.Substring(0, _end));
            var tuple = _sql.Substring(_start, _end - _start);
            for (var i = 1; i < rows; i++) builder.Append(',').Append(tuple);
            var tail = _sql.Substring(_end);
            if (_semicolon >= 0) tail = tail.Remove(_semicolon - _end, 1);
            return builder.Append(tail).ToString();
        }
        internal static MySqlInsertRewrite? TryParse(string sql, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (sql.Length >= PacketChannel.DefaultMaxPacketSize || sql.IndexOf('\0') >= 0) return null;
            var shape = Parse(sql, true, false, token);
            if (shape == null) return null;
            foreach (var mode in new[] { (false, false), (true, true), (false, true) })
            {
                var other = Parse(sql, mode.Item1, mode.Item2, token);
                if (other == null || shape._start != other._start || shape._end != other._end || shape._semicolon != other._semicolon || shape.ParameterCount != other.ParameterCount) return null;
                for (var i = 0; i < shape.ParameterCount; i++) if (shape._parameters[i] != other._parameters[i]) return null;
            }
            return shape;
        }
        private readonly struct Token
        {
            internal Token(int start, int end, char kind) { Start = start; End = end; Kind = kind; }
            internal int Start { get; }
            internal int End { get; }
            internal char Kind { get; }
        }
        private static MySqlInsertRewrite? Parse(string sql, bool backslash, bool ansi, CancellationToken cancellation)
        {
            var tokens = new List<Token>();
            for (var i = 0; i < sql.Length;)
            {
                cancellation.ThrowIfCancellationRequested();
                // shortcut: cap lexer storage, extend only if unusually large expressions need rewriting.
                if (tokens.Count == 262144) return null;
                var start = i; var c = sql[i++];
                if (c == '\0') return null;
                if (char.IsWhiteSpace(c)) continue;
                if (c == '#' || c == '-' && i + 1 < sql.Length && sql[i] == '-' && (sql[i + 1] <= ' ' || char.IsControl(sql[i + 1])))
                {
                    while (i < sql.Length && sql[i] != '\r' && sql[i] != '\n') { if ((i & 1023) == 0) cancellation.ThrowIfCancellationRequested(); i++; }
                    continue;
                }
                if (c == '/' && i < sql.Length && sql[i] == '*')
                {
                    i++;
                    if (i < sql.Length && (sql[i] == '!' || sql[i] == 'M')) return null;
                    while (i + 1 < sql.Length && !(sql[i] == '*' && sql[i + 1] == '/')) { if ((i & 1023) == 0) cancellation.ThrowIfCancellationRequested(); i++; }
                    if (i + 1 >= sql.Length) return null;
                    i += 2; continue;
                }
                if (c == '\'' || c == '"' || c == '`')
                {
                    var closed = false;
                    while (i < sql.Length)
                    {
                        if ((i & 1023) == 0) cancellation.ThrowIfCancellationRequested();
                        var next = sql[i++]; if (next == '\0') return null;
                        if (next == '\\' && backslash && c != '`' && (!ansi || c != '"')) { i++; continue; }
                        if (next != c) continue;
                        if (i < sql.Length && sql[i] == c) { i++; continue; }
                        closed = true; break;
                    }
                    if (!closed) return null;
                    tokens.Add(new Token(start, i, c == '`' ? 'I' : 'Q')); continue;
                }
                if (char.IsLetterOrDigit(c) || c == '_' || c == '$')
                {
                    while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_' || sql[i] == '$')) { if ((i & 1023) == 0) cancellation.ThrowIfCancellationRequested(); i++; }
                    tokens.Add(new Token(start, i, 'W')); continue;
                }
                tokens.Add(new Token(start, i, c));
            }
            var position = 0;
            bool Take(char kind) { if (position >= tokens.Count || tokens[position].Kind != kind) return false; position++; return true; }
            bool Word(string word)
            {
                if (position >= tokens.Count) return false;
                var item = tokens[position];
                if (item.Kind != 'W' || item.End - item.Start != word.Length || string.Compare(sql, item.Start, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;
                position++; return true;
            }
            bool Identifier() => Take('I') || Take('W');
            if (!Word("INSERT") || !Word("INTO") || !Identifier()) return null;
            if (Take('.') && !Identifier()) return null;
            if (Take('('))
            {
                if (!Identifier()) return null;
                while (Take(',')) if (!Identifier()) return null;
                if (!Take(')')) return null;
            }
            if (!Word("VALUES") || position >= tokens.Count) return null;
            var tupleStart = tokens[position].Start;
            if (!Take('(')) return null;
            var depth = 1; var parameters = new List<int>(); var end = -1;
            while (position < tokens.Count)
            {
                cancellation.ThrowIfCancellationRequested();
                var item = tokens[position++];
                if (item.Kind == ';') return null;
                if (item.Kind == '?') parameters.Add(item.Start);
                if (item.Kind == '(') depth++;
                if (item.Kind == ')' && --depth == 0) { end = item.End; break; }
            }
            if (end < 0) return null;
            var semicolon = position < tokens.Count && tokens[position].Kind == ';' ? tokens[position++].Start : -1;
            if (position != tokens.Count) return null;
            return new MySqlInsertRewrite(sql, tupleStart, end, semicolon, parameters);
        }
    }
}
#endif
