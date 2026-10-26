using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;

namespace OceanBaseConnector.Internal.Session
{
    internal sealed partial class CommandExchange
    {
        private readonly DriverDiagnostics.SessionIo? _io;
        private void WritePacket(byte[] bytes, int? count = null)
        {
            var started = _io?.Start() ?? 0;
            try { _channel.WritePacket(bytes, 0, count ?? bytes.Length); }
            finally { _io?.Stop(started, write: true); }
        }
        private Task WritePacketAsync(byte[] bytes, int? count = null) => _io == null
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
        private Task<byte[]> ReadPacketAsync() => _io == null ? _channel.ReadPacketAsync(_budget.Token) : TimedReadPacketAsync();
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
        private Task<PacketChannel.PacketPayload> OpenPayloadAsync() => _io == null
            ? _channel.OpenPayloadAsync(_budget.Token) : TimedOpenPayloadAsync();
        private async Task<PacketChannel.PacketPayload> TimedOpenPayloadAsync()
        {
            var started = _io?.Start() ?? 0;
            try { return await _channel.OpenPayloadAsync(_budget.Token).ConfigureAwait(false); }
            finally { _io?.Stop(started, write: false); }
        }
    }
}
