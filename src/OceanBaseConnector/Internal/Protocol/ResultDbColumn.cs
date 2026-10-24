using System;
using System.Data.Common;

namespace OceanBaseConnector.Internal.Protocol
{
    internal sealed class ResultDbColumn : DbColumn
    {
        internal ResultDbColumn(ResultColumn column, int ordinal, KeyInfoMetadata.ColumnInfo? keyInfo = null)
        {
            ColumnName = column.Name;
            ColumnOrdinal = ordinal;
            DataType = column.FieldType;
            DataTypeName = column.DataTypeName;
            ProviderSpecificDataType = column.ProviderSpecificFieldType;
            ProviderType = column.ProviderType;
            BaseColumnName = Known(column.OriginalName);
            BaseTableName = Known(column.OriginalTable);
            if (column.Mode == OceanBaseCompatibilityMode.MySql) BaseCatalogName = Known(column.Database);
            else BaseSchemaName = Known(column.Database);
            IsAliased = BaseColumnName == null ? (bool?)null : column.Name != BaseColumnName;
            AllowDBNull = (column.Flags & 1) == 0;
            IsAutoIncrement = (column.Flags & 512) != 0;
            IsHidden = keyInfo?.IsHidden ?? false;
            IsKey = keyInfo?.IsKey;
            IsUnique = keyInfo?.IsUnique;
            IsLong = column.TypeCode == 252 || column.TypeCode == 245 || column.TypeCode == 210 || column.TypeCode == 211;
            var size = column.Length;
            if (column.Mode == OceanBaseCompatibilityMode.MySql && DataType == typeof(string))
                size /= column.Charset == 33 || column.Charset == 83 ? 3U : 4U;
            // JSON's byte length does not establish a maximum character count.
            ColumnSize = column.TypeCode == 160 || column.TypeCode == 245 || column.TypeCode == 210 || column.TypeCode == 211 ? (int?)null : (int)Math.Min(int.MaxValue, size);
            if (column.IsDecimal)
            {
                NumericPrecision = column.Precision > 0 ? (int?)column.Precision : null;
                NumericScale = column.Precision > 0 || column.Scale != 0 ? (int?)column.Scale : null;
            }
            else
            {
                switch (column.TypeCode)
                {
                    case 1: NumericPrecision = 3; break;
                    case 2: NumericPrecision = 5; break;
                    case 13: NumericPrecision = 4; break;
                    case 3: NumericPrecision = 10; break;
                    case 9: NumericPrecision = column.Unsigned ? 8 : 7; break;
                    case 8: NumericPrecision = column.Unsigned ? 20 : 19; break;
                }
                if (NumericPrecision.HasValue) NumericScale = 0;
            }
            // Key/unique/updateability cannot be established from column flags alone (for example composite indexes).
        }

        internal Type ProviderSpecificDataType { get; }
        private OceanBaseDbType ProviderType { get; }
        public override object? this[string property] => property == "ProviderSpecificDataType" ? ProviderSpecificDataType
            : property == "ProviderType" ? (int)ProviderType : base[property];
        private static string? Known(string value) => value.Length == 0 ? null : value;
    }
}
