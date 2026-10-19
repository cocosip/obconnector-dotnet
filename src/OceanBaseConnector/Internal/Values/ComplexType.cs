using System;
using System.Collections.Generic;

namespace OceanBaseConnector.Internal.Values
{
    // Wire layout only; named type identity and dictionary freshness belong to the owning session.
    internal sealed class ComplexType
    {
        internal ComplexType(byte scalarType)
        {
            if (scalarType != 0 && scalarType != 253 && scalarType != 254 && scalarType != 203 && scalarType != 12 && scalarType != 202)
                throw new NotSupportedException("The complex member type is not supported.");
            TypeCode = scalarType; Members = System.Array.AsReadOnly(System.Array.Empty<ComplexType>());
        }

        internal ComplexType(bool collection, params ComplexType[] members)
        {
            if (members == null) throw new ArgumentNullException(nameof(members));
            if (members.Length > ComplexValueCodec.MaximumMembers || collection && members.Length != 1)
                throw new ArgumentException("A collection requires one element type; object member counts must be bounded.", nameof(members));
            var snapshot = (ComplexType[])members.Clone();
            foreach (var member in snapshot) if (member == null) throw new ArgumentException("Complex member types cannot be null.", nameof(members));
            TypeCode = 160; IsCollection = collection; Members = System.Array.AsReadOnly(snapshot);
        }

        internal byte TypeCode { get; }
        internal bool IsCollection { get; }
        internal IReadOnlyList<ComplexType> Members { get; }
    }
}
