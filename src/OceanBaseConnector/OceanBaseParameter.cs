using System;
using System.Data;
using System.Data.Common;

namespace OceanBaseConnector
{
    public sealed class OceanBaseParameter : DbParameter
    {
        private DbType _dbType = DbType.String;
        private ParameterDirection _direction = ParameterDirection.Input;
        private int _size;
        internal OceanBaseParameterCollection? Owner { get; set; }
        public override DbType DbType
        {
            get => _dbType;
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
        public override void ResetDbType() => _dbType = DbType.String;
    }
}
