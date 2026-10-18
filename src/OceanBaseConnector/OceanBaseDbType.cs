namespace OceanBaseConnector
{
    /// <summary>Provider scalar families; database type names remain available through reader metadata.</summary>
    public enum OceanBaseDbType
    {
        Decimal,
        OracleDate,
        Timestamp,
        NChar,
        NVarchar2,
        IntervalYearToMonth,
        IntervalDayToSecond,
        Json,
        Bit,
        Year,
        SByte,
        Byte,
        Int16,
        UInt16,
        Int32,
        UInt32,
        Int64,
        UInt64,
        Single,
        Double,
        Boolean,
        String,
        Binary,
        Date,
        DateTime,
        Time,
        Null,
        TimestampWithTimeZone,
        TimestampLocalTimeZone,
    }
}
