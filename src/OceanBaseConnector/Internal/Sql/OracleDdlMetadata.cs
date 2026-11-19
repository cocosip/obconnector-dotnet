using System;
using System.Collections.Generic;
using OceanBaseConnector.Internal.Values;

namespace OceanBaseConnector.Internal.Sql
{
    internal static class OracleDdlMetadata
    {
        internal static IReadOnlyList<OceanBaseIdentityColumn> ReadIdentityColumns(string ddl)
        {
            var tokens = Tokenize(ddl);
            var i = 0;
            Require(tokens, ref i, "CREATE");
            if (Take(tokens, ref i, "GLOBAL") || Take(tokens, ref i, "PRIVATE"))
                Require(tokens, ref i, "TEMPORARY");
            Require(tokens, ref i, "TABLE");
            SkipObjectName(tokens, ref i);
            Require(tokens, ref i, "(");

            var columns = new List<OceanBaseIdentityColumn>();
            do
            {
                var first = i;
                if (i == tokens.Count || !IsIdentifier(tokens[i].Text)) throw Unexpected();
                while (i < tokens.Count && tokens[i].Text != "," && tokens[i].Text != ")")
                {
                    if (Take(tokens, ref i, "(")) { SkipGroup(tokens, ref i); continue; }
                    if (i > first && Take(tokens, ref i, "GENERATED"))
                    {
                        var mode = "ALWAYS";
                        if (Take(tokens, ref i, "BY"))
                        {
                            Require(tokens, ref i, "DEFAULT");
                            mode = "BY DEFAULT";
                            if (Take(tokens, ref i, "ON"))
                            {
                                Require(tokens, ref i, "NULL");
                                mode = "BY DEFAULT ON NULL";
                            }
                        }
                        else Require(tokens, ref i, "ALWAYS");
                        Require(tokens, ref i, "AS");
                        // Virtual columns also use GENERATED ALWAYS AS followed by an expression.
                        if (!Take(tokens, ref i, "IDENTITY")) continue;
                        var optionsStart = i;
                        if (Take(tokens, ref i, "(")) SkipGroup(tokens, ref i);
                        else
                            while (ReadSequenceOption(tokens, ref i, out _)) { }
                        if (i < tokens.Count && !IsColumnClause(tokens[i].Text)) throw Unexpected();
                        var options = optionsStart == i ? "" : ddl.Substring(tokens[optionsStart].Start, tokens[i - 1].End - tokens[optionsStart].Start);
                        var name = tokens[first].Text;
                        name = name[0] == '"' ? name.Substring(1, name.Length - 2).Replace("\"\"", "\"") : name.ToUpperInvariant();
                        columns.Add(new OceanBaseIdentityColumn(name, mode, options));
                        continue;
                    }
                    i++;
                }
            } while (Take(tokens, ref i, ","));
            Require(tokens, ref i, ")");
            return columns.AsReadOnly();
        }

        internal static IReadOnlyList<string> ReadIndexMembers(string ddl)
        {
            var tokens = Tokenize(ddl);
            var i = 0;
            Require(tokens, ref i, "CREATE");
            Take(tokens, ref i, "UNIQUE");
            Require(tokens, ref i, "INDEX");
            SkipObjectName(tokens, ref i);
            Require(tokens, ref i, "ON");
            SkipObjectName(tokens, ref i);
            Require(tokens, ref i, "(");

            var members = new List<string>();
            do
            {
                var first = i;
                while (i < tokens.Count && tokens[i].Text != "," && tokens[i].Text != ")")
                {
                    if (Take(tokens, ref i, "(")) SkipGroup(tokens, ref i);
                    else i++;
                }
                var end = i;
                if (end > first + 1 && (string.Equals(tokens[end - 1].Text, "ASC", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tokens[end - 1].Text, "DESC", StringComparison.OrdinalIgnoreCase)))
                    end--;
                if (end == first) throw Unexpected();
                members.Add(ddl.Substring(tokens[first].Start, tokens[end - 1].End - tokens[first].Start));
            } while (Take(tokens, ref i, ","));
            Require(tokens, ref i, ")");
            return members;
        }

        internal static OceanBaseDecimal ReadSequenceStart(string ddl)
        {
            var tokens = Tokenize(ddl);
            var i = 0;
            Require(tokens, ref i, "CREATE");
            Require(tokens, ref i, "SEQUENCE");
            SkipObjectName(tokens, ref i);
            OceanBaseDecimal? start = null;
            while (ReadSequenceOption(tokens, ref i, out var value))
            {
                if (!value.HasValue) continue;
                if (start.HasValue) throw Unexpected();
                start = value;
            }
            Take(tokens, ref i, ";");
            if (i != tokens.Count || !start.HasValue) throw Unexpected();
            return start.Value;
        }

        private static bool ReadSequenceOption(List<(string Text, int Start, int End)> tokens, ref int i, out OceanBaseDecimal? start)
        {
            start = null;
            if (Take(tokens, ref i, "START"))
            {
                Require(tokens, ref i, "WITH");
                start = ReadInteger(tokens, ref i);
                return true;
            }
            if (Take(tokens, ref i, "INCREMENT"))
            {
                Require(tokens, ref i, "BY");
                ReadInteger(tokens, ref i);
                return true;
            }
            if (Take(tokens, ref i, "MINVALUE") || Take(tokens, ref i, "MAXVALUE") || Take(tokens, ref i, "CACHE"))
            {
                ReadInteger(tokens, ref i);
                return true;
            }
            return Take(tokens, ref i, "NOMINVALUE") || Take(tokens, ref i, "NOMAXVALUE") ||
                Take(tokens, ref i, "NOCACHE") || Take(tokens, ref i, "CYCLE") || Take(tokens, ref i, "NOCYCLE") ||
                Take(tokens, ref i, "ORDER") || Take(tokens, ref i, "NOORDER");
        }

        private static OceanBaseDecimal ReadInteger(List<(string Text, int Start, int End)> tokens, ref int i)
        {
            var sign = Take(tokens, ref i, "-") ? "-" : "";
            if (sign.Length == 0) Take(tokens, ref i, "+");
            if (i >= tokens.Count || tokens[i].Text.Length > 38) throw Unexpected();
            var number = tokens[i++].Text;
            foreach (var digit in number)
                if (digit < '0' || digit > '9') throw Unexpected();
            return DecimalCodec.Parse(sign + number);
        }

        private static bool IsColumnClause(string token) => token.ToUpperInvariant() switch
        {
            "," or ")" or "NULL" or "NOT" or "CONSTRAINT" or "PRIMARY" or "UNIQUE" or "REFERENCES" or
                "CHECK" or "ENABLE" or "DISABLE" or "COMMENT" or "COLLATE" or "VISIBLE" or "INVISIBLE" => true,
            _ => false
        };

        private static void SkipObjectName(List<(string Text, int Start, int End)> tokens, ref int i)
        {
            if (i == tokens.Count || !IsIdentifier(tokens[i++].Text)) throw Unexpected();
            if (Take(tokens, ref i, ".") && (i == tokens.Count || !IsIdentifier(tokens[i++].Text))) throw Unexpected();
        }

        private static bool IsIdentifier(string token) => token[0] == '"' || OracleCommandPlan.IsNameStart(token[0]);

        private static void SkipGroup(List<(string Text, int Start, int End)> tokens, ref int i)
        {
            var depth = 1;
            while (i < tokens.Count)
            {
                var token = tokens[i++].Text;
                if (token == "(") depth++;
                if (token == ")" && --depth == 0) return;
            }
            throw Unexpected();
        }

        private static bool Take(List<(string Text, int Start, int End)> tokens, ref int i, string text)
        {
            if (i == tokens.Count || !string.Equals(tokens[i].Text, text, StringComparison.OrdinalIgnoreCase)) return false;
            i++;
            return true;
        }

        private static void Require(List<(string Text, int Start, int End)> tokens, ref int i, string text)
        {
            if (!Take(tokens, ref i, text)) throw Unexpected();
        }

        private static List<(string Text, int Start, int End)> Tokenize(string ddl)
        {
            var tokens = new List<(string Text, int Start, int End)>();
            var i = 0;
            while (true)
            {
                SqlStatementBoundary.SkipTrivia(ddl, ref i, oracle: true);
                if (i == ddl.Length) return tokens;
                var start = i;
                var c = ddl[i];
                if (c == '"' || c == '\'') i = ParameterBindingPlan.SkipQuote(ddl, i, c);
                else if ((c == 'q' || c == 'Q' || c == 'n' || c == 'N') && i + 1 < ddl.Length)
                {
                    var q = c == 'n' || c == 'N' ? i + 1 : i;
                    if (q + 1 < ddl.Length && (ddl[q] == 'q' || ddl[q] == 'Q') && ddl[q + 1] == '\'')
                        i = ParameterBindingPlan.SkipAlternativeQuote(ddl, q);
                }
                if (i == start)
                {
                    i++;
                    if (OracleCommandPlan.IsNamePart(c))
                        while (i < ddl.Length && OracleCommandPlan.IsNamePart(ddl[i])) i++;
                }
                tokens.Add((ddl.Substring(start, i - start), start, i));
            }
        }

        private static NotSupportedException Unexpected() => new NotSupportedException("The Oracle metadata DDL has an unsupported shape.");
    }
}
