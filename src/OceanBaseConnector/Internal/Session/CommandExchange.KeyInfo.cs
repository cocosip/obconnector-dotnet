using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Sql;

namespace OceanBaseConnector.Internal.Session
{
    internal sealed partial class CommandExchange
    {
        private const string MySqlKeyInfoSql = "SELECT T.TABLE_SCHEMA, T.TABLE_NAME, S.INDEX_NAME, CASE WHEN S.INDEX_NAME IS NULL THEN NULL WHEN S.INDEX_NAME = 'PRIMARY' THEN 'P' ELSE 'U' END, S.COLUMN_NAME, CAST(S.SEQ_IN_INDEX AS CHAR), CASE WHEN S.INDEX_NAME IS NULL THEN NULL ELSE COALESCE(C.IS_NULLABLE, 'YES') END, CASE WHEN S.INDEX_NAME IS NULL THEN NULL WHEN S.SUB_PART IS NULL AND S.COLUMN_NAME IS NOT NULL THEN 'YES' ELSE 'NO' END FROM INFORMATION_SCHEMA.TABLES T LEFT JOIN INFORMATION_SCHEMA.STATISTICS S ON S.TABLE_SCHEMA = T.TABLE_SCHEMA AND S.TABLE_NAME = T.TABLE_NAME AND S.NON_UNIQUE = 0 LEFT JOIN INFORMATION_SCHEMA.COLUMNS C ON C.TABLE_SCHEMA = S.TABLE_SCHEMA AND C.TABLE_NAME = S.TABLE_NAME AND C.COLUMN_NAME = S.COLUMN_NAME WHERE T.TABLE_TYPE = 'BASE TABLE' AND T.TABLE_SCHEMA = COALESCE(?, DATABASE()) AND T.TABLE_NAME = ? ORDER BY S.INDEX_NAME, S.SEQ_IN_INDEX";
        private const string OracleKeyInfoSql = "SELECT T.OWNER, T.TABLE_NAME, K.KEY_ID, K.KEY_KIND, K.COLUMN_NAME, CAST(K.KEY_POSITION AS VARCHAR2(20)), CASE WHEN K.KEY_ID IS NULL THEN NULL WHEN A.NULLABLE = 'N' THEN 'NO' ELSE 'YES' END, CASE WHEN K.KEY_ID IS NULL THEN NULL WHEN A.COLUMN_NAME IS NOT NULL THEN 'YES' ELSE 'NO' END FROM ALL_TABLES T LEFT JOIN (SELECT C.OWNER AS TABLE_OWNER, C.TABLE_NAME, 'C' || CAST(LENGTH(C.OWNER) AS VARCHAR2(20)) || ':' || C.OWNER || C.CONSTRAINT_NAME AS KEY_ID, CASE C.CONSTRAINT_TYPE WHEN 'P' THEN 'P' ELSE 'U' END AS KEY_KIND, M.COLUMN_NAME, M.POSITION AS KEY_POSITION FROM ALL_CONSTRAINTS C JOIN ALL_CONS_COLUMNS M ON M.OWNER = C.OWNER AND M.CONSTRAINT_NAME = C.CONSTRAINT_NAME AND M.TABLE_NAME = C.TABLE_NAME WHERE C.CONSTRAINT_TYPE IN ('P', 'U') AND C.STATUS = 'ENABLED' AND C.VALIDATED = 'VALIDATED' AND C.DEFERRABLE = 'NOT DEFERRABLE' UNION ALL SELECT S.TABLE_OWNER, S.TABLE_NAME, 'I' || CAST(LENGTH(S.OWNER) AS VARCHAR2(20)) || ':' || S.OWNER || S.INDEX_NAME, 'U', M.COLUMN_NAME, M.COLUMN_POSITION FROM ALL_INDEXES S JOIN ALL_IND_COLUMNS M ON M.INDEX_OWNER = S.OWNER AND M.INDEX_NAME = S.INDEX_NAME AND M.TABLE_OWNER = S.TABLE_OWNER AND M.TABLE_NAME = S.TABLE_NAME WHERE S.UNIQUENESS = 'UNIQUE' AND S.INDEX_TYPE = 'NORMAL' AND S.STATUS = 'VALID' AND NOT EXISTS (SELECT 1 FROM ALL_CONSTRAINTS C WHERE C.INDEX_OWNER = S.OWNER AND C.INDEX_NAME = S.INDEX_NAME AND C.CONSTRAINT_TYPE IN ('P', 'U'))) K ON K.TABLE_OWNER = T.OWNER AND K.TABLE_NAME = T.TABLE_NAME LEFT JOIN ALL_TAB_COLUMNS A ON A.OWNER = T.OWNER AND A.TABLE_NAME = T.TABLE_NAME AND A.COLUMN_NAME = K.COLUMN_NAME WHERE T.OWNER = NVL(?, SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA')) AND T.TABLE_NAME = ? ORDER BY K.KEY_ID, K.KEY_POSITION";

        internal async Task<(PreparedStatement Statement, KeyInfoMetadata Metadata)> ResolveKeyInfoAsync(PreparedStatement original, bool asynchronous, CancellationToken token)
        {
            var query = KeyInfoQuery.Parse(original.CommandText, Mode);
            var unknown = new KeyInfoMetadata(original.Columns, original.Columns.Length);
            if (query == null || original.Columns.Length == 0) return (original, unknown);
            var catalog = await ReadKeyCatalogAsync(query, asynchronous, token).ConfigureAwait(false);
            if (!catalog.Matches(query, original.Columns)) return (original, unknown);
            var key = catalog.SelectKey();
            var missing = catalog.Missing(key, original.Columns);
            var statement = original;
            if (missing.Count != 0)
            {
                var sql = query.Append(missing, original.Columns);
                if (asynchronous) await SendAsync(OceanBaseCommand.SqlPacket(sql, 22, ClientEncoding), executesSql: false).ConfigureAwait(false);
                else Send(OceanBaseCommand.SqlPacket(sql, 22, ClientEncoding), executesSql: false);
                statement = (asynchronous ? await PreparedStatement.ReadAsync(this, _session, sql, token).ConfigureAwait(false) : PreparedStatement.Read(this, _session, sql))!;
                // This exchange owns the rewritten statement; the original command cache remains unchanged.
                if (HasCursorColumns(statement) && CursorColumns != null) CursorColumns.SetStatement(statement.Id);
                else statement.Release();
                if (statement.ParameterCount != original.ParameterCount || statement.Columns.Length != original.Columns.Length + missing.Count)
                    throw new ProtocolException("KeyInfo rewriting changed the result or parameter count.");
                for (var i = 0; i < original.Columns.Length; i++) KeyInfoMetadata.ValidateColumn(original.Columns[i], statement.Columns[i]);
                for (var i = 0; i < missing.Count; i++)
                    if (!catalog.IsSource(statement.Columns[original.Columns.Length + i], missing[i]))
                        throw new ProtocolException("KeyInfo hidden column source changed after preparation.");
            }
            var metadata = new KeyInfoMetadata(statement.Columns, original.Columns.Length);
            catalog.Apply(metadata, statement.Columns, key);
            return (statement, metadata);
        }

        private async Task<KeyInfoMetadata.Catalog> ReadKeyCatalogAsync(KeyInfoQuery query, bool asynchronous, CancellationToken token)
        {
            var sql = Mode == OceanBaseCompatibilityMode.MySql ? MySqlKeyInfoSql : OracleKeyInfoSql;
            if (asynchronous) await SendAsync(OceanBaseCommand.SqlPacket(sql, 22, ClientEncoding), executesSql: false).ConfigureAwait(false);
            else Send(OceanBaseCommand.SqlPacket(sql, 22, ClientEncoding), executesSql: false);
            var lookup = (asynchronous ? await PreparedStatement.ReadAsync(this, _session, sql, token).ConfigureAwait(false) : PreparedStatement.Read(this, _session, sql))!;
            var closed = false;
            try
            {
                if (lookup.ParameterCount != 2) throw new ProtocolException("Invalid KeyInfo directory parameter count.");
                var request = PreparedParameters.Execute(lookup.Id, PreparedParameters.Encode(new[] {
                    new OceanBaseParameter { DbType = DbType.String, Value = (object?)query.Schema ?? DBNull.Value },
                    new OceanBaseParameter { DbType = DbType.String, Value = query.Table }
                }, Mode, clientEncoding: ClientEncoding));
                if (asynchronous) await SendAsync(request, executesSql: false).ConfigureAwait(false); else Send(request, executesSql: false);
                var result = new QueryResult(this, binary: true, completeExchange: false);
                if (asynchronous) await result.StartAsync(token).ConfigureAwait(false); else result.Start();
                if (result.Columns.Length != 8) throw new ProtocolException("Invalid KeyInfo directory result shape.");
                foreach (var column in result.Columns)
                    if (column.TypeCode != 15 && column.TypeCode != 253 && column.TypeCode != 254 || column.FieldType != typeof(string))
                        throw new ProtocolException("KeyInfo directory requires ordinary string columns.");
                var catalog = new KeyInfoMetadata.Catalog();
                while (asynchronous ? await result.ReadAsync(token).ConfigureAwait(false) : result.Read())
                {
                    var row = new string?[8];
                    for (var i = 0; i < row.Length; i++)
                    {
                        var bytes = result.Row![i];
                        if (bytes != null)
                        {
                            if (bytes.Length > 4 * 1024 * 1024) throw new NotSupportedException("KeyInfo directory field limit exceeded.");
                            row[i] = ResultColumn.Utf8.GetString(bytes);
                        }
                    }
                    catalog.Add(row);
                }
                if (asynchronous ? await result.NextResultAsync(token).ConfigureAwait(false) : result.NextResult()) throw new ProtocolException("KeyInfo directory returned extra results.");
                if (asynchronous) await SendAsync(PreparedStatement.ClosePacket(lookup.Id), executesSql: false).ConfigureAwait(false);
                else Send(PreparedStatement.ClosePacket(lookup.Id), executesSql: false);
                ResponseComplete(expectsResponse: false); closed = true;
                return catalog;
            }
            finally { if (!closed) lookup.Release(); }
        }
    }
}
