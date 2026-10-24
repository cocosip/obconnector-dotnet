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

Types describes server named object/collection types; DataTypes separately describes built-in CLR/provider mappings and supported complex shapes. Routine metadata does not implement DeriveParameters or automatic command parameter inference.

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

Canonical MySQL declarations include signed/unsigned TINYINT, SMALLINT, MEDIUMINT, INT and BIGINT; DECIMAL and DECIMAL UNSIGNED; FLOAT/DOUBLE; CHAR/VARCHAR/TEXT; BINARY/VARBINARY/BLOB; DATE/DATETIME/TIMESTAMP/TIME; YEAR, BIT and JSON. TINYINT(1) has a Boolean mapping row only when TreatTinyAsBoolean is enabled. Unsigned/narrow/alias variants do not replace the preferred signed/general CLR mapping. MySQL BLOB uses byte[]/OceanBaseDbType.Binary, while Oracle BLOB uses a session-bound OceanBaseLob/OceanBaseDbType.Blob.

Oracle declarations include NUMBER, BINARY_FLOAT/BINARY_DOUBLE, DATE, TIMESTAMP and its timezone variants, CHAR/VARCHAR2/NCHAR/NVARCHAR2, RAW, BLOB/CLOB, both interval families, OBJECT, ARRAY and REF CURSOR. OBJECT/ARRAY rows describe supported complex codecs: obtain an OceanBaseComplexType descriptor before binding, and use the supported binary protocol/member layouts. OBJECT needs a named type; ARRAY can use a named collection or the existing supported anonymous scalar-array descriptor. They do not provide generic CREATE TYPE DDL.

NUMBER/DECIMAL each have two rows with the same SQL name and provider identity. The System.Decimal row covers precision 1..28 with 0 <= scale <= precision; otherwise an unknown/nonpositive precision, precision above 28, negative scale or scale above precision selects OceanBaseDecimal. The wide rows retain mode codec limits: Oracle precision <=38, metadata scale -84..127; MySQL precision <=65, scale 0..30 and scale <= precision. Overlapping bounds are intentional; ResultTypeCondition selects the normal reader representation, while ProviderSpecificDataType always reports the exact provider representation. Oracle TIMESTAMP similarly has DateTime for scale 0..7 and OceanBaseTimestamp for scale 8..9. MySQL zero-date shapes require provider-specific date access; ordinary DateTime conversion retains its existing checks.

REF CURSOR is Output/ReturnValue only with Use Oracle Prepare Execute and IsSupportedForResults=false; this does not add cursor-valued columns or input cursors. Oracle LOBs require negotiated Use Lob Locators, session-bound handles and a supported UTF-8 character set for CLOB. National strings require trusted national-character-set state; ordinary text and JSON still enforce their supported collations. YEAR values remain zero or 1901..2155, BIT widths 1..64 and interval precision/scale retain existing codec validation.

Creation and literal columns are hints for SQL declarations, not an identifier/value escaping API. Use parameters for values, validate precision/length arguments, and apply server-specific restrictions; unknown length limits are not unlimited sizes. This static table does not certify successful DDL, server versions, collation policy or direct/OBProxy interoperability. Named Types, routine parameter derivation and CommandBehavior.KeyInfo remain separate capabilities.

The negative Oracle metadata scale range above describes result metadata. To explicitly declare signed decimal parameter scale, use OceanBaseParameter.DecimalScale; standard DbParameter.Scale remains a byte. See the [parameter contract](obconnector-dotnet-architecture-design.md#decimal-parameter-scale) for mode limits, exact constraints and assignment precedence, and the [module record](obconnector-dotnet-completeness.md#1516-2026-10-25-有符号-decimal-scale-本地交付) for local validation. Automatic parameter derivation is still absent. Procedures/ProcedureParameters expose directory data; they do not imply MySQL CommandType.StoredProcedure execution support. Current stored-procedure execution requires Oracle mode and Use Oracle Prepare Execute=true.

## Value and identity rules

All directory columns are strings unless listed here: ORDINAL_POSITION, ATTRIBUTE_COUNT and METHOD_COUNT are Int32; PREFIX_LENGTH is Int64; the five numeric Sequences columns (MINIMUM_VALUE through LAST_NUMBER) are `OceanBaseDecimal`. DBNull is preserved. Fractional/overflowing integer metadata, unexpected result shapes and unread failures are rejected; no integer rounding or partial table is returned. IS_UNIQUE uses YES/NO strings; SORT_ORDER uses ASC/DESC or DBNull. Sequence flags retain server Y/N values. LAST_NUMBER is a server allocation/cache value, not a promise about the last value consumed by an application. Metadata calls never invoke NEXTVAL or CURRVAL.

MySQL databases occupy the Catalog part, with Schema=DBNull. Oracle owners occupy Schema, with Catalog=DBNull. Index and constraint schema values follow that convention, including referenced objects. Parent and child foreign-key columns are paired by dictionary ordinal, including composite keys. Oracle foreign keys report UPDATE_RULE=NO ACTION. Server identifier spelling is preserved; matching follows server dictionary comparison rules and collation. The driver does not uppercase or lowercase restrictions or results.

Null restrictions mean unrestricted; empty strings, percent signs and underscores remain literal values in bound equality predicates, with server empty-string semantics applying. Values are never SQL fragments or LIKE patterns. Too many restrictions fail before I/O. A non-null restriction for an absent identity part returns an empty table without a directory query (including Oracle Host or a MySQL Package). Unsupported collections, including Types/Synonyms on MySQL, throw NotSupportedException and are not advertised. A valid empty result retains the complete DataTable shape.

## Dictionaries, permissions and verification

MySQL uses INFORMATION_SCHEMA.VIEWS, STATISTICS, TABLE_CONSTRAINTS, KEY_COLUMN_USAGE and REFERENTIAL_CONSTRAINTS, plus `mysql.user` and `oceanbase.DBA_SEQUENCES`. Users and Sequences may require additional grants; only username/host and sequence metadata are selected, never password columns. Oracle uses the corresponding ALL_VIEWS, ALL_INDEXES, ALL_IND_COLUMNS, ALL_CONSTRAINTS, ALL_CONS_COLUMNS, ALL_TYPES, ALL_SEQUENCES, ALL_USERS and ALL_SYNONYMS views.

Dictionary visibility and permissions are server policy. Permission denial and missing views propagate as server errors, with no fallback to privileged internal tables, fabricated empty results or hidden retry. A known server rejection leaves the connection usable; interrupted requests follow the ordinary unknown-outcome and session-discard rules. Queries enumerate server-visible metadata and may be expensive without restrictions.

The SQL sources were checked against OceanBase [pinned dictionary definitions](https://github.com/oceanbase/oceanbase/blob/5e8a63d29dae9fde5e962b8fcc7b167d438fc343/src/share/inner_table/ob_inner_table_schema_def.py) and existing Connector/J queries. Local protocol fixtures and five-asset consumers establish local behavior, not real SQL parsing, privileges, cross-version or OBProxy compatibility. The added integration cases exercise discovery, composite key/index/FK ordering, views, Oracle types/synonyms and sequence non-advancement when a real profile is configured. See [compatibility](compatibility.md) and [verification records](obconnector-dotnet-progress.md).
