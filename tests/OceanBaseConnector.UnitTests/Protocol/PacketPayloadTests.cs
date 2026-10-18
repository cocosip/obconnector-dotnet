using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using Xunit;

namespace OceanBaseConnector.UnitTests.Protocol
{
    public sealed class PacketPayloadTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task OpeningAndReadingDoesNotPrefetchTheRestOfThePacket(bool async)
        {
            using var wire = new SpyStream(new byte[] { 5, 0, 0, 0, 10, 11, 12, 13, 14, 1, 0, 0, 1, 42 });
            var channel = new PacketChannel(wire);
            using var payload = await Open(channel, async);
            Assert.Equal(4, wire.Position);
            Assert.True(payload.CanRead);
            Assert.False(payload.CanSeek);
            Assert.False(payload.CanWrite);
            var buffer = new byte[2];
            Assert.Equal(2, await Read(payload, buffer, async));
            Assert.Equal(new byte[] { 10, 11 }, buffer);
            Assert.Equal(6, wire.Position);
            Assert.Throws<InvalidOperationException>(() => channel.ReadPacket());
            Assert.Throws<InvalidOperationException>(() => channel.BeginCommand());
            Assert.Throws<InvalidOperationException>(() => channel.WritePacket(new byte[] { 0 }));
            Assert.Throws<InvalidOperationException>(() => channel.ReplaceUnderlyingStream(wire));
            Assert.Equal(2, await Read(payload, buffer, async));
            Assert.Equal(1, await Read(payload, buffer, async));
            Assert.Equal(9, wire.Position);
            Assert.Equal(0, await Read(payload, buffer, async));
            payload.Dispose();
            Assert.Equal(new byte[] { 42 }, async ? await channel.ReadPacketAsync() : channel.ReadPacket());
            if (async) Assert.Equal(0, wire.SyncReads);
        }

        [Theory]
        [InlineData(false, 0)]
        [InlineData(true, 0)]
        [InlineData(false, 1)]
        [InlineData(true, 1)]
        public async Task IncrementalReadCrossesContinuationAndExactMultipleTerminator(bool async, int tail)
        {
            using var wire = new GeneratedFrames(PacketChannel.MaxFramePayload + tail);
            var channel = new PacketChannel(wire);
            using var payload = await Open(channel, async);
            var buffer = new byte[8192];
            long count = 0;
            int read;
            while ((read = await Read(payload, buffer, async)) != 0)
            {
                count += read;
                Assert.Equal(0x42, buffer[0]);
                Assert.Equal(0x42, buffer[read - 1]);
            }
            Assert.Equal(PacketChannel.MaxFramePayload + tail, count);
            Assert.Equal(count + 8, wire.BytesRead);
            Assert.False(wire.Closed);
            if (async) Assert.Equal(0, wire.SyncReads);
        }

        [Fact]
        public async Task ReadingThirtyTwoMiBUsesBoundedMemory()
        {
            using var wire = new GeneratedFrames(2 * PacketChannel.MaxFramePayload + 17);
            var channel = new PacketChannel(wire);
            using var payload = await Open(channel, false);
            var buffer = new byte[8192];
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            long total = 0;
            int count;
            while ((count = payload.Read(buffer, 0, buffer.Length)) != 0) total += count;
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Assert.Equal(2 * PacketChannel.MaxFramePayload + 17, total);
            Assert.InRange(allocated, 0, 256 * 1024);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task IncompleteDisposeClosesTransportAndPreventsReuse(bool async)
        {
            using var wire = new GeneratedFrames(100);
            var channel = new PacketChannel(wire);
            var payload = await Open(channel, async);
            payload.Dispose();
            payload.Dispose();
            Assert.True(wire.Closed);
            Assert.Throws<ObjectDisposedException>(() => payload.Read(new byte[1], 0, 1));
            Assert.Throws<ObjectDisposedException>(() => channel.BeginCommand());
            Assert.Throws<ObjectDisposedException>(() => channel.ReadPacket());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SequenceErrorInContinuationClosesTransport(bool async)
        {
            using var wire = new GeneratedFrames(PacketChannel.MaxFramePayload + 1, wrongSequence: true);
            var channel = new PacketChannel(wire);
            using var payload = await Open(channel, async);
            await Assert.ThrowsAsync<ProtocolException>(() => Drain(payload, async));
            Assert.True(wire.Closed);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CumulativeLimitRejectsContinuationBeforeReadingItsBody(bool async)
        {
            using var wire = new GeneratedFrames(PacketChannel.MaxFramePayload + 2);
            var channel = new PacketChannel(wire, PacketChannel.MaxFramePayload + 1);
            using var payload = await Open(channel, async);
            await Assert.ThrowsAsync<ProtocolException>(() => Drain(payload, async));
            Assert.Equal(PacketChannel.MaxFramePayload + 8, wire.BytesRead);
            Assert.True(wire.Closed);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task InitialLimitRejectsBeforeReadingBody(bool async)
        {
            using var wire = new GeneratedFrames(100);
            var channel = new PacketChannel(wire, 99);
            await Assert.ThrowsAsync<ProtocolException>(() => Open(channel, async));
            Assert.Equal(4, wire.BytesRead);
            Assert.True(wire.Closed);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task TruncatedHeaderBodyAndMissingTerminatorFail(bool async)
        {
            foreach (var bytes in new[] { new byte[] { 1, 0 }, new byte[] { 2, 0, 0, 0, 1 } })
            {
                var channel = new PacketChannel(new MemoryStream(bytes));
                await Assert.ThrowsAsync<ProtocolException>(async () =>
                {
                    using var payload = await Open(channel, async);
                    await Drain(payload, async);
                });
                Assert.Throws<ObjectDisposedException>(() => channel.BeginCommand());
            }
            using var wire = new GeneratedFrames(PacketChannel.MaxFramePayload, omitTerminator: true);
            using var full = await Open(new PacketChannel(wire), async);
            await Assert.ThrowsAsync<ProtocolException>(() => Drain(full, async));
            Assert.True(wire.Closed);
        }

        [Fact]
        public async Task CanceledReadClosesTransportAndNeverUsesSyncIo()
        {
            using var wire = new GeneratedFrames(100);
            var channel = new PacketChannel(wire);
            using var payload = await Open(channel, true);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => payload.ReadAsync(new byte[1], 0, 1, canceled.Token));
            Assert.True(wire.Closed);
            Assert.Equal(4, wire.BytesRead);
            Assert.Equal(0, wire.SyncReads);
        }

        [Fact]
        public async Task InvalidReadArgumentsAndZeroCountDoNotConsumeOrClosePayload()
        {
            using var wire = new GeneratedFrames(3);
            using var payload = await Open(new PacketChannel(wire), false);
            Assert.Throws<ArgumentNullException>(() => payload.Read(null!, 0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => payload.Read(new byte[2], -1, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => payload.Read(new byte[2], 1, 2));
            Assert.Equal(0, payload.Read(Array.Empty<byte>(), 0, 0));
            Assert.Equal(4, wire.BytesRead);
            Assert.False(wire.Closed);
            await Drain(payload, false);
            Assert.False(wire.Closed);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task EmptyPayloadReleasesChannelImmediately(bool async)
        {
            using var wire = new SpyStream(new byte[] { 0, 0, 0, 0, 1, 0, 0, 1, 42 });
            var channel = new PacketChannel(wire);
            using var empty = await Open(channel, async);
            Assert.Equal(0, await Read(empty, new byte[1], async));
            Assert.Equal(new byte[] { 42 }, async ? await channel.ReadPacketAsync() : channel.ReadPacket());
            Assert.Equal(0, await Read(empty, new byte[1], async));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task BufferedReadUsesTheSameFailureAndOwnershipRules(bool async)
        {
            using var wire = new GeneratedFrames(100);
            var channel = new PacketChannel(wire, 99);
            await Assert.ThrowsAsync<ProtocolException>(async () =>
            {
                if (async) await channel.ReadPacketAsync(); else channel.ReadPacket();
            });
            Assert.True(wire.Closed);
            Assert.Equal(4, wire.BytesRead);
            Assert.Throws<ObjectDisposedException>(() => channel.BeginCommand());
        }

        [Fact]
        public async Task CancellationAfterPartialPayloadDestroysTheChannel()
        {
            using var canceled = new CancellationTokenSource();
            using var wire = new GeneratedFrames(100, readLimit: 2, cancelAfterSixBytes: canceled);
            var channel = new PacketChannel(wire);
            using var payload = await channel.OpenPayloadAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => payload.ReadAsync(new byte[8], 0, 8, canceled.Token));
            Assert.Equal(6, wire.BytesRead);
            Assert.True(wire.Closed);
            Assert.Equal(0, wire.SyncReads);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => channel.ReadPacketAsync());
        }

        [Fact]
        public async Task PreCanceledOpenDoesNotReadOrCloseAnIdleChannel()
        {
            using var wire = new GeneratedFrames(3);
            var channel = new PacketChannel(wire);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => channel.OpenPayloadAsync(canceled.Token));
            Assert.Equal(0, wire.BytesRead);
            Assert.False(wire.Closed);
            Assert.Equal(new byte[] { 0x42, 0x42, 0x42 }, await channel.ReadPacketAsync());
            Assert.Equal(0, wire.SyncReads);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task EntireFullFrameStillRequiresItsTerminator(bool async)
        {
            using var wire = new GeneratedFrames(PacketChannel.MaxFramePayload);
            var channel = new PacketChannel(wire);
            using var payload = await Open(channel, async);
            var buffer = new byte[8192];
            var remaining = PacketChannel.MaxFramePayload;
            while (remaining != 0)
            {
                var size = Math.Min(remaining, buffer.Length);
                remaining -= async ? await payload.ReadAsync(buffer, 0, size) : payload.Read(buffer, 0, size);
            }
            Assert.Equal(PacketChannel.MaxFramePayload + 4, wire.BytesRead);
            Assert.Throws<InvalidOperationException>(() => channel.BeginCommand());
            Assert.Equal(0, await Read(payload, buffer, async));
            Assert.Equal(PacketChannel.MaxFramePayload + 8, wire.BytesRead);
            channel.BeginCommand();
            payload.Dispose();
            Assert.False(wire.Closed);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task FragmentedTransportCanMixBufferedAndStreamedPacketsAcrossSequenceWrap(bool async)
        {
            var frames = new byte[257 * 5];
            for (var i = 0; i < 257; i++)
            { frames[i * 5] = 1; frames[i * 5 + 3] = (byte)i; frames[i * 5 + 4] = (byte)(i * 7); }
            using var wire = new FragmentedStream(frames);
            var channel = new PacketChannel(wire);
            var buffer = new byte[1];
            for (var i = 0; i < 257; i++)
            {
                if (i % 2 == 0)
                {
                    using var payload = await Open(channel, async);
                    Assert.Equal(1, await Read(payload, buffer, async));
                    Assert.Equal((byte)(i * 7), buffer[0]);
                    Assert.Equal(0, await Read(payload, buffer, async));
                }
                else Assert.Equal(new byte[] { (byte)(i * 7) }, async ? await channel.ReadPacketAsync() : channel.ReadPacket());
            }
            if (async) Assert.Equal(0, wire.SyncReads);
        }

        private static Task<Stream> Open(PacketChannel channel, bool async) => async
            ? channel.OpenPayloadAsync() : Task.FromResult(channel.OpenPayload());

        private static Task<int> Read(Stream payload, byte[] buffer, bool async) => async
            ? payload.ReadAsync(buffer, 0, buffer.Length) : Task.FromResult(payload.Read(buffer, 0, buffer.Length));

        private static async Task Drain(Stream payload, bool async)
        {
            var buffer = new byte[8192];
            while (await Read(payload, buffer, async) != 0) { }
        }

        private sealed class FragmentedStream : MemoryStream
        {
            internal FragmentedStream(byte[] bytes) : base(bytes) { }
            internal int SyncReads { get; private set; }
            public override int Read(byte[] buffer, int offset, int count) { SyncReads++; return base.Read(buffer, offset, Math.Min(count, 1)); }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            { token.ThrowIfCancellationRequested(); return Task.FromResult(base.Read(buffer, offset, Math.Min(count, 1))); }
        }

        // Generates frames without retaining the logical payload in the fixture.
        private sealed class GeneratedFrames : Stream
        {
            private long _remaining;
            private int _frameLength;
            private int _framePosition;
            private int _sequence;
            private bool _done;
            private readonly bool _wrongSequence;
            private readonly bool _omitTerminator;
            private readonly int _readLimit;
            private readonly CancellationTokenSource? _cancelAfterSixBytes;
            internal GeneratedFrames(long length, bool wrongSequence = false, bool omitTerminator = false,
                int readLimit = int.MaxValue, CancellationTokenSource? cancelAfterSixBytes = null)
            { _remaining = length; _wrongSequence = wrongSequence; _omitTerminator = omitTerminator; _readLimit = readLimit; _cancelAfterSixBytes = cancelAfterSixBytes; NextFrame(); }
            internal long BytesRead { get; private set; }
            internal bool Closed { get; private set; }
            internal int SyncReads { get; private set; }
            public override bool CanRead => !Closed;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            private void NextFrame() { _frameLength = (int)Math.Min(_remaining, PacketChannel.MaxFramePayload); _framePosition = -4; }
            private int Fill(byte[] buffer, int offset, int count)
            {
                ObjectDisposedException.ThrowIf(Closed, this);
                if (_done) return 0;
                count = Math.Min(count, _readLimit);
                var read = 0;
                while (read < count)
                {
                    if (_framePosition < 0)
                    {
                        var index = _framePosition + 4;
                        buffer[offset + read++] = index == 3 ? (byte)(_wrongSequence && _sequence != 0 ? _sequence + 1 : _sequence) : (byte)(_frameLength >> (8 * index));
                        _framePosition++;
                    }
                    else
                    {
                        var size = Math.Min(count - read, _frameLength - _framePosition);
                        buffer.AsSpan(offset + read, size).Fill(0x42);
                        read += size;
                        _framePosition += size;
                        if (_framePosition == _frameLength)
                        {
                            _remaining -= _frameLength;
                            if (_frameLength < PacketChannel.MaxFramePayload || _omitTerminator && _remaining == 0) { _done = true; break; }
                            _sequence++;
                            NextFrame();
                        }
                    }
                }
                BytesRead += read;
                if (BytesRead >= 6) _cancelAfterSixBytes?.Cancel();
                return read;
            }
            public override int Read(byte[] buffer, int offset, int count) { SyncReads++; return Fill(buffer, offset, count); }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            { token.ThrowIfCancellationRequested(); return Task.FromResult(Fill(buffer, offset, count)); }
            protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); }
            public override void Flush() => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
