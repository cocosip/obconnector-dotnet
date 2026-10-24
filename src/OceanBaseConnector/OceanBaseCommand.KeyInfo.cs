using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;

namespace OceanBaseConnector
{
    public sealed partial class OceanBaseCommand
    {
        private async Task<DbDataReader> ExecuteKeyInfoAsync(CommandBehavior behavior, bool asynchronous, CancellationToken token, int arrayCount)
        {
            var operation = Start(behavior, token, false, arrayCount);
            var execution = operation.Exchange;
            execution.StartDiagnostics("execute");
            try
            {
                var original = (asynchronous ? await EnsurePreparedAsync(operation, token).ConfigureAwait(false) : EnsurePrepared(operation))!;
                var resolved = await execution.ResolveKeyInfoAsync(original, asynchronous, token).ConfigureAwait(false);
                var statement = resolved.Statement;
                if (asynchronous) await execution.ResolveComplexColumnsAsync(statement, token).ConfigureAwait(false);
                else execution.ResolveComplexColumns(statement);
                if ((behavior & CommandBehavior.SchemaOnly) != 0)
                    return new OceanBaseDataReader(execution, QueryResult.FromPrepared(execution, statement), operation.Connection, operation.Session, behavior, resolved.Metadata);
                if (operation.CursorOffset)
                {
                    if (asynchronous) await execution.EnsureCursorOffsetAsync(token).ConfigureAwait(false); else execution.EnsureCursorOffset();
                }
                var zone = asynchronous ? await execution.EnsureSessionTimeZoneAsync(token).ConfigureAwait(false) : execution.EnsureSessionTimeZone();
                PreparedParameters.ValidateSessionTimeZone(operation.Bindings.Parameters, zone);
                var national = asynchronous ? await execution.EnsureNationalCharacterSetAsync(token).ConfigureAwait(false) : execution.EnsureNationalCharacterSet();
                var parameters = HasNationalParameters(operation.Bindings.Parameters)
                    ? PreparedParameters.Encode(operation.Bindings.Parameters, execution.Mode, national) : operation.Parameters;
                ValidateCursorStatement(operation.FetchSize, statement);
                var request = PreparedParameters.Execute(statement.Id, parameters, operation.FetchSize > 0);
                if (asynchronous)
                {
                    await execution.SendLongDataAsync(statement.Id, operation.Bindings.Parameters, request.Length).ConfigureAwait(false);
                    await execution.SendAsync(request, executesSql: !IsLobRead).ConfigureAwait(false);
                }
                else
                {
                    execution.SendLongData(statement.Id, operation.Bindings.Parameters, request.Length);
                    execution.Send(request, executesSql: !IsLobRead);
                }
                var result = new QueryResult(execution, binary: true, sequential: (behavior & CommandBehavior.SequentialAccess) != 0,
                    cursorId: operation.FetchSize > 0 ? statement.Id : (uint?)null, fetchSize: operation.FetchSize, cursorOffset: operation.CursorOffset);
                if (asynchronous) await result.StartAsync(token).ConfigureAwait(false); else result.Start();
                var metadata = resolved.Metadata.ForResult(result.Columns);
                return new OceanBaseDataReader(execution, result, operation.Connection, operation.Session, behavior, metadata);
            }
            catch (System.Exception error) { throw execution.Fail(error); }
        }
    }
}
