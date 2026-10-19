using System;

namespace OceanBaseConnector
{
    /// <summary>An immutable XA branch identity. This value does not start or enlist a transaction.</summary>
    public sealed class OceanBaseXid : IEquatable<OceanBaseXid>
    {
        private readonly byte[] _globalTransactionId;
        private readonly byte[] _branchQualifier;

        /// <summary>Copies a 1–64 byte global ID and a 0–64 byte branch qualifier.
        /// The signed 32-bit format ID is preserved; -1 is reserved for NULLXID.</summary>
        public OceanBaseXid(int formatId, byte[] globalTransactionId, byte[] branchQualifier)
        {
            if (formatId == -1) throw new ArgumentOutOfRangeException(nameof(formatId), "The NULLXID format is reserved.");
            if (globalTransactionId == null) throw new ArgumentNullException(nameof(globalTransactionId));
            if (branchQualifier == null) throw new ArgumentNullException(nameof(branchQualifier));
            if (globalTransactionId.Length < 1 || globalTransactionId.Length > 64)
                throw new ArgumentException("A global transaction ID must contain 1 to 64 bytes.", nameof(globalTransactionId));
            if (branchQualifier.Length > 64)
                throw new ArgumentException("A branch qualifier must contain at most 64 bytes.", nameof(branchQualifier));
            FormatId = formatId;
            _globalTransactionId = (byte[])globalTransactionId.Clone();
            _branchQualifier = (byte[])branchQualifier.Clone();
        }

        public int FormatId { get; }
        /// <summary>Returns an independent copy of the global transaction ID.</summary>
        public byte[] GetGlobalTransactionId() => (byte[])_globalTransactionId.Clone();
        /// <summary>Returns an independent copy of the branch qualifier, which may be empty.</summary>
        public byte[] GetBranchQualifier() => (byte[])_branchQualifier.Clone();

        public bool Equals(OceanBaseXid? other) => ReferenceEquals(this, other) || other != null &&
            FormatId == other.FormatId && SameBytes(_globalTransactionId, other._globalTransactionId) &&
            SameBytes(_branchQualifier, other._branchQualifier);
        public override bool Equals(object? obj) => obj is OceanBaseXid other && Equals(other);
        public override int GetHashCode()
        {
            unchecked
            {
                var hash = FormatId * 31 + _globalTransactionId.Length;
                foreach (var value in _globalTransactionId) hash = hash * 31 + value;
                hash = hash * 31 + _branchQualifier.Length;
                foreach (var value in _branchQualifier) hash = hash * 31 + value;
                return hash;
            }
        }

        private static bool SameBytes(byte[] left, byte[] right)
        {
            if (left.Length != right.Length) return false;
            for (var i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
            return true;
        }
    }
}
