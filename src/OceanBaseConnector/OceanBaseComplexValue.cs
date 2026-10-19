using System;

namespace OceanBaseConnector
{
    /// <summary>An immutable Oracle object/collection value snapshot. Its type retains session/cache ownership.</summary>
    public sealed class OceanBaseComplexValue
    {
        private readonly object?[] _values;
        internal OceanBaseComplexValue(OceanBaseComplexType type, object?[] values)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            if (values.Length > Internal.Values.ComplexValueCodec.MaximumMembers || !type.IsCollection && values.Length != type.Members.Count)
                throw new ArgumentException("The complex value member count does not match its type or exceeds the supported limit.", nameof(values));
            var members = values.Length; long rawBytes = 0;
            foreach (var value in values)
            {
                if (value is OceanBaseComplexValue nested) members = checked(members + nested.TotalMembers);
                if (value is byte[] bytes) rawBytes += bytes.Length;
                if (members > Internal.Values.ComplexValueCodec.MaximumMembers || rawBytes > Internal.Protocol.PacketChannel.DefaultMaxPacketSize - 10)
                    throw new ArgumentException("The complex value exceeds the supported member or byte budget.", nameof(values));
            }
            Type = type; TotalMembers = members; _values = new object?[values.Length];
            for (var i = 0; i < values.Length; i++)
            {
                var value = values[i]; var member = type.Members[type.IsCollection ? 0 : i];
                if (value == null || value == DBNull.Value) { _values[i] = null; continue; }
                if (member.ComplexType != null && (!(value is OceanBaseComplexValue nested) || !ReferenceEquals(nested.Type, member.ComplexType)))
                    throw new ArgumentException("A nested complex value must use the corresponding member type.", nameof(values));
                _values[i] = value is byte[] bytes ? bytes.Clone() : value;
            }
        }
        public OceanBaseComplexType Type { get; }
        internal int TotalMembers { get; }
        internal static OceanBaseComplexValue FromResult(OceanBaseComplexType type, object?[] values)
        {
            for (var i = 0; i < values.Length; i++)
                if (values[i] is object?[] nested) values[i] = FromResult(type.Members[type.IsCollection ? 0 : i].ComplexType!, nested);
            return new OceanBaseComplexValue(type, values);
        }
        /// <summary>Returns a new array and independent RAW bytes. Nested values are immutable snapshots.</summary>
        public object?[] GetValues()
        {
            var values = (object?[])_values.Clone();
            for (var i = 0; i < values.Length; i++) if (values[i] is byte[] bytes) values[i] = bytes.Clone();
            return values;
        }
        internal object?[] WireValues()
        {
            var values = (object?[])_values.Clone();
            for (var i = 0; i < values.Length; i++) if (values[i] is OceanBaseComplexValue nested) values[i] = nested.WireValues();
            return values;
        }
    }
}
