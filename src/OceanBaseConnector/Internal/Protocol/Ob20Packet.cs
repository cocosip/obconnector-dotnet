using System;

namespace OceanBaseConnector.Internal.Protocol
{
    /// <summary>A validated OB20 frame; byte segments borrow the caller-owned frame buffer.</summary>
    internal sealed class Ob20Packet
    {
        public const int HeaderLength = 31;
        public const int MaxPayloadLength = 0xffffff - 28;
        public const uint ExtraInfoFlag = 1;
        public const uint LastPacketFlag = 2;

        private Ob20Packet(HeaderInfo header, ArraySegment<byte> extraInfo, ArraySegment<byte> payload)
        { Header = header; ExtraInfo = extraInfo; Payload = payload; }

        public HeaderInfo Header { get; }
        public ArraySegment<byte> ExtraInfo { get; }
        public ArraySegment<byte> Payload { get; }

        public static HeaderInfo ParseHeader(byte[] bytes, uint connectionId, uint requestId, byte sequence,
            int maxPayloadLength = MaxPayloadLength)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (requestId > 0xffffff) throw new ArgumentOutOfRangeException(nameof(requestId));
            if ((uint)maxPayloadLength > MaxPayloadLength) throw new ArgumentOutOfRangeException(nameof(maxPayloadLength));
            if (bytes.Length < HeaderLength) throw new ProtocolException("Truncated OB20 header.");
            var cursor = new ByteCursor(bytes, 0, HeaderLength);
            var outerLength = cursor.ReadUInt24();
            var compressedSequence = cursor.ReadUInt8();
            var uncompressedLength = cursor.ReadUInt24();
            var magic = cursor.ReadUInt16();
            var version = cursor.ReadUInt16();
            var actualConnection = cursor.ReadUInt32();
            var actualRequest = cursor.ReadUInt24();
            var actualSequence = cursor.ReadUInt8();
            var payloadLength = cursor.ReadUInt32();
            var flags = cursor.ReadUInt32();
            var reserved = cursor.ReadUInt16();
            var checksum = cursor.ReadUInt16();
            if (checksum != 0 && checksum != Ob20Checksum.Header(bytes, 0, HeaderLength - 2))
                throw new ProtocolException("OB20 header checksum mismatch.");
            if (magic != 0x20ab || version != 20) throw new ProtocolException("Unsupported OB20 magic or version.");
            if (uncompressedLength != 0) throw new ProtocolException("Compressed OB20 frames are not supported.");
            if (payloadLength > (uint)maxPayloadLength || outerLength != payloadLength + 28)
                throw new ProtocolException("OB20 payload length is inconsistent or exceeds the configured limit.");
            if (actualConnection != connectionId || actualRequest != requestId || actualSequence != sequence)
                throw new ProtocolException("OB20 connection, request or sequence mismatch.");
            return new HeaderInfo(compressedSequence, (int)payloadLength, flags, reserved);
        }

        public static Ob20Packet Parse(byte[] bytes, uint connectionId, uint requestId, byte sequence,
            int maxPayloadLength = MaxPayloadLength)
        {
            var header = ParseHeader(bytes, connectionId, requestId, sequence, maxPayloadLength);
            if (bytes.Length != HeaderLength + header.PayloadLength + 4)
                throw new ProtocolException("Truncated OB20 payload or trailing frame data.");
            var tail = new ByteCursor(bytes, bytes.Length - 4, 4).ReadUInt32();
            if (tail != 0 && tail != Ob20Checksum.Payload(0, bytes, HeaderLength, header.PayloadLength))
                throw new ProtocolException("OB20 payload checksum mismatch.");
            var offset = HeaderLength;
            var extra = new ArraySegment<byte>(bytes, offset, 0);
            if ((header.Flags & ExtraInfoFlag) != 0)
            {
                var cursor = new ByteCursor(bytes, offset, header.PayloadLength);
                var length = cursor.ReadUInt32();
                if (length > (uint)cursor.Remaining) throw new ProtocolException("OB20 extra info exceeds the payload.");
                extra = new ArraySegment<byte>(bytes, offset + 4, (int)length);
                offset += 4 + (int)length;
            }
            return new Ob20Packet(header, extra, new ArraySegment<byte>(bytes, offset, HeaderLength + header.PayloadLength - offset));
        }

        public static byte[] Build(uint connectionId, uint requestId, byte sequence, byte[] payload,
            byte[]? extraInfo = null, uint flags = LastPacketFlag, bool checksum = true)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (requestId > 0xffffff) throw new ArgumentOutOfRangeException(nameof(requestId));
            if (extraInfo == null && (flags & ExtraInfoFlag) != 0)
                throw new ArgumentException("OB20 extra info flag requires an extra info buffer.", nameof(flags));
            var length = (long)payload.Length + (extraInfo == null ? 0 : 4L + extraInfo.Length);
            if (length > MaxPayloadLength) throw new ArgumentException("OB20 frame exceeds the 24-bit outer length limit.");
            if (extraInfo != null) flags |= ExtraInfoFlag;
            var bytes = new byte[HeaderLength + (int)length + 4];
            WriteHeader(bytes, connectionId, requestId, sequence, (int)length, flags, checksum);
            var offset = HeaderLength;
            if (extraInfo != null)
            {
                Write(bytes, offset, (uint)extraInfo.Length, 4);
                Buffer.BlockCopy(extraInfo, 0, bytes, offset + 4, extraInfo.Length);
                offset += 4 + extraInfo.Length;
            }
            Buffer.BlockCopy(payload, 0, bytes, offset, payload.Length);
            if (checksum) Write(bytes, bytes.Length - 4, Ob20Checksum.Payload(0, bytes, HeaderLength, (int)length), 4);
            return bytes;
        }

        internal static void WriteHeader(byte[] bytes, uint connectionId, uint requestId, byte sequence, int length, uint flags, bool checksum)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (bytes.Length < HeaderLength || (uint)length > MaxPayloadLength || requestId > 0xffffff)
                throw new ArgumentOutOfRangeException(nameof(length));
            Array.Clear(bytes, 0, HeaderLength);
            Write(bytes, 0, (uint)length + 28, 3);
            bytes[3] = sequence;
            Write(bytes, 7, 0x20ab, 2);
            Write(bytes, 9, 20, 2);
            Write(bytes, 11, connectionId, 4);
            Write(bytes, 15, requestId, 3);
            bytes[18] = sequence;
            Write(bytes, 19, (uint)length, 4);
            Write(bytes, 23, flags, 4);
            if (checksum) Write(bytes, 29, Ob20Checksum.Header(bytes, 0, 29), 2);
        }

        private static void Write(byte[] bytes, int offset, uint value, int count)
        {
            for (var i = 0; i < count; i++) { bytes[offset + i] = (byte)value; value >>= 8; }
        }

        internal readonly struct HeaderInfo
        {
            public HeaderInfo(byte compressedSequence, int payloadLength, uint flags, ushort reserved)
            { CompressedSequence = compressedSequence; PayloadLength = payloadLength; Flags = flags; Reserved = reserved; }
            public byte CompressedSequence { get; }
            public int PayloadLength { get; }
            public uint Flags { get; }
            public ushort Reserved { get; }
        }
    }
}
