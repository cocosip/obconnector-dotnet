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

        internal static bool IsOracleDml(string text)
        {
            var position = 0;
            var keyword = ReadKeyword(text, ref position);
            return keyword == "INSERT" || keyword == "UPDATE" || keyword == "DELETE" || keyword == "MERGE";
        }

        internal static bool HasMultipleStatements(string text, OceanBaseCompatibilityMode mode)
            => ScanStatements(text, mode, typeDdl: false);

        internal static bool InvalidatesComplexTypes(string text, OceanBaseCompatibilityMode mode)
            => mode == OceanBaseCompatibilityMode.Oracle && ScanStatements(text, mode, typeDdl: true);

        private static bool IsDdl(string keyword) => keyword == "CREATE" || keyword == "ALTER" || keyword == "DROP" || keyword == "RENAME" || keyword == "TRUNCATE";

        private static bool ScanStatements(string text, OceanBaseCompatibilityMode mode, bool typeDdl)
        {
            var oracle = mode == OceanBaseCompatibilityMode.Oracle;
            // Program-unit semicolons belong to the server grammar, not to a text batch.
            if (oracle && IsOracleProgramUnit(text))
            {
                var position = 0;
                return typeDdl && IsDdl(ReadKeyword(text, ref position));
            }
            if (!oracle && IsMySqlProgramUnit(text)) return false;
            var separated = false;
            var statementStart = true;
            for (var position = 0; position < text.Length; position++)
            {
                SkipTrivia(text, ref position, oracle);
                if (position == text.Length) break;
                if (separated && !typeDdl) return true;
                if (typeDdl && statementStart)
                {
                    var keywordPosition = position;
                    if (IsDdl(ReadKeyword(text, ref keywordPosition))) return true;
                }
                statementStart = false;
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
                if (separated) statementStart = true;
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
            var keyword = ReadKeyword(text, ref position);
            if (keyword == "BEGIN" || keyword == "DECLARE") return true;
            if (keyword != "CREATE") return false;
            keyword = ReadKeyword(text, ref position);
            if (keyword == "OR")
            {
                if (ReadKeyword(text, ref position) != "REPLACE") return false;
                keyword = ReadKeyword(text, ref position);
            }
            if (keyword == "EDITIONABLE" || keyword == "NONEDITIONABLE") keyword = ReadKeyword(text, ref position);
            return keyword == "PROCEDURE" || keyword == "FUNCTION" || keyword == "PACKAGE" || keyword == "TRIGGER" || keyword == "TYPE";
        }

        private static bool IsMySqlProgramUnit(string text)
        {
            var position = 0;
            if (ReadKeyword(text, ref position, oracle: false) != "CREATE") return false;
            var keyword = ReadKeyword(text, ref position, oracle: false);
            if (keyword == "OR")
            {
                if (ReadKeyword(text, ref position, oracle: false) != "REPLACE") return false;
                keyword = ReadKeyword(text, ref position, oracle: false);
            }
            if (keyword == "DEFINER")
            {
                SkipTrivia(text, ref position, false);
                if (position == text.Length || text[position++] != '=') return false;
                if (!SkipMySqlAccountPart(text, ref position)) return false;
                SkipTrivia(text, ref position, false);
                if (position < text.Length && text[position] == '(')
                {
                    position++; SkipTrivia(text, ref position, false);
                    if (position == text.Length || text[position++] != ')') return false;
                }
                SkipTrivia(text, ref position, false);
                if (position < text.Length && text[position] == '@')
                { position++; if (!SkipMySqlAccountPart(text, ref position)) return false; }
                keyword = ReadKeyword(text, ref position, oracle: false);
            }
            return keyword == "PROCEDURE" || keyword == "FUNCTION" || keyword == "TRIGGER" || keyword == "EVENT";
        }

        private static bool SkipMySqlAccountPart(string text, ref int position)
        {
            SkipTrivia(text, ref position, false);
            if (position == text.Length) return false;
            var quote = text[position];
            if (quote == '\'' || quote == '"' || quote == '`')
            {
                for (position++; position < text.Length; position++)
                {
                    if (text[position] == '\\') { position++; continue; }
                    if (text[position] != quote) continue;
                    if (position + 1 < text.Length && text[position + 1] == quote) { position++; continue; }
                    position++; return true;
                }
                return false;
            }
            var start = position;
            while (position < text.Length && (char.IsLetterOrDigit(text[position]) || text[position] == '_' || text[position] == '$' || text[position] == '.' || text[position] == '%')) position++;
            return position != start;
        }

        private static string ReadKeyword(string text, ref int position, bool oracle = true)
        {
            SkipTrivia(text, ref position, oracle);
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
