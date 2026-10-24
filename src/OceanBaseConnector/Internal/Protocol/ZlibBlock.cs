using System;
using ICSharpCode.SharpZipLib;
using ICSharpCode.SharpZipLib.Zip.Compression;

namespace OceanBaseConnector.Internal.Protocol
{
    internal static class ZlibBlock
    {
        internal static ArraySegment<byte> Compress(byte[] data, int count)
        {
            if (count < 100) return new ArraySegment<byte>(data, 0, count);
            var deflater = new Deflater();
            deflater.SetInput(data, 0, count); deflater.Finish();
            var output = new byte[count];
            var size = 0;
            while (size < count && !deflater.IsFinished)
            {
                var written = deflater.Deflate(output, size, count - size);
                if (written == 0) break;
                size += written;
            }
            return deflater.IsFinished && size < count ? new ArraySegment<byte>(output, 0, size) : new ArraySegment<byte>(data, 0, count);
        }

        internal static byte[] Decompress(byte[] data, int expectedLength)
        {
            if (expectedLength <= 0 || expectedLength > PacketChannel.MaxFramePayload)
                throw new ProtocolException("Invalid decompressed block length.");
            try
            {
                var inflater = new Inflater();
                inflater.SetInput(data);
                var output = new byte[expectedLength];
                var size = 0;
                while (size < output.Length)
                {
                    var read = inflater.Inflate(output, size, output.Length - size);
                    if (read == 0) break;
                    size += read;
                }
                // A full output buffer alone does not prove end-of-stream or a valid Adler checksum.
                if (inflater.Inflate(new byte[1]) != 0 || size != expectedLength || !inflater.IsFinished || inflater.RemainingInput != 0)
                    throw new ProtocolException("Compressed block length, end marker or trailing data is invalid.");
                return output;
            }
            catch (SharpZipBaseException error) { throw new ProtocolException("Invalid zlib compressed block.", error); }
        }
    }
}
