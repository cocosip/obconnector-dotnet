using System;
using System.Data;
using System.Globalization;

namespace OceanBaseConnector
{
    public sealed partial class OceanBaseConnection
    {
        private static DataTable CreateDataTypes(bool mysql, bool tinyBoolean)
        {
            var table = new DataTable("DataTypes") { Locale = CultureInfo.InvariantCulture };
            foreach (var name in new[] { "TypeName", "CreateFormat", "CreateParameters", "DataType", "LiteralPrefix", "LiteralSuffix", "NativeDataType", "ProviderSpecificDataType", "RequiredFeatures", "ParameterDirections", "ResultTypeCondition" }) table.Columns.Add(name, typeof(string));
            foreach (var name in new[] { "ProviderDbType", "MinimumPrecision", "MaximumPrecision" }) table.Columns.Add(name, typeof(int));
            table.Columns.Add("ColumnSize", typeof(long));
            foreach (var name in new[] { "MinimumScale", "MaximumScale" }) table.Columns.Add(name, typeof(short));
            foreach (var name in new[] { "IsAutoIncrementable", "IsBestMatch", "IsCaseSensitive", "IsFixedLength", "IsFixedPrecisionScale", "IsLong", "IsNullable", "IsSearchable", "IsSearchableWithLike", "IsUnsigned", "IsConcurrencyType", "IsLiteralSupported", "IsSupportedForParameters", "IsSupportedForResults", "RequiresTypeName" }) table.Columns.Add(name, typeof(bool));

            DataRow TypeRow(string name, OceanBaseDbType provider, Type type, string? format = null, string? parameters = null, Type? providerType = null)
            {
                var row = table.NewRow();
                row["TypeName"] = name; row["ProviderDbType"] = (int)provider;
                row["DataType"] = type.FullName!; row["ProviderSpecificDataType"] = (providerType ?? type).FullName!;
                if (format != null) row["CreateFormat"] = format;
                if (parameters != null) row["CreateParameters"] = parameters;
                row["IsNullable"] = true; row["IsLong"] = false; row["IsConcurrencyType"] = false;
                row["IsSupportedForParameters"] = true; row["IsSupportedForResults"] = true;
                row["RequiresTypeName"] = false; row["IsBestMatch"] = false; row["IsLiteralSupported"] = false;
                table.Rows.Add(row); return row;
            }

            DataRow Number(string name, OceanBaseDbType provider, Type type, int precision, bool unsigned = false, short? minScale = 0, short? maxScale = 0)
            {
                var fraction = provider == OceanBaseDbType.Decimal;
                var format = fraction ? name.Replace(" UNSIGNED", "") + "({0},{1})" + (unsigned ? " UNSIGNED" : "") : name;
                var row = TypeRow(name, provider, type, format, fraction ? "precision,scale" : null, fraction ? typeof(OceanBaseDecimal) : null);
                row["ColumnSize"] = (long)precision; row["MinimumPrecision"] = fraction ? 1 : precision; row["MaximumPrecision"] = precision;
                if (minScale.HasValue) row["MinimumScale"] = minScale.Value;
                if (maxScale.HasValue) row["MaximumScale"] = maxScale.Value;
                row["IsUnsigned"] = unsigned; row["IsCaseSensitive"] = false; row["IsFixedLength"] = !fraction;
                row["IsBestMatch"] = true; row["IsLiteralSupported"] = true; row["LiteralPrefix"] = ""; row["LiteralSuffix"] = "";
                return row;
            }

            void DecimalRows(string name, bool unsigned = false)
            {
                var narrow = Number(name, OceanBaseDbType.Decimal, typeof(decimal), 28, unsigned, 0, 28);
                var wide = Number(name, OceanBaseDbType.Decimal, typeof(OceanBaseDecimal), mysql ? 65 : 38, unsigned, (short)(mysql ? 0 : -84), (short)(mysql ? 30 : 127));
                narrow["ResultTypeCondition"] = "1 <= precision <= 28 AND 0 <= scale <= precision";
                wide["ResultTypeCondition"] = "precision unknown OR precision > 28 OR scale < 0 OR scale > precision";
                if (unsigned) { narrow["IsBestMatch"] = false; wide["IsBestMatch"] = false; }
            }

            DataRow Text(string name, OceanBaseDbType provider, bool binary = false, bool fixedLength = false, bool longValue = false)
            {
                var row = TypeRow(name, provider, binary ? typeof(byte[]) : typeof(string), longValue ? name : name + "({0})", longValue ? null : "length");
                row["IsFixedLength"] = fixedLength; row["IsLong"] = longValue;
                if (!binary) row["RequiredFeatures"] = "Supported UTF-8 result collation";
                if (!binary || mysql)
                {
                    row["IsLiteralSupported"] = true; row["LiteralPrefix"] = binary ? "X'" : provider == OceanBaseDbType.NChar || provider == OceanBaseDbType.NVarchar2 ? "N'" : "'"; row["LiteralSuffix"] = "'";
                }
                return row;
            }

            DataRow Temporal(string name, OceanBaseDbType provider, Type type, short minimum, short maximum, Type? providerType = null)
            {
                var fraction = maximum != 0;
                var format = !fraction ? name : name.StartsWith("TIMESTAMP", StringComparison.Ordinal) ? name.Replace("TIMESTAMP", "TIMESTAMP({0})") : name + "({0})";
                var row = TypeRow(name, provider, type, format, fraction ? "scale" : null, providerType);
                row["MinimumScale"] = minimum; row["MaximumScale"] = maximum; return row;
            }

            if (mysql)
            {
                foreach (var item in new[] {
                    ("TINYINT", OceanBaseDbType.SByte, typeof(sbyte), 3, OceanBaseDbType.Byte, typeof(byte), 3),
                    ("SMALLINT", OceanBaseDbType.Int16, typeof(short), 5, OceanBaseDbType.UInt16, typeof(ushort), 5),
                    ("MEDIUMINT", OceanBaseDbType.Int32, typeof(int), 7, OceanBaseDbType.UInt32, typeof(uint), 8),
                    ("INT", OceanBaseDbType.Int32, typeof(int), 10, OceanBaseDbType.UInt32, typeof(uint), 10),
                    ("BIGINT", OceanBaseDbType.Int64, typeof(long), 19, OceanBaseDbType.UInt64, typeof(ulong), 20) })
                {
                    var signed = Number(item.Item1, item.Item2, item.Item3, item.Item4); signed["IsAutoIncrementable"] = true;
                    var unsigned = Number(item.Item1 + " UNSIGNED", item.Item5, item.Item6, item.Item7, true); unsigned["IsAutoIncrementable"] = true;
                    if (item.Item1 == "MEDIUMINT") { signed["IsBestMatch"] = false; unsigned["IsBestMatch"] = false; }
                }
                if (tinyBoolean) { var row = TypeRow("TINYINT(1)", OceanBaseDbType.Boolean, typeof(bool), "TINYINT(1)"); row["ColumnSize"] = 1L; row["IsBestMatch"] = true; }
                DecimalRows("DECIMAL"); DecimalRows("DECIMAL UNSIGNED", true);
                Number("FLOAT", OceanBaseDbType.Single, typeof(float), 7, minScale: null, maxScale: null);
                Number("DOUBLE", OceanBaseDbType.Double, typeof(double), 15, minScale: null, maxScale: null);
                Text("CHAR", OceanBaseDbType.String, fixedLength: true);
                Text("VARCHAR", OceanBaseDbType.String)["IsBestMatch"] = true;
                foreach (var name in new[] { "ENUM", "SET" })
                {
                    var row = TypeRow(name, OceanBaseDbType.String, typeof(string), name + "({0})", "labels");
                    row["RequiredFeatures"] = "Supported result collation; binary collation returns bytes";
                    row["LiteralPrefix"] = "'"; row["LiteralSuffix"] = "'"; row["IsLiteralSupported"] = true;
                }
                Text("TEXT", OceanBaseDbType.String, longValue: true);
                Text("BINARY", OceanBaseDbType.Binary, binary: true, fixedLength: true);
                Text("VARBINARY", OceanBaseDbType.Binary, binary: true)["IsBestMatch"] = true;
                Text("BLOB", OceanBaseDbType.Binary, binary: true, longValue: true);
                foreach (var prefix in new[] { "TINY", "MEDIUM", "LONG" })
                {
                    Text(prefix + "TEXT", OceanBaseDbType.String, longValue: true);
                    Text(prefix + "BLOB", OceanBaseDbType.Binary, binary: true, longValue: true);
                }
                Temporal("DATE", OceanBaseDbType.Date, typeof(DateTime), 0, 0, typeof(OceanBaseDate));
                Temporal("DATETIME", OceanBaseDbType.DateTime, typeof(DateTime), 0, 6, typeof(OceanBaseDateTime))["IsBestMatch"] = true;
                Temporal("TIMESTAMP", OceanBaseDbType.Timestamp, typeof(DateTime), 0, 6, typeof(OceanBaseDateTime));
                Temporal("TIME", OceanBaseDbType.Time, typeof(TimeSpan), 0, 6)["IsBestMatch"] = true;
                var year = Number("YEAR", OceanBaseDbType.Year, typeof(ushort), 4, true);
                year["IsBestMatch"] = false; year["RequiredFeatures"] = "Four-digit YEAR; values 0 or 1901..2155";
                var bits = TypeRow("BIT", OceanBaseDbType.Bit, typeof(ulong), "BIT({0})", "length"); bits["ColumnSize"] = 64L; bits["IsUnsigned"] = true;
                var json = TypeRow("JSON", OceanBaseDbType.Json, typeof(string), "JSON");
                json["IsLong"] = true; json["RequiredFeatures"] = "Binary or supported UTF-8 JSON result collation";
                var geometry = TypeRow("GEOMETRY", OceanBaseDbType.Binary, typeof(byte[]));
                geometry["IsSupportedForParameters"] = false;
                geometry["RequiredFeatures"] = "MySQL result type code 255; opaque bytes without a GIS codec";
            }
            else
            {
                DecimalRows("NUMBER");
                var floating = TypeRow("FLOAT", OceanBaseDbType.Decimal, typeof(OceanBaseDecimal), "FLOAT({0})", "binary precision");
                floating["MinimumPrecision"] = 1; floating["MaximumPrecision"] = 126;
                floating["IsFixedPrecisionScale"] = false;
                floating["RequiredFeatures"] = "Oracle result206; binary precision, variable scale; parameters bind as NUMBER with decimal Precision";
                Number("BINARY_FLOAT", OceanBaseDbType.Single, typeof(float), 7, minScale: null, maxScale: null);
                Number("BINARY_DOUBLE", OceanBaseDbType.Double, typeof(double), 15, minScale: null, maxScale: null);
                Temporal("DATE", OceanBaseDbType.OracleDate, typeof(DateTime), 0, 0);
                Temporal("TIMESTAMP", OceanBaseDbType.Timestamp, typeof(DateTime), 0, 7, typeof(OceanBaseTimestamp))["IsBestMatch"] = true;
                Temporal("TIMESTAMP", OceanBaseDbType.Timestamp, typeof(OceanBaseTimestamp), 8, 9)["IsBestMatch"] = true;
                Temporal("TIMESTAMP WITH TIME ZONE", OceanBaseDbType.TimestampWithTimeZone, typeof(OceanBaseTimestampWithTimeZone), 0, 9)["IsBestMatch"] = true;
                Temporal("TIMESTAMP WITH LOCAL TIME ZONE", OceanBaseDbType.TimestampLocalTimeZone, typeof(OceanBaseTimestampLocalTimeZone), 0, 9)["RequiredFeatures"] = "Oracle session time zone";
                var rowid = TypeRow("ROWID", OceanBaseDbType.String, typeof(string));
                rowid["IsSupportedForParameters"] = false;
                rowid["RequiredFeatures"] = "Oracle result type code 209; returned as strict session text";
                Text("CHAR", OceanBaseDbType.String, fixedLength: true);
                Text("VARCHAR2", OceanBaseDbType.String)["IsBestMatch"] = true;
                Text("NCHAR", OceanBaseDbType.NChar, fixedLength: true)["RequiredFeatures"] = "Trusted national character set";
                Text("NVARCHAR2", OceanBaseDbType.NVarchar2)["RequiredFeatures"] = "Trusted national character set";
                Text("RAW", OceanBaseDbType.Binary, binary: true)["IsBestMatch"] = true;
                foreach (var character in new[] { false, true })
                {
                    var lob = TypeRow(character ? "CLOB" : "BLOB", character ? OceanBaseDbType.Clob : OceanBaseDbType.Blob, typeof(OceanBaseLob), character ? "CLOB" : "BLOB");
                    lob["IsLong"] = true; lob["RequiredFeatures"] = "Negotiated Use Lob Locators; session-bound locator" + (character ? "; supported UTF-8 CLOB character set" : "");
                }
                foreach (var seconds in new[] { false, true })
                {
                    var row = TypeRow(seconds ? "INTERVAL DAY TO SECOND" : "INTERVAL YEAR TO MONTH", seconds ? OceanBaseDbType.IntervalDayToSecond : OceanBaseDbType.IntervalYearToMonth,
                        seconds ? typeof(OceanBaseIntervalDayToSecond) : typeof(OceanBaseIntervalYearToMonth), seconds ? "INTERVAL DAY({0}) TO SECOND({1})" : "INTERVAL YEAR({0}) TO MONTH", seconds ? "precision,scale" : "precision");
                    row["MinimumPrecision"] = 1; row["MaximumPrecision"] = 9; row["MinimumScale"] = (short)0; row["MaximumScale"] = (short)(seconds ? 9 : 0); row["IsBestMatch"] = true;
                }
                foreach (var collection in new[] { false, true })
                {
                    var row = TypeRow(collection ? "ARRAY" : "OBJECT", collection ? OceanBaseDbType.Array : OceanBaseDbType.Struct, typeof(OceanBaseComplexValue));
                    row["RequiresTypeName"] = !collection; row["RequiredFeatures"] = "Oracle complex type description; binary protocol; supported member layouts" + (collection ? "; named collection or supported anonymous scalar array" : "");
                }
                var cursor = TypeRow("REF CURSOR", OceanBaseDbType.RefCursor, typeof(OceanBaseRefCursor));
                cursor["IsSupportedForResults"] = false; cursor["ParameterDirections"] = "Output,ReturnValue"; cursor["RequiredFeatures"] = "Use Oracle Prepare Execute";
            }
            return table;
        }
    }
}
