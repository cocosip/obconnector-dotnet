using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;
using OceanBaseConnector.Internal.Sql;
using OceanBaseConnector.Internal.Values;

namespace OceanBaseConnector
{
    public sealed partial class OceanBaseConnection
    {
        // Built-in DBMS_XA layouts stay private; they are not entries in the public dictionary cache.
        private static readonly OceanBaseComplexType XaOracleXidType = new OceanBaseComplexType(null, "SYS", "DBMS_XA_XID", false,
            new[] {
                new OceanBaseComplexMember("FORMATID", null, "NUMBER", null, new ComplexType(0)),
                new OceanBaseComplexMember("GTRID", null, "RAW", null, new ComplexType(203)),
                new OceanBaseComplexMember("BQUAL", null, "RAW", null, new ComplexType(203)) });
        private static readonly OceanBaseComplexType XaOracleArrayType = new OceanBaseComplexType(null, "SYS", "DBMS_XA_XID_ARRAY", true,
            new[] { new OceanBaseComplexMember(null, "SYS", "DBMS_XA_XID", XaOracleXidType, XaOracleXidType.Layout) });
        private static readonly IReadOnlyDictionary<(string, string), OceanBaseComplexType> XaOracleTypes =
            new Dictionary<(string, string), OceanBaseComplexType>
            {
                [("SYS", "DBMS_XA_XID")] = XaOracleXidType,
                [("SYS", "DBMS_XA_XID_ARRAY")] = XaOracleArrayType
            };

        private async Task InitializeOracleXa(OceanBaseXaResource resource, CommandExchange exchange, bool asynchronous)
        {
            lock (_gate) { RequireCurrentXa(resource); if (resource.OracleInitialized) return; }
            if (asynchronous)
            {
                await exchange.EnsureOraclePrepareExecuteAsync(CancellationToken.None).ConfigureAwait(false);
            }
            else exchange.EnsureOraclePrepareExecute();
            var request = OceanBaseCommand.SqlPacket("set autocommit = 0", 3);
            byte[] response;
            if (asynchronous)
            {
                await exchange.SendAsync(request).ConfigureAwait(false);
                response = await exchange.ReceiveAsync(CancellationToken.None).ConfigureAwait(false);
            }
            else { exchange.Send(request); response = exchange.Receive(); }
            var ok = OkPacket.Parse(response, exchange.Capabilities);
            ValidateOracleXaOk(ok);
            if ((ok.StatusFlags & ServerStatusFlags.InTransaction) != 0)
                throw new ProtocolException("DBMS_XA initialization left an active transaction.");
            exchange.ObserveSessionState(ok); exchange.ResponseComplete();
            lock (_gate) { RequireCurrentXa(resource); resource.OracleInitialized = true; }
        }

        private async Task<OceanBaseXaVote> ExecuteOracleXa(OceanBaseXaResource resource, CommandExchange exchange, XaOperation operation, OceanBaseXid xid, int mode, bool asynchronous)
        {
            try
            {
                await InitializeOracleXa(resource, exchange, asynchronous).ConfigureAwait(false);
                var parameters = new OceanBaseParameterCollection();
                parameters.Add(new OceanBaseParameter
                {
                    ParameterName = "xid",
                    OceanBaseDbType = OceanBaseDbType.Struct,
                    ComplexType = XaOracleXidType,
                    Value = new OceanBaseComplexValue(XaOracleXidType,
                        new object?[] { xid.FormatId, xid.GetGlobalTransactionId(), xid.GetBranchQualifier() })
                });
                var argument = "";
                if (operation == XaOperation.Start || operation == XaOperation.End || operation == XaOperation.Commit)
                {
                    argument = ", :flag";
                    parameters.Add(operation == XaOperation.Commit
                        ? new OceanBaseParameter { ParameterName = "flag", DbType = DbType.Boolean, Value = mode == 1 }
                        : new OceanBaseParameter
                        {
                            ParameterName = "flag",
                            DbType = DbType.Int32,
                            Value = operation == XaOperation.Start ? mode == 1 ? 0x00200000 : mode == 2 ? 0x08000000 : 0
                                : mode == 2 ? 0x02000000 : 0x04000000
                        });
                }
                var plan = OracleCommandPlan.Create("select DBMS_XA.XA_" + operation.ToString().ToUpperInvariant() + "(:xid" + argument + ") from dual", CommandType.Text, parameters);
                var result = await RunOracleXa(resource, exchange, plan, asynchronous).ConfigureAwait(false);
                if (result.Columns.Length != 1 || !XaIntegerColumn(result.Columns[0]) &&
                    !((result.Columns[0].TypeCode == 0 || result.Columns[0].TypeCode == 246) && result.Columns[0].Scale == 0))
                    throw new ProtocolException("Invalid DBMS_XA return metadata.");
                if (!(asynchronous ? await result.ReadAsync(CancellationToken.None).ConfigureAwait(false) : result.Read()))
                    throw new ProtocolException("Missing DBMS_XA return value.");
                var code = OracleXaInteger(result.Columns[0].DecodeBinary(result.Row![0]));
                if (asynchronous ? await result.ReadAsync(CancellationToken.None).ConfigureAwait(false) : result.Read())
                    throw new ProtocolException("DBMS_XA returned more than one value.");
                ValidateOracleXaCompletion(result);
                if (code == 0) return OceanBaseXaVote.Prepared;
                if (operation == XaOperation.Prepare && code == 3) return OceanBaseXaVote.ReadOnly;
                var error = new OceanBaseException("DBMS_XA returned an unsuccessful XA code.", xaErrorCode: code);
                // A returned error can have ended or heuristically completed the branch; never keep a guessed phase.
                resource.Session.Break(); exchange.Complete(error); throw error;
            }
            catch (Exception error) { throw OracleXaFailure(resource, exchange, error); }
        }

        private async Task<OceanBaseXid[]> RecoverOracleXa(OceanBaseXaResource resource, CommandExchange exchange, bool asynchronous)
        {
            try
            {
                await InitializeOracleXa(resource, exchange, asynchronous).ConfigureAwait(false);
                var parameters = new OceanBaseParameterCollection();
                var output = new OceanBaseParameter
                {
                    ParameterName = "recovered",
                    Direction = ParameterDirection.Output,
                    OceanBaseDbType = OceanBaseDbType.Array,
                    ComplexType = XaOracleArrayType
                };
                parameters.Add(output);
                var plan = OracleCommandPlan.Create("declare x DBMS_XA_XID_ARRAY := DBMS_XA_XID_ARRAY(); BEGIN x := DBMS_XA.XA_RECOVER(); :recovered := x; END;", CommandType.Text, parameters);
                var result = await RunOracleXa(resource, exchange, plan, asynchronous).ConfigureAwait(false);
                if (result.Columns.Length != 0)
                    throw new ProtocolException("Unexpected DBMS_XA recovery result.");
                ValidateOracleXaCompletion(result);
                plan.PublishOutputs();
                if (!(output.Value is OceanBaseComplexValue array) || !ReferenceEquals(array.Type, XaOracleArrayType) || array.Count > 16383)
                    throw new ProtocolException("Invalid DBMS_XA recovery snapshot.");
                var items = array.GetValues(); var recovered = new OceanBaseXid[items.Length];
                for (var i = 0; i < recovered.Length; i++)
                {
                    if (!(items[i] is OceanBaseComplexValue item) || !ReferenceEquals(item.Type, XaOracleXidType))
                        throw new ProtocolException("Invalid DBMS_XA recovery branch.");
                    var values = item.GetValues(); var format = OracleXaInteger(values[0]);
                    var global = values[1] as byte[];
                    var branch = values[2] == null ? Array.Empty<byte>() : values[2] as byte[];
                    if (format == -1 || global == null || global.Length == 0 || global.Length > 64 || branch == null || branch.Length > 64)
                        throw new ProtocolException("DBMS_XA recovery identity is outside the supported domain.");
                    recovered[i] = new OceanBaseXid(format, global, branch);
                }
                return recovered;
            }
            catch (Exception error) { throw OracleXaFailure(resource, exchange, error); }
        }

        private static async Task<QueryResult> RunOracleXa(OceanBaseXaResource resource, CommandExchange exchange, OracleCommandPlan plan, bool asynchronous)
        {
            exchange.ComplexTypes = XaOracleTypes; plan.StatementCreated = resource.Session.QueueStatementClose;
            var parameters = plan.Bindings.Parameters;
            var request = OraclePrepareExecutePacket.Build(0, plan.Bindings.CommandText, parameters.Length,
                PreparedParameters.Encode(parameters, OceanBaseCompatibilityMode.Oracle));
            if (asynchronous) await exchange.SendAsync(request).ConfigureAwait(false); else exchange.Send(request);
            var result = new QueryResult(exchange, binary: true, completeExchange: false, oracleParameterCount: parameters.Length, oraclePlan: plan);
            if (asynchronous) await result.StartAsync(CancellationToken.None).ConfigureAwait(false); else result.Start();
            return result;
        }

        private static int OracleXaInteger(object? value)
        {
            var number = value is OceanBaseDecimal exact ? exact.ToDecimal()
                : value is decimal || value is sbyte || value is byte || value is short || value is ushort || value is int || value is uint || value is long || value is ulong
                    ? Convert.ToDecimal(value, CultureInfo.InvariantCulture) : throw new ProtocolException("DBMS_XA requires a non-NULL integer.");
            if (number < int.MinValue || number > int.MaxValue || decimal.Truncate(number) != number)
                throw new ProtocolException("DBMS_XA integer is outside the supported domain.");
            return (int)number;
        }

        private static void ValidateOracleXaCompletion(QueryResult result)
        {
            var ok = result.OracleCompletion;
            if (ok == null) throw new ProtocolException("Missing DBMS_XA completion.");
            ValidateOracleXaOk(ok);
        }

        private static void ValidateOracleXaOk(OkPacket ok)
        {
            if (ok.AffectedRows != 0 || ok.LastInsertId != 0 ||
                (ok.StatusFlags & (ServerStatusFlags.Autocommit | ServerStatusFlags.MoreResultsExists | ServerStatusFlags.PsOutParameters |
                    ServerStatusFlags.CursorExists | ServerStatusFlags.LastRowSent)) != 0)
                throw new ProtocolException("Invalid DBMS_XA completion.");
        }

        private static Exception OracleXaFailure(OceanBaseXaResource resource, CommandExchange exchange, Exception error)
        {
            var malformed = error is ProtocolException || error is FormatException || error is OverflowException;
            var mapped = exchange.Fail(malformed ? new OceanBaseException("Invalid DBMS_XA response.", innerException: error) : error);
            if (malformed) resource.Session.Break();
            return mapped;
        }
    }
}
