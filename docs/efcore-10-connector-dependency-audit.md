# EF Core 10 Connector dependency audit

## Release preparation for 0.4.0

The shared version in `Directory.Build.props` is 0.4.0 for both NuGet packages. The five-target Release solution build completed with zero warnings and errors, and the package dependency checks and release-note generator checks passed. The final independent source review found no actionable defects after the async sequential-reader regression was strengthened. This release includes the Connector changes described below.

| Check | Recorded result |
| --- | --- |
| Full offline solution regression | 10,298 unit, 4,096 consumer, 14 dependency-injection and 43 configuration tests passed, zero failed or skipped |
| MySQL and Oracle direct regression | 45 passed, zero failed or skipped |
| Oracle with PrepareExecute and LOB locators disabled | 13 passed, zero failed or skipped |
| MySQL JSON regression | 62 passed, zero failed or skipped |
| Packaged connection, reader, cancellation and transaction checks | Eleven checks per tenant mode passed, zero failed or skipped, including early-close ambient rollback and connection reuse after cancellation |
| Packaged MySQL consumer | 526 checks passed, 14 retained server-limit observations and eight dependent checks skipped, zero cleanup failures |
| Packaged Oracle consumer | All 336 stored Double binds matched their D-literal controls, zero insert errors, and all 168 wire checks passed. The server canonicalized negative zero in 24 strict bitwise comparisons for both bind and literal paths. Native JSON passed 24 writes and 48 value reads |
| Connector package SHA-256 | `84F763DCD576FF43B1C9DAE13E5E117291D3AF69F5643D9FBBF2F4E893646E6D` |
| DependencyInjection package SHA-256 | `12A0985310933412D001508C638B88131107527CE86C8C73AF581B08B990C566` |

The local packages were built from the reviewed working tree based on `b007c5e`, so their assembly informational versions identify that base revision. Publication should use the release tag and its verified artifact through the existing CI workflow. [Connector package](../TestResults/release-0.4.0/packages/OceanBaseConnector.0.4.0.nupkg) · [DependencyInjection package](../TestResults/release-0.4.0/packages/OceanBaseConnector.DependencyInjection.0.4.0.nupkg) · [Direct regression](../TestResults/release-0.4.0/direct.trx) · [Oracle alternate profile](../TestResults/release-0.4.0/oracle-text.trx) · [MySQL JSON regression](../TestResults/release-0.4.0/mysql-json.trx) · [MySQL packaged consumer](../TestResults/release-0.4.0/mysql/mysql-package.log) · [Oracle packaged consumer](../TestResults/release-0.4.0/oracle-types/candidate/oracle-package.log) · [Packaged lifecycle checks](../TestResults/release-0.4.0/common/common-package.log)

The final full offline run includes the updated metadata limit regression and the malformed-cursor fixture fix. [Full offline regression log](../TestResults/release-0.4.0/offline.log)

## Connector fixes after the audit

The local candidate `0.3.1-ef10.fixes.1` packages the working tree changes based on `b007c5e`. Its SHA-256 is `A15A0387EA5FA6D48A60AB288C0A17AD361791464D5358B1FC2329B2231640CE`. The audit tables below record the original `0.3.1-ef10.b007c5e` package as the candidate.

| Mode | EF gap | Updated contract and verification |
| --- | --- | --- |
| Oracle | B-01 | The login requests binary Double capability bit 4. Parameters still use wire type 5 and the original little-endian IEEE-754 bytes. Oracle 4.4.2.2 now preserves the requested extreme values, subnormals, ±1E-131 and 1E-120. The full probe improved from 120 to 312 exact bitwise matches out of 336, with zero insert errors and all 336 stored binds matching their D literals. The 24 bitwise differences are negative zero, which the server stores as positive zero for both paths. |
| MySQL | B-02/B-03 | Text and prepared readers, with either buffered access or SequentialAccess, accept the observed wide expression metadata up to 81 digits and 30 fractional digits. Metadata exposes the original precision and scale. Read exact values with `GetFieldValue<OceanBaseDecimal>` or `GetProviderSpecificValue`. Use CLR decimal getters when the value fits exactly. An inexact or out-of-range conversion raises OverflowException. MySQL parameter declarations retain the existing 65-digit limit. |
| MySQL | B-04/B-09 | Bind a separate `OceanBaseParameter.Clone()` to each repeated positional input. Clones preserve declaration and inference state and can join a different command while the source stays attached to its owner. For stream-backed input, call `Clone(replacementValue)` with an independent caller-owned stream per position. A shared stream is rejected before it is consumed. Text parameter directions remain input-only. |

Restore the new package by using `0.3.1-ef10.fixes.1` in the EF consumer and adding `TestResults/ef10-fixes/feed` as a NuGet source along with NuGet.org. Both archived probes loaded the packaged net10.0 asset on direct OceanBase 4.4.2.2. The MySQL probe recorded 526 checks passed, 14 reproduced server limitations, and 8 skips. The Oracle probe repeated the bind matrix and all 24 native JSON writes and 48 GetValue checks. [Package](../TestResults/ef10-fixes/feed/OceanBaseConnector.0.3.1-ef10.fixes.1.nupkg) · [MySQL package probe](../TestResults/ef10-fixes/mysql/mysql-package.log) · [Oracle package probe](../TestResults/ef10-fixes/oracle-types/candidate/oracle-package.log)

The source regression passed 45 direct MySQL and Oracle cases and 13 Oracle cases with PrepareExecute and LOB locators disabled. The post-review rerun passed all four MySQL decimal cases with the async getter reading the sequential payload first. [Direct regression](../TestResults/ef10-fixes/direct.trx) · [Oracle alternate profile](../TestResults/ef10-fixes/oracle-text.trx) · [MySQL async reader regression](../TestResults/ef10-fixes/wide-async-first.trx)

### Connector/J reference

| Gap | Java source and observed behavior | .NET implementation basis |
| --- | --- | --- |
| B-01 | The local Connector/J 2.4.14 source uses DOUBLE in `setDouble` and sends capability bits 1 and 2 when enabled. The published 2.4.19 source uses `OBBigDecimalParameter(BigDecimal.valueOf(value))` for Oracle `setDouble`. Its handshake exposes bit 4 through `oracleUseNumberForSetDouble`. On 4.4.2.2, the 2.4.19 JDBC probe returned 1426 for the two extremes and zero for the subnormals with either option value. The JDBC extension `setBINARY_DOUBLE` narrows the input to `float` before constructing its parameter and lost precision in the same probe. | OceanBase's DOUBLE decoder checks `is_support_jdbc_binary_double()` before deciding between binary double and NUMBER conversion. The .NET handshake sets that server capability and sends the existing type-5 payload. The live .NET probe preserved the seven requested values. |
| B-02/B-03 | Connector/J `ResultSet.getBigDecimal` delegates to the row protocol. Text and binary DECIMAL parsing construct Java `BigDecimal` from the decimal text. The 2.4.19 probe read all five wide expression cases in both text and prepared modes, reporting precision/scale 81/28 and 69/30. | `OceanBaseDecimal` carries the exact coefficient and scale through the existing ADO.NET provider-specific readers. The MySQL result limit follows the verified metadata and the bind limit remains 65. |
| B-04/B-09 | JDBC `setParameter` stores values by position. `setBinaryStream` wraps an `InputStream` for one position. The reviewed JDBC client exposes no equivalent public parameter clone contract. | `OceanBaseParameter.Clone` addresses ADO.NET parameter collection ownership by reusing the existing internal snapshot implementation. Repeated stream values use independent caller-owned sources. |

[Connector/J 2.4.19 source archive](https://repo.maven.apache.org/maven2/com/oceanbase/oceanbase-client/2.4.19/oceanbase-client-2.4.19-sources.jar) · [OceanBase DOUBLE decoder](https://github.com/oceanbase/oceanbase/blob/d375c6d70e8dd81c0d153496c85a1a22dfbc81f2/src/observer/mysql/obmp_stmt_execute.cpp#L2411-L2429) · [JDBC probe source](../TestResults/ef10-fixes/JdbcReference.java) · [JDBC probe results](../TestResults/ef10-fixes/jdbc-reference.log)

EF can consume these public paths in its parameter and reader mappings. Its query generation, scaffolding and connection lifetime remain provider integration work. The explicit server limits below include NCLOB DDL, quoted identifiers with embedded double quotes, LOB and sequence defaults, and directory fidelity for DESC and MySQL sequence start. Cross-owner Oracle DDL access still depends on a suitable grants profile. TLS acceptance is deferred.

## Original audit baseline

Verified on 2026-11-19. The audit compares the published OceanBaseConnector 0.3.1 assembly at `6447429f398c4b11cfaeb09335fbd7ff096b7d25` and local candidate `0.3.1-ef10.b007c5e` at `b007c5eb4f8109d377d9540dfbb3c00a3e92b00d`. The candidate includes all four Oracle changes in `d83bd98` and the subsequent Oracle foreign-key catalog query change. The results below concern public Connector APIs exercised from independent .NET 10 console consumers and catalog SQL on the same accounts. They provide inputs for EF's own migration, scaffolding and query validation. [EF gap matrix](https://github.com/cocosip/efcore.oceanbase/blob/ecbed37/docs/efcore/efcore-10-gap-matrix.md) · [EF acceptance checklist](https://github.com/cocosip/efcore.oceanbase/blob/ecbed37/docs/efcore/efcore-10-release-checklist.md) · [d83bd98 implementation and prior results](oracle-json-metadata-verification.md)

## Package and environment

| Item | Value |
| --- | --- |
| Candidate | `OceanBaseConnector.0.3.1-ef10.b007c5e.nupkg`, local feed `TestResults/ef10-audit/feed` |
| Candidate SHA-256 | `8B75F1FDADB7E3ECF5846F3CF4247D60515889BC498BDA34EBD3E34C322615BD` |
| Package assets | `netstandard2.0`, `netstandard2.1`, `net8.0`, `net9.0`, `net10.0`. Live consumers loaded `net10.0` on Windows / .NET 10.0.14 |
| Database | Direct OceanBase 4.4.2.2 MySQL and Oracle tenants. Mode checked against the server handshake. Disposable objects in test account scope |
| SQL runs | TLS Disabled, .NET User Secrets in the integration project. Oracle PrepareExecute and LOB locators on for default Oracle cases, with explicit off profiles where noted. Common probes use unpooled connections |
| TLS controls | Separate VerifyFull and Required attempts for both packages and modes. All terminated in Open before credentials were sent because the server omitted `CLIENT_SSL` |

**Status key.** *Implemented in source* identifies a public path in this source revision. *Candidate verified* means a packaged consumer exercised the specified shape. *Unconfirmed* means evidence for the named shape or its cause is still missing. *Unimplemented* means the requested public contract is absent. *Server limit* names a restriction reproduced by executing SQL directly. A single item may have a verified subset and an open boundary.

## Oracle mode

| Mode | EF gap ID | Public API | Current status | Expected and actual | Owner | Evidence or replay |
| --- | --- | --- | --- | --- | --- | --- |
| Oracle | B-10 | `OceanBaseDbType.Json`, `GetValue`, `GetString`, `GetFieldValue<string>`, `IsDBNull` | Implemented in source and candidate verified | Explicit Json and DbType.String wrote all 24 combinations of value and insertion path. Text and prepared readers returned CLR string for JSON null and the Unicode document above 5,000 characters. SQL NULL returned DBNull and IsDBNull=true. Typed string getters were exercised for non-SQL-NULL cells. In 0.3.1 explicit Json first failed at parameter type selection and old native result metadata/value access threw NotSupportedException. | Connector delivered. EF mapping, query translation and migration validation | Oracle types probe and [OracleJsonTests](../tests/OceanBaseConnector.IntegrationTests/Types/OracleJsonTests.cs) |
| Oracle | G-36 | `GetSchema("IndexColumns")` and `GetSchema("Indexes")` | Implemented in source and candidate verified for ascending indexes. Server limit for DESC and ALTER REBUILD. Cross-owner permissions unconfirmed | `(UPPER(LABEL), ID)` returned the full expression first, ID second with DBNull expression, and both positions ASC. Owner, table, index and column restrictions worked. Indexes reported uniqueness. A 1,136-character expression exceeded the directory projection, which returned 600. The candidate recovered the SQL from `DBMS_METADATA.GET_DDL('INDEX', ...)` and used it to create an equivalent index. Own index DDL access worked. The fallback requires visibility of directory rows and permission to obtain that index's DDL. A denied GET_DDL error propagates by source inspection. The account had no visible foreign expression index for a live permission comparison. 0.3.1 lacks COLUMN_EXPRESSION. Ordinary and expression DESC failed at CREATE with 600/0A000 in both packages. ALTER INDEX REBUILD failed with 900/42000. | Connector delivered supported subset. EF scaffolding, OceanBase DESC and deployed grants | Oracle schema probe and [DDL fallback](../src/OceanBaseConnector/OceanBaseConnection.Schema.OracleDdl.cs) |
| Oracle | C-03 | `GetOracleIdentityColumns` and `GetOracleIdentityColumnsAsync`, `Columns.IS_IDENTITY` | Implemented in source and candidate verified. Cross-owner permissions unconfirmed | The three modes ALWAYS, BY DEFAULT and BY DEFAULT ON NULL returned ColumnName, GenerationType and SequenceOptions, including START WITH, INCREMENT BY and the configured CACHE or NOCACHE. Inserts verified each declared mode. IS_IDENTITY was DBNull and the account received 942 from identity dictionary views. EF can query the per-table DDL API when IS_IDENTITY is unknown. An ordinary table returned an empty list. A missing object returned 31603, as did direct GET_DDL for that object. Denied DDL calls propagate as server errors by source inspection. A visible object with denied DDL privileges was unavailable for a live test. The two APIs are absent in 0.3.1. | Connector delivered. EF should preserve unknown flags, scaffold generator mode/options and handle denied DDL | Oracle schema probe and [schema contract](schema.md) |
| Oracle | D-06 | `GetOracleSequenceStartValue` and `GetOracleSequenceStartValueAsync`, `GetSchema("Sequences")` | Implemented in source and candidate verified | Both start APIs returned exact OceanBaseDecimal 17 before and after NEXTVAL=17,22. NEXTVAL then returned 27, establishing metadata did not advance it. NOCACHE LAST_NUMBER was 27 and CACHE 4 LAST_NUMBER was 37. After `ALTER SEQUENCE ... RESTART START WITH 41` both APIs read the start emitted by the server DDL, 41, with subsequent NEXTVAL=41,46. Before restart the DDL retained 17. 0.3.1 lacks both start APIs. | Connector delivered. EF should model the DDL start and keep LAST_NUMBER separate | Oracle schema probe and [schema contract](schema.md) |
| Oracle | B-01 | `DbType.Double`, `OceanBaseDbType.Double`, `Prepare`, `GetDouble` | Candidate verified for the 1E-120 control. Full-range bind unconfirmed and failure reproduced | MinValue and MaxValue failed at INSERT with 1426/22003. ±Epsilon and ±1E-131 stored zero. 1E-120 retained its bits. All seven requested D-suffixed literals retained their bits via text and prepared reads. Inferred, DbType.Double and explicit native binds, Precision 0/17, Prepare on/off and PrepareExecute on/off agreed across both packages. Server-side comparison and DUMP showed the zero before the Connector reader ran. Local encoder evidence preserves IEEE-754 bits. Further independent binding evidence is needed to distinguish type interpretation at the Connector/server boundary. | Connector Oracle bind protocol and OceanBase binding path | Oracle types probe, B-01 value matrix and local wire fixture |
| Oracle | Foreign-key catalog | `GetSchema("ForeignKeyColumns")` and `GetSchemaAsync` | Implemented in source and candidate verified | Unrestricted, schema-only and table-only sync and async calls returned the composite pairs in order. Earlier 0.3.1 and source without hints reproduced 600/HY000 during execution of the original SQL, including EXPLAIN. The error did not recur in this audit. Old sync returned rows after about 8.7–8.9 s on this fixture. Candidate reads took about 0.11–1.17 s. Original and hinted direct SQL returned equivalent pairs. See the scope timing table below. | Connector query plan workaround for OceanBase 4.4.2.2 | Oracle schema probe and [previous 600 comparison](oracle-json-metadata-verification.md) |

The foreign-key fixture references parent columns P2, P1 from child columns C1, C2. Both positions were present in every tested scope. Counts scoped to the fixture were two rows in each result. Unrestricted and schema queries can include other visible keys.

| Scope | 0.3.1 sync / async, ms | Candidate sync / async, ms | Original / hinted direct SQL in candidate run, ms |
| --- | ---: | ---: | ---: |
| All visible | 8,801 / 85 | 779 / 141 | 7,929 / 123 |
| Child schema | 8,695 / 74 | 1,088 / 123 | 7,985 / 126 |
| Child table | 8,863 / 293 | 799 / 115 | 8,054 / 135 |

These elapsed values describe one observed run and the same query shapes on the same account. They depend on catalog contents and server plan state.

## MySQL mode

| Mode | EF gap ID | Public API | Current status | Expected and actual | Owner | Evidence or replay |
| --- | --- | --- | --- | --- | --- | --- |
| MySQL | B-02 | `ExecuteReader`, `GetDataTypeName`, `GetFieldType`, `GetColumnSchema`, `IsDBNull`, `GetDecimal`, `GetFieldValue<OceanBaseDecimal>` | Candidate verified for DECIMAL(20,8) expressions and plain DECIMAL(65,28). Wide aggregate support remains blocked | Text ExecuteReader and Read can reach the row. Type name is DECIMAL. For wide SUM, AVG and zero compensation, the first tested failing API is GetFieldType. The precision validator throws NotSupportedException before numerical value conversion. GetColumnSchema and exact provider value access also fail on those descriptors. Independent SQL CAST AS CHAR confirms the computed value, zero or NULL on the server. The narrow expressions expose metadata and return exact CLR decimal whenever the data fit. | OceanBase emits expression precision above 65. Connector result metadata validation prevents use. EF cannot clear this by choosing a different getter | MySQL probe and [result metadata checks](../src/OceanBaseConnector/Internal/Protocol/ResultColumn.cs) |
| MySQL | B-03 | Same decimal reader APIs for `LEAST` and `GREATEST` | Candidate verified for DECIMAL(20,8). Wide result metadata remains blocked | Wide extrema use DECIMAL(81,28) metadata and first fail at GetFieldType. Narrow extrema report DECIMAL(28,8). For an accepted wide base-column descriptor, `GetProviderSpecificValue` and `GetFieldValue<OceanBaseDecimal>` preserve values beyond CLR decimal. `GetDecimal` and `GetFieldValue<decimal>` throw OverflowException when conversion cannot be lossless. That public lossless path depends on accepting the result descriptor first. | Connector metadata handling, informed by server expression descriptors | MySQL probe and [reader conversion](../src/OceanBaseConnector/OceanBaseDataReader.cs) |
| MySQL | B-04 | `OceanBaseCommand.CreateParameter`, `OceanBaseParameterCollection.Add/Clear`, `OceanBaseParameter` facets | Public copy API unimplemented. Independent binds candidate verified | `Snapshot()` is internal. The same parameter cannot be added twice or owned by two commands. One parameter for two `?` positions fails before execution. Two separately created Input parameters with equivalent native type, precision, scale, size and value work in order. A public copy contract is needed for EF to preserve caller metadata and ownership, including the distinction between inferred and explicitly set facets. | Connector public API and EF positional adaptation | MySQL probe and [parameter source](../src/OceanBaseConnector/OceanBaseParameter.cs) |
| MySQL | B-09 | `OceanBaseParameter.Direction`, `ExecuteReader`, Stream parameter values | Candidate verified for distinct Inputs. Safe shared Stream reuse unimplemented | Text Input works. Output, InputOutput and ReturnValue fail with NotSupportedException at planning. One Stream for one position works. Two parameters sharing the same Stream are rejected before consumption, including for seekable streams. Two independent streams work. Use independent sources and handle procedure directions through the separate procedure API. | Connector stream lifetime and EF positional adaptation | MySQL probe |
| MySQL | D-06 | `GetSchema("Sequences")`, `GetOracleSequenceStartValue`, `ExecuteNonQuery` | MySQL start API unimplemented. Runtime sequence metadata candidate verified. Server limit for column defaults | After creating START WITH 41 INCREMENT BY 3, NEXTVAL returned 41 and 44. DBA_SEQUENCES and the public collection reported LAST_NUMBER changing from 41 to 47. No start column was present. SHOW CREATE SEQUENCE failed with 1064/42000 and the tested DBMS_METADATA.GET_DDL call failed with 1054/42S22. The Oracle-specific method rejects MySQL mode. No reliable declared start source was found on this deployment. DEFAULT seq.NEXTVAL and DEFAULT (seq.NEXTVAL) failed during CREATE with 1064/42000 and 1235/0A000 respectively. Independent client evidence agrees. Explicit NEXTVAL in INSERT works. Trigger allocation with same-session CURRVAL is a separately documented server pattern. | OceanBase catalog and default grammar. Connector needs verified start source before an API. EF sequence-default mapping remains blocked | MySQL probe and [prior independent default and trigger controls](compatibility.md) |
| MySQL | G-36 | `GetSchema("IndexColumns")`, `GetSchema("Indexes")` | Candidate verified for reported BTREE order, prefix and uniqueness. Server limit for fidelity of declared DESC | CREATE accepted DESC. The tested prefix lengths 7 and 11, ordinals and uniqueness survived. The server returned ASC for DESC declarations, also on a full-width member. Literal SHOW INDEX returned seven rows in the candidate, 0.3.1 and the independent client. Its Collation=A agreed with INFORMATION_SCHEMA.STATISTICS and candidate ASC. SHOW CREATE TABLE omitted DESC in this case. No reliable alternate source was verified. EF can preserve lengths, positions and uniqueness and should treat direction as server-reported. | OceanBase MySQL index catalog, EF scaffolding | MySQL probe |

### MySQL decimal metadata observed on the connection

All rows below are protocol type 246 (DECIMAL). The precision derived from the server's length and sign/decimal-point metadata is shown with the scale.

| Input | Base column | SUM / COALESCE(SUM, 0) | AVG | LEAST / GREATEST |
| --- | --- | --- | --- | --- |
| DECIMAL(65,28) | 65,28 | 81,28 | 69,30 | 81,28 |
| DECIMAL(20,8) | 20,8 | 42,8 | 24,12 | 28,8 |

In the buffered wide NULL case IsDBNull still succeeds when metadata and GetValue fail. SequentialAccess wide AVG failed at IsDBNull with an unknown-outcome protocol error. The tested narrow prepared and sequential controls passed. Narrow empty and all-NULL aggregates preserve NULL or produce zero with COALESCE. Wide COALESCE retains the wide aggregate descriptor, including with an empty or all-NULL input. A prepared wide SUM passes Prepare and first fails in ExecuteReader with a client-local OceanBaseException wrapping the same metadata error. The historical package reproduces the wide decimal failure. The independent MySqlConnector consumer read the wide calculated values, including zero and NULL.

## Both compatibility modes

| Mode | EF gap ID | Public API | Current status | Expected and actual | Owner | Evidence or replay |
| --- | --- | --- | --- | --- | --- | --- |
| Oracle + MySQL | CON-01/04, R-03 | `OceanBaseConnection.Open/OpenAsync`, `CompatibilityMode`, `OceanBaseCommand.CreateParameter`, reader and schema APIs | Candidate verified | Both modes report 4.4.2.2 with expected mode, bind and read an Int32 value, and reuse the connection after fully consuming the reader. Disposing a partially read ordinary result discards the session and marks the connection Broken in both packages. Close and reopen restore use. Both return matching restricted Columns shapes via sync and async calls. | Connector lifetime contract, EF connection ownership | Common probe / logs |
| Oracle + MySQL | CON-05, TXN-01 | `ExecuteScalarAsync(token)`, `State`, `BeginTransaction`, `Commit`, `RollbackAsync` | Candidate verified | Pre-cancel sends no command and the connection remains reusable. In-flight read cancellation produces an unknown-outcome error, marks the connection Broken, and the same connection object works again after Close and Open. Local rollback left no row, a subsequent transaction committed its row. 0.3.1 matches. | Connector, EF must handle disposal and recovery | Common probe |
| Oracle + MySQL | G-40 | `TransactionScope`, automatic enlistment on `OpenAsync`, `EnlistTransaction` | Candidate verified for a single resource | Keeping the connection open through scope disposal commits with Complete and rolls back without it. Early close followed by Complete aborts the scope and the row remains invisible to an independent observer. 0.3.1 matches. | EF connection ownership and enlistment integration remain open | Common probe and [AmbientTransactionTests](../tests/OceanBaseConnector.IntegrationTests/Transactions/AmbientTransactionTests.cs) |
| Oracle + MySQL | R-07 | `OceanBaseConnectionStringBuilder.TlsMode`, `OpenAsync` | Server limit | TLS Disabled opened successfully. Both VerifyFull and Required fail at Open in both packages with client-local `OceanBaseException`, code 0, inner `ProtocolException`, due to absent `CLIENT_SSL`. SQL is skipped for both secure controls. | Endpoint configuration, certificate-verified SQL awaits a TLS endpoint | Common probe and [TLS handshake](../src/OceanBaseConnector/Internal/Transport/TlsHandshakeFlow.cs) |
| Oracle | B-05 | `OceanBaseCommand.ExecuteNonQuery` | Server limit | Direct `CREATE TABLE ... (VAL NCLOB)` fails at execution with `902/HY000` in both packages. EF recorded the same server restriction. Keep native NCLOB support dependent on server version and valid DDL. | OceanBase Oracle | Common probe |
| Oracle | B-07 | `OceanBaseCommand.ExecuteNonQuery` / `ExecuteScalar` | Server limit | Direct CREATE with an identifier containing a doubled embedded double quote fails at execution with `600/0A000` in both packages. A quoted identifier containing a literal backtick is created and queried. | OceanBase Oracle identifier parser | Common probe |
| MySQL | B-06 | `OceanBaseCommand.ExecuteNonQuery` / `ExecuteScalar` | Server limit | Direct BLOB and TEXT literal defaults fail at CREATE with `1067/42000` in both packages. VARBINARY(16) accepts the default and reads back byte `78` in hex. | OceanBase MySQL DDL | Common probe |
| MySQL | B-08 | `OceanBaseCommand.ExecuteScalar` | Server limit and EF SQL work | Direct bounded VARCHAR `JSON_TABLE` control with explicitly mixed UTF-8 collations returns `1267/HY000` in both packages. The matched-collation control returns true. The EF gap records a related bounded/fixed collection-root failure. The SQL control establishes a collation-domain requirement. EF still needs generated-query coverage that preserves property collation, width and comparison semantics. | EF collection-root SQL and server collation rules | Common probe and pinned EF gap matrix |

## Run totals

Counts below are checks rather than test methods. Expected server rejection and negative-control assertions are reported as such. Oracle type logs also record supporting DDL, wire and server-side comparison checks separately. TLS attempts are counted independently of cleartext SQL.

| Mode and suite | Candidate pass / fail / skip | 0.3.1 pass / fail / skip | Scope |
| --- | ---: | ---: | --- |
| Oracle common, TLS Disabled | 11 / 0 / 0 | 11 / 0 / 0 | Connection, SQL and ownership smoke checks. Expected DDL errors included as observations |
| MySQL common, TLS Disabled | 11 / 0 / 0 | 11 / 0 / 0 | Same common contract and mode-specific DDL/collation controls |
| Oracle schema probe, default options | 40 / 3 / 1 | 36 / 7 / 1 | Counts include fixture and cleanup checks. Candidate failures are DESC and ALTER REBUILD server DDL errors. Permission comparison skipped |
| Oracle schema probe, PrepareExecute and LOB locators off | 40 / 3 / 1 | — | Same schema checks with candidate option overrides |
| Oracle B-01 bind fidelity | 120 / 216 / 0 | 120 / 216 / 0 | 336 parameter writes/readbacks per package. Includes 24 signed-zero losses |
| Oracle B-01 D-literal fidelity | 52 / 4 / 0 | 52 / 4 / 0 | Seven requested magnitudes pass. Additional signed-zero control accounts for the four failures |
| Oracle B-10 typed and generic writes | 24 / 0 / 0 | 12 / 12 / 0 | Includes SQL NULL and JSON null |
| Oracle B-10 `GetValue` | 48 / 0 / 0 | 0 / 16 / 24 | Candidate `GetString` and `GetFieldValue<string>` each 32 / 0 / 16. Typed getters intentionally skipped on SQL NULL. Baseline has further reader failures before some getters |
| MySQL dedicated probe | 412 / 114 / 9 | 410 / 114 / 11 | Individual accessor checks. Multiple accessor failures can arise from the same rejected descriptor. Independent MySqlConnector SQL control 432 / 22 / 9 |
| Oracle TLS Open | 0 / 2 / 0 | 0 / 2 / 0 | VerifyFull and Required, 2 corresponding SQL checks skipped per package |
| MySQL TLS Open | 0 / 2 / 0 | 0 / 2 / 0 | Same two TLS modes and SQL skips |

The source integration checkpoint for the same commit recorded 17 complete schema cases and one Oracle JSON case in the combined run, 12 in the Oracle text schema and JSON run, and 62 MySQL JSON checks. The 44 local Oracle JSON and floating-point fixture tests used the candidate assembly. [Source regression record](oracle-json-metadata-verification.md)

## Restore and rerun

Run from the Connector checkout. The probe sources, logs and candidate package are under `TestResults/ef10-audit`. Configure the integration project's .NET User Secrets keys `ConnectionStrings:MySql` and `ConnectionStrings:Oracle` on the executing Windows account. Use test accounts with disposable object creation and cleanup permissions. Keep credentials in User Secrets and out of console output and scripts.

Rebuild the candidate from the recorded clean source revision, or use the recorded package from the local feed.

~~~powershell
dotnet pack src/OceanBaseConnector/OceanBaseConnector.csproj -c Release -p:Version=0.3.1-ef10.b007c5e -p:PackageVersion=0.3.1-ef10.b007c5e -p:RepositoryCommit=b007c5e -o TestResults/ef10-audit/feed
Get-FileHash TestResults/ef10-audit/feed/OceanBaseConnector.0.3.1-ef10.b007c5e.nupkg -Algorithm SHA256
~~~

Restore EF's test consumer with a package reference to `OceanBaseConnector` at `0.3.1-ef10.b007c5e` and include the candidate feed and NuGet.org. In a central package management project update `PackageVersion` in place of `PackageReference.Version`. This audit did not alter EF's dependency configuration.

~~~powershell
$feed = (Resolve-Path TestResults/ef10-audit/feed).Path
dotnet restore <EF-consumer-project> --source $feed --source https://api.nuget.org/v3/index.json
~~~

The detail reports include the per-case logs, supporting SQL and first failure stages. The probe directories contain package-only console projects.

| Probe | Source and evidence |
| --- | --- |
| Common | [Source](../TestResults/ef10-audit/common/Program.cs) · [Candidate](../TestResults/ef10-audit/common/candidate.log) · [0.3.1](../TestResults/ef10-audit/common/baseline.log) |
| Oracle types | [B-01 and B-10 results and commands](../TestResults/ef10-audit/oracle-types/REPORT.md) |
| Oracle schema | [G-36, C-03, D-06 and foreign-key results and commands](../TestResults/ef10-audit/oracle-schema/REPORT.md) |
| MySQL | [Probe](../TestResults/ef10-audit/mysql/Program.cs) · [Candidate](../TestResults/ef10-audit/mysql/candidate.log) · [0.3.1](../TestResults/ef10-audit/mysql/baseline.log) · [Independent SQL client](../TestResults/ef10-audit/mysql/independent.log) |

~~~powershell
$audit = (Resolve-Path TestResults/ef10-audit).Path
$cache = Join-Path $env:USERPROFILE '.nuget/packages'
$examples = (Get-Content tests/OceanBaseConnector.IntegrationTests/integration-tests.example.json -Raw |
    ConvertFrom-Json).profiles
$direct = @($examples | Where-Object name -in 'mysql-direct', 'oracle-direct')
$profiles = Join-Path (Split-Path $audit) 'schema-closeout'
New-Item -ItemType Directory -Force $profiles | Out-Null
@{ profiles = $direct } | ConvertTo-Json -Depth 15 |
    Set-Content (Join-Path $profiles 'direct.json')
$oracleText = $direct | Where-Object name -eq 'oracle-direct'
$oracleText.options.useOraclePrepareExecute = $false
$oracleText.options.useLobLocators = $false
@{ profiles = @($oracleText) } | ConvertTo-Json -Depth 15 |
    Set-Content (Join-Path $profiles 'oracle-text.json')
foreach ($item in @('common/Common.csproj', 'mysql/Probe.csproj', 'oracle-schema/OracleSchema.csproj')) {
    $project = Join-Path $audit $item
    $probeDir = Split-Path $project
    Push-Location $probeDir
    try {
        foreach ($version in @('0.3.1-ef10.b007c5e', '0.3.1')) {
            $properties = @("-p:ConnectorVersion=$version", "-p:AuditVersion=$version")
            dotnet restore $project --source "$audit/feed" --source $cache "-p:RestorePackagesPath=$probeDir/packages" -p:NuGetAudit=false @properties
            if ($LASTEXITCODE) { throw "Restore failed for $item at $version" }
            dotnet run --project $project -c Release --no-restore @properties
            if ($version -eq '0.3.1-ef10.b007c5e' -and $item -like 'oracle-schema/*') {
                dotnet run --project $project -c Release --no-restore @properties -- text
            }
            if ($version -eq '0.3.1-ef10.b007c5e' -and $item -like 'mysql/*') {
                dotnet run --project $project -c Release --no-restore @properties -- --independent
            }
        }
    } finally {
        Pop-Location
    }
}
& "$audit/oracle-types/Run.ps1" candidate
& "$audit/oracle-types/Run.ps1" baseline
~~~

The Oracle schema candidate's `text` run disables PrepareExecute and LOB locators. The Oracle types runner exercises both options. Some probes return a nonzero status for reproduced failures. Read the final per-case result and cleanup totals.

## EF handoff

- **Ready for EF integration** — Oracle native JSON parameter/result mapping and NULL semantics, ascending expression-index members including the long-expression fallback, per-table identity and sequence-start metadata, and scope-restricted foreign-key catalogs. EF still needs to wire the applicable APIs into mapping and scaffolding and verify its generated C# and DDL. At ecbed37 the Oracle index reader admits BTREE and NORMAL, and the tested expression index reports FUNCTION-BASED NORMAL.
- **Ready within the verified contracts** — MySQL BTREE prefix lengths, ordering by ordinal and uniqueness. Both modes' local transactions, cancellation and single-resource ambient behavior give EF a tested ADO.NET lifetime contract. G-40 still needs EF scope ownership validation, particularly early close. Distributed EF participation is outside this audit.
- **Connector work remains** — MySQL wide expression result metadata, a public parameter copy/ownership contract and any supported shared Stream strategy. Oracle BINARY_DOUBLE range fidelity still fails at the bind/server boundary.
- **Server or deployment work remains** — MySQL declared sequence starts and column defaults, DESC fidelity in both modes, Oracle NCLOB and embedded-quote identifiers, MySQL LOB defaults, cross-owner Oracle DDL permissions and TLS-capable endpoints. EF owns SQL generation for B-08 and must verify collation equivalence with actual generated queries.
