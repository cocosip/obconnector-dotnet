# Schema catalog API

`OceanBaseConnection.GetSchema()` returns `MetaDataCollections`. Collections are available on all five assets; net8.0/net9.0/net10.0 also override `GetSchemaAsync`. An open, idle connection is required even for static metadata. Calls use the current local transaction and the connection command timeout; asynchronous calls honor cancellation. An active reader/XA/ambient restriction is enforced by the ordinary connection and command lifecycle.

## Collections and restrictions

Oracle mode advertises 20 collections; MySQL mode advertises 18, excluding Types and Synonyms. Collection names are case-insensitive. Query `Restrictions` for `CollectionName`, `RestrictionName`, `RestrictionDefault` (DBNull, meaning unrestricted) and the one-based `RestrictionNumber`. `MetaDataCollections` reports each collection's restriction count and identifier parts.

| Collection | Ordered restrictions | Result and mode boundary |
| --- | --- | --- |
| MetaDataCollections | None | CollectionName, NumberOfRestrictions and NumberOfIdentifierParts; no query |
| DataSourceInformation | None | Product/version and existing provider information; no query |
| DataTypes | None | Mode-aware CLR/provider mappings, precision/scale, creation templates and conditional codec support; no query |
| Restrictions | None | Restriction descriptions; no query |
| Tables | Catalog, Schema, Table, TableType | TABLE_CATALOG, TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE; includes tables and views |
| Columns | Catalog, Schema, Table, Column | Existing column type, ordinal, default, nullable and precision metadata |
| Procedures | Catalog, Schema, Routine, RoutineType, Package | Existing routine/package/overload/subprogram identity |
| ProcedureParameters | Catalog, Schema, Routine, Parameter, Package | Existing top-level parameter direction, position, type identity and precision metadata |
| Views | Catalog, Schema, View | Same four identity/type columns as Tables, with TABLE_TYPE=VIEW; does not retrieve view definition SQL |
| Indexes | Catalog, Schema, Table, Index | TABLE_CATALOG, TABLE_SCHEMA, TABLE_NAME, INDEX_SCHEMA, INDEX_NAME, INDEX_TYPE, IS_UNIQUE |
| IndexColumns | Catalog, Schema, Table, Index, Column | First five Indexes identity columns, COLUMN_NAME, ORDINAL_POSITION, SORT_ORDER, PREFIX_LENGTH |
| Keys | Catalog, Schema, Table, Constraint, ConstraintType | TABLE_CATALOG, TABLE_SCHEMA, TABLE_NAME, CONSTRAINT_SCHEMA, CONSTRAINT_NAME, CONSTRAINT_TYPE; PRIMARY KEY and UNIQUE constraints |
| Constraints | Catalog, Schema, Table, Constraint, ConstraintType | Same columns as Keys, including FOREIGN KEY and CHECK when reported by the dictionary |
| ConstraintColumns | Catalog, Schema, Table, Constraint, Column | Table and constraint identity, COLUMN_NAME and ORDINAL_POSITION; column availability follows server dictionary |
| ForeignKeys | Catalog, Schema, Table, Constraint | Table/constraint identity, REFERENCED_TABLE_CATALOG, REFERENCED_TABLE_SCHEMA, REFERENCED_TABLE_NAME, REFERENCED_CONSTRAINT_SCHEMA, REFERENCED_CONSTRAINT_NAME, UPDATE_RULE, DELETE_RULE |
| ForeignKeyColumns | Catalog, Schema, Table, Constraint, Column | Table/constraint identity, COLUMN_NAME, ORDINAL_POSITION, REFERENCED_TABLE_CATALOG, REFERENCED_TABLE_SCHEMA, REFERENCED_TABLE_NAME, REFERENCED_COLUMN_NAME |
| Types | Catalog, Schema, Type, TypeCode | Oracle only: TYPE_CATALOG, TYPE_SCHEMA, TYPE_NAME, TYPE_CODE, ATTRIBUTE_COUNT, METHOD_COUNT, SUPER_TYPE_SCHEMA, SUPER_TYPE_NAME |
| Sequences | Catalog, Schema, Sequence | SEQUENCE_CATALOG, SEQUENCE_SCHEMA, SEQUENCE_NAME, MINIMUM_VALUE, MAXIMUM_VALUE, INCREMENT, CACHE_SIZE, LAST_NUMBER, CYCLE_FLAG, ORDER_FLAG |
| Users | User, Host | USER_NAME, HOST; Host identifies MySQL accounts and is DBNull for Oracle |
| Synonyms | Catalog, Schema, Synonym | Oracle only: SYNONYM_CATALOG, SYNONYM_SCHEMA, SYNONYM_NAME, OBJECT_CATALOG, OBJECT_SCHEMA, OBJECT_NAME, DB_LINK |

Types describes server named object/collection types; DataTypes separately describes built-in CLR/provider mappings and supported complex shapes. Use the separate command parameter-derivation API below to build command parameters from routine metadata.

## Parameter derivation

OceanBaseCommand.DeriveParameters(string? overload = null), DeriveParametersAsync(CancellationToken cancellationToken = default), and DeriveParametersAsync(string? overload, CancellationToken cancellationToken = default) are available on all five assets. See the [API contract](obconnector-dotnet-architecture-design.md#routine-parameter-derivation) for qualified names, Oracle overload selection, cancellation and publication rules.

Derivation requires CommandType.StoredProcedure and an open idle connection, resolves a unique visible routine signature and reads its arguments without executing the target. It uses the current transaction, one reserved session and the parent command's per-query timeout and cancellation. Success replaces existing parameters with fresh DBNull-valued parameters; failure does not publish a partial signature. Collection membership changes are checked atomically; arbitrary concurrent edits of parameter properties are not covered. Public GetSchema result shapes are unchanged. Derivation itself does not add procedure execution or remove existing Oracle PrepareExecute/type restrictions. MySQL execution has a separate [procedure contract](obconnector-dotnet-architecture-design.md#mysql-stored-procedures).

| Metadata | Derived representation and limits |
| --- | --- |
| Direction/order/names | IN/OUT/INOUT and function RETURN map to ParameterDirection; contiguous ordinals are required. Return name is RETURN_VALUE. Oracle names must fit current ASCII/unquoted binding rules; case-insensitive duplicates and return-name conflicts fail. MySQL names retain Unicode. |
| MySQL integers | Signed/unsigned provider types follow DTD_IDENTIFIER; ZEROFILL implies unsigned. TINYINT(1) follows the session TreatTinyAsBoolean policy. Fractional dimensions, inconsistent or unknown declarations fail. |
| MySQL DECIMAL/NUMERIC | Precision <=65 and scale 0..30, consistent with DTD and precision. Unsigned decimal has no supported declaration representation and fails. |
| MySQL FLOAT/DOUBLE/REAL, YEAR, BIT | Plain floating types map to Single/Double; unsigned and dimensioned floating declarations fail because the current API/codec cannot preserve their constraints. YEAR accepts the ordinary declaration or (4); other dimensions/attributes fail. BIT(1..64) retains its width in Size. |
| MySQL strings/binary/temporal | Character lengths use character count; binary lengths use octets and must fit Size. JSON, DATE, DATETIME/TIMESTAMP and TIME use existing provider types; fractional seconds are limited to 6. |
| Oracle numeric/temporal | NUMBER/DECIMAL/NUMERIC/INTEGER use Decimal and nullable precision/DecimalScale; NUMBER retains scale -84..127. BINARY_FLOAT/DOUBLE, PLS_INTEGER/BINARY_INTEGER, DATE, TIMESTAMP/TZ/LTZ and intervals use existing mappings and precision limits. Unsupported variants fail. |
| Oracle FLOAT/NUMBER_FLOAT | Plain directory names accept binary precision1..126 or NULL/-1, and variable scale NULL/-85/-1. Derived Decimal parameters retain Precision0 and undeclared scale, binding through NUMBER246; server FLOAT declarations apply coercion/rounding. Dimensioned DATA_TYPE strings and other precision/fixed-scale shapes fail. |
| Oracle text/binary/LOB/cursor | VARCHAR2/VARCHAR/CHAR, NCHAR/NVARCHAR2, RAW, BLOB/CLOB and output/return REF CURSOR map to existing provider types. Character length and binary octet length remain distinct. Input/InputOutput cursor remains unsupported. |
| Oracle named types | OBJECT/TABLE/VARRAY load the existing descriptor on the same reserved session/transaction. Incomplete TYPE_OWNER/TYPE_NAME pairs, package-local types and existing unsupported complex members fail. |

NULL DATA_TYPE rows are retained for validation; they cannot silently become an empty signature. Missing or duplicate argument positions, inconsistent identity and unsupported attributes fail with original parameters retained. Permission errors propagate with their server code/SQL state. This uses INFORMATION_SCHEMA.ROUTINES/PARAMETERS or ALL_PROCEDURES/ALL_ARGUMENTS plus existing named-type queries. Local protocol and consumer evidence does not establish actual directory privileges, server parsing, version differences or OBProxy compatibility.

Oracle IN/OUT and INOUT normalize to INOUT. Public ProcedureParameters retains raw
FLOAT/NUMBER_FLOAT names and numeric metadata. Derivation first omits CHAR_LENGTH;
only the five Oracle character families query it separately, requiring an otherwise
identical signature. Character Size never falls back to DATA_LENGTH bytes. Servers
without CHAR_LENGTH can therefore derive numeric signatures, while character
signatures still fail with the original server error and preserve existing parameters.

## DataTypes and CLR mappings

`GetSchema("DataTypes")` is static provider metadata for the authenticated compatibility mode. It uses no directory query, takes no restrictions, and returns a fresh invariant-culture table. It retains the normal open/idle, ambient/XA and cancellation guards. It advertises codec-supported canonical declarations and representation variants, not every SQL alias, character set or server-version feature. DBNull represents a database NULL value; it is not a separate SQL column declaration in this collection.

The standard columns are TypeName, ProviderDbType, ColumnSize, CreateFormat, CreateParameters, DataType, IsAutoIncrementable, IsBestMatch, IsCaseSensitive, IsFixedLength, IsFixedPrecisionScale, IsLong, IsNullable, IsSearchable, IsSearchableWithLike, IsUnsigned, MaximumScale, MinimumScale, IsConcurrencyType, IsLiteralSupported, LiteralPrefix, LiteralSuffix and NativeDataType. Additional columns make conditional and provider-specific support explicit:

| Columns | CLR column type and meaning |
| --- | --- |
| TypeName, CreateFormat, CreateParameters, DataType, LiteralPrefix, LiteralSuffix, NativeDataType | String. DataType is the CLR full name, not a Type object. CreateFormat is an invariant composite-format declaration template; CreateParameters names its ordered arguments. Missing templates/literals are DBNull. |
| ProviderDbType | Int32 corresponding to the existing OceanBaseDbType enum; it can be assigned to OceanBaseParameter.OceanBaseDbType. This is not a wire type code or System.Data.DbType value. |
| ColumnSize | Int64: maximum decimal digit precision for numeric rows, bit width for BIT, four digits for YEAR and one Boolean digit for TINYINT(1). Unestablished server string/binary/LOB/temporal lengths stay DBNull. |
| MinimumScale, MaximumScale | Int16. Decimal scale or temporal/interval fractional precision; integer/YEAR scale is zero. Bounds do not remove the precision/scale relationship in ResultTypeCondition. |
| MinimumPrecision, MaximumPrecision | Int32. Decimal precision bounds, fixed numeric digit capacity, or Oracle interval leading precision. They are not integer display-width arguments or varchar lengths. |
| ProviderSpecificDataType | String CLR full name for provider-specific reader values. This remains OceanBaseDecimal for both decimal representations, OceanBaseTimestamp for both Oracle TIMESTAMP representations, and OceanBaseDate/OceanBaseDateTime for MySQL date families. |
| IsSupportedForParameters, IsSupportedForResults, RequiresTypeName | Boolean. Parameter support remains subject to ordinary value, direction, mode and session validation; a true flag does not promise every execution shape. |
| RequiredFeatures, ParameterDirections, ResultTypeCondition | String. Session/negotiation/encoding/layout requirements, a restricted parameter-direction set when applicable, and conditions selecting the normal reader CLR representation. Unneeded conditions are DBNull. |
| All Is* columns | Boolean or DBNull when unknown. IsNullable describes the type, not a particular column constraint. Searchability, collation case rules, fixed precision/scale, native identifiers and most auto-increment policy are deliberately not inferred. IsBestMatch is a CLR mapping preference, not a server capability guarantee. |

Canonical MySQL declarations include signed/unsigned TINYINT, SMALLINT, MEDIUMINT, INT and BIGINT; DECIMAL and DECIMAL UNSIGNED; FLOAT/DOUBLE; CHAR/VARCHAR/TEXT; BINARY/VARBINARY/BLOB; TINYTEXT/MEDIUMTEXT/LONGTEXT and TINYBLOB/MEDIUMBLOB/LONGBLOB; DATE/DATETIME/TIMESTAMP/TIME; YEAR, BIT and JSON. TINYINT(1) has a Boolean mapping row only when TreatTinyAsBoolean is enabled. Unsigned/narrow/alias variants do not replace the preferred signed/general CLR mapping. MySQL BLOB uses byte[]/OceanBaseDbType.Binary, while Oracle BLOB uses a session-bound OceanBaseLob/OceanBaseDbType.Blob.

Oracle declarations include NUMBER, BINARY_FLOAT/BINARY_DOUBLE, DATE, TIMESTAMP and its timezone variants, CHAR/VARCHAR2/NCHAR/NVARCHAR2, RAW, BLOB/CLOB, both interval families, OBJECT, ARRAY and REF CURSOR. OBJECT/ARRAY rows describe supported complex codecs: obtain an OceanBaseComplexType descriptor before binding, and use the supported binary protocol/member layouts. OBJECT needs a named type; ARRAY can use a named collection or the existing supported anonymous scalar-array descriptor. They do not provide generic CREATE TYPE DDL.

MySQL also lists ENUM/SET using the existing String provider identity and label-list creation
templates `ENUM({0})`/`SET({0})`; the argument is SQL-quoted labels, not a length. Reported result
codes247/248 retain binary-collation bytes or strict text without parsing label lists. Accepting these
wire codes in either mode does not add Oracle SQL declarations. MySQL GEOMETRY result code255 returns
opaque bytes regardless of collation; it does not add a GEOMETRY inventory row, spatial parameter
type or SQL-support claim. These result-code extensions have local evidence only.

Oracle ROWID result code209 likewise has local result-reading support using String and strict session
UTF-8, independent of its reported collation. It preserves identifier text and wire length metadata;
it adds no ROWID SQL inventory entry or parameter wire identity. MySQL results reject this code.

NUMBER/DECIMAL each have two rows with the same SQL name and provider identity. The System.Decimal row covers precision 1..28 with 0 <= scale <= precision; otherwise an unknown/nonpositive precision, precision above 28, negative scale or scale above precision selects OceanBaseDecimal. The wide rows retain mode codec limits: Oracle precision <=38, metadata scale -84..127; MySQL precision <=65, scale 0..30 and scale <= precision. Overlapping bounds are intentional; ResultTypeCondition selects the normal reader representation, while ProviderSpecificDataType always reports the exact provider representation. Oracle TIMESTAMP similarly has DateTime for scale 0..7 and OceanBaseTimestamp for scale 8..9. MySQL zero-date shapes require provider-specific date access; ordinary DateTime conversion retains its existing checks.

REF CURSOR is Output/ReturnValue only with Use Oracle Prepare Execute and IsSupportedForResults=false; this does not add cursor-valued columns or input cursors. Oracle LOBs require negotiated Use Lob Locators, session-bound handles and a supported UTF-8 character set for CLOB. National strings require trusted national-character-set state; ordinary text and JSON still enforce their supported collations. YEAR values remain zero or 1901..2155, BIT widths 1..64 and interval precision/scale retain existing codec validation.

Creation and literal columns are hints for SQL declarations, not an identifier/value escaping API. Use parameters for values, validate precision/length arguments, and apply server-specific restrictions; unknown length limits are not unlimited sizes. This static table does not certify successful DDL, server versions, collation policy or direct/OBProxy interoperability. Named Types, routine parameter derivation and CommandBehavior.KeyInfo remain separate capabilities.

The negative Oracle metadata scale range above describes result metadata. To explicitly declare signed decimal parameter scale, use OceanBaseParameter.DecimalScale; standard DbParameter.Scale remains a byte. See the [parameter contract](obconnector-dotnet-architecture-design.md#decimal-parameter-scale) for mode limits, exact constraints and assignment precedence, and the [module record](obconnector-dotnet-completeness.md#1516-2026-10-25-有符号-decimal-scale-本地交付) for local validation. Parameter derivation reuses this signed representation. Procedures/ProcedureParameters expose directory data; they do not establish procedure execution support. Oracle execution requires Use Oracle Prepare Execute=true. MySQL execution uses the separate ordered-parameter and output-decoding contract above; directory signatures do not override its ReturnValue, capability, type or behavior restrictions.

## Value and identity rules

All directory columns are strings unless listed here: ORDINAL_POSITION, ATTRIBUTE_COUNT and METHOD_COUNT are Int32; PREFIX_LENGTH is Int64; the five numeric Sequences columns (MINIMUM_VALUE through LAST_NUMBER) are `OceanBaseDecimal`. DBNull is preserved. Fractional/overflowing integer metadata, unexpected result shapes and unread failures are rejected; no integer rounding or partial table is returned. IS_UNIQUE uses YES/NO strings; SORT_ORDER uses ASC/DESC or DBNull. Sequence flags retain server Y/N values. LAST_NUMBER is a server allocation/cache value, not a promise about the last value consumed by an application. Metadata calls never invoke NEXTVAL or CURRVAL.

MySQL databases occupy the Catalog part, with Schema=DBNull. Oracle owners occupy Schema, with Catalog=DBNull. Index and constraint schema values follow that convention, including referenced objects. Parent and child foreign-key columns are paired by dictionary ordinal, including composite keys. Oracle foreign keys report UPDATE_RULE=NO ACTION. Server identifier spelling is preserved; matching follows server dictionary comparison rules and collation. The driver does not uppercase or lowercase restrictions or results.

Null restrictions mean unrestricted; empty strings, percent signs and underscores remain literal values in bound equality predicates, with server empty-string semantics applying. Values are never SQL fragments or LIKE patterns. Too many restrictions fail before I/O. A non-null restriction for an absent identity part returns an empty table without a directory query (including Oracle Host or a MySQL Package). Unsupported collections, including Types/Synonyms on MySQL, throw NotSupportedException and are not advertised. A valid empty result retains the complete DataTable shape.

## Dictionaries, permissions and verification

MySQL uses INFORMATION_SCHEMA.VIEWS, STATISTICS, TABLE_CONSTRAINTS, KEY_COLUMN_USAGE and REFERENTIAL_CONSTRAINTS, plus `mysql.user` and `oceanbase.DBA_SEQUENCES`. Users and Sequences may require additional grants; only username/host and sequence metadata are selected, never password columns. Oracle uses the corresponding ALL_VIEWS, ALL_INDEXES, ALL_IND_COLUMNS, ALL_CONSTRAINTS, ALL_CONS_COLUMNS, ALL_TYPES, ALL_SEQUENCES, ALL_USERS and ALL_SYNONYMS views.

Dictionary visibility and permissions are server policy. Permission denial and missing views propagate as server errors, with no fallback to privileged internal tables, fabricated empty results or hidden retry. A known server rejection leaves the connection usable; interrupted requests follow the ordinary unknown-outcome and session-discard rules. Queries enumerate server-visible metadata and may be expensive without restrictions.

The SQL sources were checked against OceanBase [pinned dictionary definitions](https://github.com/oceanbase/oceanbase/blob/5e8a63d29dae9fde5e962b8fcc7b167d438fc343/src/share/inner_table/ob_inner_table_schema_def.py) and existing Connector/J queries. Local protocol fixtures and five-asset consumers establish local behavior, not real SQL parsing, privileges, cross-version or OBProxy compatibility. The added integration cases exercise discovery, composite key/index/FK ordering, views, Oracle types/synonyms and sequence non-advancement when a real profile is configured. See [compatibility](compatibility.md) and [verification records](obconnector-dotnet-progress.md).
