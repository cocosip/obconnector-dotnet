using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests.Protocol
{
    public sealed class Ob20TraceTests
    {
        [Theory]
        [InlineData(false, "160200ff0846554c4c5f54524300160200ffc500d3073f000000e4070100000001e50708000000000000000000f03fe6070100000001e70708000000000000000000f03fe807080000000100000000000000e907010000000100")]
        [InlineData(true, "d30745000000d3073f000000e4070100000001e50708000000000000000000f03fe6070100000001e70708000000000000000000f03fe807080000000100000000000000e9070100000001")]
        public void Ob20TraceReadsIndependentFixedJavaControlFixture(bool modern, string hex)
        {
            var trace = new Ob20TraceContext(modern); trace.ReadExtra(Convert.FromHexString(hex), modern);
            Assert.NotEmpty(trace.BeginRequest(default)); trace.CompleteRequest();
            Assert.Contains("oceanbase_dotnet", System.Text.Encoding.UTF8.GetString(trace.BeginRequest(default)));
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public void Ob20TraceControlEnablesCorrelatedSpanAndQueuesBoundedDriverRecord(bool modern)
        {
            var trace = new Ob20TraceContext(modern);
            var context = new ActivityContext(ActivityTraceId.CreateFromString("00112233445566778899aabbccddeeff"),
                ActivitySpanId.CreateFromString("1020304050607080"), ActivityTraceFlags.Recorded);
            Assert.Empty(trace.BeginRequest(context)); trace.CompleteRequest();
            trace.ReadExtra(Wrap(Control(1, 1), modern), modern);
            var request = trace.BeginRequest(context);
            Assert.NotEmpty(request);
            // The server prints UUIDs in wire order; keep the W3C trace ID byte order.
            var bytes = Unwrap(request, modern);
            Assert.Contains("00112233445566778899AABBCCDDEEFF", Convert.ToHexString(bytes));
            Assert.DoesNotContain("1020304050607080", Convert.ToHexString(bytes));
            trace.CompleteRequest();
            var flushed = Unwrap(trace.BeginRequest(context), modern);
            Assert.Contains("oceanbase_dotnet", System.Text.Encoding.UTF8.GetString(flushed));
            Assert.Contains("00112233-4455-6677-8899-aabbccddeeff", System.Text.Encoding.UTF8.GetString(flushed));
            trace.CompleteRequest();
        }

        [Fact]
        public void Ob20TraceOptionFreezesAndRequiresOb20()
        {
            var builder = new OceanBaseConnectionStringBuilder("Server=a,b;User ID=u;Use OB20=true;Enable Full Link Trace=true");
            Assert.True(builder.EnableFullLinkTrace); var options = builder.Build(); builder.EnableFullLinkTrace = false;
            Assert.True(options.EnableFullLinkTrace); Assert.All(options.Endpoints, e => Assert.True(options.ForEndpoint(e).EnableFullLinkTrace));
            Assert.True(new OceanBaseConnectionStringBuilder(options.SanitizedConnectionString).EnableFullLinkTrace);
            Assert.Throws<ArgumentException>(() => new OceanBaseConnectionStringBuilder("Server=a;User ID=u;Enable Full Link Trace=true").Build());
            Assert.False(new OceanBaseConnectionStringBuilder().EnableFullLinkTrace);
        }

        [Theory]
        [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
        public void Ob20LegacyTraceAcceptsReferenceLengthEncodingAndCaseInsensitiveKey(bool uppercase, bool referenceLength)
        {
            var control = Tlv(2003, Control(1, 1).AsSpan(6).ToArray().Concat(Tlv(2025, new byte[] { 1 })).ToArray());
            Assert.Equal(69, control.Length); var bytes = Wrap(control, false);
            if (uppercase) System.Text.Encoding.ASCII.GetBytes("FULL_TRC").CopyTo(bytes, 5);
            // Fixed Java OceanBaseSerialize uses OB_MAX_V1B=64, so length 69 is C5 00.
            if (referenceLength) bytes = bytes.Take(18).Concat(new byte[] { 0xc5, 0 }).Concat(bytes.Skip(19)).ToArray();
            var trace = new Ob20TraceContext(false); trace.ReadExtra(bytes, false);
            Assert.NotEmpty(trace.BeginRequest(default));
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public void Ob20TraceRejectsEveryTruncatedContainerAndDoesNotPublishPartialPolicy(bool modern)
        {
            var data = Wrap(Control(1, 1), modern);
            for (var size = 1; size < data.Length; size++)
            {
                var trace = new Ob20TraceContext(modern);
                Assert.Throws<ProtocolException>(() => trace.ReadExtra(data.AsSpan(0, size).ToArray(), modern));
                Assert.Empty(trace.BeginRequest(default));
            }
            var atomic = new Ob20TraceContext(modern);
            Assert.Throws<ProtocolException>(() => atomic.ReadExtra(data.Concat(new byte[] { 1 }).ToArray(), modern));
            Assert.Empty(atomic.BeginRequest(default));
            Assert.Throws<ProtocolException>(() => atomic.ReadExtra(new byte[65537], modern));
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public void Ob20TraceRejectsMalformedScalarsAndDuplicateKnownFields(bool modern)
        {
            var malformed = new[]
            {
                Tlv(2020, new byte[2]), Tlv(2021, BitConverter.GetBytes(double.NaN)), Tlv(2021, BitConverter.GetBytes(double.PositiveInfinity)),
                Tlv(2021, BitConverter.GetBytes(-0.1)), Tlv(2023, BitConverter.GetBytes(1.1)), Tlv(2024, BitConverter.GetBytes(-1L)),
                Tlv(2022, new byte[] { 0 }), Tlv(2022, new byte[] { 5 }), Tlv(2025, new byte[] { 2 }),
                Tlv(2020, new byte[] { 1 }).Concat(Tlv(2020, new byte[] { 1 })).ToArray(),
                new byte[] { 228, 7, 255, 255, 255, 255 },
            };
            foreach (var field in malformed)
                Assert.Throws<ProtocolException>(() => new Ob20TraceContext(modern).ReadExtra(Wrap(Tlv(2003, field), modern), modern));
            var query = Tlv(2010, BitConverter.GetBytes(20L)).Concat(Tlv(2011, BitConverter.GetBytes(10L))).ToArray();
            Assert.Throws<ProtocolException>(() => new Ob20TraceContext(modern).ReadExtra(Wrap(Tlv(2002, query), modern), modern));
            Assert.Throws<ProtocolException>(() => new Ob20TraceContext(modern).ReadExtra(Wrap(Tlv(2004, Tlv(2032, new byte[15])), modern), modern));
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public void Ob20TraceSkipsUnknownFieldsAndHonorsDisabledSamplingWithoutLosingPendingRecord(bool modern)
        {
            var trace = new Ob20TraceContext(modern);
            var unknown = Tlv(9000, Array.Empty<byte>());
            trace.ReadExtra(Wrap(Tlv(9001, new byte[] { 255 }).Concat(Tlv(2003, unknown.Concat(unknown).ToArray())).ToArray(), modern), modern);
            Assert.Empty(trace.BeginRequest(default)); trace.CompleteRequest();
            trace.ReadExtra(Wrap(Control(1, 1), modern), modern); Assert.NotEmpty(trace.BeginRequest(default)); trace.CompleteRequest();
            trace.ReadExtra(Wrap(Control(1, 0), modern), modern);
            var queued = Unwrap(trace.BeginRequest(default), modern); Assert.Equal(1, new ByteCursor(queued).ReadUInt16()); trace.CompleteRequest();
            Assert.Empty(trace.BeginRequest(default)); trace.CompleteRequest();
            trace.ReadExtra(Wrap(Control(0, 1), modern), modern); Assert.Empty(trace.BeginRequest(default)); trace.CompleteRequest();
            trace.ReadExtra(Wrap(Tlv(2003, Tlv(2020, new byte[] { 1 }).Concat(Tlv(2022, new byte[] { 4 })).ToArray()), modern), modern);
            Assert.Empty(trace.BeginRequest(default)); trace.CompleteRequest();
            trace.ReadExtra(Wrap(Tlv(2003, Tlv(2025, new byte[] { 1 })), modern), modern);
            Assert.NotEmpty(trace.BeginRequest(default)); trace.CompleteRequest();
        }

        [Theory]
        [InlineData(false, 127)] [InlineData(true, 127)]
        [InlineData(false, 128)] [InlineData(true, 128)]
        [InlineData(false, 255)] [InlineData(true, 255)]
        public void Ob20TraceLevelUsesSignedWireSemanticsAndShowOverridesSampling(bool modern, byte level)
        {
            var trace = new Ob20TraceContext(modern);
            trace.ReadExtra(Wrap(Control(level, 1), modern), modern);
            Assert.Equal(level < 128, trace.BeginRequest(default).Length > 0);
            trace.CompleteRequest();
            // Flush any previous record before checking the independent SHOW policy.
            trace.BeginRequest(default); trace.CompleteRequest();
            trace.ReadExtra(Wrap(Tlv(2003, Tlv(2025, new byte[] { 1 })), modern), modern);
            var bytes = Unwrap(trace.BeginRequest(default), modern);
            Assert.Contains("EE070100000001", Convert.ToHexString(bytes));
        }

        [Fact]
        public void Ob20LegacyTraceRejectsInvalidObjectsUnboundedVarintsAndMissingTerminators()
        {
            var bytes = Wrap(Control(1, 1), false); var type = (byte[])bytes.Clone(); type[0] = 1;
            Assert.Throws<ProtocolException>(() => new Ob20TraceContext(false).ReadExtra(type, false));
            var terminator = (byte[])bytes.Clone(); terminator[13] = 1;
            Assert.Throws<ProtocolException>(() => new Ob20TraceContext(false).ReadExtra(terminator, false));
            Assert.Throws<ProtocolException>(() => new Ob20TraceContext(false).ReadExtra(new byte[] { 22, 2, 0, 255, 255, 255, 255, 255 }, false));
        }

        [Theory]
        [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
        public async Task Ob20TraceControlIsAppliedOnlyAfterTailChecksum(bool async, bool corrupt)
        {
            var data = Ob20Packet.Build(7, 0, 1, new byte[] { 10, 11 }, Wrap(Control(1, 1), true), flags: 10);
            if (corrupt) data[data.Length - 1] ^= 1;
            var trace = new Ob20TraceContext(true); using var wire = new Ob20StreamTests.Wire(data); using var stream = new Ob20Stream(wire, 7, trace);
            stream.BeginRequest(); stream.Write(new byte[] { 3 }, 0, 1); stream.FinishRequest(); var bytes = new byte[1];
            Assert.Equal(1, async ? await stream.ReadAsync(bytes, 0, 1, CancellationToken.None) : stream.Read(bytes, 0, 1));
            Assert.Empty(trace.BeginRequest(default));
            if (corrupt)
            {
                await Assert.ThrowsAsync<ProtocolException>(async () =>
                { Assert.Equal(1, async ? await stream.ReadAsync(bytes, 0, 1, CancellationToken.None) : stream.Read(bytes, 0, 1)); });
                Assert.Empty(trace.BeginRequest(default)); Assert.True(wire.Closed);
            }
            else
            {
                Assert.Equal(1, async ? await stream.ReadAsync(bytes, 0, 1, CancellationToken.None) : stream.Read(bytes, 0, 1));
                stream.CompleteResponse(); Assert.NotEmpty(trace.BeginRequest(default));
            }
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public async Task Ob20TraceExtraAppearsOnlyInFirstRequestFragment(bool modern)
        {
            var trace = new Ob20TraceContext(modern); trace.ReadExtra(Wrap(Control(1, 1), modern), modern);
            using var wire = new Ob20StreamTests.Wire(Array.Empty<byte>()); using var stream = new Ob20Stream(wire, 7, trace);
            stream.BeginRequest(); var bytes = new byte[128 * 1024]; await stream.WriteAsync(bytes, 0, bytes.Length, CancellationToken.None);
            await stream.FinishRequestAsync(CancellationToken.None); stream.CompleteResponse(false);
            var frames = wire.Output.ToArray(); var offset = 0; byte sequence = 0; var payload = 0;
            while (offset < frames.Length)
            {
                var header = Ob20Packet.ParseHeader(frames.AsSpan(offset, 31).ToArray(), 7, 0, sequence);
                var frame = Ob20Packet.Parse(frames.AsSpan(offset, header.PayloadLength + 35).ToArray(), 7, 0, sequence);
                Assert.Equal(sequence == 0, frame.ExtraInfo.Count > 0); Assert.Equal(modern, (frame.Header.Flags & 8) != 0);
                Assert.Equal(offset + header.PayloadLength + 35 == frames.Length, (frame.Header.Flags & 2) != 0);
                payload += frame.Payload.Count; offset += header.PayloadLength + 35; sequence++;
            }
            Assert.Equal(3, sequence); Assert.Equal(bytes.Length, payload);
        }

        internal static byte[] Control(byte level, double sample) => Tlv(2003,
            Tlv(2020, new[] { level }).Concat(Tlv(2021, BitConverter.GetBytes(sample))).Concat(Tlv(2022, new byte[] { 1 }))
                .Concat(Tlv(2023, BitConverter.GetBytes(1d))).Concat(Tlv(2024, BitConverter.GetBytes(1L))).ToArray());
        internal static byte[] Tlv(ushort type, byte[] bytes)
        {
            using var stream = new MemoryStream(); stream.WriteByte((byte)type); stream.WriteByte((byte)(type >> 8));
            stream.Write(BitConverter.GetBytes(bytes.Length)); stream.Write(bytes); return stream.ToArray();
        }
        internal static byte[] Wrap(byte[] bytes, bool modern) => modern ? Tlv(2003, bytes) : Legacy(System.Text.Encoding.ASCII.GetBytes("full_trc")).Concat(Legacy(bytes)).ToArray();
        private static byte[] Legacy(byte[] bytes)
        {
            using var stream = new MemoryStream(); stream.Write(new byte[] { 22, 2, 0, 255 });
            var length = bytes.Length;
            while (length >= 128) { stream.WriteByte((byte)(length | 128)); length >>= 7; }
            stream.WriteByte((byte)length); stream.Write(bytes); stream.WriteByte(0); return stream.ToArray();
        }
        private static byte[] Unwrap(byte[] bytes, bool modern)
        {
            var cursor = new ByteCursor(bytes);
            if (modern) { Assert.Equal(2003, cursor.ReadUInt16()); var length = cursor.ReadUInt32(); Assert.Equal(cursor.Remaining, (int)length); return cursor.ReadRemaining(); }
            for (var value = 0; value < 2; value++)
            {
                Assert.Equal(new byte[] { 22, 2, 0, 255 }, cursor.ReadBytes(4)); var length = 0; var shift = 0; byte part;
                do { part = cursor.ReadUInt8(); length |= (part & 127) << shift; shift += 7; } while (part >= 128);
                var result = cursor.ReadBytes(length); Assert.Equal(0, cursor.ReadUInt8()); if (value == 1) { Assert.False(cursor.HasRemaining); return result; }
            }
            throw new InvalidOperationException();
        }
    }
}
