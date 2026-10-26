#if NET8_0_OR_GREATER
using System;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Sql;

namespace OceanBaseConnector
{
    public sealed partial class OceanBaseBatch
    {
        private async Task<int> ExecuteRewrittenNonQuery(bool asynchronous, CancellationToken token)
        {
            Operation? operation = null;
            try
            {
                if (_ownsConnection)
                {
                    if (asynchronous) await _connection!.OpenAsync(token).ConfigureAwait(false); else _connection!.Open();
                }
                operation = Start(CommandBehavior.Default, token);
                var total = 0; var any = operation.Commands.Length == 0;
                for (var index = 0; index < operation.Commands.Length;)
                {
                    operation.ThrowIfCanceled();
                    var command = RewriteCommand(operation, index, out var rows);
                    var owned = rows > 1;
                    try
                    {
                        operation.Activate(command, owned);
                        var reader = asynchronous ? await command.ExecuteReaderAsync(CommandBehavior.Default, operation.Token).ConfigureAwait(false)
                            : command.ExecuteReaderCore(CommandBehavior.Default, operation.Token);
                        try
                        {
                            void UpdateSingle() { if (!owned) operation.Items[index].Affected = operation.Counts[index] = reader.RecordsAffected; }
                            UpdateSingle();
                            if (asynchronous)
                            {
                                do { while (await reader.ReadAsync(operation.Token).ConfigureAwait(false)) UpdateSingle(); UpdateSingle(); }
                                while (await reader.NextResultAsync(operation.Token).ConfigureAwait(false));
                            }
                            else
                            {
                                do { while (reader.Read()) UpdateSingle(); UpdateSingle(); } while (reader.NextResult());
                            }
                            UpdateSingle();
                            var affected = reader.RecordsAffected;
                            if (owned && affected == rows)
                                for (var row = 0; row < rows; row++) operation.Items[index + row].Affected = operation.Counts[index + row] = 1;
                            if (affected >= 0) { total = checked(total + affected); any = true; }
                        }
                        finally
                        {
                            if (asynchronous) await reader.DisposeAsync().ConfigureAwait(false); else reader.Dispose();
                        }
                    }
                    catch (OceanBaseException error)
                    {
                        if (owned)
                        {
                            error.FailedBatchCommand = null;
                            error.BatchCommandGroup = Array.AsReadOnly(operation.Items.Skip(index).Take(rows).Cast<DbBatchCommand>().ToArray());
                        }
                        throw;
                    }
                    finally
                    {
                        try { if (owned) { if (asynchronous) await command.DisposeAsync().ConfigureAwait(false); else command.Dispose(); } }
                        finally { operation.Deactivate(command); }
                    }
                    index += rows;
                }
                return any ? total : -1;
            }
            catch (OperationCanceledException)
            {
                operation?.ThrowIfCanceled();
                throw;
            }
            finally
            {
                try { operation?.Dispose(); }
                finally
                {
                    if (_ownsConnection)
                    {
                        if (operation != null)
                        {
                            if (asynchronous) await operation.Connection.CloseSessionAsync(operation.Session!, operation.OpenGeneration).ConfigureAwait(false);
                            else operation.Connection.CloseSession(operation.Session!, operation.OpenGeneration);
                        }
                        else { if (asynchronous) await _connection!.CloseAsync().ConfigureAwait(false); else _connection!.Close(); }
                    }
                }
            }
        }

        private static OceanBaseCommand RewriteCommand(Operation operation, int index, out int rows)
        {
            rows = 1; var first = operation.Commands[index];
            if (operation.Session!.Info!.Mode != OceanBaseCompatibilityMode.MySql || first.CommandType != CommandType.Text || string.IsNullOrWhiteSpace(first.CommandText) ||
                index + 1 == operation.Commands.Length || !EligibleParameters(first)) return first;
            var shape = MySqlInsertRewrite.TryParse(first.CommandText, operation.Token);
            if (shape == null || shape.ParameterCount != first.Parameters.Count) return first;
            var sqlBytes = (long)shape.BaseBytes; var tupleBytes = shape.TupleBytes;
            long valuesBytes = 0; var parameters = 0;
            for (var i = index; i < operation.Commands.Length && rows <= ushort.MaxValue; i++)
            {
                operation.ThrowIfCanceled(); var item = operation.Commands[i];
                if (item.CommandType != CommandType.Text || !string.Equals(item.CommandText, first.CommandText, StringComparison.Ordinal) ||
                    item.Parameters.Count != shape.ParameterCount || !EligibleParameters(item)) break;
                var count = item.Parameters.Count;
                if (parameters + count > ushort.MaxValue) break;
                long encoded;
                try { encoded = PreparedParameters.Encode(item.Parameters.Cast<OceanBaseParameter>().ToArray()).LongLength; }
                // A later invalid row must fail in its original sequential position, after confirmed groups.
                catch (ArgumentException) { break; }
                catch (NotSupportedException) { break; }
                var nextValues = valuesBytes + (count == 0 ? 0 : encoded - (count + 7) / 8 - 1 - 2L * count);
                var nextCount = parameters + count;
                var executionBytes = 10L + (nextCount == 0 ? 0 : (nextCount + 7) / 8 + 1 + 2L * nextCount + nextValues);
                var nextSql = sqlBytes + (i == index ? 0 : 1L + tupleBytes);
                if (executionBytes > PacketChannel.DefaultMaxPacketSize || nextSql > PacketChannel.DefaultMaxPacketSize) break;
                valuesBytes = nextValues; parameters = nextCount; sqlBytes = nextSql; rows = i - index + 1;
                if (rows == ushort.MaxValue) break;
            }
            if (rows == 1) return first;
            operation.ThrowIfCanceled();
            var combined = new OceanBaseCommand
            {
                Connection = operation.Connection, Transaction = first.Transaction, CommandTimeout = first.CommandTimeout,
                CommandText = shape.Build(rows), SessionOwner = operation, RequiredSession = operation.Session,
                CancellationOrigin = operation.CancellationOrigin,
            };
            for (var i = index; i < index + rows; i++)
            {
                operation.ThrowIfCanceled();
                foreach (OceanBaseParameter parameter in operation.Commands[i].Parameters) combined.Parameters.Add(parameter.Snapshot());
            }
            operation.ThrowIfCanceled();
            return combined;
        }
        private static bool EligibleParameters(OceanBaseCommand command)
        {
            foreach (OceanBaseParameter parameter in command.Parameters)
                if (parameter.Direction != ParameterDirection.Input || parameter.Value is Stream) return false;
            return true;
        }
    }
}
#endif
