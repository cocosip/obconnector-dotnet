using System;
using System.Collections.Generic;
using System.Globalization;
using OceanBaseConnector.Internal.Sql;

namespace OceanBaseConnector.Internal.Protocol
{
    internal sealed class KeyInfoMetadata
    {
        internal sealed class ColumnInfo
        {
            internal bool? IsKey, IsUnique;
            internal bool IsHidden;
        }
        private readonly ResultColumn[] _expected;
        internal KeyInfoMetadata(ResultColumn[] columns, int visible)
        {
            _expected = columns; VisibleCount = visible; Columns = new ColumnInfo[columns.Length];
            for (var i = 0; i < columns.Length; i++) Columns[i] = new ColumnInfo { IsHidden = i >= visible };
        }
        internal int VisibleCount { get; }
        internal ColumnInfo[] Columns { get; }
        internal KeyInfoMetadata ForResult(ResultColumn[] columns)
        {
            var known = false;
            foreach (var column in Columns) known |= column.IsKey.HasValue;
            if (!known) return new KeyInfoMetadata(columns, columns.Length);
            if (columns.Length != _expected.Length) throw new ProtocolException("KeyInfo result shape changed after preparation.");
            for (var i = 0; i < columns.Length; i++)
                if (Columns[i].IsKey.HasValue) ValidateColumn(_expected[i], columns[i]);
            return this;
        }
        internal static void ValidateColumn(ResultColumn expected, ResultColumn actual)
        {
            if (expected.Name != actual.Name || expected.Database != actual.Database || expected.Table != actual.Table ||
                expected.OriginalTable != actual.OriginalTable || expected.OriginalName != actual.OriginalName ||
                expected.TypeCode != actual.TypeCode || expected.Charset != actual.Charset || expected.Flags != actual.Flags ||
                expected.Length != actual.Length || expected.Precision != actual.Precision || expected.Scale != actual.Scale)
                throw new ProtocolException("KeyInfo source metadata changed after preparation.");
        }

        internal sealed class Key
        {
            internal string Name = "", Kind = "";
            internal readonly List<string> Members = new List<string>();
            internal bool Eligible = true;
        }
        internal sealed class Catalog
        {
            private readonly bool _ignoreCase;
            private readonly Dictionary<string, Key> _keys = new Dictionary<string, Key>(StringComparer.Ordinal);
            private readonly Dictionary<string, bool> _nullable = new Dictionary<string, bool>(StringComparer.Ordinal);
            private readonly HashSet<string> _unique = new HashSet<string>(StringComparer.Ordinal);
            private string? _schema, _table;
            private int _rows, _bytes;

            internal Catalog() : this(false) { }
            internal Catalog(bool ignoreCase) => _ignoreCase = ignoreCase;
            internal void Add(string?[] row)
            {
                if (++_rows > 4096) throw new NotSupportedException("KeyInfo catalog row limit exceeded.");
                foreach (var value in row)
                    if (value != null)
                    {
                        var bytes = ResultColumn.Utf8.GetByteCount(value);
                        if (bytes > 4 * 1024 * 1024 - _bytes) throw new NotSupportedException("KeyInfo catalog byte limit exceeded.");
                        _bytes += bytes;
                    }
                if (row.Length != 8 || string.IsNullOrEmpty(row[0]) || string.IsNullOrEmpty(row[1])) throw new ProtocolException("Invalid KeyInfo table identity.");
                if (_schema != null && (_schema != row[0] || _table != row[1])) throw new ProtocolException("Ambiguous KeyInfo table identity.");
                _schema = row[0]; _table = row[1];
                if (row[2] == null)
                {
                    for (var i = 3; i < row.Length; i++) if (row[i] != null) throw new ProtocolException("Invalid empty KeyInfo index.");
                    return;
                }
                if (row[2]!.Length == 0 || row[3] != "P" && row[3] != "U" || row[6] != "YES" && row[6] != "NO" || row[7] != "YES" && row[7] != "NO" ||
                    !int.TryParse(row[5], NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal) || ordinal < 1 || ordinal > 4096)
                    throw new ProtocolException("Invalid KeyInfo index attributes.");
                if (!_keys.TryGetValue(row[2]!, out var key))
                { key = new Key { Name = row[2]!, Kind = row[3]! }; _keys.Add(key.Name, key); }
                if (key.Kind != row[3] || ordinal != key.Members.Count + 1) throw new ProtocolException("Incomplete or unordered KeyInfo index.");
                var member = row[4] ?? "";
                if (member.Length == 0 || row[7] != "YES" || key.Members.Contains(member)) key.Eligible = false;
                if (member.Length != 0)
                {
                    var nullable = row[6] == "YES";
                    if (_nullable.TryGetValue(member, out var previous) && nullable != previous) throw new ProtocolException("Conflicting KeyInfo nullability.");
                    _nullable[member] = nullable;
                }
                key.Members.Add(member);
            }

            internal Key? SelectKey()
            {
                Key? selected = null;
                foreach (var key in _keys.Values)
                {
                    if (!key.Eligible) continue;
                    if (key.Members.Count == 1) _unique.Add(key.Members[0]);
                    var nullable = false;
                    foreach (var member in key.Members) nullable |= _nullable[member];
                    if (nullable) continue;
                    if (selected == null || key.Kind == "P" && selected.Kind != "P" || key.Kind == selected.Kind &&
                        (key.Members.Count < selected.Members.Count || key.Members.Count == selected.Members.Count && string.CompareOrdinal(key.Name, selected.Name) < 0)) selected = key;
                }
                return selected;
            }

            internal bool Matches(KeyInfoQuery query, ResultColumn[] columns)
            {
                if (_schema == null || !SameIdentifier(query.Table, _table) || query.Schema != null && !SameIdentifier(query.Schema, _schema)) return false;
                foreach (var column in columns)
                    if (column.OriginalTable.Length != 0 && (!SameIdentifier(column.OriginalTable, _table) || !SameIdentifier(column.Database, _schema))) return false;
                return true;
            }

            private bool SameIdentifier(string? left, string? right) => string.Equals(left, right,
                _ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

            internal List<string> Missing(Key? key, ResultColumn[] columns)
            {
                var result = new List<string>();
                if (key == null) return result;
                foreach (var member in key.Members)
                {
                    var found = false;
                    foreach (var column in columns) found |= IsSource(column, member);
                    if (!found) result.Add(member);
                }
                return result;
            }

            internal void Apply(KeyInfoMetadata metadata, ResultColumn[] columns, Key? key)
            {
                for (var i = 0; i < columns.Length; i++)
                {
                    var column = columns[i];
                    if (!IsSource(column, column.OriginalName)) continue;
                    metadata.Columns[i].IsKey = key != null && key.Members.Contains(column.OriginalName);
                    metadata.Columns[i].IsUnique = _unique.Contains(column.OriginalName);
                    if (_nullable.TryGetValue(column.OriginalName, out var nullable) && nullable != ((column.Flags & 1) == 0))
                        throw new ProtocolException("KeyInfo source nullability changed after preparation.");
                }
            }

            internal bool IsSource(ResultColumn column, string member) => member.Length != 0 && column.Database == _schema && column.OriginalTable == _table && column.OriginalName == member;
        }
    }
}
