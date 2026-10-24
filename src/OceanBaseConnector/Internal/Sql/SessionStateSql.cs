using System;

namespace OceanBaseConnector.Internal.Sql
{
    internal static class SessionStateSql
    {
        internal static bool RequiresDiscard(string text, OceanBaseCompatibilityMode mode)
        {
            var oracle = mode == OceanBaseCompatibilityMode.Oracle;
            var words = 0;
            var alter = false;
            var create = false;
            var temporaryScope = false;
            var lockFunction = false;
            var relationName = false;
            var qualifiedRelation = false;
            // shortcut: nested CTE lock-name declarations may retire safe SQL; use a grammar parser if reuse suffers.
            var cte = false;
            var depth = 0;
            for (var position = 0; position < text.Length;)
            {
                var value = text[position];
                if (char.IsWhiteSpace(value)) { position++; continue; }
                if (!oracle && value == '#' || value == '-' && position + 1 < text.Length && text[position + 1] == '-' &&
                    (oracle || position + 2 == text.Length || text[position + 2] <= ' ' || char.IsWhiteSpace(text[position + 2])))
                {
                    while (position < text.Length && text[position] != '\r' && text[position] != '\n') position++;
                    continue;
                }
                if (value == '/' && position + 1 < text.Length && text[position + 1] == '*')
                {
                    // Executable comments can contain arbitrary statements, including version-gated ones.
                    if (!oracle && (position + 2 < text.Length && text[position + 2] == '!' ||
                        position + 3 < text.Length && (text[position + 2] == 'M' || text[position + 2] == 'm') && text[position + 3] == '!')) return true;
                    var end = text.IndexOf("*/", position + 2, StringComparison.Ordinal);
                    if (end < 0) return true;
                    position = end + 2;
                    continue;
                }
                if (lockFunction)
                {
                    if (value == '(') return true;
                    lockFunction = false;
                }
                var alternative = oracle && (value == 'q' || value == 'Q') && position + 1 < text.Length && text[position + 1] == '\'';
                var nationalAlternative = oracle && (value == 'n' || value == 'N') && position + 2 < text.Length &&
                    (text[position + 1] == 'q' || text[position + 1] == 'Q') && text[position + 2] == '\'';
                if (alternative || nationalAlternative)
                {
                    try { position = ParameterBindingPlan.SkipAlternativeQuote(text, nationalAlternative ? position + 1 : position); }
                    catch (ArgumentException) { return true; }
                    catch (NotSupportedException) { return true; }
                    continue;
                }
                if (value == '\'' || value == '"' || !oracle && value == '`')
                {
                    qualifiedRelation = relationName;
                    relationName = false;
                    var quotedStart = position + 1;
                    var closed = false;
                    for (position++; position < text.Length; position++)
                    {
                        // shortcut: SQL_MODE is not tracked; ambiguous MySQL escapes retire the session.
                        if (!oracle && text[position] == '\\') return true;
                        if (text[position] != value) continue;
                        if (position + 1 < text.Length && text[position + 1] == value) { position++; continue; }
                        position++;
                        closed = true;
                        break;
                    }
                    if (!closed) return true;
                    lockFunction = !oracle && value != '\'' && !qualifiedRelation && IsLockFunction(text, quotedStart, position - quotedStart - 1);
                    continue;
                }
                if (value == ';')
                {
                    words = 0; alter = create = temporaryScope = false;
                    relationName = qualifiedRelation = cte = false; depth = 0;
                    position++;
                    continue;
                }
                if (oracle && value == '<' && position + 1 < text.Length && text[position + 1] == '<') return true;
                if (!oracle && value == '@')
                {
                    if (position + 1 < text.Length && text[position + 1] == '@') { position += 2; continue; }
                    return true;
                }
                if (!char.IsLetterOrDigit(value) && value != '_' && value != '$' && value != '#')
                {
                    relationName = value == '.' && qualifiedRelation || value == ',' && cte && depth == 0;
                    qualifiedRelation = false;
                    if (value == '(') depth++;
                    if (value == ')' && depth > 0) depth--;
                    position++;
                    continue;
                }
                var start = position++;
                while (position < text.Length && (char.IsLetterOrDigit(text[position]) || text[position] == '_' || text[position] == '$' || text[position] == '#')) position++;
                var length = position - start;
                bool Is(string keyword) => length == keyword.Length && string.Compare(text, start, keyword, 0, length, StringComparison.OrdinalIgnoreCase) == 0;
                // A table/CTE column list is not a function call, even when its name matches a built-in.
                lockFunction = !oracle && !relationName && IsLockFunction(text, start, length);
                qualifiedRelation = relationName;
                relationName = relationName && (Is("IF") || Is("NOT") || Is("EXISTS")) ||
                    Is("INTO") || Is("TABLE") || Is("REFERENCES") || Is("WITH") || Is("RECURSIVE");
                if (words == 0 && Is("WITH")) cte = true;
                else if (depth == 0 && (Is("SELECT") || Is("INSERT") || Is("UPDATE") || Is("DELETE"))) cte = false;
                if (words == 0)
                {
                    if (Is("SET") || Is("RESET") || Is("USE") || Is("CALL") || Is("DO") || Is("EXECUTE") || Is("EXEC") ||
                        Is("PREPARE") || Is("DEALLOCATE") || Is("LOCK") || Is("UNLOCK") || Is("FLUSH") || Is("HANDLER") ||
                        Is("BEGIN") || Is("DECLARE") || Is("START") || Is("COMMIT") || Is("ROLLBACK") || Is("XA")) return true;
                    alter = Is("ALTER"); create = Is("CREATE");
                }
                else if (words == 1)
                {
                    if (alter && Is("SESSION") || create && Is("TEMPORARY")) return true;
                    temporaryScope = create && (Is("GLOBAL") || Is("LOCAL") || Is("PRIVATE"));
                }
                else if (words == 2 && temporaryScope && Is("TEMPORARY")) return true;
                if (words < 3) words++;
            }
            return false;
        }

        private static bool IsLockFunction(string text, int start, int length) =>
            length == 8 && string.Compare(text, start, "GET_LOCK", 0, length, StringComparison.OrdinalIgnoreCase) == 0 ||
            length == 12 && string.Compare(text, start, "RELEASE_LOCK", 0, length, StringComparison.OrdinalIgnoreCase) == 0 ||
            length == 17 && string.Compare(text, start, "RELEASE_ALL_LOCKS", 0, length, StringComparison.OrdinalIgnoreCase) == 0;
    }
}
