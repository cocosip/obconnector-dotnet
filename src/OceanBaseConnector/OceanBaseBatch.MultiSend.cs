#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;

namespace OceanBaseConnector
{
    public sealed partial class OceanBaseBatch
    {
        private bool IsBatchMultiSendRequested()
        {
            if (_connection == null) return false;
            if (_ownsConnection)
                return new OceanBaseConnectionStringBuilder(_connection.ConnectionString).UseBatchMultiSend;
            try { return _connection.GetOpenSession().UseBatchMultiSend; }
            catch (InvalidOperationException) { return false; }
        }

        private int ExecuteMultiSendNonQuery()
        {
            if (_ownsConnection) _connection!.Open();
            try
            {
                using var operation = Start(CommandBehavior.Default, CancellationToken.None);
                return ExecuteMultiSend(operation);
            }
            finally { if (_ownsConnection) _connection!.Close(); }
        }

        private async Task<int> ExecuteMultiSendNonQueryAsync(CancellationToken cancellationToken)
        {
            if (_ownsConnection) await _connection!.OpenAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var operation = Start(CommandBehavior.Default, cancellationToken);
                return await ExecuteMultiSendAsync(operation).ConfigureAwait(false);
            }
            finally { if (_ownsConnection) await _connection!.CloseAsync().ConfigureAwait(false); }
        }

        private int ExecuteMultiSend(Operation operation)
        {
            operation.ThrowIfCanceled();
            var requests = new byte[operation.Commands.Length][];
            for (var i = 0; i < requests.Length; i++)
            {
                operation.ThrowIfCanceled();
                requests[i] = operation.Commands[i].BuildBatchMultiSendRequest(operation.Token);
            }
            if (requests.Length == 0) return 0;

            using var exchange = StartMultiSendExchange(operation);
            var total = 0;
            foreach (var group in Groups(requests, operation.Session!.BatchMultiSendNumber))
            {
                for (var i = group.Start; i < group.End; i++)
                {
                    exchange.BatchCommand = operation.Items[i];
                    exchange.SendBatchRequest(requests[i]);
                }
                for (var i = group.Start; i < group.End; i++)
                {
                    exchange.BatchCommand = operation.Items[i];
                    var packet = exchange.Receive();
                    if (!OkPacket.IsOk(packet)) throw new ProtocolException("Batch multi-send requires affected-count responses.");
                    var ok = OkPacket.Parse(packet, exchange.Capabilities);
                    exchange.ObserveSessionState(ok);
                    exchange.CompleteBatchResponse();
                    var count = checked((int)ok.AffectedRows);
                    operation.Items[i].Affected = operation.Counts[i] = count;
                    total = checked(total + count);
                }
            }
            exchange.Complete();
            return total;
        }

        private async Task<int> ExecuteMultiSendAsync(Operation operation)
        {
            operation.ThrowIfCanceled();
            var requests = new byte[operation.Commands.Length][];
            for (var i = 0; i < requests.Length; i++)
            {
                operation.ThrowIfCanceled();
                requests[i] = await operation.Commands[i].BuildBatchMultiSendRequestAsync(operation.Token).ConfigureAwait(false);
            }
            if (requests.Length == 0) return 0;

            using var exchange = StartMultiSendExchange(operation);
            var total = 0;
            foreach (var group in Groups(requests, operation.Session!.BatchMultiSendNumber))
            {
                for (var i = group.Start; i < group.End; i++)
                {
                    exchange.BatchCommand = operation.Items[i];
                    await exchange.SendBatchRequestAsync(requests[i], operation.Token).ConfigureAwait(false);
                }
                for (var i = group.Start; i < group.End; i++)
                {
                    exchange.BatchCommand = operation.Items[i];
                    var packet = await exchange.ReceiveAsync(operation.Token).ConfigureAwait(false);
                    if (!OkPacket.IsOk(packet)) throw new ProtocolException("Batch multi-send requires affected-count responses.");
                    var ok = OkPacket.Parse(packet, exchange.Capabilities);
                    exchange.ObserveSessionState(ok);
                    exchange.CompleteBatchResponse();
                    var count = checked((int)ok.AffectedRows);
                    operation.Items[i].Affected = operation.Counts[i] = count;
                    total = checked(total + count);
                }
            }
            exchange.Complete();
            return total;
        }

        private CommandExchange StartMultiSendExchange(Operation operation)
        {
            var exchange = operation.Connection.StartCommand(operation.Session!, operation.Transaction, _timeout, operation.Token,
                release: static () => { }, owner: operation, cancellationOrigin: operation.CancellationOrigin);
            exchange.StartDiagnostics("batch.multi-send");
            return exchange;
        }

        private static IEnumerable<(int Start, int End)> Groups(byte[][] requests, int maxCount)
        {
            var start = 0;
            while (start < requests.Length)
            {
                var end = start;
                long bytes = 0;
                while (end < requests.Length && end - start < maxCount)
                {
                    var next = checked(bytes + requests[end].Length);
                    if (end > start && next > PacketChannel.DefaultMaxPacketSize) break;
                    bytes = next;
                    end++;
                }
                yield return (start, end);
                start = end;
            }
        }
    }
}
#endif
