using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Transport;
using Xunit;

namespace OceanBaseConnector.UnitTests.Transport
{
    public sealed partial class TlsHandshakeFlowTests
    {
        private static byte[] BuildInitialHandshake(CapabilityFlags capabilities)
        {
            // Minimal MySQL initial-handshake packet with a fixed layout.
            using var buffer = new MemoryStream();
            buffer.WriteByte(10); // protocol version
            WriteNullTerminated(buffer, "8.0.30-OceanBase");
            WriteUInt32(buffer, 0x0A0B0C0D); // connection id
            buffer.Write(new byte[8], 0, 8); // auth-plugin-data part 1
            WriteUInt16(buffer, (ushort)((uint)capabilities & 0xFFFF)); // capability lower half
            buffer.WriteByte(45); // charset
            WriteUInt16(buffer, 0x0002); // status flags
            WriteUInt16(buffer, (ushort)(((uint)capabilities >> 16) & 0xFFFF)); // capability upper half
            buffer.WriteByte(21); // auth plugin data length
            buffer.Write(new byte[10], 0, 10); // reserved
            WriteNullTerminated(buffer, new string('x', 13)); // auth-plugin-data part 2
            WriteNullTerminated(buffer, "mysql_native_password");
            return buffer.ToArray();
        }

        private static byte[] Frame(int sequence, byte[] payload)
        {
            var frame = new byte[4 + payload.Length];
            frame[0] = (byte)payload.Length;
            frame[1] = (byte)(payload.Length >> 8);
            frame[2] = (byte)(payload.Length >> 16);
            frame[3] = (byte)sequence;
            payload.CopyTo(frame, 4);
            return frame;
        }

        /// <summary>
        /// Reads up to count bytes; a connection close or abort (client
        /// disposal can surface as a reset instead of a clean EOF) counts as
        /// end of data and returns what arrived so far.
        /// </summary>
        private static async Task<int> ReadExactlyAsync(Stream stream, byte[] buffer, int count)
        {
            var total = 0;
            while (total < count)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer, total, count - total);
                }
                catch (IOException)
                {
                    break;
                }

                if (read <= 0)
                {
                    break;
                }

                total += read;
            }

            return total;
        }

        private static OceanBaseConnectionOptions BuildOptions(
            string host,
            OceanBaseTlsMode tlsMode,
            string tlsServerName = "")
        {
            var builder = new OceanBaseConnectionStringBuilder
            {
                Server = host,
                UserId = "u",
                Password = "p",
                TlsMode = tlsMode,
            };
            if (tlsServerName.Length > 0)
            {
                builder.TlsServerName = tlsServerName;
            }

            return builder.Build();
        }

        private sealed class LoopbackServer
        {
            private readonly TcpListener _listener;

            private LoopbackServer(TcpListener listener, Task<Task> scriptTask)
            {
                _listener = listener;
                Completion = scriptTask.Unwrap();
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            }

            public int Port { get; }

            public Task Completion { get; }

            public static LoopbackServer Start(Func<TcpClient, Task> script)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var scriptTask = Task.Factory.StartNew(
                    async () =>
                    {
                        using var client = await listener.AcceptTcpClientAsync();
                        await script(client);
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
                return new LoopbackServer(listener, scriptTask);
            }

            public void Stop()
            {
                _listener.Stop();
            }
        }

        private static CapabilityFlags ParseCapabilities(byte[] initialHandshake)
        {
            // Layout mirrors BuildInitialHandshake: 1 + version NUL + 4 + 8.
            var cursor = new ByteCursor(initialHandshake);
            cursor.ReadUInt8();
            while (cursor.ReadUInt8() != 0)
            {
            }

            cursor.ReadUInt32();
            cursor.Skip(8);
            var lower = cursor.ReadUInt16();
            cursor.ReadUInt8(); // charset
            cursor.ReadUInt16(); // status
            var upper = cursor.ReadUInt16();
            return (CapabilityFlags)((uint)lower | ((uint)upper << 16));
        }

        private static bool AcceptAnyClient(
            object sender,
            System.Security.Cryptography.X509Certificates.X509Certificate? certificate,
            System.Security.Cryptography.X509Certificates.X509Chain? chain,
            SslPolicyErrors sslPolicyErrors)
        {
            return true;
        }

        private static void WriteNullTerminated(MemoryStream stream, string value)
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(value);
            stream.Write(bytes, 0, bytes.Length);
            stream.WriteByte(0);
        }

        private static void WriteUInt16(MemoryStream stream, ushort value)
        {
            stream.WriteByte((byte)value);
            stream.WriteByte((byte)(value >> 8));
        }

        private static void WriteUInt32(MemoryStream stream, uint value)
        {
            stream.WriteByte((byte)value);
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)(value >> 16));
            stream.WriteByte((byte)(value >> 24));
        }
    }
}
