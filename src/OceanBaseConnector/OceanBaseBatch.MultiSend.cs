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
        internal bool IsBatchMultiSendRequested()
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
                return ExecuteMultiSend(operation).Affected;
            }
            finally { if (_ownsConnection) _connection!.Close(); }
        }

        private async Task<int> ExecuteMultiSendNonQueryAsync(CancellationToken cancellationToken)
        {
            if (_ownsConnection) await _connection!.OpenAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var operation = Start(CommandBehavior.Default, cancellationToken);
                return (await ExecuteMultiSendAsync(operation).ConfigureAwait(false)).Affected;
            }
            finally { if (_ownsConnection) await _connection!.CloseAsync().ConfigureAwait(false); }
        }

        private object? ExecuteMultiSendScalar()
        {
            if (_ownsConnection) _connection!.Open();
            try
            {
                using var operation = Start(CommandBehavior.Default, CancellationToken.None);
                return ExecuteMultiSend(operation, captureScalar: true).Scalar;
            }
            finally { if (_ownsConnection) _connection!.Close(); }
        }

        private async Task<object?> ExecuteMultiSendScalarAsync(CancellationToken cancellationToken)
        {
            if (_ownsConnection) await _connection!.OpenAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var operation = Start(CommandBehavior.Default, cancellationToken);
                return (await ExecuteMultiSendAsync(operation, captureScalar: true).ConfigureAwait(false)).Scalar;
            }
            finally { if (_ownsConnection) await _connection!.CloseAsync().ConfigureAwait(false); }
        }

        private (int Affected, object? Scalar) ExecuteMultiSend(Operation operation, bool captureScalar = false)
        {
            operation.ThrowIfCanceled();
            var requests = new byte[operation.Commands.Length][];
            for (var i = 0; i < requests.Length; i++)
            {
                operation.ThrowIfCanceled();
                requests[i] = operation.Commands[i].BuildBatchMultiSendRequest(operation.Token);
            }
            if (requests.Length == 0) return (0, null);

            using var exchange = StartMultiSendExchange(operation);
            var total = 0;
            var anyAffected = false;
            var scalar = new ScalarState();
            foreach (var group in PlanMultiSendGroups(requests, operation.Session!.BatchMultiSendNumber))
            {
                for (var i = group.Start; i < group.End; i++)
                {
                    exchange.BatchCommand = operation.Items[i];
                    exchange.SendBatchRequest(requests[i]);
                }
                for (var i = group.Start; i < group.End; i++)
                {
                    exchange.BatchCommand = operation.Items[i];
                    exchange.BeginBatchResponse();
                    var count = ConsumeBatchResponse(exchange, operation, captureScalar && !scalar.Set, scalar);
                    operation.Items[i].Affected = operation.Counts[i] = count;
                    if (count >= 0) { anyAffected = true; total = checked(total + count); }
                }
            }
            exchange.Complete();
            return (anyAffected ? total : -1, scalar.Value);
        }

        private async Task<(int Affected, object? Scalar)> ExecuteMultiSendAsync(Operation operation, bool captureScalar = false)
        {
            operation.ThrowIfCanceled();
            var requests = new byte[operation.Commands.Length][];
            for (var i = 0; i < requests.Length; i++)
            {
                operation.ThrowIfCanceled();
                requests[i] = await operation.Commands[i].BuildBatchMultiSendRequestAsync(operation.Token).ConfigureAwait(false);
            }
            if (requests.Length == 0) return (0, null);

            using var exchange = StartMultiSendExchange(operation);
            var total = 0;
            var anyAffected = false;
            var scalar = new ScalarState();
            foreach (var group in PlanMultiSendGroups(requests, operation.Session!.BatchMultiSendNumber))
            {
                for (var i = group.Start; i < group.End; i++)
                {
                    exchange.BatchCommand = operation.Items[i];
                    await exchange.SendBatchRequestAsync(requests[i], operation.Token).ConfigureAwait(false);
                }
                for (var i = group.Start; i < group.End; i++)
                {
                    exchange.BatchCommand = operation.Items[i];
                    exchange.BeginBatchResponse();
                    var count = await ConsumeBatchResponseAsync(exchange, operation, captureScalar && !scalar.Set, scalar).ConfigureAwait(false);
                    operation.Items[i].Affected = operation.Counts[i] = count;
                    if (count >= 0) { anyAffected = true; total = checked(total + count); }
                }
            }
            exchange.Complete();
            return (anyAffected ? total : -1, scalar.Value);
        }

        private sealed class ScalarState
        {
            internal object? Value;
            internal bool Set;
        }

        private static int ConsumeBatchResponse(CommandExchange exchange, Operation operation, bool captureScalar, ScalarState scalar)
        {
            var result = new QueryResult(exchange, binary: true, completeExchange: false);
            result.Start();
            using var reader = new OceanBaseDataReader(exchange, result, operation.Connection, operation.Session!, CommandBehavior.Default, leaveExchangeOpen: true);
            if (captureScalar)
            {
                while (reader.FieldCount == 0 && reader.NextResult()) { }
                if (reader.FieldCount != 0 && reader.Read()) { scalar.Value = reader.GetValue(0); scalar.Set = true; }
            }
            while (reader.Read()) { }
            while (reader.NextResult()) while (reader.Read()) { }
            return reader.RecordsAffected;
        }

        private static async Task<int> ConsumeBatchResponseAsync(CommandExchange exchange, Operation operation, bool captureScalar, ScalarState scalar)
        {
            var result = new QueryResult(exchange, binary: true, completeExchange: false);
            await result.StartAsync(operation.Token).ConfigureAwait(false);
            await using var reader = new OceanBaseDataReader(exchange, result, operation.Connection, operation.Session!, CommandBehavior.Default, leaveExchangeOpen: true);
            if (captureScalar)
            {
                while (reader.FieldCount == 0 && await reader.NextResultAsync(operation.Token).ConfigureAwait(false)) { }
                if (reader.FieldCount != 0 && await reader.ReadAsync(operation.Token).ConfigureAwait(false)) { scalar.Value = reader.GetValue(0); scalar.Set = true; }
            }
            while (await reader.ReadAsync(operation.Token).ConfigureAwait(false)) { }
            while (await reader.NextResultAsync(operation.Token).ConfigureAwait(false))
                while (await reader.ReadAsync(operation.Token).ConfigureAwait(false)) { }
            return reader.RecordsAffected;
        }

        internal CommandExchange StartMultiSendExchange(Operation operation)
        {
            var exchange = operation.Connection.StartCommand(operation.Session!, operation.Transaction, _timeout, operation.Token,
                release: static () => { }, owner: operation, cancellationOrigin: operation.CancellationOrigin);
            exchange.StartDiagnostics("batch.multi-send");
            return exchange;
        }

        internal static bool CanUseMultiSendReader(CommandBehavior behavior) =>
            (behavior & ~CommandBehavior.CloseConnection) == CommandBehavior.Default;

        internal void SendMultiSendGroup(Operation operation, CommandExchange exchange, byte[][] requests, ref int next, out int end)
        {
            end = next;
            while (end < requests.Length && end - next < operation.Session!.BatchMultiSendNumber) end++;
            for (var index = next; index < end; index++)
            {
                exchange.BatchCommand = operation.Items[index];
                exchange.SendBatchRequest(requests[index]);
            }
            next = end;
        }

        internal async Task<int> SendMultiSendGroupAsync(Operation operation, CommandExchange exchange, byte[][] requests, int next, CancellationToken token)
        {
            var end = next;
            while (end < requests.Length && end - next < operation.Session!.BatchMultiSendNumber) end++;
            for (var index = next; index < end; index++)
            {
                exchange.BatchCommand = operation.Items[index];
                await exchange.SendBatchRequestAsync(requests[index], token).ConfigureAwait(false);
            }
            return end;
        }

        internal static IEnumerable<(int Start, int End)> PlanMultiSendGroups(byte[][] requests, int maxCount)
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
