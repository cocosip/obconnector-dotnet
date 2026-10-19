using System;
using System.Collections.Generic;
using OceanBaseConnector.Internal.Protocol;

namespace OceanBaseConnector.Internal.Sql
{
    internal sealed class OracleArrayBindingPlan
    {
        private readonly bool _pl;
        private readonly OceanBaseComplexType[] _outputs;
        private readonly OceanBaseArrayBindingStatus[] _status;
        private readonly long?[] _affected;
        private readonly int[] _errors;
        private readonly List<object[]>?[] _rows;
        private int _next;
        private int _lastOutput = -1;
        private long _bytes;
        private int _cells;
        internal OceanBaseArrayBindingResult? Completed { get; private set; }
        internal bool Published { get; set; }
        internal bool AcceptsFlags(uint flags) => _outputs.Length == 0 ? flags == 2 : _pl ? flags == 6 || flags == 7 : flags == 3;

        internal OracleArrayBindingPlan(int count, bool pl, OceanBaseComplexType[] outputs)
        {
            if (count < 1 || count > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(count));
            if (outputs.Length > 4093) throw new ArgumentOutOfRangeException(nameof(outputs));
            _pl = pl; _outputs = outputs; _status = new OceanBaseArrayBindingStatus[count];
            _affected = new long?[count]; _errors = new int[count]; _rows = new List<object[]>?[count];
        }

        internal void ValidateColumn(int ordinal, ResultColumn column)
        {
            if (ordinal == 0 || ordinal == 1)
            {
                if (column.Name != (ordinal == 0 ? "sql_no" : "error_code") || column.TypeCode != 8 || column.Unsigned)
                    throw Invalid();
            }
            else if (ordinal == _outputs.Length + 2)
            { if (column.Name != "error_message" || column.TypeCode != 253 || column.FieldType != typeof(string)) throw Invalid(); }
            else
            {
                var member = _outputs[ordinal - 2].Members[0];
                if (member.ComplexType != null) column.BindComplexOutput(member.ComplexType);
                else if (member.Layout.TypeCode == 0 ? !column.IsDecimal : column.TypeCode != member.Layout.TypeCode) throw Invalid();
                if ((member.Layout.TypeCode == 253 || member.Layout.TypeCode == 254) && column.FieldType != typeof(string)) throw Invalid();
            }
            _ = column.FieldType;
        }

        internal void Capture(ResultColumn[] columns, byte[]?[] row)
        {
            if (columns.Length != _outputs.Length + 3 || row.Length != columns.Length) throw Invalid();
            for (var i = 0; i < columns.Length; i++) ValidateColumn(i, columns[i]);
            if (row[0] == null || row[1] == null) throw Invalid();
            var position = (long)columns[0].DecodeBinary(row[0]); var code = (long)columns[1].DecodeBinary(row[1]);
            if (position < 0 || code < int.MinValue || code > ushort.MaxValue || code == 0 && row[row.Length - 1] != null) throw Invalid();
            if (row[row.Length - 1] != null) _ = columns[columns.Length - 1].DecodeBinary(row[row.Length - 1]);
            var indexed = _pl || _outputs.Length != 0 || code != 0;
            var index = indexed ? position < _status.Length ? (int)position : throw Invalid() : _next;
            if (index >= _status.Length || index < _next || indexed && _outputs.Length == 0 && index != _next ||
                _errors[index] != 0 || index < _lastOutput) throw Invalid();
            if (_pl && _outputs.Length != 0 && index > _next + (_rows[_next] == null ? 0 : 1)) throw Invalid();
            object[]? values = null;
            if (code != 0)
            { for (var i = 2; i < row.Length - 1; i++) if (row[i] != null) throw Invalid(); }
            else if (_outputs.Length != 0)
            {
                if (_cells > ushort.MaxValue - _outputs.Length) throw Invalid();
                values = new object[_outputs.Length]; var bytes = _bytes;
                for (var i = 0; i < values.Length; i++)
                {
                    bytes += row[i + 2]?.Length ?? 0;
                    if (bytes > PacketChannel.DefaultMaxPacketSize) throw Invalid();
                    values[i] = columns[i + 2].DecodeBinary(row[i + 2]);
                }
                _bytes = bytes;
            }
            if (_outputs.Length != 0)
            {
                if (_lastOutput >= 0 && index > _lastOutput && _errors[_lastOutput] == 0) _status[_lastOutput] = OceanBaseArrayBindingStatus.Succeeded;
                for (var i = _next; i < index; i++) _status[i] = OceanBaseArrayBindingStatus.Succeeded;
            }
            if (code != 0)
            {
                _status[index] = OceanBaseArrayBindingStatus.Failed; _errors[index] = (int)code;
                _next = index + 1;
            }
            else if (_outputs.Length == 0)
            {
                _status[index] = OceanBaseArrayBindingStatus.Succeeded; _affected[index] = _pl ? (long?)null : position; _next++;
            }
            else
            {
                (_rows[index] ??= new List<object[]>()).Add(values!); _cells += values!.Length;
                _lastOutput = index; _next = index;
            }
        }

        internal OceanBaseArrayBindingResult Finish(long affected)
        {
            if (affected < 0) throw Invalid();
            if (_outputs.Length == 0)
            {
                if (_next != _status.Length) throw Invalid();
                if (!_pl)
                {
                    long total = 0;
                    foreach (var count in _affected)
                    {
                        if (count > long.MaxValue - total) throw Invalid();
                        total += count ?? 0;
                    }
                    if (total != affected) throw Invalid();
                }
            }
            else
            {
                if (_pl)
                    for (var i = 0; i < _status.Length; i++) if (_rows[i] == null && _errors[i] == 0) throw Invalid();
            }
            return Completed = Create(true, affected);
        }

        internal OceanBaseArrayBindingResult Snapshot() => Create(false, null);
        private OceanBaseArrayBindingResult Create(bool complete, long? affected)
        {
            var items = new OceanBaseArrayBindingItem[_status.Length];
            for (var i = 0; i < items.Length; i++)
            {
                // A terminal OK is provisional until CommandExchange validates the enclosing OB20 boundary.
                var status = complete && _errors[i] == 0 ? OceanBaseArrayBindingStatus.Succeeded : _status[i];
                items[i] = new OceanBaseArrayBindingItem(i, status, _affected[i], _errors[i], _rows[i]?.ToArray() ?? Array.Empty<object[]>());
            }
            return new OceanBaseArrayBindingResult(items, complete, affected);
        }
        private static ProtocolException Invalid() => new ProtocolException("Invalid Oracle array binding result.");
    }
}
