# Compatibility and known gaps

## 2026-10-26 P1-09 OCP HTTP discovery

Developed from master/5cbe5c9, P1-09/W4. All five assets expose OceanBaseOcpAddressResolver with a caller-owned HttpClient, explicit HTTP(S) origin, net service name and optional expected SERVICE_NAME. Each logical Open fetches a fresh fixed-route OCP snapshot before idle reuse. The request escapes appName and carries no database credentials. See [contract](obconnector-dotnet-architecture-design.md#ocp-http-discovery), [full evidence](obconnector-dotnet-completeness.md#1553-2026-10-26-ocp-http地址发现) and [audit](obconnector-dotnet-driver-audit.md#48-ocp-http地址发现2026-10-26).

HTTP200, UTF-8 JSON, 1MiB actual bytes, 16 value levels/4096 value nodes, exact NET_SERVICE_NAME and optional exact SERVICE_NAME metadata are enforced. One TCP address group preserves order/duplicates and existing256/65536 limits; explicit policies/weights/blacklists/multiple groups fail. The driver has no HTTP retry/cache/stale fallback. The caller controls HTTP authentication/TLS/proxy/handlers and must disable automatic redirects; final-URI rejection cannot undo a followed redirect. Send/body cancellation is passed through, but arbitrary handlers and long bounded CPU scans are not forcibly interrupted. HttpClient.Timeout with ResponseHeadersRead does not alone bound body consumption.

最终完整单测5788通过（新增101），相关五资产消费者354通过（新增64），均零失败/跳过；Release全解决方案0警告/错误（14.57秒），5C#限定格式验证通过且无诊断。单测9分14秒，消费者7分20秒；五资产同一.NET10host运行，没有新增真实OCP/网络HTTP或实库互操作验收。

Original platform-reader failures (__type mapping, numeric syntax, truncated JSON and trailing commas), exact-node fixture correction and raw numeric whitespace RED remain recorded. The replacement strict bounded parser has a313-fragment syntax comparison against test-only JsonDocument, not exhaustive grammar proof. Controlled HTTP handlers and loopback database peers establish local behavior only; no actual OCP/network HTTP, enterprise trust, Linux or independent-runtime acceptance is claimed. Full OCP/TNS policies/groups/weights, proxy topology/reroute, faults/load and release acceptance remain open. All23IDs/eightpackages stay open; local master only, no push/publication.

## 2026-10-26 P1-09 TNS file discovery

Developed from master/9dc3f06, P1-09/W4. All five assets expose the immutable OceanBaseTnsAddressResolver for explicit UTF-8 file/alias discovery through the existing address-resolver API. Each logical Open reloads the file before idle pool reuse. Standard/wrapped descriptions, aliases/comments/quotes, TCP IPv4/IPv6 and optional exact SERVICE_NAME file-metadata binding are supported. Unsupported policy/group/weight/include/protocol options fail explicitly. See [contract and examples](obconnector-dotnet-architecture-design.md#tns-file-discovery), [complete evidence](obconnector-dotnet-completeness.md#1552-2026-10-26-tns文件地址发现) and [audit](obconnector-dotnet-driver-audit.md#47-tns文件地址发现2026-10-26).

Limits are 1MiB actual UTF-8 bytes, 16 descriptor levels, 4096 combined nodes/aliases, 256 addresses and 65536 canonical output characters. Publish complete replacements; in-place writes have no snapshot guarantee. Only file-open FileNotFound or Win32 sharing/lock conflicts retry, at most five attempts with four cancelable 25ms waits. Persistent conflicts still fail; no stale fallback. The 100ms configured waits do not bound blocked native IO. Pre-cancel/wait-cancel tests do not prove comprehensive mid-read cancellation. Windows sharing tests do not establish Linux acceptance.

最终完整单测5687通过（新增67），相关五资产消费者290通过（新增64），均零失败/跳过；Release全解决方案0警告/错误（23.62秒），5C#限定格式验证通过并保留既有workspace加载警告。单测9分13秒，消费者6分57秒；五资产在同一.NET10host运行，无新增实库互操作验收。

Original missing-type, PORT injection, oversized-alias allocation, blank quoted-name and file replacement failures are retained. File.Move failure and transient File.Replace sharing/missing observations also occurred outside the sandbox; bounded opening retries address those transient observations without promising continuous availability. Five assets share one .NET10host. No new real database/OBProxy/network-filesystem acceptance is claimed. OCP, richer TNS policies, proxy reroute, enterprise trust, faults/load, independent platforms/runtimes and release acceptance remain open; all23IDs/eight packages stay open. Local master commit only, no push/publication.

## 2026-10-26 P1-09 endpoint TCP health cache

Developed from master/bc84ba1. Host Recheck Seconds (default0,0..3600seconds) propagates through every asset's builder/copies/serialization/pool identity. A bounded1024 monotonic failure cache stably puts recent TCP failures last, retaining all candidates and duplicates as fallback. Selection is frozen across each physical Open's retry rounds. Only pre-Attach IOException/SocketException with an uncanceled budget records a failure, including final attempts; TCP success clears it before handshake. Greeting/TLS/auth/identity/SQL/cancellation do not poison reachability. See [contract](obconnector-dotnet-architecture-design.md#endpoint-tcp-health), [complete evidence](obconnector-dotnet-completeness.md#1551-2026-10-26-端点tcp健康缓存) and [audit](obconnector-dotnet-driver-audit.md#46-端点tcp健康缓存2026-10-26).

Pool/private-source/unpooled-selector lifetimes own their caches; ordinary matching borrowers and prewarm sessions share pool state. Pool Clear, credential retirement and Dispose replace pool-owned caches so late old feedback cannot affect a new generation, including reserve-to-constructor races. Pool APIs do not clear unpooled selector-owned hints, which follow selector lifetime. Empty ordinary pool removal also removes health state. There is no global map or active migration/replay.

最终完整单测5620通过（新增71：健康缓存69、池身份2），相关五资产消费者226通过（新增52），均零失败/跳过；Release全解决方案0警告/错误，12C#限定格式验证通过并保留既有workspace警告。五资产同一.NET10host消费；本模块没有新增实库互操作验收。

受限首轮完整单测5480通过/140失败：109为PFX导入、5为Schannel凭据、20为VerifyFull预期不匹配、5为silent TLS等待超时、1为既有异步拓扑超时；相关消费者146通过/80失败，80项均在TestCertificateFactory的PFX导入。相同10个TLS/清池用例普通用户环境通过后，保持源码、夹具、预算和五资产二进制不变，完整单测5620通过（9分21秒）、同226消费者通过（6分31秒）。所有初次失败均同名通过，原报告不删除；拓扑超时原因尚未确认，不声称修复其根因。完整5549单测基线及174消费者基线全部保留。报告位于忽略目录TestResults/endpoint-health，执行日志为根目录TestResults-endpoint-health-*.log，应单独归档。

Initial18unknown-keyRED,2pass/16preferenceRED,84/126GREEN and6expected epoch-mutation failures retained; restored source15targeted checks passed. The portable elapsed-budget test blocks the first TCP metric callback for1200ms with timeout1: local deadline-before-feedback evidence, not actual network TCP timeout or Linux execution. Existing real reports/probes remain historical; this module adds no real database interoperability. OCP/TNS/equivalent adapter, proxy topology/reroute, real health/fault/load, enterprise trust, independent runtimes/platforms and release acceptance remain open. All23IDs/eight packages remain open; local master only, no push/publication.


## 2026-10-25 P1-09 bounded TCP connection retry

Developed from master/0ec3e1c. Connect Retry Count (default0,0..10 additional complete candidate rounds) and Connect Retry Interval (default1,1..60seconds) propagate through builder/copies/pool identity. Full-round TCP failures alone wait/retry; TCP success makes greeting/TLS/auth/identity/command failures terminal. Positive count shares the logical Open deadline across pool wait/prewarm, attempts and backoff; zero retains existing waiting semantics. Resolve/credential snapshot once per logical Open. Caller cancellation/Close interrupt backoff, and subsequent explicit Open can reclaim capacity. No active-session reconnect, transaction migration or SQL replay. See [contract](obconnector-dotnet-architecture-design.md#connect-retry-policy) and [retained evidence](obconnector-dotnet-completeness.md#1550-2026-10-25-tcp-建连重试预算与退避).

最终完整单测5549通过（新增64：重试61、池身份3），相关五资产消费者174通过（新增52）；Release全解决方案及修正后消费者构建0警告/错误，7C#限定格式验证通过并保留既有workspace警告。五资产在同一.NET10host消费；无凭据/无SQL探针5022ms读0greeting，本模块没有新增实库互操作验收。

Original6unknown-keyRED,2pass/4TCPRED,70/113GREEN,4warmupRED,125focused121pass/4Listen-before-Accept fixture failures retained. Shared logical budget fixes the four no-callback MinPool2 warmup cases; listening TCS corrects the four peer scripts without relaxing product assertions. Original build/fixture/NuGet and UTF8 script failures retained in the complete module record. Five assets share one.NET10host; no real-server/OBProxy/independent-runtime/load/release acceptance claimed. Full23IDs/eightpackages remain open; local master only, no push/publication.


## Connection-level server errors

Developed from `master/917e9cc`, P0-05/W2 with P0-02/P0-04 boundaries. Connection-level ERR handling now retires failed sessions and preserves the current request's outcome semantics. See the [error and recovery contract](obconnector-dotnet-architecture-design.md#connection-level-server-errors) and [full module record](obconnector-dotnet-completeness.md#1549-2026-10-25-连接级服务器错误与显式连接重建).

Final complete units: 5,485 passed, including 67 new cases. Affected consumers: 172 passed, including 80 new cases across five assets on one .NET10 host. Release build: zero warnings/errors; six changed C# files pass scoped format verification with the retained workspace-load warning. Without integration opt-in, nine configuration cases passed and two transaction cases were not executed.

The first full unit run had 5,484 passes and one existing partial-cursor fixture failure. Its server terminator write hit a reset before accepting the replacement, which then timed out waiting for a greeting. The fixture now finishes writing the page before the partial branch closes; row two and the terminator remain unread, and isolation assertions remain. The original report is preserved; this does not establish the cause of earlier historical timeouts. The first consumer run had 142 passes/30 failures because the Oracle fixture wrongly required a time-zone probe before transaction controls; optional-probe handling corrected it, without product changes or relaxed assertions.

The four selected real transaction regressions all failed before the initial greeting. An independent no-credential/no-SQL TCP probe also received zero greeting bytes after 5,035ms. No real connection-level ERR fault was exercised, and neither this local ERR matrix nor historical database passes establish final real-server acceptance. Full reset, server/proxy/coordinator failures, enterprise trust, load, independent runtimes and package/release acceptance remain open. All 23 gap IDs/eight work packages remain open; no push or publication.


## Closed callback pool cleanup

Developed from `master/80ddfbe` for P0-01/W1. A pooled Open or OpenAsync inside a Closed state handler completes the captured close chain before borrowing, including nested callbacks and another logical borrower. Pool capacity, captured ownership, early Closed events, external asynchronous rollback interleaving and old-reader generation guards remain in force. Completed scopes release their captured delegates; asynchronous cleanup also releases its lifted local delegate before publishing completion. See the [contract](obconnector-dotnet-architecture-design.md#pooled-close-callbacks) and [full evidence](obconnector-dotnet-completeness.md#1548-2026-10-25-closed-回调中的满池重开与清理资源释放).

The final full unit run passed 5,418 cases, and all 233 selected consumer cases passed. The consumer selection includes the original 173 cases, 40 new callback cases and 20 existing reader lease cases. The earlier 173-case selection omitted those last two groups. There are 92 new unit cases; two existing TLS timeout cases intentionally migrate to successful reuse of the same capacity-one pool. Release build: zero warnings and errors. Scoped formatting and verification of nine changed C# files passed with the existing workspace-load warning. Without integration opt-in, nine configuration cases passed and 12 cases were not executed.

The final real-server repeat failed all 74 executable cases; one case was not executed because alternateDatabase was not configured. Both profiles closed before the initial greeting. An independent TcpClient received zero greeting bytes without sending credentials or SQL. The earlier real run passed 74 cases, including the 16 new cases, but preceded the memory fix and cannot replace final-source acceptance. Historical server evidence covers OceanBase 4.4.2.2 MySQL/Oracle direct connections on Windows/.NET 10 with TLS disabled; five consumer assets share one .NET 10 host.

Original timeout regressions, the four memory failures, the intermediate three-pass/one-fail result and later four-pass result remain recorded. The first full run on final source passed 5,417 cases with one existing synchronous partial-cursor replacement handshake timeout. All 26 focused checks and the complete 5,418-case repeat passed on unchanged binaries; this does not establish a root-cause fix for the original timeout.

Other transaction-log callbacks opening their own full pool, complete reset, concurrency, load, proxy, trust, platform, runtime and package acceptance remain open. Historical nested-table column rejection, unresolved complex RETURNING identity and the unexplained RSA timing failure are retained. Full consumer, DI, package, independent-runtime and full-database suites were not rerun. All 23 gap IDs and eight work packages remain open; this is a local master commit, with no push or publication.

## Complex national member encoding

Developed from `master/a1dddde`, P1-07/W5. Named and nested NCHAR/NVARCHAR2 members, scalar factory arrays and PL outputs use strict encoding according to direction: connection encoding for inputs, queried AL16UTF16 for stored members, and the result snapshot's session national encoding for PL outputs. See the [contract](obconnector-dotnet-architecture-design.md#complex-national-members) and [complete evidence](obconnector-dotnet-completeness.md#1547-2026-10-25-复杂类型-national-成员与方向编码). No public API, configuration or dependencies were added.

Other database national encodings, anonymous ordinary non-NULL national results without trusted storage context, and complex whole-column RETURNING with unresolved identity remain unsupported. Such RETURNING failures preserve prior output values, report an unknown execution outcome and break the session; the application must reconcile the write.

The final full unit run passed all 5,326 cases outside the sandbox with collection parallelism disabled; all 180 related consumer cases passed. The earlier full run (5,325 passed, one RSA-stage wait failed) and sandbox run (5,187 passed, 139 failed) remain recorded. The focused RSA run passed 10 cases and the TLS run outside the sandbox passed five; these repeats do not establish a root-cause fix for the original timing failure.

The selected Oracle suite passed 64 of 65 cases; the historical nested-table column case still fails at the server. The 56 new cases comprise 48 positive cases and eight RETURNING rejection guards. Without integration opt-in, 13 cases passed and 105 were not executed. The Release build had no warnings or errors; scoped format and verification of 18 C# files exited successfully with the existing workspace warning retained. Original failures, the NLS review fix and mutation evidence, and three migrated old cases remain recorded. The existing object fixture now loads its descriptor after table DDL; production freshness and identity checks remain strict.

The five consumer assets share one .NET 10 host. Real-server evidence is limited to Oracle mode on OceanBase 4.4.2.2, a direct connection, Windows, disabled TLS and database AL16UTF16. Full consumer, DI, package, independent-runtime and full database suites were not rerun. All 23 gap IDs and eight work packages remain open; delivery is a local master commit.

## Oracle output FetchSize hint

Oracle OUT/RETURNING and procedure/function execution now accept a positive command [FetchSize hint](obconnector-dotnet-architecture-design.md#oracle-output-fetch-hint) with explicit or implicit preparation. Internal output rows stay in the execution response with cursorFlag0 and no FETCH; returned REF CURSOR readers use their own page size. This does not add output-result pagination. Output offset and ExecuteArray positive FetchSize remain unsupported; ordinary query cursors keep their existing behavior.

[Module evidence](obconnector-dotnet-completeness.md#1546-2026-10-25-oracle-输出执行与-fetchsize-提示): original24RED, final5280unit, affected510consumer (30added), expanded58real (20added), noopt13pass/101NotExecuted. New real cases cover Oracle4.4.2.2 direct/Windows/.NET10/TLSdisabled and five command shapes; consumers load five assets on one.NET10host. Source review found no actionable findings. No full consumer/DI/package/independent-runtime/full-database rerun or proxy/trust/platform acceptance; REF CURSOR IN/INOUT/columns and all23gap IDs remain open. Historical module snapshots below retain their original boundaries.

## Lost transaction acknowledgement

Commit/rollback response loss now has sixteen controlled real-server cases and sixteen local lifecycle cases. The local relay first receives a real server reply, then drops/truncates it. The driver reports unknown outcome and discards the session; independent committed-data reconciliation and a fresh same-endpoint Max1 lease work before clearing. This is existing behavior acceptance, with no product/API change. [Application reconciliation guide](unknown-outcome-recovery.md), [full evidence/limits](obconnector-dotnet-completeness.md#1545-2026-10-25-事务响应丢失与池恢复验收).

Final full units5234pass; expanded related suite160pass/4configskips includes4local fixture cases; noopt13pass/100NotExecuted. Original fixture failures retained and stop/Accept race addressed. Direct4.4.2.2 dual-mode/Windows/.NET10/TLSdisabled only, fault after a real response. Server/coordinator crash, other send boundaries and complete proxy/TLS/platform acceptance remain open. Consumers/DI/packages/runtimes/full database suites were not rerun.

## External persistent LOB acceptance

Four direct Oracle cases now prove actual external BLOB/CLOB locators, exact full data and healthy same-session pool reuse after disposal (100000/220000 UTF8 bytes, sync/async). Eighteen local controlled in-flight chunk cases cover cancellation/Close/disposal/timeout and same-endpoint single-capacity replacement before clearing; these local cases do not prove real-server in-flight cancellation. [Full evidence and limits](obconnector-dotnet-completeness.md#1544-2026-10-25-外置持久-lob-读取与池生命周期验收).

Tests/docs only. Final units5218pass; scoped real119pass/4configuration skips; noopt9pass/99NotExecuted. Review diagnostics/budget suggestions addressed; original Close-assertion failures retained. Direct OceanBase4.4.2.2/Windows/.NET10/TLSdisabled only, new real casesOracle only. Full fault/concurrency/load/reset/other-deployment acceptance remains open; consumers/DI/packages/independent runtimes/full database suites were not rerun. The next section retains the earlier inline-only module's evidence and limits.

## Persistent LOB and REF CURSOR pool isolation acceptance

Same-endpoint single-capacity pool tests now cover twenty persistent BLOB/CLOB and eight Oracle output-cursor lifecycle cases, both locally and on the configured direct Oracle server. Active LOB handles discard the old physical session; fully disposed read-only LOBs allow reset/reuse. Stale reads, writes, truncation, binding and disposal preserve the new lease, and an independent unpooled observer verifies the stored value's exact text/bytes. REF cursor phases additionally retain conservative opaque-call session retirement; they do not prove reuse after cursor release. [Contract and limits](obconnector-dotnet-completeness.md#518-readerlob-与-ref-cursor-的剩余验收), [original failures and final evidence](obconnector-dotnet-completeness.md#1543-2026-10-25-持久-lob-与-ref-cursor-的同端点池隔离验收).

This is acceptance of existing behavior, with no production change. Full units pass5198; scoped real Connections/XA/REF CURSOR/LOB pass115 with4configuration skips; no-opt-in Integration9pass/98NotExecuted. All twenty real persistent locators report inline=True: external LOB network reads/cancellation, full concurrency/load and complete reset matrices remain open. Real scope remains direct OceanBase4.4.2.2, Windows/.NET10, TLSdisabled; new resource cases are Oracle-only. Consumers, DI, packages, independent runtimes, OBProxy, enterprise trust and full database suites were not rerun. Original fixture failures and historical server failures remain recorded.

## Pooled reader CloseConnection ownership

A completed reader's CloseConnection callback now checks the original logical Open lease, so it cannot close a later lease that reuses the same pooled physical session. Batch captures that lease with its reservation and guards the release-to-close window. Current-lease reader closure and public Close retain their behavior. [Contract](obconnector-dotnet-architecture-design.md#closeconnection-reader-ownership) and [original failures/final evidence](obconnector-dotnet-completeness.md#1541-2026-10-25-旧-reader-延迟关闭与池租约隔离).

Final full units pass 5,170, five-asset consumers pass 2,918, DI passes14; direct Connections+XA+REF CURSOR+LOB passes87 with4configuration skips. New same-physical-session tests pass both MySQL/Oracle sync/async. Consumers run in one .NET10 host; real evidence is limited to OceanBase4.4.2.2 direct, Windows/.NET10, TLS disabled. This does not close the complete reader/resource/reset, arbitrary concurrency/load, OBProxy, trust, independent-runtime or release matrix; prior failures and skips remain historical.

## Pooled XA isolation acceptance

New local tests cover seven XA phases, both modes and sync/async Close: dedicated sessions close without reset or implicit XA completion; a single-capacity pool authenticates a replacement before clearing, and stale resource APIs reject after logical reopen. Twenty new direct cases cover idle/completed dedication and prepared-branch recovery after Close. Final full units pass 5,162; Connections + XA passes 76 with 4 configuration skips. These are existing behavior acceptance results, with no production code changes. They apply only to OceanBase 4.4.2.2 direct MySQL/Oracle on Windows/.NET 10 with TLS disabled. Real active/suspended disconnect recovery, response loss, coordinator crash and the complete deployment matrix remain open. [Full evidence and retained limits](obconnector-dotnet-completeness.md#1540-2026-10-25-xa-池隔离与-prepared-分支恢复验收).

## Oracle unknown-precision NUMBER results

The [result contract](obconnector-dotnet-architecture-design.md#oracle-number-results-with-unknown-precision) preserves exact Oracle values when precision is unknown; advertised schema metadata remains unchanged. Known-precision and parameter constraints remain strict. The coefficient/value-scale representation can change to reflect the returned text. Four new direct Oracle4.4.2.2/Windows/.NET10/TLS-disabled tests cover bindings and real table/CAST rounding; this is a scoped result-reading regression, not full Oracle acceptance.

**Oracle autocommit follow-up:** the previously invisible fresh/post-reset implicit writes now pass after explicit initialization. The direct MySQL+Oracle pool suite changed from18pass/4fail/1skip to22pass/0fail/1skip; the four same-name Oracle failures cover sync/async fresh and reused sessions. The missing alternateDatabase profile still skips ChangeDatabase. These are OceanBase4.4.2.2/Windows/.NET10/TLS-disabled results; full pool/resource/XA/OBProxy/other-platform acceptance remains open. The [baseline contract](obconnector-dotnet-architecture-design.md#oracle-autocommit-baseline) and completeness15.37/15.38 retain the original failures and RED checkpoints.

## Oracle autocommit verification scope

The final full unit suite passes 5,134 cases, full five-asset consumers pass 2,898 cases, and the final direct Connections + OracleNumber suite passes 52 with 4 configuration skips. Full consumers use one .NET 10 host. Separately, the two standalone raw-wire samples compile and run on installed Windows .NET 8.0.31 / 9.0.20 / 10.0.12 (6 smoke checks); these limited samples do not establish full suites or database acceptance on those runtimes. Original failures and exact limits remain in [the module record](obconnector-dotnet-completeness.md#1539-2026-10-25-oracle-自动提交与真实池隔离). Full OBProxy, trusted TLS, resource, XA and concurrency acceptance remain open.

## Slow client operation diagnostics

[Slow Query Threshold](obconnector-dotnet-architecture-design.md#slow-query-diagnostics) is an opt-in millisecond client-lifetime threshold, default zero. It covers named execute/cursor.fetch exchanges through reader completion, includes consumption time, preserves errors/unknown outcomes and emits no SQL/values. It provides independent Warning/counter/existing-span channels. Server-only profiling, protocol diagnostics, load limits and the complete deployment/runtime matrix are not implied.

## Connection phase diagnostics

The [phase-duration histogram](obconnector-dotnet-architecture-design.md#connection-phase-diagnostics) measures actual acquisition/discovery, transport/handshake and pool wait/probe attempts without adding high-cardinality or sensitive labels. It preserves existing operation telemetry. A phase interrupted by a linked timeout token may report `canceled` while the enclosing Open reports `timeout`; an unsignaled finite pool wait reports `elapsed`. The full P2-04 diagnostics scope, deployment performance limits and independent-runtime acceptance remain open.

## Oracle standalone Prepare

[Standalone preparation](obconnector-dotnet-architecture-design.md#oracle-standalone-prepare) now supports Oracle text OUT/RETURNING, stored procedures and function returns. It neither executes the target nor consumes input values; server parse/privilege/metadata-lock costs remain possible. The command retains a statement only until the next execution transfers ownership to its cursor owner. Full parameter metadata and the exact pure-OUT DML split layout are validated separately; other mismatches reject. At that preparation-module checkpoint, positive FetchSize for output execution was unsupported; the later output-hint module above accepts the hint without paging internal outputs. Prepare still ignores execution FetchSize; REF CURSOR IN/INOUT/column values remain unsupported.

The direct Oracle4.4.2.2 profile passed ten sync/async block/RETURNING/procedure/function/cursor cases and twenty final cases including existing procedure/cursor and prepared array regressions. Preparation left the test table empty and outputs unchanged; REF CURSORs remained consumable after command disposal. The initial two RETURNING count failures (one wire input plus one output column), profile-data fixture error and all original reports remain in [the module record](obconnector-dotnet-completeness.md#1534-2026-10-25-oracle-独立-prepare). This profile uses TLS disabled on Windows/.NET10; real proxy/TLS, other versions and metadata-lock/fault matrices remain open.

## Result character sets

Ordinary result strings now use the [explicit strict collation map](obconnector-dotnet-architecture-design.md#result-character-sets): ASCII, MySQL latin1/CP1252, GBK, GB18030-2005, UTF-16/LE and mapped UTF-8 families. The result decoder and MySQL width metadata share one map; binary63, JSON/CLOB and trusted Oracle national rules keep their own contracts. Unknown/2022 families are rejected, not guessed. Client command/parameter encoding remains UTF-8; this is not general charset negotiation.

The real direct OceanBase4.4.2.2 MySQL test passes six charset cases, each covering eight text/prepared × buffered/sequential × sync/async combinations, including exact GB18030 corrected characters, NULL and connection reuse. UTF16/LE direct SET attempts remain recorded as server1231/42000 failures; the final cases use `character_set_results=NULL` to preserve expression encoding and assert string FieldType. The initial Int32-versus-Int64 literal assertion and an intermediate test-source compilation failure are retained as fixture/development failures. Local dual-mode reader tests and five-asset consumers cover additional paths; they do not establish Oracle/proxy/TLS compatibility. Full evidence and environment limitations are in [the module record](obconnector-dotnet-completeness.md#1532-2026-10-25-普通结果字符集).

## Ordinary pool configuration identity

Internal [pool configuration normalization](obconnector-dotnet-architecture-design.md#ordinary-pool-configuration-identity) now unifies equivalent option order, typed spelling and effective defaults for ordinary pool reuse, capacity, failure cleanup and clearing. Parsed empty PAM factors remain distinct from absent factors across Open/Close/Clear; text values, policy/certificate references and private sources retain their isolation. Public ConnectionString serialization is unchanged. Pooled Open inside a Closed state handler now completes the captured cleanup before borrowing; see [the current callback contract](obconnector-dotnet-architecture-design.md#pooled-close-callbacks). Other transaction-log reentry can still wait on its own full pool. Full reset, arbitrary reentry/concurrency, long-running load, proxy/platform and independent-runtime acceptance remain open; see [the module record](obconnector-dotnet-completeness.md#1531-2026-10-25-普通池配置归一化).

## MySQL stored-procedure execution

The module developed from `1bb13cb` adds MySQL CommandType.StoredProcedure with Input/Output/InputOutput in collection order. It requires effective PS multi-results, uses prepared SET/CALL plus a hidden output SELECT, and drains remaining results on reader close. Output values follow actual SELECT metadata rather than forced parameter DbType conversion. ReturnValue, FetchSize, SchemaOnly and KeyInfo are rejected; InputOutput streams are unsupported. Oracle execution keeps its PrepareExecute contract. See [API and lifecycle rules](obconnector-dotnet-architecture-design.md#mysql-stored-procedures) and [final module evidence](obconnector-dotnet-completeness.md#1530-2026-10-25-mysql-存储过程执行本地交付).

Final full regression records4414unit and2558consumer passes, zero failures/skips, plus14DI passes. Consumer count/name/ID multisets add80cases without removing existing ones; all11loaded DLL hashes/timestamps remain unchanged. These five assets execute on one .NET10host, not five independent runtimes. The earlier [development checkpoint](obconnector-dotnet-completeness.md#1529-2026-10-25-mysql-过程开发与完整文档检查点) retains local TLS certificate-loading failures and partial results. The same built80-case consumer selection passes under the normal user environment without changing certificates or the driver; sandbox failures occurred during PKCS#12 private-key import before TLS/procedure exchange. These are local transport fixtures, not real TLS interoperability. A default-policy direct-server run passed the four procedure cases and missing-routine1305 case; a broader applicable MySQL run passed74/skipped5, with9configuration cases included. The original unfiltered run retains19Oracle-only no-data failures and one stale null-enlistment assertion, corrected to ArgumentNullException. Historical suite totals, the Oracle nested-table-column failure and cleanup failure below remain unchanged.

The tables distinguish recorded passes, failures, explicitly unsupported behavior and unverified combinations. Implemented APIs and passing local fixtures do not establish server compatibility.

Routine parameter derivation has a local implementation on all five assets: [API contract](obconnector-dotnet-architecture-design.md#routine-parameter-derivation) and [mapping limits](schema.md#parameter-derivation). It uses a unique visible routine identity, one reserved session/current transaction, per-query timeout, cancellation and atomic collection-membership publication. It does not execute the target or add MySQL StoredProcedure execution. Actual directory parsing, permissions, declarations, version and OBProxy acceptance remain unverified; local fixtures cannot establish them. The [development checkpoint](obconnector-dotnet-completeness.md#1517-2026-10-25-参数推导开发与完整文档检查点) retains earlier failures and is not the final delivery state.

User-SQL pool state classification now has a shared mode-aware path and a target-send boundary: [contract](obconnector-dotnet-architecture-design.md#pooled-session-state-after-user-sql). Commented/later statements and opaque calls retire the session; preparation and local rejection do not mark it as executed. MySQL quoted backslash/SQL_MODE ambiguity is conservative and can reduce reuse. This does not close arbitrary function/trigger effects, resource/fault/concurrency matrices or real reset acceptance; see P0-02 in the completeness baseline.

Opt-in [prepared statement caching](obconnector-dotnet-architecture-design.md#prepared-statement-cache) has a local implementation on all five assets. The idle LRU is per physical session; reset, observed DDL/session context and successful ChangeDatabase invalidate entries. Held statements are revalidated after acquiring the exclusive session lease. Active ownership and deferred closes remain exclusive; metadata probes and Oracle PrepareExecute keep their existing owners. External DDL/reprepare, server resources, OBProxy, load and independent runtimes remain unverified; synthetic prepare delays and packet counts are not real database performance evidence.

Oracle [complex-type DDL invalidation](obconnector-dotnet-architecture-design.md#complex-type-ddl-invalidation) has a local implementation: observed text CREATE/ALTER/DROP/RENAME/TRUNCATE target execution invalidates descriptions independently of statement caching. Prepare and pre-send rejection preserve them; reload types and rebuild bindings after invalidation. External or opaque DDL requires explicit `ClearComplexTypeCache`. Real DDL/OBProxy/implicit-commit/lock acceptance remains open.

## Server and topology matrix

| Target | Status | Boundary |
| --- | --- | --- |
| Direct OceanBase 4.4.2.2, MySQL mode | Partially verified | 2026-10-24 integration: 60 passed; TLS disabled. |
| Direct OceanBase 4.4.2.2, Oracle mode | Partially verified; suite fails | 2026-10-24 integration: 79 passed, 1 failed (nested-table column); TLS disabled. |
| Other OceanBase versions, either mode | Unverified on real servers | No exact-release allowlist. Open requires a parseable actual OceanBase version, a session mode matching authentication, and the required base protocol/authentication capabilities. Function-specific version/capability checks still apply. |
| OBProxy, either mode | Unverified | No verified proxy-version/routing/fault matrix. Direct-server results do not establish OBProxy compatibility. |
| Native MySQL / native Oracle | Unsupported | This provider targets OceanBase; MySQL wire compatibility does not bypass OceanBase identity confirmation. |

All successful opens confirm `ob_version()` and `@@ob_compatibility_mode` in one read-only query;
MySQL-mode opens also confirm `@@autocommit` in the same query and reject sessions with autocommit
disabled (see the unsupported table below). The reported mode must agree with authentication; missing, unknown or conflicting identity data
fails Open. Product versions are distinct from the MySQL-compatible greeting. A parseable new
release is not rejected merely because it has no recorded acceptance run, but an untested
release is not thereby supported or certified. No blanket minimum OceanBase release is claimed
from the available evidence. PrepareExecute >=2.2.76 and offset fetch >=2.2.5 are feature gates only.

Client behavior is independent of server version: `Treat Tiny As Boolean=true` maps signed MySQL
TINYINT(1) to Boolean (nonzero=true), and `Use Affected Rows=false` requests matched UPDATE rows.
Both can be changed explicitly; neither fabricates a server capability or selects a JDBC profile.
See the [API reference](obconnector-dotnet-architecture-design.md) for value and option rules.

## Recorded database tests

### 2026-10-24 integration suites

The current working tree was tested on Windows/.NET 10 against direct OceanBase 4.4.2.2 in both modes with TLS disabled, using .NET User Secrets. Oracle enabled PrepareExecute and LOB locators. Both modes supplied verified slow SQL for cancellation and command timeout.

| Mode | Passed | Failed |
| --- | ---: | ---: |
| MySQL | 60 | 0 |
| Oracle | 79 | 1 |

Five additional environment-dependent tests were skipped: unsupported authentication, disabled-autocommit login, alternate database, OB20 and trace. Nine configuration regression tests passed separately from the database cases. The raw run is `TestResults/review-20261024/direct-final.trx` (ignored local evidence).

Real testing identified two driver defects: MySQL identity confirmation rejected the BIGINT metadata returned for `@@autocommit`, and complex Oracle pure-output parameters sent a typed complex NULL instead of a protocol NULL placeholder. Both are fixed in this working tree. Collection procedure IN/OUT and IN/InputOutput round trips now pass; the input, response-type and session-generation checks remain enforced. The remaining nested-table-column failure is described below.

### 2026-10-21 acceptance console (historical)

The following results were recorded on 2026-10-21 for runtime-code baseline `304642e` with dependency updates, on Windows/.NET 10. Both profiles connected directly to OceanBase 4.4.2.2 with TLS disabled. Oracle enabled PrepareExecute and LOB locators; MySQL configured a slow query for cancellation and timeout tests.

| Mode | Passed | Failed | Skipped | Cleanup failures | Exit code |
| --- | ---: | ---: | ---: | ---: | ---: |
| MySQL | 38 | 0 | 27 | 0 | 0 |
| Oracle | 49 | 1 | 15 | 1 | 1 |

The Oracle suite did not pass. Skips include mode-specific and missing-environment cases; they are not passes. The original report paths were `TestResults/release-parity-{mysql,oracle}-02.json`. Those ignored reports are absent from this checkout, so these counts are retained from the [original verification record](https://github.com/cocosip/obconnector-dotnet/blob/f63d57c/docs/obconnector-dotnet-progress.md), not recomputed from reports or a new database run.

### Failed: Oracle nested-table columns

`complex.collection` fails at the second statement, before binding any collection value:

```sql
CREATE TYPE OBA_NUMBER_LIST AS TABLE OF NUMBER;
CREATE TABLE OBA_COLLECTION_TABLE (id INTEGER PRIMARY KEY, value OBA_NUMBER_LIST);
```

On the tested server, both .NET and Connector/J 2.4.14 returned error `600 / 0A000` (`nested table type not supported`). Java reproduced this with ordinary prepare and combined PrepareExecute. The pinned [server DDL resolver](https://github.com/oceanbase/oceanbase/blob/5e8a63d29dae9fde5e962b8fcc7b167d438fc343/src/sql/resolver/ddl/ob_ddl_resolver.cpp#L3930) also rejects nested-table column types.

This is a limitation of collection-valued table columns on that server. It does not establish a limitation on collection procedure parameters or array binding. The recorded `complex.object` and `oracle.array-binding` cases passed. Java's [collection example](https://github.com/oceanbase/obconnector-j/blob/6adc311ba5db8b2f19f98f3e6e4734667fdfdda1/src/test/java/com/oceanbase/jdbc/ComplexOracleTest.java#L411) uses a procedure parameter, a different operation.

The cleanup failure was an attempted DROP of the table whose CREATE failed. The original record reports independent cleanup finding no remaining owned objects or prepared XA branches. It does not cancel the failed case or change exit code 1. Original comparison/cleanup paths were `TestResults/release-parity-java-01.log`, `TestResults/release-parity-java-01-cleanup.log` and `TestResults/release-parity-{mysql,oracle}-02-cleanup.log`; these files are also absent from this checkout.

## Deferred acceptance cases

These six entries in [DatabaseCases.cs](../tests/OceanBaseConnector.Acceptance/DatabaseCases.cs) have explicit `DeferredReason` values. They report skips, not successful validation.

| Case | Missing validation / implementation |
| --- | --- |
| `matrix.tls-authentication` | Trusted certificate chains, hostnames, plugin combinations and negative authentication profiles. TLS-disabled acceptance does not validate VerifyFull. |
| `matrix.ob20-trace-negotiation` | Actual server OB20/full-link-trace negotiation and trace output. Ordinary SQL success does not prove either was negotiated. |
| `matrix.topology-failover` | Controlled discovery, endpoint failures and routing on real deployments. |
| `matrix.network-outcome-unknown` | Mid-request/response failures, cancellation ordering and interrupted commit/rollback on real deployments. |
| `matrix.pool-reset` | Local pooling/reset fixtures pass; real OceanBase/OBProxy reset behavior and cross-version validation remain open. |
| `matrix.xa-crash-recovery` | External coordinator recovery under client/server process failure. Basic XA phase tests do not cover this. |

Further skips depend on selected groups, compatibility mode, `slowQuery`, `alternateDatabase`, DDL permissions and Oracle extension options. If a connection prerequisite fails, dependent cases are skipped. See [AcceptanceSuite.SkipReason](../tests/OceanBaseConnector.Acceptance/AcceptanceSuite.cs); a zero exit code can still include skips.

## Explicitly unsupported APIs and shapes

| Area | Unsupported behavior |
| --- | --- |
| Pooling | Maximum capacity, wait timeout/cancellation, minimum-pool prewarming with failed-warmup rollback, borrow validation including MySQL autocommit status, idle/total lifetime reaping, prepared statement generation invalidation, unconsumed REF CURSOR/live LOB disposal, recognized user session-state or temporary-object discard, empty static-registry cleanup and pool lifecycle metrics are supported locally. Client certificates are supported with reference/fingerprint pool isolation and owned copies. ClearPool/ClearAllPools retire ordinary idle/borrowed sessions; private sources use Clear or ClearPool. Active work continues; old borrowed sessions still occupy capacity until closed. Lifetime reaping remains lazy. Full resource reset and server-specific reset interoperability are not yet validated. See [clearing contract](obconnector-dotnet-architecture-design.md#explicit-pool-clearing). |
| Pool routing identity | Ordinary pools now compare both connection string and resolver reference identity. Every logical Open resolves once and checks idle-session host/port membership against that snapshot, including minimum-pool prewarm. Removed endpoints are discarded before reuse; active borrowed sessions are not migrated. Resolver failure/cancellation cannot fall back to idle or configured endpoints. Data sources retain private pools. DNS IP changes behind an unchanged hostname and real topology/fault profiles remain unverified. See P0-06 in the [gap baseline](obconnector-dotnet-completeness.md). |
| Multi-statements | Disabled by default. `Allow Multi Statements=true` enables negotiated text multi-statements when the server advertises `CLIENT_MULTI_STATEMENTS`; parameters, Prepare, server cursors and Oracle output paths are rejected. Real OceanBase/OBProxy error and cancellation matrices remain unverified. |
| Oracle program units | Anonymous BEGIN/DECLARE blocks (including labels) and CREATE procedure/function/package/trigger/type units do not require multi-statement negotiation for internal semicolons. The complete program is sent for server parsing; real-server validation of program forms and invalid trailing SQL remains open. |
| LOCAL INFILE | Opt-in protocol path is implemented. `Allow Local Infile=true` advertises `CLIENT_LOCAL_FILES`; `LocalInfileStreamFactory` supplies the readable stream, and the driver never opens the server filename as a local path. Factory/read/cancel/final-response failures destroy the physical session. Real OceanBase/OBProxy `LOAD DATA LOCAL INFILE`, proxy policy, long-file and interruption behavior remain unverified. |
| Compression and OB20 extensions | Standard MySQL zlib compression is opt-in with `Use Compression=true`, switches after authentication and requires server support; unsupported servers fail before credentials. All five assets share bounded block framing and strict zlib validation. `Use Compression=true` with `Use OB20=true` is rejected before networking. OB20 compressed frames and proxy reroute remain unsupported; real OceanBase/OBProxy compression interoperability is unverified. |
| TLS/mTLS | `VerifyFull` uses platform validation by default; the optional `RemoteCertificateValidationCallback` replaces that decision and is supported on all assets, with a builder entry on modern data sources. Callbacks require VerifyFull and are isolated by reference identity in ordinary pools; they run only on new physical TLS sessions. `Required` encrypts without server identity validation. `ClientCertificate` supports pooled/direct connections; modern builder `UseClientCertificate` captures a source-owned snapshot even without pooling. Ordinary certificates must remain valid through Open, while source originals may be disposed after Build. `Check Certificate Revocation=true` enables platform revocation checks; the callback must enforce its intended handling of those errors. Local mTLS and policy isolation have loopback coverage; real enterprise CA/mTLS, rotation, revocation and cross-platform profiles remain unverified. See [client certificate contract](obconnector-dotnet-architecture-design.md#tls-client-certificates) and [server validation contract](obconnector-dotnet-architecture-design.md#tls-server-validation). |
| Decimal parameter declarations | Nullable OceanBaseParameter.DecimalScale supports explicit Oracle -84..127 and MySQL 0..30, with per-mode precision bounds and scale <= nonzero precision for MySQL. Standard Scale clears the override; a non-null override clears the old Scale and clearing it does not restore that overwritten declaration. Exact constraints reject rounding, typed NULL retains metadata validation and non-decimal parameters reject the override. Supported server/client prepare paths share the declaration. Local unit/five-asset wire evidence is recorded in the [module record](obconnector-dotnet-completeness.md#1516-2026-10-25-有符号-decimal-scale-本地交付); real NUMBER/DECIMAL declarations and version/OBProxy matrices remain unverified. Parameter derivation retains the signed declaration. |
| ADO.NET adapters and key metadata | `OceanBaseDataAdapter` and `OceanBaseCommandBuilder` have a local [fill/CRUD generation implementation](obconnector-dotnet-architecture-design.md#data-adapters-and-command-generation) on all five assets: synchronous sequential updates, mappings/events, caller transactions and optimistic concurrency. Automatic UPDATE/DELETE require a complete visible chosen row key; unknown shapes and context-dependent locator/complex/cursor bindings require explicit commands. Generated identity/default refresh is not automatic; real CRUD/locking/version/OBProxy/runtime acceptance remains open. `KeyInfo` has a bounded [source-key/hidden-column contract](obconnector-dotnet-architecture-design.md#key-information-and-hidden-columns): same-session pre-execution catalog snapshots, primary/nonnullable unique row keys and hidden tail columns only for proven single-base-table SELECTs. Nullable single-column UNIQUE is distinct from a nonnullable row key; composite members are not individually unique. Complex/ambiguous queries preserve SQL and shape with unknown keys, and use actual execution metadata. Real dictionary/permissions/DDL/version/OBProxy acceptance remains open. `SchemaOnly` uses server-prepare metadata for a single accepted text query with input parameters, never executing the target, uploading/encoding values or using client fallback. It reuses valid prepared metadata and ignores fetch/offset hints. Server preparation errors propagate; prepare-time metadata can differ from the executed result, and server parsing/metadata locks remain possible. Named complex metadata may require directory lookup. Local sync/async/five-asset regressions exist; real side-effect/lock/large-result acceptance remains open. `DbBatch` accepts only `CloseConnection` and `SequentialAccess`. |
| Disabled autocommit | MySQL-mode sessions whose `@@autocommit` is 0; Open fails because the provider cannot commit implicit transactions. Connector/J forces `SET autocommit=1` instead; this driver sets no session variables. |
| Ambient transactions | Local TransactionScope/EnlistTransaction uses the coordinator single-resource slot on one physical session. Second database resources and promotion are rejected; unknown COMMIT reports InDoubt, confirmed commit survives failed cleanup, and late callbacks cannot affect a reopened/pooled session. After successful enlistment, reopening after Close inside the unfinished scope remains unsupported; an Open whose initialization failed can be retried after the failed resource is released. Distributed atomic commit/recovery and conflicting ambient/local/XA combinations remain unsupported. Initialization is synchronous even for OpenAsync but observes the opening token; cancellation during a pending control response closes the failed connection, discards the session and releases the enlistment/pool slot. The token does not affect commit/rollback after successful Open. CommandTimeout remains a separate exchange budget; scope timeout does not immediately cancel the wire wait. Local/five-asset evidence is recorded in the latest module checkpoint; real OceanBase/OBProxy/independent-runtime acceptance remains open. See the [ambient contract](obconnector-dotnet-architecture-design.md#ambient-transactions). Explicit XA remains separate. |
| Standard assets | `DbDataSource`, `DbBatch`, savepoint APIs and GSSAPI require modern TFMs; complete cross-TFM behavior and real GSSAPI/KDC validation remain unverified. |
| Savepoints | Oracle release; quoted/Unicode names and leading underscores such as `__EFSavePoint`. |
| Binary stream parameters | InputOutput, text streams, array-binding combinations and client-prepare fallback. |
| Oracle outputs/cursors | REF CURSOR Input/InputOutput and ordinary cursor-valued columns; output offset and ExecuteArray positive FetchSize. Standalone Prepare and positive command hints for ordinary output plans are supported; internal output rows do not use cursor paging. |
| Complex types | Text-protocol complex results, cyclic layouts and LOB/cursor members; other national storage encodings than queried AL16UTF16, anonymous ordinary national members without trusted storage context, complex whole-column RETURNING with unresolved source identity, and undeclared or stale type identities. |
| Array execution | SELECT, DDL and FetchSize; execution does not commit or roll back automatically. |
| Discovery and recovery | Basic TCP TNS file and OCP HTTP address discovery are implemented; full OCP/TNS policy/group discovery, transparent migration of active sessions/transactions and automatic SQL replay remain unsupported. |

For supported alternatives and binding rules, see the [API reference](obconnector-dotnet-architecture-design.md).

## Credential provision and rotation

Per-logical-Open sync/async acquisition, full password replacement, provider-reference pool isolation, version ordering, expiry and old-session retirement have local implementations; see the [credential contract](credentials.md). The [verification ledger](obconnector-dotnet-progress.md) records final module gates and earlier test-wiring failures. This is local protocol evidence, not a real credential-service or plugin compatibility claim.

Live-source version history survives Clear; empty ordinary registry eviction resets pool-local history. Providers own caching/refresh and are never disposed by the driver. Existing borrowers are not interrupted by rotation or login-credential expiry. Real credential services, token/plugin rotation, KDC, OBProxy and cross-platform behavior remain unverified; local native/PAM fixtures do not close that matrix.

## Catalog metadata

`GetSchema` provides a local implementation for 20 Oracle / 18 MySQL collections, including static DataTypes with final local unit and five-asset consumer evidence; see the [module record](obconnector-dotnet-completeness.md#1514-2026-10-25-datatypes-本地交付); the full [Schema contract](schema.md) lists shapes, restrictions and permission requirements. Types/Synonyms are explicitly unsupported on MySQL. Static DataTypes now reports canonical codec-supported declarations and CLR/provider representation variants, with explicit precision/scale, session/encoding/negotiation requirements and parameter/result limitations. It does not validate server DDL or add KeyInfo or a DataAdapter implementation. Automatic parameter derivation is a separate command API with the mapping limits described above. The previously committed dictionary SQL is checked against pinned OceanBase source, with protocol fixtures and five-asset catalog consumers; the final affected consumer run also executes all 16 DataTypes catalog cases on the .NET 10 host; actual dictionary parsing, privileges, collation, older versions and OBProxy still require real-server reports. The historical 2026-10-24 database passes predate this extension and do not validate it.

## Runtime and integration coverage

Local fixture and package checks cover different baselines and do not replace database validation. The integration suite (connection and identity gates: direct/OBProxy mode checks, TLS-name rejection, unsupported-plugin and autocommit-disabled rejection, plus the full functional suites) has not been established as passing; it runs only against a configured external database and is skipped everywhere else.

Full non-Windows deployment, every runtime implementing .NET Standard, real Kerberos/KDC credentials and a complete TLS/plugin matrix remain unverified. Historical .NET Framework checks cover basic consumption through a net48 host running installed Framework 4.8.1, not formal original-4.8 or full SQL/TLS support. See [verification status and remaining work](obconnector-dotnet-progress.md).
