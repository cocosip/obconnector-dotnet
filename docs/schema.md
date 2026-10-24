# Schema catalog API

`OceanBaseConnection.GetSchema()` returns `MetaDataCollections`. Collections are available on all five assets; net8.0/net9.0/net10.0 also override `GetSchemaAsync`. An open, idle connection is required even for static metadata. Calls use the current local transaction and the connection command timeout; asynchronous calls honor cancellation. An active reader/XA/ambient restriction is enforced by the ordinary connection and command lifecycle.

## Collections and restrictions

Oracle mode advertises 19 collections; MySQL mode advertises 17, excluding Types and Synonyms. Collection names are case-insensitive. Query `Restrictions` for `CollectionName`, `RestrictionName`, `RestrictionDefault` (DBNull, meaning unrestricted) and the one-based `RestrictionNumber`. `MetaDataCollections` reports each collection's restriction count and identifier parts.

| Collection | Ordered restrictions | Result and mode boundary |
| --- | --- | --- |
| MetaDataCollections | None | CollectionName, NumberOfRestrictions and NumberOfIdentifierParts; no query |
| DataSourceInformation | None | Product/version and existing provider information; no query |
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

Types describes server named object/collection types, not the ADO.NET `DataTypes` collection or a complete CLR mapping table. Routine metadata does not implement `DeriveParameters` or automatic command parameter inference. These remain separate capabilities.

## Value and identity rules

All directory columns are strings unless listed here: ORDINAL_POSITION, ATTRIBUTE_COUNT and METHOD_COUNT are Int32; PREFIX_LENGTH is Int64; the five numeric Sequences columns (MINIMUM_VALUE through LAST_NUMBER) are `OceanBaseDecimal`. DBNull is preserved. Fractional/overflowing integer metadata, unexpected result shapes and unread failures are rejected; no integer rounding or partial table is returned. IS_UNIQUE uses YES/NO strings; SORT_ORDER uses ASC/DESC or DBNull. Sequence flags retain server Y/N values. LAST_NUMBER is a server allocation/cache value, not a promise about the last value consumed by an application. Metadata calls never invoke NEXTVAL or CURRVAL.

MySQL databases occupy the Catalog part, with Schema=DBNull. Oracle owners occupy Schema, with Catalog=DBNull. Index and constraint schema values follow that convention, including referenced objects. Parent and child foreign-key columns are paired by dictionary ordinal, including composite keys. Oracle foreign keys report UPDATE_RULE=NO ACTION. Server identifier spelling is preserved; matching follows server dictionary comparison rules and collation. The driver does not uppercase or lowercase restrictions or results.

Null restrictions mean unrestricted; empty strings, percent signs and underscores remain literal values in bound equality predicates, with server empty-string semantics applying. Values are never SQL fragments or LIKE patterns. Too many restrictions fail before I/O. A non-null restriction for an absent identity part returns an empty table without a directory query (including Oracle Host or a MySQL Package). Unsupported collections, including Types/Synonyms on MySQL, throw NotSupportedException and are not advertised. A valid empty result retains the complete DataTable shape.

## Dictionaries, permissions and verification

MySQL uses INFORMATION_SCHEMA.VIEWS, STATISTICS, TABLE_CONSTRAINTS, KEY_COLUMN_USAGE and REFERENTIAL_CONSTRAINTS, plus `mysql.user` and `oceanbase.DBA_SEQUENCES`. Users and Sequences may require additional grants; only username/host and sequence metadata are selected, never password columns. Oracle uses the corresponding ALL_VIEWS, ALL_INDEXES, ALL_IND_COLUMNS, ALL_CONSTRAINTS, ALL_CONS_COLUMNS, ALL_TYPES, ALL_SEQUENCES, ALL_USERS and ALL_SYNONYMS views.

Dictionary visibility and permissions are server policy. Permission denial and missing views propagate as server errors, with no fallback to privileged internal tables, fabricated empty results or hidden retry. A known server rejection leaves the connection usable; interrupted requests follow the ordinary unknown-outcome and session-discard rules. Queries enumerate server-visible metadata and may be expensive without restrictions.

The SQL sources were checked against OceanBase [pinned dictionary definitions](https://github.com/oceanbase/oceanbase/blob/5e8a63d29dae9fde5e962b8fcc7b167d438fc343/src/share/inner_table/ob_inner_table_schema_def.py) and existing Connector/J queries. Local protocol fixtures and five-asset consumers establish local behavior, not real SQL parsing, privileges, cross-version or OBProxy compatibility. The added integration cases exercise discovery, composite key/index/FK ordering, views, Oracle types/synonyms and sequence non-advancement when a real profile is configured. See [compatibility](compatibility.md) and [verification records](obconnector-dotnet-progress.md).
