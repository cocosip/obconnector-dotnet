using System;
using System.Data;
using System.IO;
using OceanBaseConnector.Internal.Values;

namespace OceanBaseConnector.Internal.Protocol
{
    internal static class PreparedParameters
    {
        internal static byte[] Encode(OceanBaseParameter[] parameters, OceanBaseCompatibilityMode mode = OceanBaseCompatibilityMode.MySql)
        {
            if (parameters.Length == 0) return Array.Empty<byte>();
            if (parameters.Length > ushort.MaxValue) throw new ArgumentException("The prepared parameter count exceeds the protocol limit.");
            var nullBytes = (parameters.Length + 7) / 8;
            var header = nullBytes + 1 + parameters.Length * 2;
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, ResultColumn.Utf8, leaveOpen: true);
            writer.Write(new byte[header]);
            stream.Position = nullBytes;
            writer.Write((byte)1); // Send types on every execution, including changed values and NULLs.
            stream.Position = header;
            for (var i = 0; i < parameters.Length; i++)
            {
                var parameter = parameters[i];
                var type = TypeCode(parameter, mode);
                var value = parameter.Value;
                if (value == null || value == DBNull.Value)
                {
                    type = 6;
                    var end = stream.Position;
                    stream.Position = i / 8;
                    var bits = stream.ReadByte();
                    stream.Position--;
                    writer.Write((byte)(bits | (1 << (i % 8))));
                    stream.Position = end;
                }
                else if (parameter.DbType == DbType.Decimal) WriteDecimal(writer, parameter, value);
                else if (parameter.DbType == DbType.Date || parameter.DbType == DbType.DateTime || parameter.DbType == DbType.DateTime2)
                {
                    if (!(value is DateTime date)) throw new ArgumentException("The parameter value does not match its declared DbType.");
                    var oracleDate = mode == OceanBaseCompatibilityMode.Oracle && parameter.DbType == DbType.Date;
                    if (oracleDate && parameter.DeclaredScale.GetValueOrDefault() != 0) throw new ArgumentException("Oracle DATE has no fractional seconds.");
                    TemporalCodec.WriteDate(writer, type, date, oracleDate ? (byte?)0 : parameter.DeclaredScale);
                }
                else if (parameter.DbType == DbType.Time)
                {
                    if (!(value is TimeSpan time)) throw new ArgumentException("The parameter value does not match its declared DbType.");
                    TemporalCodec.WriteTime(writer, time, parameter.DeclaredScale);
                }
                else WriteValue(writer, parameter.DbType, value);
                var position = stream.Position;
                stream.Position = nullBytes + 1 + i * 2;
                writer.Write(type);
                stream.Position = position;
                if (position > PacketChannel.DefaultMaxPacketSize - 10) throw TooLarge();
            }
            return stream.ToArray();
        }

        internal static byte[] Execute(uint id, byte[] parameters)
        {
            var packet = new byte[10 + parameters.Length];
            packet[0] = 23;
            for (var i = 0; i < 4; i++) packet[i + 1] = (byte)(id >> (i * 8));
            packet[6] = 1;
            Buffer.BlockCopy(parameters, 0, packet, 10, parameters.Length);
            return packet;
        }

        private static ushort TypeCode(OceanBaseParameter parameter, OceanBaseCompatibilityMode mode)
        {
            if (parameter.OceanBaseDbType.HasValue)
            {
                var providerType = parameter.OceanBaseDbType.Value;
                if (!OceanBaseParameter.ProviderDbType(providerType).HasValue)
                    throw new NotSupportedException("The provider-specific parameter type is not supported yet.");
                if (providerType == OceanBaseDbType.OracleDate && mode != OceanBaseCompatibilityMode.Oracle)
                    throw new NotSupportedException("OracleDate requires Oracle mode.");
                if (providerType == OceanBaseDbType.Null)
                {
                    if (parameter.Value != null && parameter.Value != DBNull.Value)
                        throw new ArgumentException("A Null parameter cannot contain a non-null value.");
                    return 6;
                }
            }
            if (parameter.DbType == DbType.Decimal && (parameter.Precision > 28 || parameter.Scale > 28))
                throw new NotSupportedException("The decimal parameter precision or scale requires C03.");
            if (parameter.DbType == DbType.Decimal && parameter.Precision != 0 && parameter.Scale > parameter.Precision)
                throw new ArgumentException("Decimal scale exceeds its declared precision.");
            var temporalScale = parameter.DbType == DbType.Time ? 6 : parameter.DbType == DbType.Date ? 0 : mode == OceanBaseCompatibilityMode.Oracle ? 7 : 6;
            if ((parameter.DbType == DbType.Date || parameter.DbType == DbType.DateTime || parameter.DbType == DbType.DateTime2 || parameter.DbType == DbType.Time) && parameter.Scale > temporalScale)
                throw new NotSupportedException("The temporal parameter precision is not supported.");
            switch (parameter.DbType)
            {
                case DbType.Boolean:
                case DbType.SByte: return 1;
                case DbType.Byte: return 0x8001;
                case DbType.Int16: return 2;
                case DbType.UInt16: return 0x8002;
                case DbType.Int32: return 3;
                case DbType.UInt32: return 0x8003;
                case DbType.Int64: return 8;
                case DbType.UInt64: return 0x8008;
                case DbType.Single: return 4;
                case DbType.Double: return 5;
                case DbType.Decimal: return 246;
                case DbType.Date: return mode == OceanBaseCompatibilityMode.Oracle ? (ushort)12 : (ushort)10;
                case DbType.DateTime:
                case DbType.DateTime2: return mode == OceanBaseCompatibilityMode.Oracle ? (ushort)202 : (ushort)12;
                case DbType.Time:
                    if (mode != OceanBaseCompatibilityMode.MySql) throw new NotSupportedException("TIME duration parameters require MySQL mode.");
                    return 11;
                case DbType.String:
                case DbType.AnsiString:
                case DbType.StringFixedLength:
                case DbType.AnsiStringFixedLength: return 253;
                case DbType.Binary: return mode == OceanBaseCompatibilityMode.Oracle ? (ushort)253 : (ushort)252;
                default: throw new NotSupportedException("The parameter type is not supported yet.");
            }
        }

        private static void WriteValue(BinaryWriter writer, DbType type, object value)
        {
            switch (type)
            {
                case DbType.Boolean when value is bool boolean: writer.Write((byte)(boolean ? 1 : 0)); return;
                case DbType.SByte when value is sbyte signedByte: writer.Write(signedByte); return;
                case DbType.Byte when value is byte unsignedByte: writer.Write(unsignedByte); return;
                case DbType.Int16 when value is short signedShort: writer.Write(signedShort); return;
                case DbType.UInt16 when value is ushort unsignedShort: writer.Write(unsignedShort); return;
                case DbType.Int32 when value is int signedInt: writer.Write(signedInt); return;
                case DbType.UInt32 when value is uint unsignedInt: writer.Write(unsignedInt); return;
                case DbType.Int64 when value is long signedLong: writer.Write(signedLong); return;
                case DbType.UInt64 when value is ulong unsignedLong: writer.Write(unsignedLong); return;
                case DbType.Single when value is float single: writer.Write(single); return;
                case DbType.Double when value is double number: writer.Write(number); return;
                case DbType.String:
                case DbType.AnsiString:
                case DbType.StringFixedLength:
                case DbType.AnsiStringFixedLength:
                    if (!(value is string text)) break;
                    CheckLength(writer, ResultColumn.Utf8.GetByteCount(text));
                    WriteBytes(writer, ResultColumn.Utf8.GetBytes(text));
                    return;
                case DbType.Binary when value is byte[] bytes: CheckLength(writer, bytes.Length); WriteBytes(writer, bytes); return;
            }
            throw new ArgumentException("The parameter value does not match its declared DbType.");
        }

        private static void WriteDecimal(BinaryWriter writer, OceanBaseParameter parameter, object value)
        {
            var number = value is decimal exact ? new OceanBaseDecimal(exact) : value is OceanBaseDecimal provider ? provider
                : throw new ArgumentException("The parameter value does not match its declared DbType.");
            if (number.Scale < 0 || number.Scale > 28 || parameter.Precision > 28 || parameter.Scale > 28)
                throw new NotSupportedException("The decimal parameter precision or scale requires C03.");
            number.ToDecimal(); // Validate the CLR range exactly, without rounding.
            var scale = parameter.DeclaredScale ?? number.Scale;
            if (parameter.Precision != 0 && scale > parameter.Precision)
                throw new ArgumentException("Decimal scale exceeds its declared precision.");
            if (parameter.Precision != 0 || parameter.DeclaredScale.HasValue)
                number = DecimalCodec.Constrain(number, parameter.Precision == 0 ? 29 : parameter.Precision, scale);
            WriteBytes(writer, ResultColumn.Utf8.GetBytes(number.ToString()));
        }

        private static void CheckLength(BinaryWriter writer, int count)
        {
            var prefix = count < 251 ? 1 : count <= ushort.MaxValue ? 3 : count <= 0xFFFFFF ? 4 : 9;
            if (count > PacketChannel.DefaultMaxPacketSize - 10 - writer.BaseStream.Position - prefix) throw TooLarge();
        }
        private static void WriteBytes(BinaryWriter writer, byte[] bytes)
        {
            var count = bytes.Length;
            if (count < 251) writer.Write((byte)count);
            else if (count <= ushort.MaxValue) { writer.Write((byte)252); writer.Write((ushort)count); }
            else if (count <= 0xFFFFFF) { writer.Write((byte)253); writer.Write((byte)count); writer.Write((byte)(count >> 8)); writer.Write((byte)(count >> 16)); }
            else { writer.Write((byte)254); writer.Write((ulong)count); }
            writer.Write(bytes);
        }
        private static ArgumentException TooLarge() => new ArgumentException("Prepared parameter data exceeds the packet size limit.");
    }
}
