using System;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;
using OceanBaseConnector.Internal.Sql;

namespace OceanBaseConnector
{
    public sealed partial class OceanBaseCommand : DbCommand
    {
        private readonly OceanBaseParameterCollection _parameters = new OceanBaseParameterCollection();
        private OceanBaseConnection? _connection;
        private OceanBaseTransaction? _transaction;
        private int _timeout = OceanBaseConnectionStringBuilder.DefaultCommandTimeoutSeconds;
        private readonly object _gate = new object();
        private CommandExchange? _execution;
        private CancellationTokenSource? _derivation;
        private PreparedStatement? _prepared;
        private PhysicalSession? _oracleSession;
        private bool _explicitPreparation;
        private string? _mysqlVariablePrefix;
        private bool _disposed;
        private int _fetchSize;
        internal PhysicalSession? RequiredSession { get; set; }
        internal object? SessionOwner { get; set; }
        internal long? RequiredTransactionEpoch { get; set; }
        internal bool IsLobRead { get; set; }
        internal bool HasSentSql { get; private set; }
        internal Func<CancellationToken>? CancellationOrigin { get; set; }
#if NET8_0_OR_GREATER
        internal DbBatchCommand? BatchCommand { get; set; }
        internal Action? BatchFailed { get; set; }
#endif
        public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get => _timeout; set { if (value < 0) throw new ArgumentOutOfRangeException(nameof(value)); _timeout = value; } }
        /// <summary>Rows requested per server cursor fetch; zero uses ordinary result streaming.
        /// Oracle internal output rows and cursor-valued columns do not page their parent result;
        /// returned REF CURSOR readers have their own page size.</summary>
        public int FetchSize { get => _fetchSize; set { if (value < 0) throw new ArgumentOutOfRangeException(nameof(value)); _fetchSize = value; } }
        /// <summary>Use Oracle offset fetch for a positive FetchSize; requires OceanBase 2.2.5 or later.
        /// Not supported for Oracle output plans or cursor-valued result columns.</summary>
        public bool UseCursorOffset { get; set; }
        /// <summary>Allow scalar input text execution only after an unsupported-prepare error (1295/HY000).
        /// Disabled by default; explicit Prepare, cursors and Oracle PrepareExecute never fall back.</summary>
        public bool AllowClientSidePrepareFallback { get; set; }
        public override CommandType CommandType { get; set; } = CommandType.Text;
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; } = UpdateRowSource.Both;
        protected override DbConnection? DbConnection
        {
            get => _connection;
            set => _connection = value == null ? null : value as OceanBaseConnection ?? throw new ArgumentException("An OceanBaseConnection is required.", nameof(value));
        }
        protected override DbTransaction? DbTransaction
        {
            get => _transaction;
            set => _transaction = value == null ? null : value as OceanBaseTransaction ?? throw new ArgumentException("An OceanBaseTransaction is required.", nameof(value));
        }
        protected override DbParameterCollection DbParameterCollection => _parameters;
        protected override DbParameter CreateDbParameter() => new OceanBaseParameter();
        public override int ExecuteNonQuery()
        {
            using var reader = ExecuteDbDataReader(CommandBehavior.Default);
            do { while (reader.Read()) { } } while (reader.NextResult());
            return reader.RecordsAffected;
        }
        public override object? ExecuteScalar()
        {
            using var reader = ExecuteDbDataReader(CommandBehavior.Default);
            while (reader.FieldCount == 0 && reader.NextResult()) { }
            var value = reader.Read() && reader.FieldCount != 0 ? reader.GetValue(0) : null;
            do { while (reader.Read()) { } } while (reader.NextResult());
            return value;
        }
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
            => ExecuteReaderCore(behavior, CancellationToken.None);
        internal DbDataReader ExecuteReaderCore(CommandBehavior behavior, CancellationToken token, int arrayCount = 0)
        {
            if ((behavior & CommandBehavior.KeyInfo) != 0) return ExecuteKeyInfoAsync(behavior, false, token, arrayCount).GetAwaiter().GetResult();
            var operation = Start(behavior, token, false, arrayCount);
            var execution = operation.Exchange;
            execution.StartDiagnostics("execute");
            try
            {
                if ((behavior & CommandBehavior.SchemaOnly) != 0)
                {
                    var metadata = EnsurePrepared(operation, useCache: false)!;
                    execution.ResolveComplexColumns(metadata);
                    return new OceanBaseDataReader(execution, QueryResult.FromPrepared(execution, metadata), operation.Connection, operation.Session, behavior);
                }
                if (operation.Oracle != null) execution.EnsureOraclePrepareExecute();
                if (operation.CursorOffset) execution.EnsureCursorOffset();
                var zone = execution.EnsureSessionTimeZone();
                PreparedParameters.ValidateSessionTimeZone(operation.Bindings.Parameters, zone);
                var national = execution.EnsureNationalCharacterSet();
                operation.MySql?.Initialize(execution, operation.Session);
                var statement = operation.Binary && (operation.Oracle == null || _prepared != null || HasStreamParameters(operation.Bindings.Parameters))
                    ? EnsurePrepared(operation, operation.ClientFallback) : null;
                var text = operation.Binary && operation.Oracle == null && statement == null
                    ? ClientPreparedSql.Build(operation.Bindings, execution.Mode) : operation.Bindings.CommandText;
                ValidateCursorStatement(operation.FetchSize, statement);
                var fetchSize = ResultFetchSize(operation.FetchSize, operation.CursorOffset, statement);
                if (statement != null && operation.Oracle == null) execution.ResolveComplexColumns(statement);
                if (statement != null && operation.Oracle == null) TransferCursorColumns(statement, execution);
                if (execution.SourceComplexTypes != null)
                {
                    zone = execution.EnsureSessionTimeZone();
                    PreparedParameters.ValidateSessionTimeZone(operation.Bindings.Parameters, zone);
                    national = execution.EnsureNationalCharacterSet();
                }
                var parameters = HasNationalParameters(operation.Bindings.Parameters)
                    ? PreparedParameters.Encode(operation.Bindings.Parameters, execution.Mode, national) : operation.Parameters;
                var request = operation.Oracle?.ArrayBinding != null ? OraclePrepareExecutePacket.BuildArray(statement?.Id ?? 0, operation.Bindings.CommandText, operation.Bindings.Parameters, national)
                    : operation.Oracle != null ? OraclePrepareExecutePacket.Build(statement?.Id ?? 0, operation.Bindings.CommandText, operation.Bindings.Parameters.Length, parameters)
                    : statement == null ? SqlPacket(text, 3) : PreparedParameters.Execute(statement.Id, parameters, fetchSize > 0);
                if (statement != null) execution.SendLongData(statement.Id, operation.Bindings.Parameters, request.Length);
                execution.BeforeSuccessfulCompletion = operation.MySql != null ? operation.MySql.PublishOutputs : operation.Oracle == null ? null : operation.Oracle.PublishOutputs;
                execution.Send(request, executesSql: !IsLobRead);
                var result = new QueryResult(execution, operation.Oracle != null || statement != null, sequential: (behavior & CommandBehavior.SequentialAccess) != 0,
                    cursorId: fetchSize > 0 ? statement!.Id : (uint?)null, fetchSize: fetchSize,
                    oracleParameterCount: operation.Oracle != null ? operation.Bindings.Parameters.Length : (int?)null, oraclePlan: operation.Oracle, cursorOffset: operation.CursorOffset, mysqlPlan: operation.MySql);
                result.Start();
                return new OceanBaseDataReader(execution, result, operation.Connection, operation.Session, behavior);
            }
            catch (Exception error) { throw execution.Fail(error); }
        }
        public override void Prepare()
            => PrepareCore(CancellationToken.None);
        internal void PrepareCore(CancellationToken token)
        {
            var operation = Start(CommandBehavior.Default, token, true);
            operation.Exchange.StartDiagnostics("prepare");
            try { EnsurePrepared(operation); operation.Exchange.Complete(); }
            catch (Exception error) { throw operation.Exchange.Fail(error); }
        }
        public override void Cancel() { lock (_gate) { _execution?.Cancel(); _derivation?.Cancel(); } }
        public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
        {
            using var reader = await ExecuteDbDataReaderAsync(CommandBehavior.Default, cancellationToken).ConfigureAwait(false);
            do { while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) { } }
            while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));
            return reader.RecordsAffected;
        }
        public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
        {
            using var reader = await ExecuteDbDataReaderAsync(CommandBehavior.Default, cancellationToken).ConfigureAwait(false);
            while (reader.FieldCount == 0 && await reader.NextResultAsync(cancellationToken).ConfigureAwait(false)) { }
            var value = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && reader.FieldCount != 0 ? reader.GetValue(0) : null;
            do { while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) { } }
            while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));
            return value;
        }
        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
            => ExecuteReaderCoreAsync(behavior, cancellationToken);
        private async Task<DbDataReader> ExecuteReaderCoreAsync(CommandBehavior behavior, CancellationToken cancellationToken, int arrayCount = 0)
        {
            if ((behavior & CommandBehavior.KeyInfo) != 0) return await ExecuteKeyInfoAsync(behavior, true, cancellationToken, arrayCount).ConfigureAwait(false);
            var operation = Start(behavior, cancellationToken, false, arrayCount);
            var execution = operation.Exchange;
            execution.StartDiagnostics("execute");
            try
            {
                if ((behavior & CommandBehavior.SchemaOnly) != 0)
                {
                    var metadata = (await EnsurePreparedAsync(operation, cancellationToken, useCache: false).ConfigureAwait(false))!;
                    await execution.ResolveComplexColumnsAsync(metadata, cancellationToken).ConfigureAwait(false);
                    return new OceanBaseDataReader(execution, QueryResult.FromPrepared(execution, metadata), operation.Connection, operation.Session, behavior);
                }
                if (operation.Oracle != null) await execution.EnsureOraclePrepareExecuteAsync(cancellationToken).ConfigureAwait(false);
                if (operation.CursorOffset) await execution.EnsureCursorOffsetAsync(cancellationToken).ConfigureAwait(false);
                var zone = await execution.EnsureSessionTimeZoneAsync(cancellationToken).ConfigureAwait(false);
                PreparedParameters.ValidateSessionTimeZone(operation.Bindings.Parameters, zone);
                var national = await execution.EnsureNationalCharacterSetAsync(cancellationToken).ConfigureAwait(false);
                if (operation.MySql != null) await operation.MySql.InitializeAsync(execution, operation.Session, cancellationToken).ConfigureAwait(false);
                var statement = operation.Binary && (operation.Oracle == null || _prepared != null || HasStreamParameters(operation.Bindings.Parameters))
                    ? await EnsurePreparedAsync(operation, cancellationToken, operation.ClientFallback).ConfigureAwait(false) : null;
                var text = operation.Binary && operation.Oracle == null && statement == null
                    ? ClientPreparedSql.Build(operation.Bindings, execution.Mode) : operation.Bindings.CommandText;
                ValidateCursorStatement(operation.FetchSize, statement);
                var fetchSize = ResultFetchSize(operation.FetchSize, operation.CursorOffset, statement);
                if (statement != null && operation.Oracle == null) await execution.ResolveComplexColumnsAsync(statement, cancellationToken).ConfigureAwait(false);
                if (statement != null && operation.Oracle == null) TransferCursorColumns(statement, execution);
                if (execution.SourceComplexTypes != null)
                {
                    zone = await execution.EnsureSessionTimeZoneAsync(cancellationToken).ConfigureAwait(false);
                    PreparedParameters.ValidateSessionTimeZone(operation.Bindings.Parameters, zone);
                    national = await execution.EnsureNationalCharacterSetAsync(cancellationToken).ConfigureAwait(false);
                }
                var parameters = HasNationalParameters(operation.Bindings.Parameters)
                    ? PreparedParameters.Encode(operation.Bindings.Parameters, execution.Mode, national) : operation.Parameters;
                var request = operation.Oracle?.ArrayBinding != null ? OraclePrepareExecutePacket.BuildArray(statement?.Id ?? 0, operation.Bindings.CommandText, operation.Bindings.Parameters, national)
                    : operation.Oracle != null ? OraclePrepareExecutePacket.Build(statement?.Id ?? 0, operation.Bindings.CommandText, operation.Bindings.Parameters.Length, parameters)
                    : statement == null ? SqlPacket(text, 3) : PreparedParameters.Execute(statement.Id, parameters, fetchSize > 0);
                if (statement != null) await execution.SendLongDataAsync(statement.Id, operation.Bindings.Parameters, request.Length).ConfigureAwait(false);
                execution.BeforeSuccessfulCompletion = operation.MySql != null ? operation.MySql.PublishOutputs : operation.Oracle == null ? null : operation.Oracle.PublishOutputs;
                await execution.SendAsync(request, executesSql: !IsLobRead).ConfigureAwait(false);
                var result = new QueryResult(execution, operation.Oracle != null || statement != null, sequential: (behavior & CommandBehavior.SequentialAccess) != 0,
                    cursorId: fetchSize > 0 ? statement!.Id : (uint?)null, fetchSize: fetchSize,
                    oracleParameterCount: operation.Oracle != null ? operation.Bindings.Parameters.Length : (int?)null, oraclePlan: operation.Oracle, cursorOffset: operation.CursorOffset, mysqlPlan: operation.MySql);
                await result.StartAsync(cancellationToken).ConfigureAwait(false);
                return new OceanBaseDataReader(execution, result, operation.Connection, operation.Session, behavior);
            }
            catch (Exception error) { throw execution.Fail(error); }
        }
#if NETSTANDARD2_1 || NET8_0_OR_GREATER
        public override async Task PrepareAsync(CancellationToken cancellationToken = default)
        {
            var operation = Start(CommandBehavior.Default, cancellationToken, true);
            operation.Exchange.StartDiagnostics("prepare");
            try { await EnsurePreparedAsync(operation, cancellationToken).ConfigureAwait(false); operation.Exchange.Complete(); }
            catch (Exception error) { throw operation.Exchange.Fail(error); }
        }

        public override async ValueTask DisposeAsync()
        {
            await DisposeCoreAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
#endif
        internal async Task DisposeCoreAsync()
        {
            var statement = DetachStatement();
            if (statement != null) await PreparedStatement.CloseIdleAsync(statement.Session, _timeout).ConfigureAwait(false);
            if (_oracleSession != null && !ReferenceEquals(_oracleSession, statement?.Session)) await PreparedStatement.CloseIdleAsync(_oracleSession, _timeout).ConfigureAwait(false);
            _oracleSession = null;
        }
        private (CommandExchange Exchange, PhysicalSession Session, OceanBaseConnection Connection, ParameterBindingPlan Bindings, byte[] Parameters, bool Binary, int FetchSize, OracleCommandPlan? Oracle, bool ClientFallback, bool CursorOffset, MySqlCommandPlan? MySql)
            Start(CommandBehavior behavior, CancellationToken token, bool prepareOnly, int arrayCount = 0)
        {
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(OceanBaseCommand));
                if (_execution != null || _derivation != null) throw new InvalidOperationException("The command already has an active operation.");
                HasSentSql = false;
                if (CommandType != CommandType.Text && CommandType != CommandType.StoredProcedure)
                    throw new NotSupportedException("This CommandType is not supported.");
                if ((behavior & ~(CommandBehavior.CloseConnection | CommandBehavior.SequentialAccess | CommandBehavior.SingleRow | CommandBehavior.SingleResult | CommandBehavior.SchemaOnly | CommandBehavior.KeyInfo)) != 0)
                    throw new NotSupportedException("This CommandBehavior is not supported yet.");
                var text = CommandText;
                if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("CommandText is required.");
                var schemaOnly = (behavior & CommandBehavior.SchemaOnly) != 0;
                var keyInfo = (behavior & CommandBehavior.KeyInfo) != 0;
                if (keyInfo && (CommandType != CommandType.Text || HasOutputParameters() || arrayCount != 0))
                    throw new NotSupportedException("KeyInfo requires text execution with input parameters only.");
                if (schemaOnly && (CommandType != CommandType.Text || !IsSchemaOnlyQuery(text) || HasOutputParameters()))
                    throw new NotSupportedException("SchemaOnly requires a text query with input parameters only.");
                if (token.IsCancellationRequested) { CancellationOrigin?.Invoke().ThrowIfCancellationRequested(); token.ThrowIfCancellationRequested(); }
                var connection = _connection ?? throw new InvalidOperationException("A connection is required.");
                connection.EnsureAmbientTransaction();
                var transaction = connection.ResolveCommandTransaction(_transaction);
                var session = connection.GetCommandSession(transaction, SessionOwner);
                var cursorOffset = !prepareOnly && !schemaOnly && UseCursorOffset;
                if (cursorOffset && (_fetchSize == 0 || session.Info!.Mode != OceanBaseCompatibilityMode.Oracle))
                    throw new NotSupportedException("Offset fetch requires Oracle mode and a positive FetchSize.");
                if (arrayCount != 0 && (!session.UseOraclePrepareExecute || session.Info!.Mode != OceanBaseCompatibilityMode.Oracle || transaction == null))
                    throw new NotSupportedException("Array binding requires Oracle mode, Use Oracle Prepare Execute=true and an explicit transaction.");
                if (RequiredSession != null && !ReferenceEquals(session, RequiredSession))
                    throw new InvalidOperationException("The connection's physical session changed before the operation started.");
                var length = ResultColumn.Utf8.GetByteCount(text);
                if (length >= PacketChannel.DefaultMaxPacketSize) throw new ArgumentException("Command text exceeds the packet size limit.");
                var oracle = session.UseOraclePrepareExecute && session.Info!.Mode == OceanBaseCompatibilityMode.Oracle &&
                    (arrayCount != 0 || CommandType == CommandType.StoredProcedure || HasOutputParameters()) ? OracleCommandPlan.Create(text, CommandType, _parameters, arrayCount) : null;
                var mysql = CommandType == CommandType.StoredProcedure && session.Info!.Mode == OceanBaseCompatibilityMode.MySql
                    ? MySqlCommandPlan.Create(text, _parameters, _mysqlVariablePrefix ??= "obc_" + Guid.NewGuid().ToString("N")) : null;
                if (mysql != null && (_fetchSize > 0 || !session.Info!.Capabilities.HasEffective(CapabilityFlags.ClientPsMultiResults)))
                    throw new NotSupportedException("MySQL stored procedures require PS multi-results support and do not support FetchSize.");
                var multiStatement = CommandType == CommandType.Text && SqlStatementBoundary.HasMultipleStatements(text, session.Info!.Mode);
                if (schemaOnly && multiStatement) throw new NotSupportedException("SchemaOnly requires a single statement.");
                if (keyInfo && multiStatement) throw new NotSupportedException("KeyInfo requires a single statement.");
                if (multiStatement && !session.Info!.Capabilities.HasEffective(CapabilityFlags.ClientMultiStatements))
                    throw new NotSupportedException("Multi-statement execution requires Allow Multi Statements=true and server support.");
#if NET8_0_OR_GREATER
                if (BatchCommand is OceanBaseBatchCommand batch)
                {
                    if (oracle != null) batch.BindOutputs(oracle.Outputs);
                    if (mysql != null) batch.BindOutputs(mysql.Outputs);
                }
#endif
                if (oracle == null && mysql == null && CommandType != CommandType.Text) throw new NotSupportedException("Oracle stored procedures require Use Oracle Prepare Execute=true.");
                if (oracle != null && cursorOffset) throw new NotSupportedException("Oracle output execution does not support offset fetch.");
                if (arrayCount != 0 && _fetchSize > 0) throw new NotSupportedException("Array binding does not support FetchSize.");
                var bindings = mysql?.Bindings ?? oracle?.Bindings ?? ParameterBindingPlan.Create(text, session.Info!.Mode, _parameters);
                if (multiStatement && (bindings.Parameters.Length != 0 || prepareOnly || _fetchSize > 0 || oracle != null))
                    throw new NotSupportedException("Multi-statement execution does not support parameters, Prepare or server cursors.");
                var clientFallback = AllowClientSidePrepareFallback && !prepareOnly && !schemaOnly && !keyInfo && !_explicitPreparation && _fetchSize == 0 && oracle == null && mysql == null;
                // Stream identity must not depend on a user stream's equality implementation.
                ConditionalWeakTable<Stream, OceanBaseParameter>? streamBindings = null;
                foreach (var parameter in bindings.Parameters)
                {
                    if (schemaOnly || !(parameter.Value is Stream source)) continue;
                    streamBindings ??= new ConditionalWeakTable<Stream, OceanBaseParameter>();
                    if (streamBindings.TryGetValue(source, out _)) throw new NotSupportedException("A Stream cannot be bound to multiple parameter positions.");
                    streamBindings.Add(source, parameter);
                }
                var streams = streamBindings != null;
                if (streams && clientFallback) throw new NotSupportedException("Stream parameters require server preparation without client fallback.");
                if (clientFallback || streams)
                {
                    var snapshots = new OceanBaseParameter[bindings.Parameters.Length];
                    for (var i = 0; i < snapshots.Length; i++) snapshots[i] = bindings.Parameters[i].Snapshot();
                    bindings = new ParameterBindingPlan(bindings.CommandText, snapshots, bindings.BindingPositions);
                }
                foreach (var parameter in bindings.Parameters)
                    if (!schemaOnly && (parameter.OceanBaseDbType == OceanBaseDbType.Blob || parameter.OceanBaseDbType == OceanBaseDbType.Clob || parameter.Value is OceanBaseLob) && !session.UseLobLocators)
                        throw new NotSupportedException("LOB parameters require negotiated Use Lob Locators=true.");
                // Internal PL/RETURNING output rows are not a fetch cursor.
                var fetchSize = prepareOnly || schemaOnly || oracle != null ? 0 : _fetchSize;
                if (prepareOnly) _explicitPreparation = true;
                var binary = keyInfo || schemaOnly || oracle != null || mysql != null || _explicitPreparation || bindings.Parameters.Length != 0 || fetchSize > 0 || _prepared != null && _prepared.CommandText == bindings.CommandText;
                var parameters = binary && !prepareOnly && !schemaOnly ? PreparedParameters.Encode(bindings.Parameters, session.Info!.Mode, validateOnly: true) : Array.Empty<byte>();
                if (arrayCount != 0) _ = OraclePrepareExecutePacket.BuildArray(0, bindings.CommandText, bindings.Parameters, validateOnly: true);
                if (_prepared != null && (!binary || !ReferenceEquals(_prepared.Session, session) ||
                    _prepared.Generation != session.Generation || _prepared.CacheEpoch != session.StatementCacheEpoch || _prepared.CommandText != bindings.CommandText))
                { _prepared.Session.ReturnPreparedStatement(_prepared); _prepared = null; }
                if (_prepared != null && !PreparedParameterCountMatches(_prepared, bindings.Parameters.Length, oracle)) throw ParameterCountMismatch();
                CommandExchange? execution = null;
                execution = connection.StartCommand(session, transaction, _timeout, token, () =>
                {
                    execution?.CursorColumns?.EndExecution();
                    lock (_gate)
                    {
                        if (ReferenceEquals(_execution, execution))
                        {
                            HasSentSql = execution!.HasSentSql;
                            if (!execution.Succeeded && _prepared != null) _prepared.Cacheable = false;
                            if (oracle?.ArrayBinding != null) ArrayBindingResult = oracle.ArrayBinding.Published ? oracle.ArrayBinding.Completed
                                : execution.HasSentSql ? oracle.ArrayBinding.Snapshot() : null;
                            _execution = null;
                        }
                    }
                }, SessionOwner, CancellationOrigin, RequiredTransactionEpoch, schemaOnly ? null : bindings.Parameters, validateComplexTypes: !prepareOnly && !schemaOnly);
#if NET8_0_OR_GREATER
                execution.BatchCommand = BatchCommand;
                execution.BatchFailed = BatchFailed;
#endif
                execution.DiscardSessionAfterExecution = !prepareOnly && !schemaOnly &&
                    (CommandType == CommandType.StoredProcedure || SessionStateSql.RequiresDiscard(text, session.Info!.Mode));
                execution.InvalidatePreparedStatements = session.StatementCachingEnabled && !prepareOnly && !schemaOnly &&
                    (CommandType != CommandType.Text || !SqlStatementBoundary.CanCacheStatement(text, session.Info!.Mode));
                execution.InvalidateComplexTypes = !prepareOnly && !schemaOnly && CommandType == CommandType.Text &&
                    SqlStatementBoundary.InvalidatesComplexTypes(text, session.Info!.Mode);
                _execution = execution;
                if (arrayCount != 0) ArrayBindingResult = null;
                if (!prepareOnly && !schemaOnly && session.Info!.Mode == OceanBaseCompatibilityMode.Oracle)
                    execution.CursorColumns = new OracleCursorOwner(connection, session, transaction, _timeout);
                if (oracle != null && !prepareOnly)
                {
                    oracle.Cursors = execution.CursorColumns!;
                    oracle.StatementCreated = oracle.Cursors.SetStatement;
                    _oracleSession = session;
                }
                return (execution, session, connection, bindings, parameters, binary, fetchSize, oracle, clientFallback, cursorOffset, mysql);
            }
        }

        private void TransferCursorColumns(PreparedStatement statement, CommandExchange execution)
        {
            if (!statement.HasCursorColumns) return;
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(OceanBaseCommand));
                var owner = execution.CursorColumns ?? throw new NotSupportedException("Cursor columns require Oracle mode.");
                owner.SetStatement(statement.Id);
                if (ReferenceEquals(_prepared, statement)) _prepared = null;
                _oracleSession = statement.Session;
            }
        }

        private static bool HasNationalParameters(OceanBaseParameter[] parameters)
        {
            foreach (var parameter in parameters)
                if (parameter.OceanBaseDbType == OceanBaseDbType.NChar || parameter.OceanBaseDbType == OceanBaseDbType.NVarchar2 ||
                    PreparedParameters.GetComplexType(parameter)?.Layout.HasNationalMembers == true) return true;
            return false;
        }

        private static bool HasStreamParameters(OceanBaseParameter[] parameters)
        {
            foreach (var parameter in parameters) if (parameter.Value is Stream) return true;
            return false;
        }

        private bool HasOutputParameters()
        {
            foreach (OceanBaseParameter parameter in _parameters) if (parameter.Direction != ParameterDirection.Input) return true;
            return false;
        }

        private PreparedStatement? EnsurePrepared((CommandExchange Exchange, PhysicalSession Session, OceanBaseConnection Connection, ParameterBindingPlan Bindings, byte[] Parameters, bool Binary, int FetchSize, OracleCommandPlan? Oracle, bool ClientFallback, bool CursorOffset, MySqlCommandPlan? MySql) operation, bool allowFallback = false, bool useCache = true)
        {
            var retained = GetPrepared(operation.Session, operation.Bindings.Parameters.Length, operation.Oracle);
            if (retained != null) return retained;
            useCache = useCache && operation.Session.StatementCachingEnabled && operation.Oracle == null &&
                SqlStatementBoundary.CanCacheStatement(operation.Bindings.CommandText, operation.Exchange.Mode);
            var cached = useCache ? operation.Session.TakePreparedStatement(operation.Bindings.CommandText) : null;
            if (cached != null) return PublishPrepared(cached, operation.Bindings.Parameters.Length);
            operation.Exchange.Send(SqlPacket(operation.Bindings.CommandText, 22), executesSql: false);
            var statement = PreparedStatement.Read(operation.Exchange, operation.Session, operation.Bindings.CommandText, allowFallback);
            if (statement != null) statement.Cacheable = useCache;
            return statement == null ? null : PublishPrepared(statement, operation.Bindings.Parameters.Length, operation.Oracle);
        }

        private async Task<PreparedStatement?> EnsurePreparedAsync((CommandExchange Exchange, PhysicalSession Session, OceanBaseConnection Connection, ParameterBindingPlan Bindings, byte[] Parameters, bool Binary, int FetchSize, OracleCommandPlan? Oracle, bool ClientFallback, bool CursorOffset, MySqlCommandPlan? MySql) operation, CancellationToken token, bool allowFallback = false, bool useCache = true)
        {
            var retained = GetPrepared(operation.Session, operation.Bindings.Parameters.Length, operation.Oracle);
            if (retained != null) return retained;
            useCache = useCache && operation.Session.StatementCachingEnabled && operation.Oracle == null &&
                SqlStatementBoundary.CanCacheStatement(operation.Bindings.CommandText, operation.Exchange.Mode);
            var cached = useCache ? operation.Session.TakePreparedStatement(operation.Bindings.CommandText) : null;
            if (cached != null) return PublishPrepared(cached, operation.Bindings.Parameters.Length);
            await operation.Exchange.SendAsync(SqlPacket(operation.Bindings.CommandText, 22), executesSql: false).ConfigureAwait(false);
            var statement = await PreparedStatement.ReadAsync(operation.Exchange, operation.Session, operation.Bindings.CommandText, token, allowFallback).ConfigureAwait(false);
            if (statement != null) statement.Cacheable = useCache;
            return statement == null ? null : PublishPrepared(statement, operation.Bindings.Parameters.Length, operation.Oracle);
        }

        private PreparedStatement PublishPrepared(PreparedStatement statement, int count, OracleCommandPlan? oracle = null)
        {
            lock (_gate)
            {
                if (_disposed || !PreparedParameterCountMatches(statement, count, oracle))
                {
                    statement.Release();
                    if (_disposed) throw new ObjectDisposedException(nameof(OceanBaseCommand));
                    throw ParameterCountMismatch();
                }
                var owner = oracle?.Cursors;
                if (owner == null) _prepared = statement;
                else
                {
                    if (ReferenceEquals(_prepared, statement)) _prepared = null;
                    owner.SetStatement(statement.Id);
                }
                return statement;
            }
        }

        private static bool PreparedParameterCountMatches(PreparedStatement statement, int count, OracleCommandPlan? oracle)
        {
            if (statement.ParameterCount == count) return true;
            // Oracle DML RETURNING describes pure OUT binds as result columns during COM_STMT_PREPARE.
            if (oracle == null || !SqlStatementBoundary.IsOracleDml(statement.CommandText) ||
                oracle.Outputs.Length == 0 || statement.ColumnCount != oracle.Outputs.Length ||
                statement.ParameterCount + statement.ColumnCount != count) return false;
            foreach (var output in oracle.Outputs) if (output.Direction != ParameterDirection.Output) return false;
            return true;
        }

        private PreparedStatement? GetPrepared(PhysicalSession session, int count, OracleCommandPlan? oracle)
        {
            lock (_gate)
            {
                if (_prepared != null && (_prepared.Database != session.Database ||
                    _prepared.Generation != session.Generation || _prepared.CacheEpoch != session.StatementCacheEpoch))
                { _prepared.Release(); _prepared = null; }
                return _prepared == null ? null : PublishPrepared(_prepared, count, oracle);
            }
        }

        private static void ValidateCursorStatement(int fetchSize, PreparedStatement? statement)
        {
            if (fetchSize > 0 && (statement == null || statement.ColumnCount == 0))
                throw new NotSupportedException("Server cursors require a prepared statement with result columns.");
        }

        private static int ResultFetchSize(int requested, bool cursorOffset, PreparedStatement? statement)
        {
            if (statement?.HasCursorColumns != true) return requested;
            if (cursorOffset) throw new NotSupportedException("Offset fetch is not supported for cursor-valued result columns.");
            return 0;
        }

        internal static byte[] SqlPacket(string text, byte code)
        {
            var bytes = ResultColumn.Utf8.GetBytes(text);
            var packet = new byte[bytes.Length + 1];
            packet[0] = code;
            Buffer.BlockCopy(bytes, 0, packet, 1, bytes.Length);
            return packet;
        }

        private static bool IsSchemaOnlyQuery(string text)
        {
            var position = 0;
            while (true)
            {
                while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
                if (position + 1 < text.Length && text[position] == '-' && text[position + 1] == '-')
                {
                    position += 2;
                    while (position < text.Length && text[position] != '\r' && text[position] != '\n') position++;
                    continue;
                }
                if (position + 1 < text.Length && text[position] == '/' && text[position + 1] == '*')
                {
                    var end = text.IndexOf("*/", position + 2, StringComparison.Ordinal);
                    if (end < 0) return false;
                    position = end + 2;
                    continue;
                }
                break;
            }
            return StartsWithKeyword(text, position, "SELECT") || StartsWithKeyword(text, position, "SHOW") ||
                StartsWithKeyword(text, position, "DESCRIBE") || StartsWithKeyword(text, position, "DESC") ||
                StartsWithKeyword(text, position, "EXPLAIN") || StartsWithKeyword(text, position, "VALUES");
        }

        private static bool StartsWithKeyword(string text, int position, string keyword) =>
            position + keyword.Length <= text.Length &&
            string.Compare(text, position, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) == 0 &&
            (position + keyword.Length == text.Length || !IsIdentifierPart(text[position + keyword.Length]));

        private static bool IsIdentifierPart(char value) => char.IsLetterOrDigit(value) || value == '_' || value == '$' || value == '#';

        private static bool IsDashCommentStart(string text, int position) =>
            position + 1 < text.Length && text[position] == '-' && text[position + 1] == '-' &&
            (position + 2 == text.Length || char.IsWhiteSpace(text[position + 2]));

        private PreparedStatement? DetachStatement()
        {
            lock (_gate)
            {
                _disposed = true;
                _execution?.Cancel();
                _derivation?.Cancel();
                var statement = _prepared;
                _prepared = null;
                if (statement != null)
                {
                    if (_execution != null) statement.Cacheable = false;
                    statement.Session.ReturnPreparedStatement(statement);
                }
                return statement;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                var statement = DetachStatement();
                if (statement != null) PreparedStatement.CloseIdle(statement.Session, _timeout);
                if (_oracleSession != null && !ReferenceEquals(_oracleSession, statement?.Session)) PreparedStatement.CloseIdle(_oracleSession, _timeout);
            }
            base.Dispose(disposing);
        }
        private static ArgumentException ParameterCountMismatch() => new ArgumentException("The server's prepared parameter count does not match the supplied bindings.");
    }
}
