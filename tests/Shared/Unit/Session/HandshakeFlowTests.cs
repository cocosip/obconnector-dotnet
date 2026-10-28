using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;
using OceanBaseConnector.Internal.Transport;
using OceanBaseConnector.UnitTests.TestInfrastructure;
using OceanBaseConnector.UnitTests.Transport;
using Xunit;

namespace OceanBaseConnector.UnitTests.Session
{
    public sealed partial class HandshakeFlowTests
    {

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

        private static byte[] BuildInitialHandshake(
            string serverVersion = "5.7.4-OceanBase 4.2.1",
            string? pluginName = "mysql_native_password",
            byte[]? seed = null,
            CapabilityFlags extraCapabilities = CapabilityFlags.None)
        {
            seed ??= SeedBytes(SeedHex);
            var buffer = new MemoryStream();
            buffer.WriteByte(10);
            buffer.Write(Encoding.ASCII.GetBytes(serverVersion));
            buffer.WriteByte(0);
            buffer.WriteByte(0xD2);
            buffer.WriteByte(0x04);
            buffer.WriteByte(0);
            buffer.WriteByte(0);
            buffer.Write(seed, 0, 8);
            buffer.WriteByte(0);
            var capsLower = (ushort)((uint)(CapabilityFlags.ClientSsl |
                CapabilityFlags.ClientProtocol41 |
                CapabilityFlags.ClientSecureConnection |
                (pluginName is null ? CapabilityFlags.None : CapabilityFlags.ClientPluginAuth) |
                CapabilityFlags.ClientPluginAuthLenencClientData |
                CapabilityFlags.ClientSessionTrack | extraCapabilities) & 0xFFFF);
            buffer.WriteByte((byte)capsLower);
            buffer.WriteByte((byte)(capsLower >> 8));
            buffer.WriteByte(45);
            buffer.WriteByte(0x02);
            buffer.WriteByte(0x00);
            var capsUpper = (ushort)(((uint)(CapabilityFlags.ClientSsl |
                CapabilityFlags.ClientProtocol41 |
                CapabilityFlags.ClientSecureConnection |
                (pluginName is null ? CapabilityFlags.None : CapabilityFlags.ClientPluginAuth) |
                CapabilityFlags.ClientPluginAuthLenencClientData |
                CapabilityFlags.ClientSessionTrack | extraCapabilities) >> 16) & 0xFFFF);
            buffer.WriteByte((byte)capsUpper);
            buffer.WriteByte((byte)(capsUpper >> 8));
            buffer.WriteByte((byte)(seed.Length + 1));
            buffer.Write(new byte[6], 0, 6);
            buffer.Write(new byte[4], 0, 4);
            buffer.Write(seed, 8, seed.Length - 8);
            buffer.WriteByte(0);
            if (pluginName is not null)
            {
                buffer.Write(Encoding.ASCII.GetBytes(pluginName));
                buffer.WriteByte(0);
            }
            return buffer.ToArray();
        }

        private static byte[] BuildAuthOk(
            bool oracleMode,
            string? obCapabilityFlag = null,
            long affectedRows = 0)
        {
            var status = ServerStatusFlags.Autocommit;
            if (oracleMode)
            {
                status |= ServerStatusFlags.ObServerUseOracleMode;
            }

            var buffer = new MemoryStream();
            buffer.WriteByte(0);
            WriteLenenc(buffer, affectedRows);
            WriteLenenc(buffer, 0);
            buffer.WriteByte((byte)status);
            buffer.WriteByte((byte)((ushort)status >> 8));
            buffer.WriteByte(0);
            buffer.WriteByte(0);

            if (obCapabilityFlag is not null)
            {
                status |= ServerStatusFlags.ServerSessionStateChanged;
                // Rewrite the status field (offset 3) including the change bit.
                var withChange = (ushort)status;
                var position = buffer.Position;
                buffer.Position = 3;
                buffer.WriteByte((byte)withChange);
                buffer.WriteByte((byte)(withChange >> 8));
                buffer.Position = position;

                buffer.WriteByte(0); // empty info string
                var state = new MemoryStream();
                state.WriteByte(OkPacket.SessionTrackSystemVariables);
                WriteLenenc(state, 20 + obCapabilityFlag.Length);
                WriteLenenc(state, 18);
                state.Write(Encoding.ASCII.GetBytes("ob_capability_flag"));
                WriteLenenc(state, obCapabilityFlag.Length);
                state.Write(Encoding.ASCII.GetBytes(obCapabilityFlag));
                var stateBytes = state.ToArray();
                WriteLenenc(buffer, stateBytes.Length);
                buffer.Write(stateBytes, 0, stateBytes.Length);
            }

            return buffer.ToArray();
        }

        private static OceanBaseConnectionOptions BuildOptions(
            string password = "secretpw",
            OceanBaseTlsMode tlsMode = OceanBaseTlsMode.Disabled,
            OceanBaseCompatibilityMode? expectedMode = null)
        {
            var builder = new OceanBaseConnectionStringBuilder
            {
                Server = "127.0.0.1",
                UserId = "u@sys",
                Password = password,
                TlsMode = tlsMode,
            };
            if (expectedMode.HasValue)
            {
                builder.ExpectedMode = expectedMode;
            }

            return builder.Build();
        }

        private static byte[] ReadExact(Stream stream, int count)
        {
            var buffer = new byte[count];
            var total = 0;
            while (total < count)
            {
                var read = stream.Read(buffer, total, count - total);
                if (read <= 0)
                {
                    throw new IOException($"Server saw only {total} of {count} expected bytes.");
                }

                total += read;
            }

            return buffer;
        }
        private const string SeedHex = "01020304050607081112131415161718191a1b1c";

        private static byte[] SeedBytes(string hex)
        {
            var seed = new byte[hex.Length / 2];
            for (var i = 0; i < seed.Length; i++)
            {
                seed[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }

            return seed;
        }

        private static void WriteLenenc(MemoryStream stream, long value)
        {
            if (value < 251)
            {
                stream.WriteByte((byte)value);
                return;
            }

            throw new NotSupportedException("Test helper only writes small lenenc values.");
        }
    }
}
