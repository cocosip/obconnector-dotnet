using System;
using System.Data;
using System.Data.Common;

namespace OceanBaseConnector
{
    public sealed class OceanBaseParameter : DbParameter
    {
        private DbType? _dbType;
        private OceanBaseDbType? _providerType;
        private byte? _scale;
        private ParameterDirection _direction = ParameterDirection.Input;
        private int _size;
        internal OceanBaseParameterCollection? Owner { get; set; }
        public override DbType DbType
        {
            get => _dbType ?? InferType(Value);
            set { if (!Enum.IsDefined(typeof(DbType), value)) throw new ArgumentOutOfRangeException(nameof(value)); _dbType = value; _providerType = null; }
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
        public override byte Scale { get => _scale ?? 0; set => _scale = value; }
        internal byte? DeclaredScale => _scale;
        internal OceanBaseParameter Snapshot()
        {
            var copy = (OceanBaseParameter)MemberwiseClone();
            copy.Owner = null;
            if (copy.Value is byte[] bytes) copy.Value = (byte[])bytes.Clone();
            return copy;
        }
        public override void ResetDbType() { _dbType = null; _providerType = null; }

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
                case global::OceanBaseConnector.OceanBaseDbType.Null: return DbType.Object;
                default: return null;
            }
        }

        private static DbType InferType(object? value)
        {
            if (value == null || value == DBNull.Value || value is string) return DbType.String;
            if (value is byte[]) return DbType.Binary;
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
