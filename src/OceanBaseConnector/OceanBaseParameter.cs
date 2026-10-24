using System;
using System.Data;
using System.Data.Common;
using System.IO;

namespace OceanBaseConnector
{
    public sealed class OceanBaseParameter : DbParameter
    {
        private DbType? _dbType;
        private OceanBaseDbType? _providerType;
        private OceanBaseComplexType? _complexType;
        private byte? _scale;
        private int? _decimalScale;
        private ParameterDirection _direction = ParameterDirection.Input;
        private int _size;
        internal OceanBaseParameterCollection? Owner { get; set; }
        public override DbType DbType
        {
            get => _dbType ?? InferType(Value);
            set { if (!Enum.IsDefined(typeof(DbType), value)) throw new ArgumentOutOfRangeException(nameof(value)); _dbType = value; _providerType = null; _complexType = null; }
        }
        /// <summary>An optional provider-specific type; setting DbType or ResetDbType clears it.</summary>
        public OceanBaseDbType? OceanBaseDbType
        {
            get => _providerType;
            set
            {
                if (value.HasValue && !Enum.IsDefined(typeof(OceanBaseDbType), value.Value)) throw new ArgumentOutOfRangeException(nameof(value));
                _providerType = value;
                _dbType = value.HasValue ? ProviderDbType(value.Value) : null;
                if (value != global::OceanBaseConnector.OceanBaseDbType.Array && value != global::OceanBaseConnector.OceanBaseDbType.Struct) _complexType = null;
            }
        }
        /// <summary>A session-bound Oracle type, required for NULL and complex outputs.
        /// Anonymous complex elements are Input-only in scalar execution; ExecuteArray binds their element outputs separately.</summary>
        public OceanBaseComplexType? ComplexType
        {
            get => _complexType;
            set
            {
                _complexType = value;
                _providerType = value == null ? (OceanBaseDbType?)null : value.IsCollection
                    ? global::OceanBaseConnector.OceanBaseDbType.Array : global::OceanBaseConnector.OceanBaseDbType.Struct;
                _dbType = value == null ? (DbType?)null : DbType.Object;
            }
        }
        public override ParameterDirection Direction
        {
            get => _direction;
            set { if (!Enum.IsDefined(typeof(ParameterDirection), value)) throw new ArgumentOutOfRangeException(nameof(value)); _direction = value; }
        }
        public override bool IsNullable { get; set; }
        public override string ParameterName { get; set; } = string.Empty;
        public override string SourceColumn { get; set; } = string.Empty;
        public override object? Value { get; set; }
        public override bool SourceColumnNullMapping { get; set; }
        public override DataRowVersion SourceVersion { get; set; } = DataRowVersion.Current;
        public override int Size { get => _size; set { if (value < -1) throw new ArgumentOutOfRangeException(nameof(value)); _size = value; } }
        public override byte Precision { get; set; }
        public override byte Scale
        {
            get => _decimalScale.HasValue && _decimalScale.Value >= 0 ? (byte)_decimalScale.Value : _scale ?? 0;
            set { _scale = value; _decimalScale = null; }
        }
        /// <summary>An explicit decimal scale, including Oracle negative scales (-84 through 127).
        /// MySQL allows 0 through 30. Setting Scale clears this override; Scale reads zero for a negative override.
        /// Values requiring rounding are rejected. Null uses the ordinary Scale or value inference.</summary>
        public int? DecimalScale
        {
            get => _decimalScale;
            set
            {
                if (value.HasValue && (value.Value < -84 || value.Value > 127)) throw new ArgumentOutOfRangeException(nameof(value));
                _decimalScale = value;
                if (value.HasValue) _scale = null;
            }
        }
        internal byte? DeclaredScale => _scale;
        internal int? DeclaredDecimalScale => _decimalScale ?? (int?)_scale;
        internal void ClearScaleDeclaration() { _scale = null; _decimalScale = null; }
        internal OceanBaseParameter Snapshot()
        {
            var copy = (OceanBaseParameter)MemberwiseClone();
            copy.Owner = null;
            if (copy.Value is byte[] bytes) copy.Value = (byte[])bytes.Clone();
            return copy;
        }
        public override void ResetDbType() { _dbType = null; _providerType = null; _complexType = null; }

        internal static DbType? ProviderDbType(OceanBaseDbType type)
        {
            switch (type)
            {
                case global::OceanBaseConnector.OceanBaseDbType.Decimal: return DbType.Decimal;
                case global::OceanBaseConnector.OceanBaseDbType.OracleDate:
                case global::OceanBaseConnector.OceanBaseDbType.Date: return DbType.Date;
                case global::OceanBaseConnector.OceanBaseDbType.TimestampLocalTimeZone:
                case global::OceanBaseConnector.OceanBaseDbType.Timestamp: return DbType.DateTime2;
                case global::OceanBaseConnector.OceanBaseDbType.TimestampWithTimeZone: return DbType.DateTimeOffset;
                case global::OceanBaseConnector.OceanBaseDbType.SByte: return DbType.SByte;
                case global::OceanBaseConnector.OceanBaseDbType.Byte: return DbType.Byte;
                case global::OceanBaseConnector.OceanBaseDbType.Int16: return DbType.Int16;
                case global::OceanBaseConnector.OceanBaseDbType.Year:
                case global::OceanBaseConnector.OceanBaseDbType.UInt16: return DbType.UInt16;
                case global::OceanBaseConnector.OceanBaseDbType.Int32: return DbType.Int32;
                case global::OceanBaseConnector.OceanBaseDbType.UInt32: return DbType.UInt32;
                case global::OceanBaseConnector.OceanBaseDbType.Int64: return DbType.Int64;
                case global::OceanBaseConnector.OceanBaseDbType.Bit:
                case global::OceanBaseConnector.OceanBaseDbType.UInt64: return DbType.UInt64;
                case global::OceanBaseConnector.OceanBaseDbType.Single: return DbType.Single;
                case global::OceanBaseConnector.OceanBaseDbType.Double: return DbType.Double;
                case global::OceanBaseConnector.OceanBaseDbType.Boolean: return DbType.Boolean;
                case global::OceanBaseConnector.OceanBaseDbType.Json:
                case global::OceanBaseConnector.OceanBaseDbType.NChar:
                case global::OceanBaseConnector.OceanBaseDbType.NVarchar2:
                case global::OceanBaseConnector.OceanBaseDbType.String: return DbType.String;
                case global::OceanBaseConnector.OceanBaseDbType.Binary: return DbType.Binary;
                case global::OceanBaseConnector.OceanBaseDbType.DateTime: return DbType.DateTime;
                case global::OceanBaseConnector.OceanBaseDbType.Time: return DbType.Time;
                case global::OceanBaseConnector.OceanBaseDbType.IntervalYearToMonth:
                case global::OceanBaseConnector.OceanBaseDbType.IntervalDayToSecond:
                case global::OceanBaseConnector.OceanBaseDbType.RefCursor:
                case global::OceanBaseConnector.OceanBaseDbType.Blob:
                case global::OceanBaseConnector.OceanBaseDbType.Clob:
                case global::OceanBaseConnector.OceanBaseDbType.Array:
                case global::OceanBaseConnector.OceanBaseDbType.Struct:
                case global::OceanBaseConnector.OceanBaseDbType.Null: return DbType.Object;
                default: return null;
            }
        }

        private static DbType InferType(object? value)
        {
            if (value == null || value == DBNull.Value || value is string) return DbType.String;
            if (value is byte[] || value is Stream) return DbType.Binary;
            if (value is bool) return DbType.Boolean;
            if (value is byte) return DbType.Byte;
            if (value is sbyte) return DbType.SByte;
            if (value is short) return DbType.Int16;
            if (value is ushort) return DbType.UInt16;
            if (value is int) return DbType.Int32;
            if (value is uint) return DbType.UInt32;
            if (value is long) return DbType.Int64;
            if (value is ulong) return DbType.UInt64;
            if (value is float) return DbType.Single;
            if (value is double) return DbType.Double;
            if (value is decimal || value is OceanBaseDecimal) return DbType.Decimal;
            if (value is OceanBaseTimestamp || value is OceanBaseTimestampLocalTimeZone) return DbType.DateTime2;
            if (value is OceanBaseTimestampWithTimeZone) return DbType.DateTimeOffset;
            if (value is OceanBaseDate) return DbType.Date;
            if (value is OceanBaseDateTime) return DbType.DateTime;
            if (value is DateTime) return DbType.DateTime;
            if (value is DateTimeOffset) return DbType.DateTimeOffset;
            if (value is TimeSpan) return DbType.Time;
            if (value is Guid) return DbType.Guid;
            return DbType.Object;
        }
    }
}
