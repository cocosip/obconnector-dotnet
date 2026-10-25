using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace OceanBaseConnector.Internal
{
    internal static class OcpAddressParser
    {
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        internal static string Parse(byte[] bytes, string name, string? service, CancellationToken token)
            => ParseSnapshot(bytes, name, service, token).CanonicalAddresses;

        internal static DiscoverySnapshot ParseSnapshot(byte[] bytes, string name, string? service, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Node root;
            try
            {
                root = new Parser(Utf8.GetString(bytes), token).Parse();
            }
            catch (DecoderFallbackException) { throw Invalid(); }
            var envelope = Object(root);
            foreach (var flag in new[] { "successful", "success" })
                if (envelope.TryGetValue(flag, out var success) && (success.Type != "boolean" || success.Value != "true")) throw Invalid();
            if (envelope.TryGetValue("status", out var status) && (status.Type != "number" || status.Value != "200")) throw Invalid();
            var data = Object(Required(envelope, "data"), "NET_SERVICE_NAME", "DESCRIPTION");
            if (!string.Equals(String(Required(data, "NET_SERVICE_NAME")), name, StringComparison.Ordinal)) throw Invalid();
            var description = Object(Required(data, "DESCRIPTION"), "ADDRESS_LIST", "CONNECT_DATA", "OBLB", "LOAD_BALANCE", "OBLB_STRATEGY", "LOAD_BALANCE_STRATEGY", "OBLB_GROUP_STRATEGY", "OBLB_BLACKLIST", "OBLB_RETRY_ALL_DOWNS");
            var groups = Array(Required(description, "ADDRESS_LIST"));
            if (groups.Count == 0 || groups.Count > 256) throw Invalid();
            var tns = new StringBuilder("APP=(DESCRIPTION=");
            AppendPolicies(tns, description);
            if (description.TryGetValue("OBLB_RETRY_ALL_DOWNS", out var retry)) tns.Append("(OBLB_RETRY_ALL_DOWNS=").Append(Quote(Integer(retry))).Append(')');
            if (description.TryGetValue("OBLB_BLACKLIST", out var blacklist)) AppendBlacklist(tns, blacklist);
            foreach (var groupNode in groups)
            {
                var group = Object(groupNode, "ADDRESS", "OBLB", "LOAD_BALANCE", "OBLB_STRATEGY", "LOAD_BALANCE_STRATEGY");
                var addresses = Array(Required(group, "ADDRESS"));
                if (addresses.Count == 0 || addresses.Count > 256) throw Invalid();
                tns.Append("(ADDRESS_LIST="); AppendPolicies(tns, group);
                foreach (var address in addresses)
                {
                    var fields = Object(address, "PROTOCOL", "HOST", "PORT", "WEIGHT");
                    tns.Append("(ADDRESS=(PROTOCOL=").Append(Quote(String(Required(fields, "PROTOCOL"))))
                        .Append(")(HOST=").Append(Quote(String(Required(fields, "HOST")))).Append(")(PORT=")
                        .Append(Quote(Integer(Required(fields, "PORT")))).Append(')');
                    if (fields.TryGetValue("WEIGHT", out var weight)) tns.Append("(WEIGHT=").Append(Quote(Integer(weight))).Append(')');
                    tns.Append(')');
                }
                tns.Append(')');
            }
            if (description.TryGetValue("CONNECT_DATA", out var connect))
            {
                var fields = Object(connect, "SERVICE_NAME");
                tns.Append("(CONNECT_DATA=(SERVICE_NAME=").Append(Quote(String(Required(fields, "SERVICE_NAME")))).Append("))");
            }
            tns.Append(')');
            return TnsAddressParser.ParseSnapshot(tns.ToString(), "APP", service, token);
        }

        private static void AppendBlacklist(StringBuilder tns, Node node)
        {
            var fields = Object(node, "APPEND_STRATEGY", "REMOVE_STRATEGY");
            tns.Append("(OBLB_BLACKLIST=");
            foreach (var key in new[] { "APPEND_STRATEGY", "REMOVE_STRATEGY" })
            {
                if (!fields.TryGetValue(key, out var value)) continue;
                var strategy = key == "APPEND_STRATEGY" ? Object(value, "NAME", "RETRYTIMES", "DURATION") : Object(value, "NAME", "TIMEOUT");
                tns.Append('(').Append(key).Append("=(NAME=").Append(Quote(String(Required(strategy, "NAME")))).Append(')');
                foreach (var number in new[] { "RETRYTIMES", "DURATION", "TIMEOUT" })
                    if (strategy.TryGetValue(number, out var amount)) tns.Append('(').Append(number).Append('=').Append(Quote(Integer(amount))).Append(')');
                tns.Append(')');
            }
            tns.Append(')');
        }

        private static void AppendPolicies(StringBuilder tns, Dictionary<string, Node> fields)
        {
            foreach (var key in new[] { "OBLB", "LOAD_BALANCE", "OBLB_STRATEGY", "LOAD_BALANCE_STRATEGY", "OBLB_GROUP_STRATEGY" })
                if (fields.TryGetValue(key, out var value)) tns.Append('(').Append(key).Append('=').Append(Quote(String(value))).Append(')');
        }
        private static string Integer(Node node)
        {
            if ((node.Type != "string" && node.Type != "number") || node.Value.Length == 0) throw Invalid();
            foreach (var digit in node.Value) if (digit < '0' || digit > '9') throw Invalid();
            return node.Value;
        }

        private static Dictionary<string, Node> Object(Node node, params string[] allowed)
        {
            if (node.Type != "object") throw Invalid();
            var result = new Dictionary<string, Node>(StringComparer.Ordinal);
            foreach (var child in node.Children)
            {
                if (allowed.Length != 0 && System.Array.IndexOf(allowed, child.Name) < 0) throw Unsupported();
                result.Add(child.Name, child);
            }
            return result;
        }
        private static List<Node> Array(Node node) => node.Type == "array" ? node.Children : throw Invalid();
        private static string String(Node node) => node.Type == "string" ? node.Value : throw Invalid();
        private static Node Required(Dictionary<string, Node> fields, string key) => fields.TryGetValue(key, out var value) ? value : throw Invalid();
        private static string Quote(string text) => "\"" + text.Replace("\"", "\"\"") + "\"";
        private static FormatException Invalid() => new FormatException("Invalid or excessive OCP discovery response.");
        private static NotSupportedException Unsupported() => new NotSupportedException("Unsupported OCP descriptor policy or shape.");
        private sealed class Node
        {
            internal Node(string name, string type) { Name = name; Type = type; }
            internal string Name { get; }
            internal string Type { get; }
            internal string Value { get; set; } = "";
            internal List<Node> Children { get; } = new List<Node>();
        }

        // The framework JSON-to-XML reader repairs truncated input and loses __type keys.
        private sealed class Parser
        {
            private readonly string _text;
            private readonly CancellationToken _token;
            private int _position;
            private int _count;
            internal Parser(string text, CancellationToken token) { _text = text; _token = token; _position = text.Length > 0 && text[0] == '\uFEFF' ? 1 : 0; }
            internal Node Parse()
            {
                var node = Read("root", 1); Skip();
                if (_position != _text.Length) throw Invalid();
                _token.ThrowIfCancellationRequested(); return node;
            }
            private Node Read(string name, int depth)
            {
                _token.ThrowIfCancellationRequested();
                if (++_count > 4096 || depth > 16) throw Invalid();
                Skip(); var next = Peek;
                if (next == '{' || next == '[')
                {
                    _position++; var isObject = next == '{'; var end = isObject ? '}' : ']';
                    var node = new Node(name, isObject ? "object" : "array");
                    var keys = new HashSet<string>(StringComparer.Ordinal);
                    if (Take(end)) return node;
                    do
                    {
                        if (_count == 4096) throw Invalid();
                        var key = "item";
                        if (isObject)
                        {
                            key = Quoted();
                            if (key == "__type") throw Unsupported();
                            if (!keys.Add(key)) throw Invalid();
                            Expect(':');
                        }
                        node.Children.Add(Read(key, depth + 1));
                        if (Take(end)) return node;
                        Expect(',');
                    } while (true);
                }
                if (next == '"') return new Node(name, "string") { Value = Quoted() };
                foreach (var literal in new[] { "true", "false", "null" })
                    if (_position + literal.Length <= _text.Length && string.CompareOrdinal(_text, _position, literal, 0, literal.Length) == 0)
                    {
                        _position += literal.Length;
                        return new Node(name, literal == "null" ? "null" : "boolean") { Value = literal };
                    }
                var start = _position;
                TakeRaw('-');
                if (!TakeRaw('0')) Digits(nonzero: true);
                if (TakeRaw('.')) Digits(nonzero: false);
                if (TakeRaw('e') || TakeRaw('E')) { if (Peek == '+' || Peek == '-') _position++; Digits(nonzero: false); }
                return new Node(name, "number") { Value = _text.Substring(start, _position - start) };
            }
            private void Digits(bool nonzero)
            {
                if (Peek < (nonzero ? '1' : '0') || Peek > '9') throw Invalid();
                do { _position++; } while (Peek >= '0' && Peek <= '9');
            }
            private string Quoted()
            {
                Expect('"'); var value = new StringBuilder();
                while (_position < _text.Length)
                {
                    var next = _text[_position++];
                    if (next == '"') return value.ToString();
                    if (next < 32) throw Invalid();
                    if (next != '\\') { value.Append(next); continue; }
                    if (_position == _text.Length) throw Invalid();
                    switch (_text[_position++])
                    {
                        case '"': value.Append('"'); break;
                        case '\\': value.Append('\\'); break;
                        case '/': value.Append('/'); break;
                        case 'b': value.Append('\b'); break;
                        case 'f': value.Append('\f'); break;
                        case 'n': value.Append('\n'); break;
                        case 'r': value.Append('\r'); break;
                        case 't': value.Append('\t'); break;
                        case 'u':
                            var code = 0;
                            for (var i = 0; i < 4; i++)
                            {
                                if (_position == _text.Length) throw Invalid();
                                var digit = _text[_position++];
                                var hex = digit >= '0' && digit <= '9' ? digit - '0' : digit >= 'a' && digit <= 'f' ? digit - 'a' + 10 : digit >= 'A' && digit <= 'F' ? digit - 'A' + 10 : -1;
                                if (hex < 0) throw Invalid();
                                code = code * 16 + hex;
                            }
                            value.Append((char)code); break;
                        default: throw Invalid();
                    }
                }
                throw Invalid();
            }
            private char Peek => _position < _text.Length ? _text[_position] : '\0';
            private void Skip() { while (Peek == ' ' || Peek == '\t' || Peek == '\r' || Peek == '\n') _position++; }
            private bool TakeRaw(char expected) { if (Peek != expected) return false; _position++; return true; }
            private bool Take(char expected) { Skip(); return TakeRaw(expected); }
            private void Expect(char expected) { if (!Take(expected)) throw Invalid(); }
        }
    }
}
