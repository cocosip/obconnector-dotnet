using System;
using System.Text;
using OceanBaseConnector.Internal.Protocol;

namespace OceanBaseConnector.Internal.Sql
{
    internal sealed class MySqlLiteralContext
    {
        private readonly bool _mb3;
        private readonly bool _noBackslashEscapes;
        private readonly ClientCharacterSet? _clientEncoding;

        private MySqlLiteralContext(bool mb3, bool noBackslashEscapes, ClientCharacterSet? clientEncoding)
        { _mb3 = mb3; _noBackslashEscapes = noBackslashEscapes; _clientEncoding = clientEncoding; }

        internal static MySqlLiteralContext Parse(string client, string connection, string collation, string modes, ClientCharacterSet? configuredEncoding = null)
        {
            var clientMb3 = IsMb3(client);
            var connectionMb3 = IsMb3(connection);
            var nonUtf8 = configuredEncoding != null && configuredEncoding.Name != "utf8mb4" && configuredEncoding.Name != "utf8mb3";
            if (nonUtf8)
            {
                if (!configuredEncoding!.Matches(client) || !configuredEncoding.Matches(connection))
                    throw new NotSupportedException("The literal session character sets do not match the configured client encoding.");
            }
            else if ((!clientMb3 && !string.Equals(client, "utf8mb4", StringComparison.OrdinalIgnoreCase)) ||
                (!connectionMb3 && !string.Equals(connection, "utf8mb4", StringComparison.OrdinalIgnoreCase)))
                throw new NotSupportedException("MySQL client string preparation requires UTF-8 or a matching configured client character set.");
            ValidateName(collation);
            if (!(nonUtf8 ? configuredEncoding!.MatchesCollation(collation) :
                connectionMb3 ? collation.StartsWith("utf8_", StringComparison.OrdinalIgnoreCase) || collation.StartsWith("utf8mb3_", StringComparison.OrdinalIgnoreCase)
                : collation.StartsWith("utf8mb4_", StringComparison.OrdinalIgnoreCase)))
                throw new NotSupportedException("The MySQL connection collation does not match its character set.");
            var noBackslashEscapes = false;
            if (modes.Length != 0)
                foreach (var mode in modes.Split(','))
                {
                    ValidateName(mode);
                    if (string.Equals(mode, "NO_BACKSLASH_ESCAPES", StringComparison.OrdinalIgnoreCase)) noBackslashEscapes = true;
                }
            return new MySqlLiteralContext(clientMb3 || connectionMb3, noBackslashEscapes, nonUtf8 ? configuredEncoding : null);
        }

        private static bool IsMb3(string name) => string.Equals(name, "utf8", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "utf8mb3", StringComparison.OrdinalIgnoreCase);

        private static void ValidateName(string value)
        {
            if (value.Length == 0 || value.Length > 128) throw new NotSupportedException("Invalid MySQL literal session state.");
            foreach (var c in value)
                if (!(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '_'))
                    throw new NotSupportedException("Invalid MySQL literal session state.");
        }

        internal void ValidateText(string text)
        {
            _ = (_clientEncoding ?? (Encoding)ResultColumn.Utf8).GetByteCount(text);
            if (_mb3)
                foreach (var c in text)
                    if (char.IsSurrogate(c)) throw new ArgumentException("Supplementary characters cannot be client prepared through UTF8MB3.");
        }

        internal string Literal(string text)
        {
            ValidateText(text);
            if (_noBackslashEscapes && text.IndexOf('\0') >= 0)
                throw new NotSupportedException("MySQL client NUL literals require backslash escapes.");
            var literal = new StringBuilder().Append('\'');
            foreach (var c in text)
            {
                if (c == '\'') literal.Append("''");
                else if (_noBackslashEscapes) literal.Append(c);
                else
                    switch (c)
                    {
                        case '\\': literal.Append("\\\\"); break;
                        case '\0': literal.Append("\\0"); break;
                        case '\b': literal.Append("\\b"); break;
                        case '\n': literal.Append("\\n"); break;
                        case '\r': literal.Append("\\r"); break;
                        case '\t': literal.Append("\\t"); break;
                        case '\u001a': literal.Append("\\Z"); break;
                        default: literal.Append(c); break;
                    }
            }
            return literal.Append('\'').ToString();
        }
    }
}
