using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;
using OceanBaseConnector.Internal.Sql;

namespace OceanBaseConnector
{
    public sealed partial class OceanBaseConnection
    {
        internal PhysicalSession BeginParameterDerivation(OceanBaseCommand metadata, PhysicalSession? requiredSession)
        {
            EnsureAmbientTransaction();
            lock (_gate)
            {
                var transaction = ResolveCommandTransaction((OceanBaseTransaction?)metadata.Transaction);
                var session = GetCommandSession(transaction);
                if (requiredSession != null && !ReferenceEquals(session, requiredSession)) throw new InvalidOperationException("The command's physical session changed.");
                metadata.Transaction = transaction; metadata.RequiredSession = session; metadata.RequiredTransactionEpoch = _transactionEpoch;
                metadata.SessionOwner = metadata; session.Reserve(metadata);
                return session;
            }
        }

        internal void PublishDerivedParameters(OceanBaseCommand metadata, PhysicalSession session, OceanBaseParameterCollection collection, OceanBaseParameter[] parameters, int version)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(GetCommandSession((OceanBaseTransaction?)metadata.Transaction, metadata), session) || _transactionEpoch != metadata.RequiredTransactionEpoch)
                    throw new InvalidOperationException("The derivation session or transaction changed.");
                collection.ReplaceDerived(parameters, version);
            }
        }

        internal async Task<OceanBaseParameter[]> ReadDerivedParameters(OceanBaseCommand command, string text, string? overload, bool asynchronous, CancellationToken token)
        {
            var session = command.RequiredSession!; var mysql = session.Info!.Mode == OceanBaseCompatibilityMode.MySql;
            var parts = RoutineParameterDerivation.ParseName(text, mysql);
            if (mysql && overload != null || overload != null && (overload.Length == 0 || overload.Length > 40 || overload.IndexOf('\0') >= 0))
                throw new ArgumentException("Invalid routine overload selector.", nameof(overload));
            var filters = new string?[5]; string predicate;
            if (mysql)
            {
                filters[0] = parts.Length == 2 ? parts[0] : null; filters[2] = parts[parts.Length - 1];
                predicate = parts.Length == 1 ? "ROUTINE_SCHEMA = DATABASE()" : "1 = 1";
            }
            else
            {
                const string package = "CASE WHEN OBJECT_TYPE = 'PACKAGE' THEN OBJECT_NAME ELSE NULL END";
                filters[2] = parts[parts.Length - 1];
                if (parts.Length == 1) predicate = "OWNER = SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA') AND " + package + " IS NULL";
                else if (parts.Length == 3) { filters[1] = parts[0]; filters[4] = parts[1]; predicate = "1 = 1"; }
                else
                {
                    predicate = "(OWNER = :prefix AND " + package + " IS NULL) OR (OWNER = SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA') AND " + package + " = :prefix)";
                    command.Parameters.Add(new OceanBaseParameter { ParameterName = "prefix", DbType = DbType.String, Value = parts[0] });
                }
            }
            using var routines = CreateRoutineDirectoryTable(false);
            command.CommandText = RoutineDirectorySql(false, mysql, filters, command, predicate: predicate);
            await ReadDerivationTable(command, routines, asynchronous, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var routine = RoutineParameterDerivation.SelectRoutine(routines, overload, mysql);
            command.Parameters.Clear();
            filters = new[] { RoutineParameterDerivation.Text(routine, "ROUTINE_CATALOG"), RoutineParameterDerivation.Text(routine, "ROUTINE_SCHEMA"),
                RoutineParameterDerivation.Text(routine, "ROUTINE_NAME"), null, RoutineParameterDerivation.Text(routine, "PACKAGE_NAME") };
            if (mysql)
            {
                predicate = "ROUTINE_TYPE = ?";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "kind", DbType = DbType.String, Value = routine["ROUTINE_TYPE"] });
            }
            else
            {
                predicate = "SUBPROGRAM_ID = :subprogram";
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "subprogram", DbType = DbType.Int32, Value = routine["SUBPROGRAM_ID"] });
                if (routine.IsNull("PACKAGE_NAME")) predicate += " AND PACKAGE_NAME IS NULL";
                if (routine.IsNull("OVERLOAD")) predicate += " AND OVERLOAD IS NULL";
                else
                {
                    predicate += " AND CAST(OVERLOAD AS VARCHAR2(40)) = :overload";
                    command.Parameters.Add(new OceanBaseParameter { ParameterName = "overload", DbType = DbType.String, Value = routine["OVERLOAD"] });
                }
            }
            using var arguments = CreateRoutineDirectoryTable(true, true);
            command.CommandText = RoutineDirectorySql(true, mysql, filters, command, derivation: true, predicate: predicate);
            await ReadDerivationTable(command, arguments, asynchronous, token).ConfigureAwait(false);
            var rows = RoutineParameterDerivation.OrderParameters(arguments, routine, mysql);
            var parameters = new OceanBaseParameter[rows.Length];
            for (var i = 0; i < rows.Length; i++)
            {
                token.ThrowIfCancellationRequested(); var row = rows[i]; OceanBaseComplexType? complex = null;
                if (!mysql && !row.IsNull("TYPE_OWNER") && !row.IsNull("TYPE_NAME"))
                {
                    if (!row.IsNull("TYPE_SUBNAME")) throw new NotSupportedException("Package-local PL/SQL argument types are not supported.");
                    complex = await GetDerivedComplexType(RoutineParameterDerivation.RequiredText(row, "TYPE_OWNER"), RoutineParameterDerivation.RequiredText(row, "TYPE_NAME"), command, asynchronous, token).ConfigureAwait(false);
                }
                parameters[i] = RoutineParameterDerivation.Map(row, mysql, session.TreatTinyAsBoolean, complex);
            }
            token.ThrowIfCancellationRequested(); return parameters;
        }

        private static async Task ReadDerivationTable(OceanBaseCommand command, DataTable table, bool asynchronous, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using var reader = asynchronous ? await command.ExecuteReaderAsync(token).ConfigureAwait(false) : command.ExecuteReaderCore(CommandBehavior.Default, token);
            ValidateSchemaResult(table, reader); var bytes = 0;
            while (asynchronous ? await reader.ReadAsync(token).ConfigureAwait(false) : reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (table.Rows.Count >= RoutineParameterDerivation.MaximumParameters) throw new NotSupportedException("Routine metadata row limit exceeded.");
                AddSchemaRow(table, reader);
                foreach (var value in table.Rows[table.Rows.Count - 1].ItemArray)
                    if (value is string content)
                    {
                        var count = ResultColumn.Utf8.GetByteCount(content);
                        if (count > 4 * 1024 * 1024 - bytes) throw new NotSupportedException("Routine metadata byte limit exceeded.");
                        bytes += count;
                    }
            }
            if (asynchronous ? await reader.NextResultAsync(token).ConfigureAwait(false) : reader.NextResult()) throw new NotSupportedException("Routine metadata returned extra results.");
        }
    }
}
