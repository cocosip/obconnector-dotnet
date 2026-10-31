using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;

namespace OceanBaseConnector.Internal.Session
{
    internal sealed partial class CommandExchange
    {
        private const string ComplexColumnSql = "SELECT DISTINCT NVL(C.DATA_TYPE_OWNER, D.REFERENCED_OWNER) AS DATA_TYPE_OWNER, C.DATA_TYPE FROM ALL_TAB_COLUMNS C LEFT JOIN ALL_DEPENDENCIES D ON C.DATA_TYPE_OWNER IS NULL AND D.OWNER = C.OWNER AND D.NAME = C.TABLE_NAME AND D.TYPE = 'TABLE' AND D.REFERENCED_TYPE = 'TYPE' AND D.REFERENCED_NAME = C.DATA_TYPE AND D.REFERENCED_LINK_NAME IS NULL WHERE C.OWNER = ? AND C.TABLE_NAME = ? AND C.COLUMN_NAME = ?";
        internal Dictionary<(string, string, string, string), OceanBaseComplexType>? SourceComplexTypes { get; private set; }

        internal bool HasCursorColumns(PreparedStatement statement)
        {
            foreach (var column in statement.Columns)
            {
                if (column.TypeCode == 163) return true;
                if (column.ComplexType?.Layout.HasCursorMembers == true) return true;
                var metadata = column.ComplexMetadata;
                if (column.TypeCode != 160 || metadata == null) continue;
                if (metadata.Name.Length != 0 && ComplexTypes != null && ComplexTypes.TryGetValue((metadata.Schema, metadata.Name), out var named) && named.Layout.HasCursorMembers)
                    return true;
                if (SourceComplexTypes != null)
                    foreach (var entry in SourceComplexTypes)
                        if (entry.Key.Item1 == column.Database && entry.Key.Item2 == column.OriginalTable && entry.Key.Item3 == column.OriginalName && entry.Value.Layout.HasCursorMembers)
                            return true;
            }
            return false;
        }

        internal void ResolveComplexColumns(PreparedStatement statement)
        {
            foreach (var column in statement.Columns)
            {
                if (!NeedsComplexColumnOwner(column)) continue;
                Send(OceanBaseCommand.SqlPacket(ComplexColumnSql, 22), executesSql: false);
                var lookup = PreparedStatement.Read(this, _session, ComplexColumnSql)!;
                var closed = false;
                try
                {
                    var request = ComplexColumnRequest(lookup, column);
                    Send(request, executesSql: false);
                    var result = new QueryResult(this, binary: true, completeExchange: false);
                    result.Start();
                    var identity = ReadComplexColumnIdentity(result, result.Read());
                    if (result.Read()) throw new ProtocolException("The complex source column query returned extra rows.");
                    Send(PreparedStatement.ClosePacket(lookup.Id), executesSql: false); ResponseComplete(expectsResponse: false); closed = true;
                    PublishComplexColumn(column, identity);
                }
                finally { if (!closed) lookup.Release(); }
            }
        }

        internal async Task ResolveComplexColumnsAsync(PreparedStatement statement, CancellationToken token)
        {
            foreach (var column in statement.Columns)
            {
                if (!NeedsComplexColumnOwner(column)) continue;
                await SendAsync(OceanBaseCommand.SqlPacket(ComplexColumnSql, 22), executesSql: false).ConfigureAwait(false);
                var lookup = (await PreparedStatement.ReadAsync(this, _session, ComplexColumnSql, token).ConfigureAwait(false))!;
                var closed = false;
                try
                {
                    var request = ComplexColumnRequest(lookup, column);
                    await SendAsync(request, executesSql: false).ConfigureAwait(false);
                    var result = new QueryResult(this, binary: true, completeExchange: false);
                    await result.StartAsync(token).ConfigureAwait(false);
                    var identity = ReadComplexColumnIdentity(result, await result.ReadAsync(token).ConfigureAwait(false));
                    if (await result.ReadAsync(token).ConfigureAwait(false)) throw new ProtocolException("The complex source column query returned extra rows.");
                    await SendAsync(PreparedStatement.ClosePacket(lookup.Id), executesSql: false).ConfigureAwait(false); ResponseComplete(expectsResponse: false); closed = true;
                    PublishComplexColumn(column, identity);
                }
                finally { if (!closed) lookup.Release(); }
            }
        }

        private bool NeedsComplexColumnOwner(ResultColumn column)
        {
            var metadata = column.ComplexMetadata;
            if (metadata == null || metadata.Schema.Length != 0) return false;
            // Bound SELECT preparation can omit the type name as well as its owner.
            if (metadata.Name.Length == 0 && (column.Database.Length == 0 || column.OriginalTable.Length == 0 || column.OriginalName.Length == 0)) return false;
            if (SourceComplexTypes != null)
                foreach (var key in SourceComplexTypes.Keys)
                    if (key.Item1 == column.Database && key.Item2 == column.OriginalTable && key.Item3 == column.OriginalName &&
                        (metadata.Name.Length == 0 || key.Item4 == metadata.Name)) return false;
            ValidateComplexSourceName(column.Database); ValidateComplexSourceName(column.OriginalTable); ValidateComplexSourceName(column.OriginalName);
            return true;
        }

        private byte[] ComplexColumnRequest(PreparedStatement statement, ResultColumn column)
        {
            if (statement.ParameterCount != 3) throw new ProtocolException("Invalid complex source column query parameter count.");
            return PreparedParameters.Execute(statement.Id, PreparedParameters.Encode(new[] {
                new OceanBaseParameter { Value = column.Database }, new OceanBaseParameter { Value = column.OriginalTable },
                new OceanBaseParameter { Value = column.OriginalName }
            }, OceanBaseCompatibilityMode.Oracle, clientEncoding: ClientEncoding));
        }

        private static (string, string) ReadComplexColumnIdentity(QueryResult result, bool hasRow)
        {
            if (result.Columns.Length != 2 || !hasRow) throw new NotSupportedException("The complex source column type owner is unavailable.");
            var identity = new string[2];
            for (var i = 0; i < identity.Length; i++)
            {
                var column = result.Columns[i];
                if ((column.TypeCode != 15 && column.TypeCode != 253 && column.TypeCode != 254) || column.FieldType != typeof(string))
                    throw new ProtocolException("The complex source column query requires ordinary string columns.");
                var bytes = result.Row![i];
                if (bytes == null && i == 0) throw new NotSupportedException("The complex source column type owner is unavailable.");
                if (bytes == null || bytes.Length == 0 || bytes.Length > 512) throw new ProtocolException("Invalid complex source column type identifier.");
                identity[i] = ResultColumn.Utf8.GetString(bytes);
                ValidateComplexSourceName(identity[i]);
            }
            return (identity[0], identity[1]);
        }

        private void PublishComplexColumn(ResultColumn column, (string Owner, string Name) identity)
        {
            // Source table ownership is independent of its declared type ownership.
            if (column.ComplexMetadata!.Name.Length != 0 && identity.Name != column.ComplexMetadata.Name ||
                ComplexTypes == null || !ComplexTypes.TryGetValue(identity, out var description))
                throw new NotSupportedException("The complex source column requires its exact preloaded type description.");
            (SourceComplexTypes ??= new Dictionary<(string, string, string, string), OceanBaseComplexType>())
                .Add((column.Database, column.OriginalTable, column.OriginalName, identity.Name), description);
        }

        private static void ValidateComplexSourceName(string name)
        {
            if (name.Length == 0 || name.IndexOf('\0') >= 0 || ResultColumn.Utf8.GetByteCount(name) > 512)
                throw new NotSupportedException("A named complex result without an owner requires an exact source table and column identity.");
        }
    }
}
