using System;

namespace OceanBaseConnector.Internal.Protocol
{
    /// <summary>
    /// Bounds-checked cursor over a byte range. Every read validates the
    /// remaining length before touching the buffer, and length arguments are
    /// validated with unsigned comparisons so crafted negative or overflowing
    /// counts cannot bypass the check.
    /// </summary>
    internal sealed class ByteCursor
    {
        private readonly byte[] _data;
        private readonly int _offset;
        private readonly int _length;
        private int _position;

        public ByteCursor(byte[] data)
        {
            // No constructor chaining: chained arguments evaluate data.Length
            // before the null check and turn null into a NullReferenceException.
            if (data is null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            _data = data;
            _offset = 0;
            _length = data.Length;
        }

        public ByteCursor(byte[] data, int offset, int length)
        {
            if (data is null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            if ((uint)offset > (uint)data.Length || (uint)length > (uint)(data.Length - offset))
            {
                throw new ArgumentOutOfRangeException(nameof(offset), "Offset and length exceed the buffer.");
            }

            _data = data;
            _offset = offset;
            _length = length;
        }

        public int Remaining => _length - _position;

        public bool HasRemaining => _position < _length;

        public byte ReadUInt8()
        {
            EnsureAvailable(1);
            return _data[_offset + _position++];
        }

        public ushort ReadUInt16()
        {
            EnsureAvailable(2);
            var value = (ushort)(_data[_offset + _position] | (_data[_offset + _position + 1] << 8));
            _position += 2;
            return value;
        }

        /// <summary>Reads the protocol's 3-byte little-endian length field.</summary>
        public uint ReadUInt24()
        {
            EnsureAvailable(3);
            var value = (uint)(_data[_offset + _position] |
                (_data[_offset + _position + 1] << 8) |
                (_data[_offset + _position + 2] << 16));
            _position += 3;
            return value;
        }

        public uint ReadUInt32()
        {
            EnsureAvailable(4);
            var value = (uint)(_data[_offset + _position] |
                (_data[_offset + _position + 1] << 8) |
                (_data[_offset + _position + 2] << 16) |
                (_data[_offset + _position + 3] << 24));
            _position += 4;
            return value;
        }

        public ulong ReadUInt64()
        {
            EnsureAvailable(8);
            ulong value = 0;
            for (var i = 7; i >= 0; i--)
            {
                value = (value << 8) | _data[_offset + _position + i];
            }

            _position += 8;
            return value;
        }

        public byte[] ReadBytes(int count)
        {
            EnsureAvailable(count);
            var result = new byte[count];
            Buffer.BlockCopy(_data, _offset + _position, result, 0, count);
            _position += count;
            return result;
        }

        public void Skip(int count)
        {
            EnsureAvailable(count);
            _position += count;
        }

        /// <summary>Returns the unconsumed bytes as a copy.</summary>
        public byte[] ReadRemaining()
        {
            return ReadBytes(_length - _position);
        }

        /// <summary>
        /// Reads a length-encoded integer: 0-250 in one byte, 0xFC/0xFD/0xFE
        /// prefixes introduce 2/3/8-byte values. 0xFB marks a NULL string and
        /// is rejected here; this reader is only used where NULL is invalid.
        /// </summary>
        public long ReadLengthEncodedInteger()
        {
            var prefix = ReadUInt8();
            switch (prefix)
            {
                case 0xFB:
                    throw new ProtocolException("Unexpected NULL marker in a length-encoded integer.");
                case 0xFC:
                    return ReadUInt16();
                case 0xFD:
                    return ReadUInt24();
                case 0xFE:
                    var value = ReadUInt64();
                    if (value > long.MaxValue) throw new ProtocolException("Length-encoded integer exceeds Int64.");
                    return (long)value;
                case 0xFF:
                    throw new ProtocolException("Invalid length-encoded integer marker.");
                default:
                    return prefix;
            }
        }

        public void SkipLengthEncodedInteger()
        {
            ReadLengthEncodedInteger();
        }

        public byte[] ReadLengthEncodedBytes()
        {
            var length = ReadLengthEncodedInteger();
            if (length > int.MaxValue || (uint)length > (uint)(_length - _position))
            {
                throw new ProtocolException(
                    $"Length-encoded value of {length} byte(s) exceeds the remaining {_length - _position} byte(s).");
            }

            return ReadBytes((int)length);
        }

        public byte[]? ReadNullableLengthEncodedBytes()
        {
            EnsureAvailable(1);
            if (_data[_offset + _position] == 0xFB) { _position++; return null; }
            return ReadLengthEncodedBytes();
        }

        /// <summary>Reads bytes up to (not including) the next NUL byte and skips it.</summary>
        public byte[] ReadNullTerminatedBytes()
        {
            var start = _position;
            while (_position < _length && _data[_offset + _position] != 0)
            {
                _position++;
            }

            EnsureAvailable(1);
            var result = new byte[_position - start];
            Buffer.BlockCopy(_data, _offset + start, result, 0, result.Length);
            _position++; // consume the NUL
            return result;
        }

        public string ReadNullTerminatedAscii()
        {
            return System.Text.Encoding.ASCII.GetString(ReadNullTerminatedBytes());
        }

        private void EnsureAvailable(int count)
        {
            // Unsigned comparison: negative or overflowing counts read as huge.
            if ((uint)count > (uint)(_length - _position))
            {
                throw new ProtocolException(
                    $"Byte cursor underrun: requested {count} byte(s), {(_length - _position)} remaining.");
            }
        }
    }
}
