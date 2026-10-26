using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;

namespace OceanBaseConnector.Internal
{
    internal static class TnsAddressParser
    {
        internal static string Parse(string text, string alias, string? service, CancellationToken token) =>
            ParseSnapshot(text, alias, service, token).CanonicalAddresses;

        internal static DiscoverySnapshot ParseSnapshot(string text, string alias, string? service, CancellationToken token)
        {
            var selected = new Parser(text, token).Select(alias);
            if (!Is(selected.Key, "DESCRIPTION")) throw Unsupported();
            var children = Children(selected);
            var strategy = Policy(children, DiscoveryStrategy.Inherit, true);
            var addresses = new List<Node>(); var lists = new List<Node>(); Node? connect = null;
            DiscoveryBlacklistPolicy? blacklist = null; int? retry = null;
            foreach (var child in children)
            {
                if (Is(child.Key, "ADDRESS")) addresses.Add(child);
                else if (Is(child.Key, "ADDRESS_LIST")) lists.Add(child);
                else if (Is(child.Key, "CONNECT_DATA"))
                {
                    if (connect != null) throw Invalid();
                    connect = child;
                }
                else if (Is(child.Key, "OBLB_BLACKLIST"))
                {
                    if (blacklist != null) throw Invalid();
                    blacklist = ReadBlacklist(child);
                }
                else if (Is(child.Key, "OBLB_RETRY_ALL_DOWNS"))
                {
                    if (retry.HasValue) throw Invalid();
                    retry = Unsigned(child);
                }
                else if (!IsPolicy(child.Key, true)) throw Unsupported();
            }
            if (lists.Count != 0 && addresses.Count != 0) throw Unsupported();
            if (connect != null)
            {
                var fields = Children(connect);
                if (fields.Count != 1 || !Is(fields[0].Key, "SERVICE_NAME")) throw Unsupported();
                var declared = Value(fields[0]);
                if (service == null) throw new NotSupportedException("TNS SERVICE_NAME requires an expected service name.");
                if (!string.Equals(declared, service, StringComparison.Ordinal)) throw Invalid();
            }
            else if (service != null) throw Invalid();
            var groups = new List<DiscoveryGroup>();
            if (lists.Count == 0) groups.Add(ReadGroup(addresses, strategy, token));
            else foreach (var list in lists)
            {
                var fields = Children(list); var members = new List<Node>(); Node? localBlacklist = null;
                foreach (var child in fields)
                    if (Is(child.Key, "ADDRESS")) members.Add(child);
                    else if (Is(child.Key, "OBLB_BLACKLIST"))
                    {
                        if (localBlacklist != null) throw Invalid();
                        localBlacklist = child;
                    }
                    else if (!IsPolicy(child.Key, false)) throw Unsupported();
                groups.Add(ReadGroup(members, Policy(fields, strategy, false), token, localBlacklist == null ? null : ReadBlacklist(localBlacklist)));
            }
            token.ThrowIfCancellationRequested();
            return new DiscoverySnapshot(groups, blacklist, retry);
        }

        private static DiscoveryBlacklistPolicy ReadBlacklist(Node node)
        {
            var fields = node.Children == null && node.Value == "" ? new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase) : Fields(node, "APPEND_STRATEGY", "REMOVE_STRATEGY");
            var retryDuration = false; var retryTimes = 1; var duration = 0; var timeout = 50;
            if (fields.TryGetValue("APPEND_STRATEGY", out var append))
            {
                var values = Fields(append, "NAME", "RETRYTIMES", "DURATION");
                var name = Value(Required(values, "NAME"));
                if (Is(name, "RETRYDURATION"))
                {
                    retryDuration = true; retryTimes = Unsigned(Required(values, "RETRYTIMES")); duration = Unsigned(Required(values, "DURATION"));
                }
                else if (!Is(name, "NORMAL") || values.Count != 1) throw Unsupported();
            }
            if (fields.TryGetValue("REMOVE_STRATEGY", out var remove))
            {
                var values = Fields(remove, "NAME", "TIMEOUT");
                if (!Is(Value(Required(values, "NAME")), "TIMEOUT")) throw Unsupported();
                timeout = Unsigned(Required(values, "TIMEOUT"));
            }
            return new DiscoveryBlacklistPolicy(retryDuration, retryTimes, duration, timeout);
        }
        private static Dictionary<string, Node> Fields(Node node, params string[] allowed)
        {
            var result = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in Children(node))
            {
                var known = false;
                foreach (var key in allowed) if (Is(field.Key, key)) { known = true; break; }
                if (!known) throw Unsupported();
                if (result.ContainsKey(field.Key)) throw Invalid();
                result.Add(field.Key, field);
            }
            return result;
        }
        private static Node Required(Dictionary<string, Node> fields, string key) => fields.TryGetValue(key, out var value) ? value : throw Invalid();
        private static int Unsigned(Node node) => int.TryParse(Value(node), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : throw Invalid();

        private static DiscoveryGroup ReadGroup(List<Node> addresses, DiscoveryStrategy strategy, CancellationToken token, DiscoveryBlacklistPolicy? blacklist = null)
        {
            if (addresses.Count == 0 || addresses.Count > 256) throw Invalid();
            var endpoints = new List<OceanBaseEndpoint>(); var weights = new List<int>(); long total = 0;
            foreach (var address in addresses)
            {
                token.ThrowIfCancellationRequested();
                string? host = null, port = null, protocol = null, weight = null;
                foreach (var child in Children(address))
                {
                    if (Is(child.Key, "HOST")) { if (host != null) throw Invalid(); host = Value(child); }
                    else if (Is(child.Key, "PORT")) { if (port != null) throw Invalid(); port = Value(child); }
                    else if (Is(child.Key, "PROTOCOL")) { if (protocol != null) throw Invalid(); protocol = Value(child); }
                    else if (Is(child.Key, "WEIGHT")) { if (weight != null) throw Invalid(); weight = Value(child); }
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
                var amount = 1;
                if (weight != null)
                {
                    if (strategy != DiscoveryStrategy.ServerAffinity) throw Unsupported();
                    if (!int.TryParse(weight, NumberStyles.None, CultureInfo.InvariantCulture, out amount)) throw Invalid();
                }
                endpoints.Add(parsed[0]); weights.Add(amount); total += amount;
            }
            if (strategy == DiscoveryStrategy.ServerAffinity && total == 0) throw Invalid();
            return new DiscoveryGroup(endpoints, weights, strategy, blacklist);
        }

        private static bool IsPolicy(string key, bool description) => Is(key, "OBLB") || Is(key, "LOAD_BALANCE") ||
            Is(key, "OBLB_STRATEGY") || Is(key, "LOAD_BALANCE_STRATEGY") || (description && Is(key, "OBLB_GROUP_STRATEGY"));

        private static DiscoveryStrategy Policy(List<Node> fields, DiscoveryStrategy inherited, bool description)
        {
            DiscoveryStrategy? strategy = null; var enabled = false;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in fields)
            {
                if (!IsPolicy(field.Key, description)) continue;
                var key = Is(field.Key, "LOAD_BALANCE") ? "OBLB" : Is(field.Key, "LOAD_BALANCE_STRATEGY") ? "OBLB_STRATEGY" : field.Key;
                if (!seen.Add(key)) throw Invalid();
                var value = Value(field);
                if (Is(key, "OBLB"))
                {
                    if (!Is(value, "ON")) throw Unsupported();
                    enabled = true;
                }
                else if (Is(key, "OBLB_GROUP_STRATEGY"))
                {
                    if (!Is(value, "ROTATION")) throw Unsupported();
                }
                else if (Is(value, "ROTATION")) strategy = DiscoveryStrategy.Rotation;
                else if (Is(value, "RANDOM")) strategy = DiscoveryStrategy.Random;
                else if (Is(value, "SERVERAFFINITY")) strategy = DiscoveryStrategy.ServerAffinity;
                else throw Unsupported();
            }
            return strategy ?? (enabled ? DiscoveryStrategy.Random : inherited);
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
