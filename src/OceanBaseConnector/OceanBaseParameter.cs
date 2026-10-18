using System;
using System.Data;
using System.Data.Common;

namespace OceanBaseConnector
{
    public sealed class OceanBaseParameter : DbParameter
    {
        private DbType? _dbType;
        private ParameterDirection _direction = ParameterDirection.Input;
        private int _size;
        internal OceanBaseParameterCollection? Owner { get; set; }
        public override DbType DbType
        {
            get => _dbType ?? InferType(Value);
            set { if (!Enum.IsDefined(typeof(DbType), value)) throw new ArgumentOutOfRangeException(nameof(value)); _dbType = value; }
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
        public override byte Scale { get; set; }
        public override void ResetDbType() => _dbType = null;

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
            if (value is decimal) return DbType.Decimal;
            if (value is DateTime) return DbType.DateTime;
            if (value is DateTimeOffset) return DbType.DateTimeOffset;
            if (value is TimeSpan) return DbType.Time;
            if (value is Guid) return DbType.Guid;
            return DbType.Object;
        }
    }
}
