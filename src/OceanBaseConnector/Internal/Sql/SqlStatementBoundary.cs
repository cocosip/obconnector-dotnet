using System;

namespace OceanBaseConnector.Internal.Sql
{
    internal static class SqlStatementBoundary
    {
        internal static bool CanCacheStatement(string text, OceanBaseCompatibilityMode mode)
        {
            var position = 0;
            SkipTrivia(text, ref position, mode == OceanBaseCompatibilityMode.Oracle);
            var start = position;
            while (position < text.Length && (char.IsLetterOrDigit(text[position]) || text[position] == '_' || text[position] == '$' || text[position] == '#')) position++;
            var keyword = text.Substring(start, position - start).ToUpperInvariant();
            return (keyword == "SELECT" || keyword == "WITH" || keyword == "INSERT" || keyword == "UPDATE" ||
                keyword == "DELETE" || keyword == "REPLACE" || keyword == "MERGE") &&
                !HasMultipleStatements(text, mode) && !SessionStateSql.RequiresDiscard(text, mode);
        }

        internal static bool HasMultipleStatements(string text, OceanBaseCompatibilityMode mode)
        {
            var oracle = mode == OceanBaseCompatibilityMode.Oracle;
            // Program-unit semicolons belong to the server's PL/SQL grammar, not to a text batch.
            if (oracle && IsOracleProgramUnit(text)) return false;
            var separated = false;
            for (var position = 0; position < text.Length; position++)
            {
                SkipTrivia(text, ref position, oracle);
                if (position == text.Length) break;
                if (separated) return true;
                var value = text[position];
                if (oracle && (value == 'q' || value == 'Q') && position + 1 < text.Length && text[position + 1] == '\'')
                {
                    position = ParameterBindingPlan.SkipAlternativeQuote(text, position) - 1;
                    continue;
                }
                if (value == '\'' || value == '"' || !oracle && value == '`')
                {
                    if (oracle) { position = ParameterBindingPlan.SkipQuote(text, position, value) - 1; continue; }
                    for (position++; position < text.Length; position++)
                    {
                        if (text[position] == '\\') { position++; continue; }
                        if (text[position] != value) continue;
                        if (position + 1 < text.Length && text[position + 1] == value) { position++; continue; }
                        break;
                    }
                    continue;
                }
                separated = value == ';';
            }
            return false;
        }

        private static bool IsOracleProgramUnit(string text)
        {
            var position = 0;
            SkipTrivia(text, ref position, true);
            while (position + 1 < text.Length && text[position] == '<' && text[position + 1] == '<')
            {
                position += 2;
                while (position + 1 < text.Length && !(text[position] == '>' && text[position + 1] == '>'))
                    position = text[position] == '"' ? ParameterBindingPlan.SkipQuote(text, position, '"') : position + 1;
                if (position + 1 >= text.Length) return false;
                position += 2;
                SkipTrivia(text, ref position, true);
            }
            var keyword = ReadOracleKeyword(text, ref position);
            if (keyword == "BEGIN" || keyword == "DECLARE") return true;
            if (keyword != "CREATE") return false;
            keyword = ReadOracleKeyword(text, ref position);
            if (keyword == "OR")
            {
                if (ReadOracleKeyword(text, ref position) != "REPLACE") return false;
                keyword = ReadOracleKeyword(text, ref position);
            }
            if (keyword == "EDITIONABLE" || keyword == "NONEDITIONABLE") keyword = ReadOracleKeyword(text, ref position);
            return keyword == "PROCEDURE" || keyword == "FUNCTION" || keyword == "PACKAGE" || keyword == "TRIGGER" || keyword == "TYPE";
        }

        private static string ReadOracleKeyword(string text, ref int position)
        {
            SkipTrivia(text, ref position, true);
            var start = position;
            while (position < text.Length && (char.IsLetterOrDigit(text[position]) || text[position] == '_' || text[position] == '$' || text[position] == '#')) position++;
            return text.Substring(start, position - start).ToUpperInvariant();
        }

        private static void SkipTrivia(string text, ref int position, bool oracle)
        {
            while (position < text.Length)
            {
                if (char.IsWhiteSpace(text[position])) { position++; continue; }
                if (!oracle && text[position] == '#' || position + 1 < text.Length && text[position] == '-' && text[position + 1] == '-' &&
                    (oracle || position + 2 == text.Length || char.IsWhiteSpace(text[position + 2])))
                {
                    while (position < text.Length && text[position] != '\r' && text[position] != '\n') position++;
                    continue;
                }
                if (text[position] == '/' && position + 1 < text.Length && text[position + 1] == '*')
                {
                    var end = text.IndexOf("*/", position + 2, StringComparison.Ordinal);
                    position = end < 0 ? text.Length : end + 2;
                    continue;
                }
                break;
            }
        }
    }
}
