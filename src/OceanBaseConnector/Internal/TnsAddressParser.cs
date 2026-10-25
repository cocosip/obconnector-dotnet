using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace OceanBaseConnector.Internal
{
    internal static class TnsAddressParser
    {
        internal static string Parse(string text, string alias, string? service, CancellationToken token)
        {
            var selected = new Parser(text, token).Select(alias);
            if (!Is(selected.Key, "DESCRIPTION")) throw Unsupported();
            var addresses = new List<Node>(); Node? connect = null; Node? list = null;
            foreach (var child in Children(selected))
            {
                if (Is(child.Key, "ADDRESS")) addresses.Add(child);
                else if (Is(child.Key, "ADDRESS_LIST"))
                {
                    if (list != null) throw Unsupported();
                    list = child;
                }
                else if (Is(child.Key, "CONNECT_DATA"))
                {
                    if (connect != null) throw Invalid();
                    connect = child;
                }
                else throw Unsupported();
            }
            if (list != null)
            {
                if (addresses.Count != 0) throw Unsupported();
                foreach (var child in Children(list))
                {
                    if (!Is(child.Key, "ADDRESS")) throw Unsupported();
                    addresses.Add(child);
                }
            }
            if (addresses.Count == 0 || addresses.Count > 256) throw Invalid();
            if (connect != null)
            {
                var fields = Children(connect);
                if (fields.Count != 1 || !Is(fields[0].Key, "SERVICE_NAME")) throw Unsupported();
                var declared = Value(fields[0]);
                if (service == null) throw new NotSupportedException("TNS SERVICE_NAME requires an expected service name.");
                if (!string.Equals(declared, service, StringComparison.Ordinal)) throw Invalid();
            }
            else if (service != null) throw Invalid();
            var result = new StringBuilder();
            foreach (var address in addresses)
            {
                token.ThrowIfCancellationRequested();
                string? host = null, port = null, protocol = null;
                foreach (var child in Children(address))
                {
                    if (Is(child.Key, "HOST")) { if (host != null) throw Invalid(); host = Value(child); }
                    else if (Is(child.Key, "PORT")) { if (port != null) throw Invalid(); port = Value(child); }
                    else if (Is(child.Key, "PROTOCOL")) { if (protocol != null) throw Invalid(); protocol = Value(child); }
                    else throw Unsupported();
                }
                if (host == null || port == null || protocol == null) throw Invalid();
                if (!Is(protocol, "TCP")) throw Unsupported();
                if (host.IndexOf(',') >= 0) throw Invalid();
                var endpointText = (host.IndexOf(':') >= 0 && !host.StartsWith("[", StringComparison.Ordinal) ? "[" + host + "]" : host) + ":" + port;
                OceanBaseEndpoint[] parsed;
                try { parsed = OceanBaseEndpoint.Parse(endpointText, 2881); }
                catch (ArgumentException) { throw Invalid(); }
                if (parsed.Length != 1) throw Invalid();
                if (result.Length != 0) result.Append(',');
                result.Append(parsed[0].ToString());
                if (result.Length > 65536) throw Invalid();
            }
            token.ThrowIfCancellationRequested();
            return result.ToString();
        }

        private static bool Is(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        private static FormatException Invalid() => new FormatException("Invalid or excessive TNS address configuration.");
        private static NotSupportedException Unsupported() => new NotSupportedException("Unsupported TNS descriptor option or protocol.");
        private static List<Node> Children(Node node) => node.Children ?? throw Invalid();
        private static string Value(Node node) => node.Children == null && !string.IsNullOrWhiteSpace(node.Value) ? node.Value! : throw Invalid();

        private sealed class Node
        {
            internal Node(string key, string? value, List<Node>? children) { Key = key; Value = value; Children = children; }
            internal string Key { get; }
            internal string? Value { get; }
            internal List<Node>? Children { get; }
        }

        private sealed class Parser
        {
            private readonly string _text;
            private readonly CancellationToken _token;
            private readonly HashSet<string> _aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private int _position;
            private int _nodes;
            internal Parser(string text, CancellationToken token) { _text = text; _token = token; _position = text.Length != 0 && text[0] == '\uFEFF' ? 1 : 0; }

            internal Node Select(string alias)
            {
                Node? selected = null;
                Skip();
                while (_position < _text.Length)
                {
                    var wrapped = Take('(');
                    var aliases = new List<string> { Alias() };
                    while (Take(',')) aliases.Add(Alias());
                    Expect('='); var node = ReadNode(1);
                    if (wrapped) Expect(')');
                    foreach (var name in aliases) if (Is(name, alias)) selected = node;
                    Skip();
                }
                _token.ThrowIfCancellationRequested();
                return selected ?? throw new FormatException("Requested TNS alias is missing.");
            }

            private string Alias()
            {
                Count(); var name = Name();
                if (Is(name, "IFILE")) throw Unsupported();
                if (!_aliases.Add(name)) throw Invalid();
                return name;
            }

            private Node ReadNode(int depth)
            {
                Count();
                if (depth > 16) throw Invalid();
                Expect('('); var key = Name();
                if (Is(key, "IFILE")) throw Unsupported();
                Expect('='); Skip();
                List<Node>? children = null; string? value = null;
                if (_position < _text.Length && _text[_position] == '(')
                {
                    children = new List<Node>();
                    do { children.Add(ReadNode(depth + 1)); Skip(); } while (_position < _text.Length && _text[_position] == '(');
                }
                else if (_position < _text.Length && (_text[_position] == '\'' || _text[_position] == '"')) value = Quoted();
                else
                {
                    var start = _position;
                    while (_position < _text.Length && _text[_position] != ')' && _text[_position] != '#')
                    {
                        if (_text[_position] == '(' || _text[_position] == '=') throw Invalid();
                        _position++;
                    }
                    value = _text.Substring(start, _position - start).Trim();
                }
                Expect(')'); return new Node(key, value, children);
            }

            private string Name()
            {
                Skip();
                if (_position < _text.Length && (_text[_position] == '\'' || _text[_position] == '"'))
                {
                    var name = Quoted();
                    if (string.IsNullOrWhiteSpace(name)) throw Invalid();
                    return name;
                }
                var start = _position;
                while (_position < _text.Length && !char.IsWhiteSpace(_text[_position]) && "=(),#".IndexOf(_text[_position]) < 0) _position++;
                if (start == _position) throw Invalid();
                return _text.Substring(start, _position - start);
            }

            private string Quoted()
            {
                var quote = _text[_position++]; var value = new StringBuilder();
                while (_position < _text.Length)
                {
                    var next = _text[_position++];
                    if (next != quote) { value.Append(next); continue; }
                    if (_position < _text.Length && _text[_position] == quote) { value.Append(quote); _position++; }
                    else return value.ToString();
                }
                throw Invalid();
            }

            private bool Take(char expected)
            {
                Skip();
                if (_position == _text.Length || _text[_position] != expected) return false;
                _position++; return true;
            }
            private void Expect(char expected) { if (!Take(expected)) throw Invalid(); }
            private void Count() { _token.ThrowIfCancellationRequested(); if (++_nodes > 4096) throw Invalid(); }
            private void Skip()
            {
                _token.ThrowIfCancellationRequested();
                while (_position < _text.Length)
                {
                    if (char.IsWhiteSpace(_text[_position])) _position++;
                    else if (_text[_position] == '#') while (_position < _text.Length && _text[_position] != '\n') _position++;
                    else break;
                }
            }
        }
    }
}
