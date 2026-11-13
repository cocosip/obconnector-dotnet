# Column metadata for reverse engineering: investigation

Date: 2026-11-13. This is an attribution report and a proposed contract, not an
implemented feature or server acceptance record.

Implementation update: the approved reduced scope is now implemented in the
working tree; [schema.md](schema.md#column-metadata-and-completeness) is the
current public contract. A further pinned-source check established that Oracle
VIRTUAL_COLUMN=NO does not exclude stored generation. Such rows keep computed
and default metadata unknown; they do not report an ordinary column. The
historical investigation and baseline evidence below remain distinct from
implementation verification recorded in [the plan](column-metadata-plan.md).

## Conclusion

The public Columns collection has a Connector-owned information gap. Both the
v0.2.0 tag and the inspected working tree define only twelve columns and omit
directory information needed to reconstruct column declarations and generation
behavior. This is a catalog API enhancement, with no evidence of a wire-protocol
or ordinary data read/write defect.

Some requested facts also have server dictionary limitations. A directory field
existing does not establish that it is populated. In particular, the inspected
OceanBase source defines Oracle IDENTITY_COLUMN as a constant NULL. It would be
incorrect to convert that NULL to false or conclude that identity is unsupported.

## Evidence and boundaries

- Connector working tree: master, 083ab60c7bf44411a2c2b14e0758b2b2c222de27.
  The v0.2.0 tag has the same twelve-field Columns declaration.
- Connector/J: local source at commit
  6adc311ba5db8b2f19f98f3e6e4734667fdfdda1, version 2.4.14.
- Server source: OceanBase v4.4.2_CE, commit
  e859d1b9c9a6d11d856f4fed5b6385f1a6795820. This public source baseline is
  **not** live evidence for the configured servers or an Enterprise build.
- A temporary read-only probe used the published OceanBaseConnector 0.2.0,
  existing integration User Secrets and bounded connection/command timeouts.
  Both configured modes failed during OpenAsync with OceanBaseException,
  ServerErrorCode=0, and no SQL state. No directory statement ran and no server
  version was obtained. No DDL or data writes were attempted. The failure does
  not establish whether either server supports a particular catalog field.

## Attribution by capability

| Requested information | Inspected source evidence | Attribution |
| --- | --- | --- |
| MySQL full declaration | INFORMATION_SCHEMA.COLUMNS.COLUMN_TYPE uses the server type formatter with column accuracy, extended type information and zerofill | Connector does not select it |
| MySQL auto increment / generated storage / ON UPDATE | EXTRA is populated from the corresponding column flags; temporal ON UPDATE includes fractional precision | Connector does not select it |
| MySQL generated expression | GENERATION_EXPRESSION is populated for generated columns | Connector does not select it |
| MySQL comments / collation | The information-schema implementation supplies COLUMN_COMMENT and COLLATION_NAME | Connector does not select them |
| Oracle BYTE/CHAR / RAW / numeric / temporal declaration | ALL_TAB_COLS exposes DATA_LENGTH, CHAR_LENGTH, CHAR_USED, DATA_PRECISION and DATA_SCALE; temporal DATA_TYPE strings already contain precision and zone/interval qualifiers | Connector loses relevant components; reconstruction must respect the actual OceanBase representation |
| Oracle generated-column classification | ALL_TAB_COLS exposes VIRTUAL_COLUMN; ALL_TAB_COLUMNS does not project that field in this source baseline | Connector needs an additional directory source while retaining current row visibility |
| Oracle identity classification | ALL_TAB_COLS_V$ defines IDENTITY_COLUMN as CAST(NULL AS VARCHAR2(3)) | Server dictionary limitation in this source baseline; unknown is not false |
| Oracle identity mode/options | The schema printer has ALWAYS, BY DEFAULT, BY DEFAULT ON NULL and sequence-option output | Possible DDL-based source; this does not prove live availability or privileges |
| DDL-ready defaults | The schema printer treats expression defaults separately from literal defaults; MySQL EXTRA in this source does not expose a DEFAULT_GENERATED discriminator | COLUMN_DEFAULT alone is insufficient for the general case; use a verified discriminator or authoritative DDL |

For Oracle, DATA_DEFAULT is also used for generated expressions. It must not
automatically become DEFAULT_VALUE_SQL for every row. Do not infer identity
from a sequence-looking default, infer stored generation merely from an absent
virtual flag, or invent a missing length/precision.

## Java comparison

JDBC DatabaseMetaData.getColumns is the analogous public API. The method and
field names differ from ADO.NET; the underlying reverse-engineering requirement
is shared.

- MySQL Connector/J reads COLUMN_TYPE, COLUMN_COMMENT, COLUMN_DEFAULT and EXTRA.
  It supplies IS_AUTOINCREMENT and IS_GENERATEDCOLUMN. Its TYPE_NAME expression
  removes size parentheses, so it is not the complete declaration requested here.
  It does not return the generated expression, storage flag or ON UPDATE SQL in
  this method. Its exact EXTRA comparisons should not be copied blindly.
- Oracle Connector/J reads ALL_TAB_COLUMNS and optionally ALL_COL_COMMENTS.
  It assigns IS_AUTOINCREMENT="NO" and IS_GENERATEDCOLUMN=null in client code.
  Those values are not proof of server functionality or a contract to copy.

## Proposed compatible contract

### Scope revision: public catalog metadata only

The first implementation is a bounded catalog API enhancement, not a promise
of lossless reconstruction for every column. Use OceanBase's public
INFORMATION_SCHEMA and SYS/ALL_* catalog views. Do not modify the server,
Connector/J, the wire protocol, or ExpectedMode. Do not query internal
__ALL_* tables directly.

The appended ADO.NET field names and three-state presence flags below are
Connector-defined API conventions, not an OceanBase server standard or an
exact JDBC result shape. Their values must come from established catalog facts.
Connector/J is a reference for public directory access, not a requirement to
copy its hard-coded values or limit all metadata to its current field set.

| First implementation | Explicitly deferred |
| --- | --- |
| Select MySQL COLUMN_TYPE, EXTRA, GENERATION_EXPRESSION, collation and comments; map established flags and expressions | SHOW CREATE TABLE fetching and parsing, including per-table fallback queries |
| Read Oracle public length/precision/semantics fields and VIRTUAL_COLUMN from ALL_TAB_COLS while retaining the existing Columns row visibility | DBMS_METADATA fetching/parsing and identity sequence discovery |
| Reconstruct only type declarations whose public catalog components are sufficient; keep unknown values as DBNull | A general SQL/DDL parser, original-DDL recovery, and speculative inference |
| Provide three-state flags and DDL-ready defaults only where the public catalog establishes their meaning | Reconstructing ambiguous defaults by guessing from text or relying on DEFAULT_GENERATED |
| Preserve existing public fields, restrictions and lifecycle/error behavior | Java SDK changes, protocol changes and any claim of complete EF reverse engineering |

IDENTITY_GENERATION and IDENTITY_OPTIONS_SQL are deferred rather than added as
mostly unavailable fields in the first contract. IS_IDENTITY remains included
with DBNull for unestablished Oracle identity metadata. A missing catalog field
may justify a narrowly scoped capability check; it does not justify a general
fallback framework. Permission and operational errors continue to propagate.

This reduced scope may leave particular tables unsuitable for safe scaffolding,
especially when Oracle identity or default semantics cannot be established.
Consumers must receive that limitation explicitly; it is not a reason to turn
unknown into false. Broader recovery requires a separately justified change
after real-server validation.

Prefer appending fields to Columns. Keep the original twelve fields, their
ordinals, CLR types and raw COLUMN_DEFAULT meaning. Preserve the ordered exact,
parameterized Catalog/Schema/Table/Column restrictions and absent-identifier
behavior. Keep the same authenticated mode, session/transaction ownership,
timeouts, cancellation and error propagation; do not change ExpectedMode.

| Appended field | DataColumn.DataType | Proposed meaning |
| --- | --- | --- |
| TYPE_DECLARATION | System.String | Semantically complete type declaration, without default/generation clauses; DBNull when not reliably available |
| IS_IDENTITY | System.Boolean | True/false only when established; DBNull when unavailable |
| IS_COMPUTED | System.Boolean | True for an established generated column, false for an established ordinary column, DBNull when classification is unknown |
| COMPUTED_EXPRESSION | System.String | Generated expression; DBNull for ordinary columns or unavailable information |
| IS_STORED | System.Boolean | True for stored, false for virtual; DBNull for ordinary columns or unknown storage |
| HAS_DEFAULT | System.Boolean | True for an established default, false for an established absence, DBNull when presence is unknown |
| DEFAULT_VALUE_SQL | System.String | DDL-ready default SQL, preserving literal versus expression semantics; DBNull for no known default or unavailable information |
| HAS_ON_UPDATE | System.Boolean | True for established ON UPDATE behavior, false for established absence, DBNull when presence is unknown |
| ON_UPDATE_SQL | System.String | MySQL update expression without the ON UPDATE keywords; DBNull when absent, unavailable or inapplicable |
| COLLATION_NAME | System.String | Actual column collation when available; otherwise DBNull |
| COLUMN_COMMENT | System.String | Actual directory comment; preserve a known empty string and use DBNull for SQL NULL/unavailable information |

These are proposed names/types, not fields available in 0.2.0. DBNull is a SQL
NULL value in a typed DataColumn; Boolean columns must not use a default false.

### Presence and completeness

IS_COMPUTED/COMPUTED_EXPRESSION, HAS_DEFAULT/DEFAULT_VALUE_SQL and
HAS_ON_UPDATE/ON_UPDATE_SQL use the same rules:

| Presence flag | Corresponding SQL/expression | Meaning and consumer action |
| --- | --- | --- |
| false | DBNull | Confirmed absent; no behavior to reconstruct |
| true | Authoritative SQL text | Confirmed present and expression available; the consumer must support the expression/behavior or reject it |
| true | DBNull | Confirmed present but expression unavailable; incomplete metadata, never treat as absent |
| DBNull | DBNull | Presence unknown; incomplete metadata, never treat as absent |

A false or unknown flag with non-null expression text is an inconsistent result
and must not be published. Whitespace/empty directory placeholders must not be
used as successful expression recovery; SQL for an actual empty-string default
is quoted SQL text, not an empty metadata string. A recovered authoritative
expression establishes presence and therefore requires a true flag.

An explicit DEFAULT NULL, when distinguishable in an authoritative source, is
HAS_DEFAULT=true with DEFAULT_VALUE_SQL="NULL". If the source cannot distinguish
no default from an explicit DEFAULT NULL, HAS_DEFAULT remains DBNull; do not
guess false from a NULL COLUMN_DEFAULT alone. The legacy COLUMN_DEFAULT field
retains its existing raw meaning. A generated expression stored in DATA_DEFAULT
does not itself establish a separate default clause.

IS_COMPUTED=false requires IS_STORED=DBNull. IS_COMPUTED=true may have
IS_STORED=DBNull when storage is unestablished; that is still incomplete for
reconstruction. IS_IDENTITY retains the same true/false/DBNull classification;
a true flag does not imply that identity mode and options were recovered.
Identity mode/options are outside the first implementation's contract.

A constant-NULL directory field, a missing field, or unavailable classification
must never become false. Proven inapplicability may establish absence, but the
client must not infer it solely from the compatibility-mode name. Permission,
connection, cancellation and other operational failures retain their existing
exception behavior; do not catch them and publish a successful table of unknowns.

Consumers must inspect presence flags before the expression fields. They may
accept a confirmed ordinary column with confirmed absent defaults/ON UPDATE,
but must explicitly handle or reject any unknown presence, missing required
expression, or missing required storage/identity details. These rules avoid
both silently losing behavior and rejecting every known ordinary column.

Appending columns preserves named-field access and existing ordinals. Consumers
that require exactly twelve columns would observe a shape change. No fixed-size
result guarantee was found in the documented public contract; a separate
collection is not currently justified. Existing exact-shape tests will need
intentional updates without weakening checks for the original fields.

## Implementation implications

1. Verify directory field presence **and values** on the intended server builds.
   Use current Columns for row identity/visibility, with supplemental sources
   where necessary. Unknown-column fallback must not swallow permission errors,
   cancellation, connection failures or unrelated SQL errors.
2. For missing discriminators, retain unknown/incomplete metadata and document
   the unsupported reconstruction shape. SHOW CREATE TABLE/DBMS_METADATA
   recovery is explicitly outside the first implementation. Do not add a DDL
   parser to resolve ambiguity in this catalog enhancement.
3. Extend the existing shared schema operation and typed materialization.
   CreateDirectoryTable currently assigns integer types to every ordinal >=9;
   appending strings/Booleans requires explicit typing. AddSchemaRow also needs
   deliberate Boolean handling rather than its integer fallback.
4. Cover the public sync/async interfaces with loopback, consumer and live tests;
   include the requested type/default/generation matrix, unchanged restrictions,
   names, transactions, cancellation and exception behavior. Keep server evidence
   separate from fixtures. Add no EF Core dependency.
5. For each presence/expression pair, test confirmed absence, present/complete,
   present/incomplete and unknown through the public sync/async interfaces.
   Assert Boolean DataColumn types, actual DBNull values and preservation of
   known ordinary columns. Include explicit DEFAULT NULL versus an ambiguous
   NULL directory value, empty-string literals versus empty placeholders, missing
   fields and constant-NULL identity metadata. Exercise permission/connection
   errors and cancellation as exceptions, not successful unknown metadata.

## Verification completed for this investigation

- Release ConnectionSchema/CatalogSchema unit selection: 44 passed, 0 failed.
- Release ConnectionSchemaConsumerTests/CatalogSchemaConsumerTests: 12 passed,
  0 failed, using public APIs against local protocol fixtures.
- Existing integration configuration and cleanup rules were inspected; the
  read-only connection attempts described above did not reach directory SQL.
- No product changes, commit, new package, full regression run, live column
  matrix or newly verified server version are supplied by this investigation.

## Source pointers

- [Connector schema implementation](../src/OceanBaseConnector/OceanBaseConnection.Schema.cs)
- [Existing schema contract](schema.md)
- [JDBC MySQL getColumns](https://github.com/oceanbase/obconnector-j/blob/6adc311ba5db8b2f19f98f3e6e4734667fdfdda1/src/main/java/com/oceanbase/jdbc/JDBC4DatabaseMetaData.java#L1233)
- [JDBC Oracle getColumns](https://github.com/oceanbase/obconnector-j/blob/6adc311ba5db8b2f19f98f3e6e4734667fdfdda1/src/main/java/com/oceanbase/jdbc/OceanBaseOracleDatabaseMetadata.java#L249)
- [OceanBase MySQL column directory](https://github.com/oceanbase/oceanbase/blob/e859d1b9c9a6d11d856f4fed5b6385f1a6795820/src/observer/virtual_table/ob_information_columns_table.cpp#L559)
- [OceanBase Oracle column views](https://github.com/oceanbase/oceanbase/blob/e859d1b9c9a6d11d856f4fed5b6385f1a6795820/src/share/inner_table/ob_inner_table_schema_def.py#L48128)
- [OceanBase default SQL printing](https://github.com/oceanbase/oceanbase/blob/e859d1b9c9a6d11d856f4fed5b6385f1a6795820/src/share/schema/ob_schema_printer.cpp#L310)
- [OceanBase identity SQL printing](https://github.com/oceanbase/oceanbase/blob/e859d1b9c9a6d11d856f4fed5b6385f1a6795820/src/share/schema/ob_schema_printer.cpp#L533)
