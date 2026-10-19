using System;

namespace OceanBaseConnector.Internal.Protocol
{
    internal static class Ob20Checksum
    {
        private static readonly uint[] PayloadTable = CreatePayloadTable();

        public static ushort Header(byte[] bytes, int offset, int count)
        {
            ValidateRange(bytes, offset, count);
            uint crc = 0;
            for (var i = offset; i < offset + count; i++)
            {
                crc ^= bytes[i];
                for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xa001u);
            }
            return (ushort)crc;
        }

        // OB20 uses the raw Castagnoli accumulator: initial zero, no final complement.
        public static uint Payload(uint state, byte[] bytes, int offset, int count)
        {
            ValidateRange(bytes, offset, count);
            for (var i = offset; i < offset + count; i++) state = (state >> 8) ^ PayloadTable[(state ^ bytes[i]) & 255];
            return state;
        }

        private static uint[] CreatePayloadTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < table.Length; i++)
            {
                var crc = i;
                for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0x82f63b78u);
                table[i] = crc;
            }
            return table;
        }

        private static void ValidateRange(byte[] bytes, int offset, int count)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if ((uint)offset > (uint)bytes.Length || (uint)count > (uint)(bytes.Length - offset))
                throw new ArgumentOutOfRangeException(nameof(offset));
        }
    }
}
