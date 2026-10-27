using System.IO;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;

namespace OceanBaseConnector.Internal.Session
{
    internal sealed partial class CommandExchange
    {
        private readonly DriverDiagnostics.SessionIo? _io;
        private int ReadSource(Stream source, byte[] buffer, int offset, int count, bool localInfile = false)
        {
            var started = _io?.StartSourceRead() ?? 0;
            try { return source.Read(buffer, offset, count); }
            finally { _io?.StopSourceRead(started, localInfile); }
        }
        private Task<int> ReadSourceAsync(Stream source, byte[] buffer, int offset, int count, bool localInfile = false) =>
            _io?.SourceReadsEnabled == true ? TimedReadSourceAsync(source, buffer, offset, count, localInfile)
                : source.ReadAsync(buffer, offset, count, _budget.Token);
        private async Task<int> TimedReadSourceAsync(Stream source, byte[] buffer, int offset, int count, bool localInfile)
        {
            var started = _io?.StartSourceRead() ?? 0;
            try { return await source.ReadAsync(buffer, offset, count, _budget.Token).ConfigureAwait(false); }
            finally { _io?.StopSourceRead(started, localInfile); }
        }
        private void WritePacket(byte[] bytes, int? count = null)
        {
            var started = _io?.Start() ?? 0;
            try { _channel.WritePacket(bytes, 0, count ?? bytes.Length); }
            finally { _io?.Stop(started, write: true); }
        }
        private Task WritePacketAsync(byte[] bytes, int? count = null) => _io?.ProtocolEnabled != true
            ? _channel.WritePacketAsync(bytes, 0, count ?? bytes.Length, _budget.Token) : TimedWritePacketAsync(bytes, count);
        private async Task TimedWritePacketAsync(byte[] bytes, int? count)
        {
            var started = _io?.Start() ?? 0;
            try { await _channel.WritePacketAsync(bytes, 0, count ?? bytes.Length, _budget.Token).ConfigureAwait(false); }
            finally { _io?.Stop(started, write: true); }
        }
        private byte[] ReadPacket()
        {
            var started = _io?.Start() ?? 0;
            try { return _channel.ReadPacket(); }
            finally { _io?.Stop(started, write: false); }
        }
        private Task<byte[]> ReadPacketAsync() => _io?.ProtocolEnabled != true ? _channel.ReadPacketAsync(_budget.Token) : TimedReadPacketAsync();
        private async Task<byte[]> TimedReadPacketAsync()
        {
            var started = _io?.Start() ?? 0;
            try { return await _channel.ReadPacketAsync(_budget.Token).ConfigureAwait(false); }
            finally { _io?.Stop(started, write: false); }
        }
        private PacketChannel.PacketPayload OpenPayload()
        {
            var started = _io?.Start() ?? 0;
            try { return _channel.OpenPayload(); }
            finally { _io?.Stop(started, write: false); }
        }
        private Task<PacketChannel.PacketPayload> OpenPayloadAsync() => _io?.ProtocolEnabled != true
            ? _channel.OpenPayloadAsync(_budget.Token) : TimedOpenPayloadAsync();
        private async Task<PacketChannel.PacketPayload> TimedOpenPayloadAsync()
        {
            var started = _io?.Start() ?? 0;
            try { return await _channel.OpenPayloadAsync(_budget.Token).ConfigureAwait(false); }
            finally { _io?.Stop(started, write: false); }
        }
    }
}
