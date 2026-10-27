using System;
using System.IO;
using System.Text;

namespace OceanBaseConnector.Internal.Protocol
{
    internal sealed class LobLocator
    {
        internal int Version { get; }
        internal long ByteLength { get; }
        internal bool HasInlineData { get; }
        internal ArraySegment<byte> InlineData { get; }
        private readonly int? _payloadSizeOffset;
        private readonly int? _diskSizeOffset;

        private LobLocator(int version, long length, bool inline, ArraySegment<byte> data = default, int? payloadSizeOffset = null, int? diskSizeOffset = null)
        { Version = version; ByteLength = length; HasInlineData = inline; InlineData = data; _payloadSizeOffset = payloadSizeOffset; _diskSizeOffset = diskSizeOffset; }

        internal byte[] EncodeInline(Encoding encoding)
        {
            if (!HasInlineData) throw new InvalidOperationException("An external LOB has no inline payload to encode.");
            var text = ResultColumn.Utf8.GetString(InlineData.Array!, InlineData.Offset, InlineData.Count);
            var count = encoding.GetByteCount(text);
            if ((long)InlineData.Offset + count > PacketChannel.DefaultMaxPacketSize - 10)
                throw new ArgumentException("The encoded LOB locator exceeds the packet size limit.");
            var bytes = new byte[InlineData.Offset + count];
            Buffer.BlockCopy(InlineData.Array!, 0, bytes, 0, InlineData.Offset);
            encoding.GetBytes(text, 0, text.Length, bytes, InlineData.Offset);
            using var stream = new MemoryStream(bytes);
            using var writer = new BinaryWriter(stream);
            if (_payloadSizeOffset.HasValue) { stream.Position = _payloadSizeOffset.Value; writer.Write((uint)count); }
            if (_diskSizeOffset.HasValue) { stream.Position = _diskSizeOffset.Value; writer.Write((ulong)count); }
            return bytes;
        }

        internal static LobLocator Parse(byte[] bytes)
        {
            var reader = new ByteCursor(bytes);
            var common = reader.ReadUInt32(); var header = reader.ReadUInt32();
            if (common == 0x4c4f4221)
            {
                if (header != 1) throw new NotSupportedException("This LOB locator version is not supported.");
                if (reader.ReadUInt64() > long.MaxValue) throw Invalid(); // snapshot_version is signed
                reader.Skip(14); // table, column, flags
                if (reader.ReadUInt16() != 0) throw new NotSupportedException("LOB storage options are not supported.");
                var offset = reader.ReadUInt32(); var length = reader.ReadUInt32();
                return Inline(bytes, 1, Range(reader, offset), length, payloadSizeOffset: 36);
            }
            if ((header & 255) != 2)
            {
                if (header == 1) throw Invalid();
                throw new NotSupportedException("This LOB locator version is not supported.");
            }
            ValidateCommon(common, memory: true);
            var type = (header >> 8) & 15; var flags = header >> 12;
            var inline = (flags & 2) != 0; var simple = (flags & 8) != 0; var external = (flags & 16) != 0;
            if (type < 1 || type > 3 || (flags & 32) != 0 || simple && (!inline || external)) throw Invalid();
            if (type == 3) throw new NotSupportedException("Delta LOB locators are not supported.");
            if (simple) return Inline(bytes, 2, 8, (uint)reader.Remaining);

            int? payloadStart = null; uint payloadLength = 0;
            if (external)
            {
                reader.Skip(22); var rowkeySize = reader.ReadUInt16();
                var offset = reader.ReadUInt32(); payloadLength = reader.ReadUInt32();
                payloadStart = Range(reader, offset);
                var extensionSize = reader.ReadUInt16(); reader.Skip(extensionSize); reader.Skip(rowkeySize);
            }
            var diskStart = bytes.Length - reader.Remaining;
            var diskFlags = ValidateCommon(reader.ReadUInt32(), memory: false);
            var initialized = (diskFlags & 1) != 0; var diskInline = (diskFlags & 4) != 0;
            long? byteLength = null;
            if (initialized)
            {
                reader.Skip(16); var length = reader.ReadUInt64();
                if (length > long.MaxValue) throw Invalid();
                byteLength = (long)length;
            }
            if (!diskInline) reader.Skip(56); // outrow context48 + character length8
            var dataStart = bytes.Length - reader.Remaining;
            if (payloadStart.HasValue)
            {
                // Empty external locators may point to the synthetic four-byte disk header.
                if (payloadStart.Value != dataStart && !(inline && !initialized && payloadLength == 0 && payloadStart.Value == diskStart))
                    throw Invalid();
            }
            if (!inline)
            {
                if (diskInline || !byteLength.HasValue || reader.HasRemaining || payloadLength != 0) throw Invalid();
                return new LobLocator(2, byteLength.Value, false);
            }
            var inlineLength = external ? payloadLength : (uint)reader.Remaining;
            if (byteLength.HasValue && byteLength.Value != inlineLength) throw Invalid();
            return Inline(bytes, 2, dataStart, inlineLength, external ? 36 : (int?)null, initialized ? diskStart + 20 : (int?)null);
        }

        private static uint ValidateCommon(uint common, bool memory)
        {
            var flags = common >> 8;
            if ((common & 255) != 1 || common >> 17 != 0 || ((flags & 256) != 0) != memory || (flags & 4) == 0 && (flags & 1) == 0)
                throw Invalid();
            return flags;
        }

        private static int Range(ByteCursor reader, uint offset)
        {
            if (offset > reader.Remaining) throw Invalid();
            // Range is relative to data after the fixed 40-byte header in both versions.
            return 40 + (int)offset;
        }

        private static LobLocator Inline(byte[] bytes, int version, int offset, uint length, int? payloadSizeOffset = null, int? diskSizeOffset = null)
        {
            if ((uint)offset > (uint)bytes.Length || length != (uint)(bytes.Length - offset)) throw Invalid();
            return new LobLocator(version, length, true, new ArraySegment<byte>(bytes, offset, (int)length), payloadSizeOffset, diskSizeOffset);
        }

        private static ProtocolException Invalid() => new ProtocolException("Invalid LOB locator layout or length.");
    }
}
