using System;
using System.Collections.Generic;

namespace OceanBaseConnector
{
    /// <summary>The server-confirmed outcome of one array binding position; success does not commit the transaction.</summary>
    public enum OceanBaseArrayBindingStatus { Unknown, Succeeded, Failed }

    /// <summary>An immutable snapshot of array execution, including partial results after an interrupted response.</summary>
    public sealed class OceanBaseArrayBindingResult
    {
        internal OceanBaseArrayBindingResult(OceanBaseArrayBindingItem[] items, bool complete, long? affected)
        { Items = Array.AsReadOnly(items); IsComplete = complete; RecordsAffected = affected; }
        public IReadOnlyList<OceanBaseArrayBindingItem> Items { get; }
        /// <summary>True only after the complete successful protocol boundary was validated.</summary>
        public bool IsComplete { get; }
        /// <summary>The terminal cumulative count; null when no trustworthy terminal OK was received.</summary>
        public long? RecordsAffected { get; }
    }

    /// <summary>One zero-based input position. Output rows follow output parameter order in SQL binding order.</summary>
    public sealed class OceanBaseArrayBindingItem
    {
        private readonly object[][] _rows;
        internal OceanBaseArrayBindingItem(int index, OceanBaseArrayBindingStatus status, long? affected, int error, object[][] rows)
        { Index = index; Status = status; RecordsAffected = affected; ServerErrorCode = error; _rows = Copy(rows); }
        public int Index { get; }
        public OceanBaseArrayBindingStatus Status { get; }
        /// <summary>Per-item DML count without RETURNING; null for PL, output rows, failed or unknown items.</summary>
        public long? RecordsAffected { get; }
        /// <summary>Server error number, or zero. Server messages are omitted because they may contain bind values.</summary>
        public int ServerErrorCode { get; }
        /// <summary>Independent snapshots of all OUT/RETURNING rows, including RAW bytes.</summary>
        public IReadOnlyList<IReadOnlyList<object>> OutputRows
        {
            get
            {
                var rows = Copy(_rows); var result = new IReadOnlyList<object>[rows.Length];
                for (var i = 0; i < rows.Length; i++) result[i] = Array.AsReadOnly(rows[i]);
                return Array.AsReadOnly(result);
            }
        }
        private static object[][] Copy(object[][] rows)
        {
            var result = new object[rows.Length][];
            for (var i = 0; i < rows.Length; i++)
            {
                result[i] = (object[])rows[i].Clone();
                for (var j = 0; j < result[i].Length; j++) if (result[i][j] is byte[] bytes) result[i][j] = bytes.Clone();
            }
            return result;
        }
    }
}
