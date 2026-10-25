using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using OceanBaseConnector.Internal.Protocol;
using OceanBaseConnector.Internal.Session;
using OceanBaseConnector.Internal.Values;

namespace OceanBaseConnector
{
    public sealed partial class OceanBaseConnection
    {
        // Published dictionaries are immutable so active/completed readers can retain their exact snapshot.
        private Dictionary<(string Schema, string Name), OceanBaseComplexType> _complexTypes = new Dictionary<(string, string), OceanBaseComplexType>();
        private ComplexTypeContext? _complexTypeContext;
        private int _complexTypeMembers, _complexTypeDictionaryBytes;
        private static readonly string[] ComplexHeaderColumns = { "TYPE_SCHEMA", "TYPE_NAME", "TYPE_KIND", "MEMBER_COUNT" };
        private static readonly string[] ComplexMemberColumns = { "MEMBER_NAME", "MEMBER_POSITION", "MEMBER_SCHEMA", "MEMBER_TYPE", "MEMBER_KIND" };

        /// <summary>Loads a complete Oracle type using exact dictionary schema/name and the explicitly bound transaction.</summary>
        public OceanBaseComplexType GetComplexType(string schemaName, string typeName, OceanBaseTransaction? transaction = null)
            => GetComplexTypeCore(schemaName, typeName, transaction, false, CancellationToken.None).GetAwaiter().GetResult();
        /// <summary>Loads a type on this physical session using asynchronous dictionary queries.</summary>
        public Task<OceanBaseComplexType> GetComplexTypeAsync(string schemaName, string typeName, OceanBaseTransaction? transaction = null, CancellationToken cancellationToken = default)
            => GetComplexTypeCore(schemaName, typeName, transaction, true, cancellationToken);

        /// <summary>Creates a session-bound anonymous array of NUMBER, VARCHAR2, CHAR, RAW, DATE or TIMESTAMP.</summary>
        public OceanBaseComplexType CreateArrayType(string elementTypeName, OceanBaseTransaction? transaction = null)
        {
            if (elementTypeName == null) throw new ArgumentNullException(nameof(elementTypeName));
            var name = elementTypeName.ToUpperInvariant(); byte code;
            switch (name)
            {
                case "NUMBER": code = 0; break;
                case "VARCHAR2": code = 253; break;
                case "CHAR": code = 254; break;
                case "RAW": code = 203; break;
                case "DATE": code = 12; break;
                case "TIMESTAMP": code = 202; break;
                default: throw new NotSupportedException("The anonymous array element type is not supported.");
            }
            EnsureAmbientTransaction();
            lock (_gate)
            {
                var context = EnsureComplexTypeContext(RequireComplexSession(transaction), transaction);
                return new OceanBaseComplexType(context, "", "", true,
                    new[] { new OceanBaseComplexMember(null, null, name, null, new ComplexType(code)) });
            }
        }

        /// <summary>Creates a session-bound anonymous array whose elements use an already loaded named type.</summary>
        public OceanBaseComplexType CreateArrayType(OceanBaseComplexType elementType, OceanBaseTransaction? transaction = null)
        {
            if (elementType == null) throw new ArgumentNullException(nameof(elementType));
            if (elementType.IsAnonymous) throw new NotSupportedException("An anonymous element has no named type identity.");
            EnsureAmbientTransaction();
            lock (_gate)
            {
                var session = RequireComplexSession(transaction); ValidateComplexBinding(elementType, session, transaction);
                if (elementType.NestingDepth >= 32) throw new NotSupportedException("The array would exceed the supported nesting depth.");
                return new OceanBaseComplexType(elementType.Context, "", "", true,
                    new[] { new OceanBaseComplexMember(null, elementType.SchemaName, elementType.TypeName, elementType, elementType.Layout) });
            }
        }

        /// <summary>Invalidates all cached types and their values. Call after external type DDL, then reload.</summary>
        public void ClearComplexTypeCache(OceanBaseTransaction? transaction = null)
        {
            EnsureAmbientTransaction();
            lock (_gate)
            {
                RequireComplexSession(transaction); _complexTypes = new Dictionary<(string, string), OceanBaseComplexType>(); _complexTypeContext = null;
                _complexTypeMembers = _complexTypeDictionaryBytes = 0;
            }
        }

        private PhysicalSession RequireComplexSession(OceanBaseTransaction? transaction, object? owner = null)
        {
            var session = GetCommandSession(transaction, owner);
            if (session.Info!.Mode != OceanBaseCompatibilityMode.Oracle) throw new NotSupportedException("Complex types require Oracle mode.");
            return session;
        }

        internal void ValidateComplexType(OceanBaseComplexType type)
        {
            EnsureAmbientTransaction();
            lock (_gate)
            {
                var context = type.Context ?? throw new NotSupportedException("An anonymous result descriptor has no dictionary session.");
                var session = GetCommandSession(context.Transaction);
                ValidateComplexBinding(type, session, context.Transaction);
            }
        }

        private void ValidateComplexBinding(OceanBaseComplexType type, PhysicalSession session, OceanBaseTransaction? transaction)
        {
            var context = type.Context;
            if (context == null || !ReferenceEquals(context.Connection, this) || !ReferenceEquals(context, _complexTypeContext) ||
                !ReferenceEquals(session, context.Session) || !ReferenceEquals(context.Transaction, transaction) || context.Epoch != _transactionEpoch || context.MetadataEpoch != session.ComplexTypeEpoch ||
                !type.IsAnonymous && (!_complexTypes.TryGetValue((type.SchemaName, type.TypeName), out var cached) || !ReferenceEquals(cached, type)))
                throw new InvalidOperationException("The complex type's session, transaction or cache generation has changed.");
        }

        private ComplexTypeContext EnsureComplexTypeContext(PhysicalSession session, OceanBaseTransaction? transaction)
        {
            if (_complexTypeContext == null || !ReferenceEquals(_complexTypeContext.Session, session) ||
                !ReferenceEquals(_complexTypeContext.Transaction, transaction) || _complexTypeContext.Epoch != _transactionEpoch || _complexTypeContext.MetadataEpoch != session.ComplexTypeEpoch)
            {
                _complexTypes = new Dictionary<(string, string), OceanBaseComplexType>();
                _complexTypeContext = new ComplexTypeContext(this, session, transaction, _transactionEpoch, _builder.CommandTimeout);
                _complexTypeMembers = _complexTypeDictionaryBytes = 0;
            }
            return _complexTypeContext;
        }

        internal Task<OceanBaseComplexType> GetDerivedComplexType(string schemaName, string typeName, OceanBaseCommand command, bool asynchronous, CancellationToken token)
            => GetComplexTypeCore(schemaName, typeName, (OceanBaseTransaction?)command.Transaction, asynchronous, token,
                command.SessionOwner, command.RequiredSession, command.RequiredTransactionEpoch, command.CommandTimeout, command.CancellationOrigin);

        private async Task<OceanBaseComplexType> GetComplexTypeCore(string schemaName, string typeName, OceanBaseTransaction? transaction, bool async, CancellationToken token, object? reservationOwner = null, PhysicalSession? requiredSession = null, long? requiredEpoch = null, int? timeout = null, Func<CancellationToken>? cancellationOrigin = null)
        {
            ValidateComplexName(schemaName, nameof(schemaName)); ValidateComplexName(typeName, nameof(typeName));
            EnsureAmbientTransaction(); token.ThrowIfCancellationRequested();
            ComplexTypeContext context; var owner = reservationOwner ?? new object(); int members, dictionaryBytes;
            lock (_gate)
            {
                var session = RequireComplexSession(transaction, reservationOwner);
                if (requiredSession != null && !ReferenceEquals(session, requiredSession) || requiredEpoch.HasValue && requiredEpoch != _transactionEpoch)
                    throw new InvalidOperationException("The type loading session or transaction changed.");
                context = EnsureComplexTypeContext(session, transaction);
                if (_complexTypes.TryGetValue((schemaName, typeName), out var cached)) return cached;
                members = _complexTypeMembers; dictionaryBytes = _complexTypeDictionaryBytes;
                if (reservationOwner == null) session.Reserve(owner);
            }
            var command = new OceanBaseCommand
            {
                Connection = this,
                Transaction = transaction,
                CommandTimeout = timeout ?? context.Timeout,
                RequiredSession = context.Session,
                RequiredTransactionEpoch = context.Epoch,
                SessionOwner = owner,
                CancellationOrigin = cancellationOrigin
            };
            try
            {
                var loader = new ComplexTypeLoader(this, context, command, async, token, members, dictionaryBytes);
                var result = await loader.Load(schemaName, typeName, 0).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    var session = GetCommandSession(transaction, owner);
                    if (!ReferenceEquals(session, context.Session) || context.Epoch != _transactionEpoch || context.MetadataEpoch != session.ComplexTypeEpoch || !ReferenceEquals(_complexTypeContext, context))
                        throw new InvalidOperationException("The complex type dictionary context changed while loading.");
                    if (loader.Types.Count > ComplexValueCodec.MaximumMembers - _complexTypes.Count) throw new NotSupportedException("The type cache exceeds its supported size.");
                    var published = new Dictionary<(string, string), OceanBaseComplexType>(_complexTypes);
                    foreach (var entry in loader.Types) published.Add(entry.Key, entry.Value);
                    _complexTypes = published;
                    _complexTypeMembers = loader.MemberCount; _complexTypeDictionaryBytes = loader.DictionaryBytes;
                }
                return result;
            }
            finally
            {
                if (reservationOwner == null) context.Session.ReleaseReservation(owner);
                if (async) await command.DisposeCoreAsync().ConfigureAwait(false); else command.Dispose();
            }
        }

        private static void ValidateComplexName(string name, string parameter)
        {
            if (name == null) throw new ArgumentNullException(parameter);
            if (name.Length == 0 || name.Length > 512 || name.IndexOf('\0') >= 0 || ResultColumn.Utf8.GetByteCount(name) > 512)
                throw new ArgumentException("A dictionary identifier must be non-empty, valid Unicode, contain no NUL and fit 512 UTF-8 bytes.", parameter);
        }

        private sealed class ComplexTypeLoader
        {
            private readonly OceanBaseConnection _connection;
            private readonly ComplexTypeContext _context;
            private readonly OceanBaseCommand _command;
            private readonly bool _async;
            private readonly CancellationToken _token;
            private readonly HashSet<(string, string)> _loading = new HashSet<(string, string)>();
            private int _members;
            private int _dictionaryBytes;
            internal int MemberCount => _members;
            internal int DictionaryBytes => _dictionaryBytes;
            internal readonly Dictionary<(string Schema, string Name), OceanBaseComplexType> Types = new Dictionary<(string, string), OceanBaseComplexType>();
            internal ComplexTypeLoader(OceanBaseConnection connection, ComplexTypeContext context, OceanBaseCommand command, bool async, CancellationToken token, int members, int dictionaryBytes)
            { _connection = connection; _context = context; _command = command; _async = async; _token = token; _members = members; _dictionaryBytes = dictionaryBytes; }

            internal async Task<OceanBaseComplexType> Load(string schema, string name, int depth)
            {
                _token.ThrowIfCancellationRequested();
                if (depth >= 32) throw InvalidDictionary();
                var key = (schema, name);
                OceanBaseComplexType? cached;
                lock (_connection._gate)
                {
                    if (!ReferenceEquals(_connection._complexTypeContext, _context)) throw new InvalidOperationException("The type cache changed while loading.");
                    if (!Types.TryGetValue(key, out cached)) _connection._complexTypes.TryGetValue(key, out cached);
                }
                if (cached != null)
                { if (depth + cached.NestingDepth > 32) throw InvalidDictionary(); return cached; }
                if (!_loading.Add(key)) throw new NotSupportedException("Recursive complex type definitions are not supported.");
                var header = await Query(schema, name, null, 1).ConfigureAwait(false);
                if (header.Count != 1 || Text(header[0][0]) != schema || Text(header[0][1]) != name) throw InvalidDictionary();
                var kind = Text(header[0][2]); var collection = kind == "COLLECTION";
                if (!collection && kind != "OBJECT") throw InvalidDictionary();
                var count = collection ? 1 : Count(header[0][3]);
                if (count > ComplexValueCodec.MaximumMembers - _members) throw InvalidDictionary();
                _members += count;
                var rows = await Query(schema, name, collection, count).ConfigureAwait(false);
                if (rows.Count != count) throw InvalidDictionary();
                var members = new OceanBaseComplexMember[count]; var names = new HashSet<string>(StringComparer.Ordinal);
                for (var i = 0; i < count; i++)
                {
                    var row = rows[i]; if (Count(row[1]) != i + 1) throw InvalidDictionary();
                    var memberName = OptionalText(row[0]);
                    if (collection ? memberName != null : memberName == null || !names.Add(memberName)) throw InvalidDictionary();
                    if (memberName != null) ValidateComplexName(memberName, "memberName");
                    var childSchema = OptionalText(row[2]); var childName = Text(row[3]); var childKind = Text(row[4]);
                    ValidateComplexName(childName, "memberType"); if (childSchema != null) ValidateComplexName(childSchema, "memberSchema");
                    OceanBaseComplexType? complex = null; ComplexType layout;
                    if (childKind == "OBJECT" || childKind == "COLLECTION")
                    {
                        if (childSchema == null) throw InvalidDictionary();
                        complex = await Load(childSchema, childName, depth + 1).ConfigureAwait(false);
                        if (complex.IsCollection != (childKind == "COLLECTION")) throw InvalidDictionary(); layout = complex.Layout;
                    }
                    else
                    {
                        if (childKind != childName || childSchema != null && childSchema != "SYS") throw InvalidDictionary();
                        byte code;
                        switch (childKind)
                        {
                            case "NUMBER": code = 0; break;
                            case "VARCHAR2": case "VARCHAR": code = 253; break;
                            case "CHAR": code = 254; break;
                            case "RAW": code = 203; break;
                            case "DATE": code = 12; break;
                            case "TIMESTAMP": code = 202; break;
                            default: throw new NotSupportedException("The complex dictionary member type is not supported.");
                        }
                        layout = new ComplexType(code);
                    }
                    members[i] = new OceanBaseComplexMember(memberName, childSchema, childName, complex, layout);
                }
                var type = new OceanBaseComplexType(_context, schema, name, collection, members);
                if (depth + type.NestingDepth > 32) throw InvalidDictionary();
                Types.Add(key, type); _loading.Remove(key); return type;
            }

            private async Task<List<object[]>> Query(string schema, string name, bool? collection, int maximum)
            {
                _command.CommandText = collection == null
                    ? "SELECT OWNER AS TYPE_SCHEMA, TYPE_NAME, TYPECODE AS TYPE_KIND, ATTRIBUTES AS MEMBER_COUNT FROM ALL_TYPES WHERE OWNER = :owner AND TYPE_NAME = :type_name"
                    : collection.Value
                        ? "SELECT NULL AS MEMBER_NAME, 1 AS MEMBER_POSITION, B.ELEM_TYPE_OWNER AS MEMBER_SCHEMA, B.ELEM_TYPE_NAME AS MEMBER_TYPE, NVL(A.TYPECODE, B.ELEM_TYPE_NAME) AS MEMBER_KIND FROM ALL_COLL_TYPES B LEFT JOIN ALL_TYPES A ON A.OWNER = B.ELEM_TYPE_OWNER AND A.TYPE_NAME = B.ELEM_TYPE_NAME WHERE B.OWNER = :owner AND B.TYPE_NAME = :type_name"
                        : "SELECT B.ATTR_NAME AS MEMBER_NAME, B.ATTR_NO AS MEMBER_POSITION, B.ATTR_TYPE_OWNER AS MEMBER_SCHEMA, B.ATTR_TYPE_NAME AS MEMBER_TYPE, NVL(A.TYPECODE, B.ATTR_TYPE_NAME) AS MEMBER_KIND FROM ALL_TYPE_ATTRS B LEFT JOIN ALL_TYPES A ON A.OWNER = B.ATTR_TYPE_OWNER AND A.TYPE_NAME = B.ATTR_TYPE_NAME WHERE B.OWNER = :owner AND B.TYPE_NAME = :type_name ORDER BY B.ATTR_NO";
                _command.Parameters.Clear();
                _command.Parameters.Add(new OceanBaseParameter { ParameterName = "owner", Value = schema });
                _command.Parameters.Add(new OceanBaseParameter { ParameterName = "type_name", Value = name });
                using var reader = _async ? await _command.ExecuteReaderAsync(_token).ConfigureAwait(false) : _command.ExecuteReaderCore(CommandBehavior.Default, _token);
                var columns = collection == null ? ComplexHeaderColumns : ComplexMemberColumns;
                if (reader.FieldCount != columns.Length) throw InvalidDictionary();
                for (var i = 0; i < columns.Length; i++) if (!string.Equals(reader.GetName(i), columns[i], StringComparison.OrdinalIgnoreCase)) throw InvalidDictionary();
                var rows = new List<object[]>();
                while (_async ? await reader.ReadAsync(_token).ConfigureAwait(false) : reader.Read())
                {
                    if (rows.Count >= maximum) throw InvalidDictionary();
                    var row = new object[columns.Length]; reader.GetValues(row);
                    foreach (var value in row)
                    {
                        if (!(value is string text)) continue;
                        if (text.Length > 512) throw InvalidDictionary();
                        var bytes = ResultColumn.Utf8.GetByteCount(text);
                        if (bytes > 512 || bytes > 4 * 1024 * 1024 - _dictionaryBytes) throw InvalidDictionary();
                        _dictionaryBytes += bytes;
                    }
                    rows.Add(row);
                }
                if (_async ? await reader.NextResultAsync(_token).ConfigureAwait(false) : reader.NextResult()) throw InvalidDictionary();
                return rows;
            }

            private static string Text(object value) => value is string text && text.Length != 0 ? text : throw InvalidDictionary();
            private static string? OptionalText(object value) => value == DBNull.Value ? null : Text(value);
            private static int Count(object value)
            {
                try
                {
                    var number = value is OceanBaseDecimal exact ? exact.ToDecimal() : value is string text ? DecimalCodec.Parse(text).ToDecimal()
                        : value is decimal || value is sbyte || value is byte || value is short || value is ushort || value is int || value is uint || value is long || value is ulong
                            ? Convert.ToDecimal(value, CultureInfo.InvariantCulture) : throw InvalidDictionary();
                    if (number < 0 || number > ComplexValueCodec.MaximumMembers || decimal.Truncate(number) != number) throw InvalidDictionary();
                    return (int)number;
                }
                catch (Exception error) when (error is FormatException || error is OverflowException) { throw InvalidDictionary(); }
            }
            private static NotSupportedException InvalidDictionary() => new NotSupportedException("The complex type dictionary layout is missing, inconsistent or exceeds its supported bounds.");
        }
    }
}
