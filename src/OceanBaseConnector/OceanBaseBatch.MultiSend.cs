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
            try { return CanUseMultiSend(_connection.GetOpenSession()); }
            catch (InvalidOperationException)
            {
                return _ownsConnection && new OceanBaseConnectionStringBuilder(_connection.ConnectionString).UseBatchMultiSend;
            }
        }

        internal bool CanUseMultiSend(PhysicalSession session) =>
            session.UseBatchMultiSend && !(session.Info?.Mode == OceanBaseCompatibilityMode.Oracle && HasStreamParameter());

        private bool HasStreamParameter()
        {
            foreach (OceanBaseBatchCommand command in _commands)
                if (command.HasStreamParameter) return true;
            return false;
        }

        private int ExecuteMultiSendNonQuery()
        {
            if (_ownsConnection) _connection!.Open();
            try
            {
                if (_ownsConnection && !CanUseMultiSend(_connection!.GetOpenSession())) return ExecuteSequentialNonQuery();
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
                if (_ownsConnection && !CanUseMultiSend(_connection!.GetOpenSession())) return await ExecuteSequentialNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
                if (_ownsConnection && !CanUseMultiSend(_connection!.GetOpenSession())) return ExecuteSequentialScalar();
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
                if (_ownsConnection && !CanUseMultiSend(_connection!.GetOpenSession())) return await ExecuteSequentialScalarAsync(cancellationToken).ConfigureAwait(false);
                using var operation = Start(CommandBehavior.Default, cancellationToken);
                return (await ExecuteMultiSendAsync(operation, captureScalar: true).ConfigureAwait(false)).Scalar;
            }
            finally { if (_ownsConnection) await _connection!.CloseAsync().ConfigureAwait(false); }
        }

        private int ExecuteSequentialNonQuery()
        {
            using var reader = StartSequentialReader();
            do { while (reader.Read()) { } } while (reader.NextResult());
            return reader.RecordsAffected;
        }

        private async Task<int> ExecuteSequentialNonQueryAsync(CancellationToken token)
        {
            await using var reader = await StartSequentialReaderAsync(token).ConfigureAwait(false);
            do { while (await reader.ReadAsync(token).ConfigureAwait(false)) { } }
            while (await reader.NextResultAsync(token).ConfigureAwait(false));
            return reader.RecordsAffected;
        }

        private object? ExecuteSequentialScalar()
        {
            using var reader = StartSequentialReader();
            while (reader.FieldCount == 0 && reader.NextResult()) { }
            var value = reader.FieldCount != 0 && reader.Read() ? reader.GetValue(0) : null;
            do { while (reader.Read()) { } } while (reader.NextResult());
            return value;
        }

        private async Task<object?> ExecuteSequentialScalarAsync(CancellationToken token)
        {
            await using var reader = await StartSequentialReaderAsync(token).ConfigureAwait(false);
            while (reader.FieldCount == 0 && await reader.NextResultAsync(token).ConfigureAwait(false)) { }
            var value = reader.FieldCount != 0 && await reader.ReadAsync(token).ConfigureAwait(false) ? reader.GetValue(0) : null;
            do { while (await reader.ReadAsync(token).ConfigureAwait(false)) { } }
            while (await reader.NextResultAsync(token).ConfigureAwait(false));
            return value;
        }

        private OceanBaseBatchReader StartSequentialReader()
        {
            var operation = Start(_ownsConnection ? CommandBehavior.CloseConnection : CommandBehavior.Default, CancellationToken.None);
            var reader = new OceanBaseBatchReader(operation); operation.Reader = reader;
            try { reader.Start(); return reader; }
            catch { reader.Close(); throw; }
        }

        private async Task<OceanBaseBatchReader> StartSequentialReaderAsync(CancellationToken token)
        {
            var operation = Start(_ownsConnection ? CommandBehavior.CloseConnection : CommandBehavior.Default, token);
            var reader = new OceanBaseBatchReader(operation); operation.Reader = reader;
            try { await reader.StartAsync().ConfigureAwait(false); return reader; }
            catch { await reader.CloseAsync().ConfigureAwait(false); throw; }
        }

        private (int Affected, object? Scalar) ExecuteMultiSend(Operation operation, bool captureScalar = false)
        {
            operation.ThrowIfCanceled();
            var requests = new OceanBaseBatchMultiSendRequest[operation.Commands.Length];
            operation.MultiSendRequests = requests;
            for (var i = 0; i < requests.Length; i++)
            {
                operation.ThrowIfCanceled();
                requests[i] = operation.Commands[i].BuildBatchMultiSendRequest(operation.Token, CanRetainLobResults(operation), discardLobResults: !captureScalar);
            }
            if (requests.Length == 0) return (0, null);

            using var exchange = StartMultiSendExchange(operation, discardLobResults: !captureScalar);
            var total = 0;
            var anyAffected = false;
            using var scalar = new ScalarState();
            foreach (var group in PlanMultiSendGroups(requests, operation.Session!.BatchMultiSendNumber))
            {
                for (var i = group.Start; i < group.End; i++)
                {
                    exchange.BatchCommand = operation.Items[i];
                    SendMultiSendRequest(exchange, requests[i]);
                }
                for (var i = group.Start; i < group.End; i++)
                {
                    exchange.BatchCommand = operation.Items[i];
                    exchange.BeginBatchResponse(requests[i].SourceComplexTypes, requests[i].SessionTimeZone, requests[i].NationalCharacterSet);
                    var count = ConsumeBatchResponse(exchange, operation, captureScalar && !scalar.Set, scalar, requests[i]);
                    operation.Items[i].Affected = operation.Counts[i] = count;
                    if (count >= 0) { anyAffected = true; total = checked(total + count); }
                }
            }
            exchange.Complete();
            scalar.Delivered = true;
            return (anyAffected ? total : -1, scalar.Value);
        }

        private async Task<(int Affected, object? Scalar)> ExecuteMultiSendAsync(Operation operation, bool captureScalar = false)
        {
            operation.ThrowIfCanceled();
            var requests = new OceanBaseBatchMultiSendRequest[operation.Commands.Length];
            operation.MultiSendRequests = requests;
            for (var i = 0; i < requests.Length; i++)
            {
                operation.ThrowIfCanceled();
                requests[i] = await operation.Commands[i].BuildBatchMultiSendRequestAsync(operation.Token, CanRetainLobResults(operation), discardLobResults: !captureScalar).ConfigureAwait(false);
            }
            if (requests.Length == 0) return (0, null);

            using var exchange = StartMultiSendExchange(operation, discardLobResults: !captureScalar);
            var total = 0;
            var anyAffected = false;
            using var scalar = new ScalarState();
            foreach (var group in PlanMultiSendGroups(requests, operation.Session!.BatchMultiSendNumber))
            {
                for (var i = group.Start; i < group.End; i++)
                {
                    exchange.BatchCommand = operation.Items[i];
                    await SendMultiSendRequestAsync(exchange, requests[i], operation.Token).ConfigureAwait(false);
                }
                for (var i = group.Start; i < group.End; i++)
                {
                    exchange.BatchCommand = operation.Items[i];
                    exchange.BeginBatchResponse(requests[i].SourceComplexTypes, requests[i].SessionTimeZone, requests[i].NationalCharacterSet);
                    var count = await ConsumeBatchResponseAsync(exchange, operation, captureScalar && !scalar.Set, scalar, requests[i]).ConfigureAwait(false);
                    operation.Items[i].Affected = operation.Counts[i] = count;
                    if (count >= 0) { anyAffected = true; total = checked(total + count); }
                }
            }
            exchange.Complete();
            scalar.Delivered = true;
            return (anyAffected ? total : -1, scalar.Value);
        }

        private sealed class ScalarState : IDisposable
        {
            internal object? Value;
            internal bool Set;
            internal bool Delivered;
            public void Dispose() { if (!Delivered) Internal.Values.ComplexValueCodec.DisposeLobValues(Value); }
        }

        private static int ConsumeBatchResponse(CommandExchange exchange, Operation operation, bool captureScalar, ScalarState scalar, OceanBaseBatchMultiSendRequest request)
        {
            var result = request.CreateResult(exchange);
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

        private static async Task<int> ConsumeBatchResponseAsync(CommandExchange exchange, Operation operation, bool captureScalar, ScalarState scalar, OceanBaseBatchMultiSendRequest request)
        {
            var result = request.CreateResult(exchange);
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

        internal CommandExchange StartMultiSendExchange(Operation operation, bool discardLobResults = false)
        {
            var exchange = operation.Connection.StartCommand(operation.Session!, operation.Transaction, _timeout, operation.Token,
                release: static () => { }, owner: operation, cancellationOrigin: operation.CancellationOrigin);
            exchange.StartDiagnostics("batch.multi-send");
            exchange.AllowBatchLobResults = CanRetainLobResults(operation) || discardLobResults;
            return exchange;
        }

        internal bool CanRetainLobResults(Operation operation) =>
            operation.Reader != null || !_ownsConnection && (operation.Behavior & CommandBehavior.CloseConnection) == 0;

        internal static bool CanUseMultiSendReader(CommandBehavior behavior) =>
            (behavior & ~CommandBehavior.CloseConnection) == CommandBehavior.Default;

        internal void SendMultiSendGroup(Operation operation, CommandExchange exchange, OceanBaseBatchMultiSendRequest[] requests, ref int next, out int end)
        {
            end = next;
            while (end < requests.Length && end - next < operation.Session!.BatchMultiSendNumber) end++;
            for (var index = next; index < end; index++)
            {
                exchange.BatchCommand = operation.Items[index];
                SendMultiSendRequest(exchange, requests[index]);
            }
            next = end;
        }

        internal async Task<int> SendMultiSendGroupAsync(Operation operation, CommandExchange exchange, OceanBaseBatchMultiSendRequest[] requests, int next, CancellationToken token)
        {
            var end = next;
            while (end < requests.Length && end - next < operation.Session!.BatchMultiSendNumber) end++;
            for (var index = next; index < end; index++)
            {
                exchange.BatchCommand = operation.Items[index];
                await SendMultiSendRequestAsync(exchange, requests[index], token).ConfigureAwait(false);
            }
            return end;
        }

        private static void SendMultiSendRequest(CommandExchange exchange, OceanBaseBatchMultiSendRequest request)
        {
            if (request.LongDataParameters != null)
                exchange.SendBatchLongData(request.StatementId, request.LongDataParameters, request.Packet.Length);
            exchange.SendBatchRequest(request.Packet);
        }

        private static async Task SendMultiSendRequestAsync(CommandExchange exchange, OceanBaseBatchMultiSendRequest request, CancellationToken token)
        {
            if (request.LongDataParameters != null)
                await exchange.SendBatchLongDataAsync(request.StatementId, request.LongDataParameters, request.Packet.Length, token).ConfigureAwait(false);
            await exchange.SendBatchRequestAsync(request.Packet, token).ConfigureAwait(false);
        }

        internal static IEnumerable<(int Start, int End)> PlanMultiSendGroups(OceanBaseBatchMultiSendRequest[] requests, int maxCount)
        {
            var start = 0;
            while (start < requests.Length)
            {
                var end = start;
                long bytes = 0;
                while (end < requests.Length && end - start < maxCount)
                {
                    var next = checked(bytes + requests[end].Packet.Length);
                    if (end > start && next > PacketChannel.DefaultMaxPacketSize) break;
                    bytes = next;
                    end++;
                }
                yield return (start, end);
                start = end;
            }
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
