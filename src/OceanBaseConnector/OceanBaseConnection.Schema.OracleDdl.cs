using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Sql;

namespace OceanBaseConnector
{
    public sealed partial class OceanBaseConnection
    {
        /// <summary>Reads one Oracle table's identity definitions using DBMS_METADATA.GET_DDL.</summary>
        public IReadOnlyList<OceanBaseIdentityColumn> GetOracleIdentityColumns(string tableName, string? schemaName = null)
        {
            using var command = CreateOracleDdlCommand("TABLE", tableName, schemaName, CancellationToken.None);
            return OracleDdlMetadata.ReadIdentityColumns(ReadOracleDdl(command.ExecuteScalar()));
        }

        /// <summary>Reads one Oracle table's identity definitions using DBMS_METADATA.GET_DDL.</summary>
        public async Task<IReadOnlyList<OceanBaseIdentityColumn>> GetOracleIdentityColumnsAsync(string tableName, string? schemaName = null, CancellationToken cancellationToken = default)
        {
            using var command = CreateOracleDdlCommand("TABLE", tableName, schemaName, cancellationToken);
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            var ddl = await ReadOracleDdlAsync(value, cancellationToken).ConfigureAwait(false);
            return OracleDdlMetadata.ReadIdentityColumns(ddl);
        }

        /// <summary>Reads the START WITH value in one Oracle sequence's DDL.</summary>
        public OceanBaseDecimal GetOracleSequenceStartValue(string sequenceName, string? schemaName = null)
        {
            using var command = CreateOracleDdlCommand("SEQUENCE", sequenceName, schemaName, CancellationToken.None);
            return OracleDdlMetadata.ReadSequenceStart(ReadOracleDdl(command.ExecuteScalar()));
        }

        /// <summary>Reads the START WITH value in one Oracle sequence's DDL.</summary>
        public async Task<OceanBaseDecimal> GetOracleSequenceStartValueAsync(string sequenceName, string? schemaName = null, CancellationToken cancellationToken = default)
        {
            using var command = CreateOracleDdlCommand("SEQUENCE", sequenceName, schemaName, cancellationToken);
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            var ddl = await ReadOracleDdlAsync(value, cancellationToken).ConfigureAwait(false);
            return OracleDdlMetadata.ReadSequenceStart(ddl);
        }

        private static bool CanRetryOracleIndexColumns(OceanBaseException error, DataTable table, bool mysql) =>
            !mysql && table.TableName == "IndexColumns" && error.ServerErrorCode == 600 && !error.IsExecutionOutcomeUnknown;

        private static void SelectOracleIndexExpressionPositions(DataTable table, OceanBaseCommand command)
        {
            // OceanBase 4.4.2.2's expression projection can fail when its VARCHAR2(1000) cast overflows.
            table.Clear();
            command.CommandText = command.CommandText.Replace(OracleIndexExpressionProjection,
                "CASE WHEN e.COLUMN_POSITION IS NOT NULL THEN '1' END AS COLUMN_EXPRESSION");
        }

        private async Task ReadOracleIndexExpressionsAsync(DataTable table, OceanBaseCommand catalogCommand, bool asynchronous, CancellationToken token)
        {
            var definitions = new Dictionary<(string Owner, string Name), IReadOnlyList<string>>();
            foreach (DataRow row in table.Rows)
            {
                if (row.IsNull("COLUMN_EXPRESSION")) continue;
                var key = (Owner: (string)row["INDEX_SCHEMA"], Name: (string)row["INDEX_NAME"]);
                if (!definitions.TryGetValue(key, out var members))
                {
                    using var command = CreateOracleDdlCommand("INDEX", key.Name, key.Owner, token);
                    command.RequiredSession = catalogCommand.RequiredSession;
                    command.RequiredTransactionEpoch = catalogCommand.RequiredTransactionEpoch;
                    var ddl = asynchronous
                        ? await ReadOracleDdlAsync(await command.ExecuteScalarAsync(token).ConfigureAwait(false), token).ConfigureAwait(false)
                        : ReadOracleDdl(command.ExecuteScalar());
                    members = OracleDdlMetadata.ReadIndexMembers(ddl);
                    definitions.Add(key, members);
                }
                var ordinal = (int)row["ORDINAL_POSITION"];
                if (ordinal <= 0 || ordinal > members.Count)
                    throw new NotSupportedException("The Oracle index DDL does not match the index member catalog.");
                row["COLUMN_EXPRESSION"] = members[ordinal - 1];
            }
        }

        private OceanBaseCommand CreateOracleDdlCommand(string kind, string objectName, string? schemaName, CancellationToken token)
        {
            if (string.IsNullOrEmpty(objectName)) throw new ArgumentException("An object name is required.", nameof(objectName));
            if (schemaName?.Length == 0) throw new ArgumentException("A schema name cannot be empty.", nameof(schemaName));
            EnsureAmbientTransaction();
            token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var session = GetOpenSession();
                if (session.Info!.Mode != OceanBaseCompatibilityMode.Oracle)
                    throw new NotSupportedException("This metadata API requires Oracle compatibility mode.");
                var schema = schemaName == null ? "USER" : ":schema_name";
                var command = new OceanBaseCommand
                {
                    Connection = this,
                    Transaction = _transaction,
                    CommandTimeout = _builder.CommandTimeout,
                    RequiredSession = session,
                    RequiredTransactionEpoch = _transactionEpoch,
                    CommandText = "SELECT DBMS_METADATA.GET_DDL('" + kind + "', :object_name, " + schema + ") FROM DUAL"
                };
                command.Parameters.Add(new OceanBaseParameter { ParameterName = "object_name", Value = objectName });
                if (schemaName != null)
                    command.Parameters.Add(new OceanBaseParameter { ParameterName = "schema_name", Value = schemaName });
                return command;
            }
        }

        private static string ReadOracleDdl(object? value)
        {
            if (value is OceanBaseLob lob)
            {
                using (lob)
                using (var reader = lob.OpenTextReader())
                    value = reader.ReadToEnd();
            }
            return RequireOracleDdlText(value);
        }

        private static async Task<string> ReadOracleDdlAsync(object? value, CancellationToken token)
        {
            if (value is OceanBaseLob lob)
            {
#if NETSTANDARD2_1 || NET8_0_OR_GREATER
                await using var handle = lob;
#else
                using var handle = lob;
#endif
                token.ThrowIfCancellationRequested();
                using var reader = handle.OpenTextReader();
#if NET8_0_OR_GREATER
                value = await reader.ReadToEndAsync(token).ConfigureAwait(false);
#else
                value = await reader.ReadToEndAsync().ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
#endif
            }
            return RequireOracleDdlText(value);
        }

        private static string RequireOracleDdlText(object? value) =>
            value is string ddl && ddl.Length > 0 ? ddl : throw new NotSupportedException("Oracle DBMS_METADATA.GET_DDL did not return DDL text.");
    }
}
