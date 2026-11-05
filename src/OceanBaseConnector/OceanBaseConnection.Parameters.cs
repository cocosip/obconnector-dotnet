using System;
using System.Collections.Generic;
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
            var argumentSql = RoutineDirectorySql(true, mysql, filters, command, derivation: true, predicate: predicate);
            // Older ALL_ARGUMENTS views lack CHAR_LENGTH; numeric signatures must not require it.
            command.CommandText = mysql ? argumentSql : argumentSql.Replace("CHAR_LENGTH AS CHARACTER_LENGTH", "NULL AS CHARACTER_LENGTH");
            await ReadDerivationTable(command, arguments, asynchronous, token).ConfigureAwait(false);
            var topLevelArguments = TopLevelArguments(arguments);
            var rows = RoutineParameterDerivation.OrderParameters(topLevelArguments, routine, mysql);
            if (!mysql && Array.Exists(rows, RoutineParameterDerivation.RequiresCharacterLength))
            {
                using var lengths = CreateRoutineDirectoryTable(true, true);
                command.CommandText = argumentSql;
                await ReadDerivationTable(command, lengths, asynchronous, token).ConfigureAwait(false);
                var characterRows = RoutineParameterDerivation.OrderParameters(TopLevelArguments(lengths), routine, false);
                if (rows.Length != characterRows.Length) throw new NotSupportedException("The routine signature changed while reading character lengths.");
                for (var i = 0; i < rows.Length; i++)
                {
                    foreach (DataColumn column in arguments.Columns)
                        if (column.ColumnName != "CHARACTER_LENGTH" && !Equals(rows[i][column.ColumnName], characterRows[i][column.ColumnName]))
                            throw new NotSupportedException("The routine signature changed while reading character lengths.");
                    rows[i]["CHARACTER_LENGTH"] = characterRows[i]["CHARACTER_LENGTH"];
                }
            }
            var parameters = new OceanBaseParameter[rows.Length];
            for (var i = 0; i < rows.Length; i++)
            {
                token.ThrowIfCancellationRequested(); var row = rows[i]; OceanBaseComplexType? complex = null;
                if (!mysql && !row.IsNull("TYPE_OWNER") && !row.IsNull("TYPE_NAME"))
                {
                    var package = RoutineParameterDerivation.Text(row, "TYPE_SUBNAME");
                    if (package != null)
                    {
                        if (!string.Equals(RoutineParameterDerivation.RequiredText(row, "DATA_TYPE"), "PL/SQL RECORD", StringComparison.OrdinalIgnoreCase))
                            throw new NotSupportedException("Package-local argument types other than RECORD are not supported.");
                        var members = RecordMembers(arguments, row);
                        complex = CreateDerivedRecordType(RoutineParameterDerivation.RequiredText(row, "TYPE_OWNER"),
                            RoutineParameterDerivation.RequiredText(row, "TYPE_NAME"), package, members, command);
                    }
                    else
                        complex = await GetDerivedComplexType(RoutineParameterDerivation.RequiredText(row, "TYPE_OWNER"), RoutineParameterDerivation.RequiredText(row, "TYPE_NAME"), command, asynchronous, token).ConfigureAwait(false);
                }
                parameters[i] = RoutineParameterDerivation.Map(row, mysql, session.TreatTinyAsBoolean, complex);
            }
            if (parameters.Length != 0 && parameters[0].Direction == ParameterDirection.ReturnValue)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 1; i < parameters.Length; i++) names.Add(parameters[i].ParameterName);
                var name = "RETURN_VALUE";
                for (var suffix = 1; names.Contains(name); suffix++)
                    name = "RETURN_VALUE_" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
                parameters[0].ParameterName = name;
            }
            token.ThrowIfCancellationRequested(); return parameters;
        }

        private static DataTable TopLevelArguments(DataTable source)
        {
            var result = source.Clone();
            foreach (DataRow row in source.Rows)
            {
                var level = source.Columns.Contains("ARGUMENT_LEVEL") && !row.IsNull("ARGUMENT_LEVEL")
                    ? (int?)row["ARGUMENT_LEVEL"] : null;
                if (!level.HasValue || level.Value == 0) result.ImportRow(row);
            }
            return result;
        }

        private static OceanBaseComplexMember[] RecordMembers(DataTable arguments, DataRow record)
        {
            var identity = new[] { "ROUTINE_CATALOG", "ROUTINE_SCHEMA", "ROUTINE_NAME", "PACKAGE_NAME", "OVERLOAD", "SUBPROGRAM_ID" };
            var candidates = new List<DataRow>();
            var recordSequence = RoutineParameterDerivation.Integer(record, "ARGUMENT_SEQUENCE");
            var nextTopSequence = (int?)null;
            foreach (DataRow row in arguments.Rows)
            {
                var matches = true;
                foreach (var column in identity)
                    if (!Equals(row[column], record[column])) { matches = false; break; }
                if (!matches) continue;
                var level = RoutineParameterDerivation.Integer(row, "ARGUMENT_LEVEL");
                if (level.HasValue && level.Value > 1) throw new NotSupportedException("Nested RECORD fields are not supported.");
                if (level == 1) candidates.Add(row);
            }
            if (recordSequence.HasValue)
            {
                foreach (DataRow row in arguments.Rows)
                {
                    var level = RoutineParameterDerivation.Integer(row, "ARGUMENT_LEVEL");
                    var sequence = RoutineParameterDerivation.Integer(row, "ARGUMENT_SEQUENCE");
                    if (level.GetValueOrDefault() == 0 && sequence.HasValue && sequence > recordSequence &&
                        (nextTopSequence == null || sequence < nextTopSequence)) nextTopSequence = sequence;
                }
                candidates.RemoveAll(row =>
                {
                    var sequence = RoutineParameterDerivation.Integer(row, "ARGUMENT_SEQUENCE");
                    return !sequence.HasValue || sequence <= recordSequence || nextTopSequence.HasValue && sequence >= nextTopSequence;
                });
            }
            else
            {
                var ordinal = RoutineParameterDerivation.Integer(record, "ORDINAL_POSITION");
                candidates.RemoveAll(row => RoutineParameterDerivation.Integer(row, "ORDINAL_POSITION") != ordinal);
            }
            candidates.Sort((left, right) =>
            {
                var a = RoutineParameterDerivation.Integer(left, "ARGUMENT_SEQUENCE");
                var b = RoutineParameterDerivation.Integer(right, "ARGUMENT_SEQUENCE");
                return a.HasValue && b.HasValue ? a.Value.CompareTo(b.Value) : 0;
            });
            if (candidates.Count == 0) throw new NotSupportedException("Package-local PL/SQL RECORD field metadata is unavailable.");
            foreach (var candidate in candidates)
                if (!RoutineParameterDerivation.Integer(candidate, "ARGUMENT_SEQUENCE").HasValue)
                    throw new NotSupportedException("RECORD field sequence metadata is unavailable.");
            var members = new OceanBaseComplexMember[candidates.Count];
            var names = new HashSet<string>(StringComparer.Ordinal);
            var expectedSequence = recordSequence.HasValue ? recordSequence.Value + 1 : (int?)null;
            for (var i = 0; i < candidates.Count; i++)
            {
                var sequence = RoutineParameterDerivation.Integer(candidates[i], "ARGUMENT_SEQUENCE");
                if (sequence.HasValue && expectedSequence.HasValue && sequence != expectedSequence++)
                    throw new NotSupportedException("RECORD field sequence metadata is not contiguous.");
                if (sequence.HasValue && i > 0 && expectedSequence == null)
                {
                    var previous = RoutineParameterDerivation.Integer(candidates[i - 1], "ARGUMENT_SEQUENCE");
                    if (!previous.HasValue || sequence <= previous) throw new NotSupportedException("RECORD field sequence metadata is invalid.");
                }
                members[i] = RoutineParameterDerivation.MapRecordMember(candidates[i]);
                if (!names.Add(members[i].Name!)) throw new NotSupportedException("RECORD field labels are duplicated.");
            }
            return members;
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
