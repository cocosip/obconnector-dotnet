# Client SQL and parameter character sets

`OceanBaseConnectionStringBuilder.CharacterSet` / `Character Set` selects the encoding of
SQL, database-selection requests and ordinary character parameters. `CharacterSet` is a keyword
alias. Names are case insensitive and canonicalized; `utf8` means `utf8mb3`.

| Accepted names | Input boundary |
| --- | --- |
| `utf8mb4` (default) | Existing strict UTF-8 behavior, including supplementary characters |
| `utf8mb3`, `ascii`, `latin1` | No supplementary characters in mb3; ASCII is 7-bit; MySQL latin1 uses CP1252 |
| `gb2312`, `gbk`, `gb18030`, `gb18030_2022` | GB2312 uses the strict OceanBase mapping; GB18030 uses the 2005 mapping unless 2022 is selected |
| `dec8`, `tis620`, `euckr`, `sjis`, `big5`, `hkscs`, `hkscs31` | Existing pinned server mappings, with ASCII syntax preserved for input |

UTF-16/LE, binary and unknown names fail configuration validation before networking. Encoders
reject malformed Unicode, unmappable characters and aliases that do not decode to the original
text. They never substitute `?`. SJIS backslash is sent as ASCII `5C`, while non-ASCII characters
retain their strict server mapping. GBK's CP936-only euro alias and HKSCS's lossy U+2593 alias are
rejected. GB2312 rejects GBK-only characters instead of silently using the CP936 superset. This option
does not select a different result decoder or make unsupported types legal.

```csharp
using var connection = new OceanBaseConnection(
    "Server=db.example;User ID=u;ExpectedMode=Oracle;Character Set=gbk");
await connection.OpenAsync();
using var command = connection.CreateCommand();
command.CommandText = "SELECT :value FROM dual"; // Oracle mode example
command.Parameters.Add(new OceanBaseParameter { ParameterName = "value", Value = "中文" });
var value = await command.ExecuteScalarAsync();
```

## Initialization and pooling

The omitted option and explicit `utf8mb4` use the existing handshake and command behavior; they
share pool identity. They do not enable additional session charset management. Authentication,
user/database handshake fields and initial identity queries remain UTF-8 (handshake collation45).

A non-default name enables managed input encoding. After authentication and identity confirmation,
MySQL Open executes the Connector/J-compatible `SET NAMES <canonical name>`; its client,
connection and result settings therefore use the configured family. Oracle Open instead uses three
separate quoted assignments: `SET character_set_client = '<name>'`,
`SET character_set_connection = '<name>'` and `SET character_set_results = 'utf8mb4'`.
Both modes read client/connection/results/collation state; the Oracle SELECT includes `FROM DUAL`.
Fresh Open and pool restoration share the same mode-specific SQL. MySQL input and result names must
match the configured family; Oracle input names must match while results remain utf8mb4. A configured
`utf8mb3` also accepts the server's `utf8mb4` names and collations: OceanBase 4.4.2.2 normalizes
`utf8`/`utf8mb3` assignments this way. The mb3 encoder still rejects supplementary characters, and its
pool identity remains distinct from mb4. Other charset families retain exact matching. In both modes
collation must belong to the accepted connection family. The connection is
published only after this response is complete and validated. Unknown/rejected settings fail Open.
Result identifiers and error handling keep their existing UTF-8 contract; ordinary result values
continue to use their column collations.

The canonical name partitions pools and survives endpoint and credential snapshots. Following
`COM_RESET_CONNECTION`, managed settings are reapplied and verified before later autocommit or
database initialization. Failed/canceled/timed-out restoration discards the physical session.
Statement-cache IDs remain per physical session and reset generation; no ID crosses charset pools.

## Cached session trust

Initialization establishes a baseline, not continuous observation. Applications must keep client,
connection and result encodings fixed for the connection lifetime. Do not execute independent
`SET NAMES`/charset changes or allow procedures/functions/triggers to change them. Close/Open with
the desired connection option to change encoding.

SQL classified as stateless keeps the cached baseline and introduces no charset query before its
next command. Potentially stateful/opaque SQL invalidates trust when sent; a reported mismatching
charset session variable also invalidates it. The next encoding-bearing request checks the four
settings before transmitting its payload. A mismatch or malformed response breaks the session.
For a managed client charset, the fallback's separate fresh-state query also invalidates cached
trust when either input charset differs from the configured family. It rejects the target after
draining the response; a subsequent payload must pass the ordinary four-setting verification
before it can be sent.
The existing conservative classifier includes CALL/SET/PLSQL, executable comments, user variables
and MySQL quoted backslashes. Therefore some otherwise ordinary SQL also causes rechecking.

**A recheck is an actual SELECT** (with `FROM DUAL` in Oracle mode). After classified SQL it can change session diagnostic functions
such as `ROW_COUNT()`, `FOUND_ROWS()` and warning diagnostics. Use returned ADO.NET counts/results
and avoid depending on those session functions across such commands. Hidden effects of arbitrary
functions/triggers are not comprehensively detected. This is the same incomplete implicit-state
boundary described in the pool isolation contract, not a claim of arbitrary SQL parsing.

Verification never enters a staged stream/piece upload or unread response. Trust is checked before
uploads; a later reported change while data is staged breaks the session before execute. Generated
ASCII XA controls bypass the ordinary query check so they do not gain SQL between XA phases.
Business commands retain their existing XA ownership and active-branch restrictions.

## Command and type boundaries

The selected encoding reaches text/prepare SQL, scalar parameters, ordinary nested object/collection
members and type identities, Oracle PrepareExecute SQL lengths/checksum and array columns, procedure
inputs, metadata lookup parameters, KeyInfo rewrites and INSERT rewrite budgets. Byte limits use
the encoding of the actual payload. Unrepresentable target SQL or ordinary parameters fail local
prevalidation before target preparation/execution.

MySQL string client-prepare fallback accepts the existing UTF-8 families and explicitly configured
non-UTF8 input names in the table above, including GB2312, GBK, both GB18030 revisions, EUCKR, SJIS, Big5 and
HKSCS/HKSCS31. Each fallback reads actual client/connection/collation/sql_mode
state. Both non-UTF8 input names must match the configured family; the collation must belong to it.
Strict configured encoding rejects unrepresentable text. Existing quote/backslash escaping, NUL
rejection under NO_BACKSLASH_ESCAPES, single-statement parsing, packet limits and the exact
1295/HY000 fallback gate remain. Escaping runs on Unicode characters before encoding: a DBCS
trail byte `5C` stays inside its character and is never escaped as a standalone backslash.
UTF8MB3 rejects supplementary text. Oracle scalar literal fallback uses the selected encoding
for its 4000-byte limit.

Use explicit ASCII result aliases for expressions containing non-UTF8 literals. A direct-server
probe returned some unaliased expression names in client bytes despite UTF8MB4 result settings;
the existing strict UTF-8 metadata decoder rejected them. This module does not change that
metadata contract. An explicit `AS value` avoids relying on such derived column names.
Further raw probes observed both UTF-8 and client bytes for unaliased text expressions across
executions, while prepared and explicit-alias samples returned UTF-8. A client-encoded name may
also be valid UTF-8, so trying another decoder only on failure would still silently misname columns.
No heuristic metadata decoder change was made.

Explicit MySQL JSON parameters use the selected strict client encoding and the existing ordinary
string wire type253. Unrepresentable text fails prevalidation before target preparation/execution;
ASCII JSON escapes can represent characters outside the selected charset. The driver preserves
JSON text rather than parsing or serializing it. SQL NULL remains distinct from JSON text `null`.
Oracle mode and nonzero Precision/Scale remain rejected, including for SQL NULL. JSON results
retain strict UTF-8 decoding. Opt-in ordinary client fallback accepts explicit JSON Input after a
complete automatic-Prepare1295/HY000 response, using fresh charset/collation/SQL_MODE facts and
ordinary string escaping. Typed SQL NULL needs no literal-state query; raw NUL with
NO_BACKSLASH_ESCAPES remains rejected. Size retains the existing string hint semantics and does
not truncate the JSON. See [fallback evidence and limits](compatibility.md).

The fixed server's
[ObJsonTC result branch](https://github.com/oceanbase/oceanbase/blob/5d6cb5cbc3f7c1ab6eb22e40abec8e160a8764d5/src/observer/mysql/obsm_utils.cpp)
uses [json_cell_str](https://github.com/oceanbase/oceanbase/blob/5d6cb5cbc3f7c1ab6eb22e40abec8e160a8764d5/deps/oblib/src/rpc/obmysql/ob_mysql_util.cpp)
to print the binary JSON value directly into a length-encoded text buffer, without a client
charset conversion argument. This agrees with the existing UTF-8 result contract; configured
input bytes do not imply results use that encoding. Other server/proxy result metadata and
storage profiles still need live verification; this source check adds no live compatibility claim.

The fixed OceanBase source's
[is_valid_for_json](https://github.com/oceanbase/oceanbase/blob/5d6cb5cbc3f7c1ab6eb22e40abec8e160a8764d5/src/sql/engine/expr/ob_expr_json_func_helper.cpp#L1263)
requests UTF8MB4 calculation collation for nonbinary strings from other charsets. This is source
evidence for server-side conversion, not a completed deployment/storage matrix.
Standalone binary NCHAR/NVARCHAR2 parameters retain the independently verified national encoding.
Oracle text multi-statements instead encode explicit NChar/NVarchar2 inputs as ASCII Unicode
literals, so their values can contain characters outside the selected client charset. Ordinary
SQL text still requires representability. Input validation and the total SQL packet budget remain;
result decoding still requires fresh trusted national state. Explicit national/result names now also
use the fourteen existing strict mapped encodings; absent names retain Unicode-only collation fallback.
See the [national input evidence](compatibility.md). Complex national input members use
the trusted connection charset, now resolving all configured client names through the strict client
encoder, including explicit utf8mb3 and the thirteen non-UTF8 families. Legacy UTF8/UTF16 handling
is preserved. Nested members, scalar factory arrays and ExecuteArray share this input path;
unrepresentable text fails before target execution. PL complex output shares the explicit national
resolver; stored national members still require AL16UTF16. See the [scalar encoding evidence](compatibility.md).
Named/nested PL outputs, named-object storage and anonymous array DML additionally have a
[392-case direct acceptance matrix](compatibility.md)
covering the twelve names in that historical matrix plus UTF8/UTF16; the GB2312/EUCKR additions
have separate local coverage; other storage encodings and deployments remain open.
CLOB write buffers use ordinary string parameters and therefore
inherit the selected client encoding. The complete WriteText input is checked for representability
before its first write chunk, using bounded chunks for encoding checks; a rejected suffix leaves
the locator and handle usable. Later
server/network failures can still leave a completed prefix, as documented by the LOB mutation API.
Named complex CLOB input is a separate content protocol: it uses strict UTF-8 because the schema
member LONG_BLOB parser does not convert client bytes. This also applies to nested named elements;
BLOB members remain raw byte arrays. Anonymous scalar CLOB arrays bind locator handles instead.
Scalar and anonymous-array CLOB locator parameters encode inline UTF-8 content into the selected
strict client charset in a sending copy. V1 uses this conversion; V2 enables it at authenticated
OceanBase product version4.4.1 or later, following the newer server inrow conversion path.
Legacy/unknown V2 identity preserves the original bytes; older backports remain unverified.
Only payload bytes and declared byte sizes change. Original handles, UTF-8 readers, opaque
identity/rowkey/extensions, BLOBs and external locators without inline content remain unchanged.
When inline conversion runs, invalid UTF-8 or unrepresentable input rejects before target SQL;
local conversion rejection also preserves handles used by write/trim/free operations. Opaque
default/legacy paths retain their existing validation behavior. Named CLOB content still uses
strict UTF-8, independently of this locator parameter conversion.

CLOB reads retain the existing
UTF-8 contract. Binary streams, CLOB storage rules, LOCAL INFILE filename rules
and remaining extended metadata are not expanded by this option.

## Evidence boundary

Byte-exact loopback fixtures cover all fourteen non-default names in both compatibility modes,
sync/async text and server preparation, UTF-8 results/identifiers, EOF variants and pool restoration.
Additional local regressions cover trust invalidation, procedure outputs, staged data, generated XA
controls, nested complex/array encodings, byte budgets and strict alias rejection. Five product assets
are consumed in one .NET10 process; this does not establish five independent runtimes.

The initial client-encoding module used local fixtures only. The JSON extension adds exact bytes
for all fourteen non-default names, both row protocols and sync/async, plus five-asset consumption.
Its opt-in MySQL tests check JSON storage, escaped supplementary values, SQL NULL versus JSON null,
invalid input and subsequent command reuse. The current real attempt failed all ten cases while
reading the initial greeting, before authentication or JSON SQL; both existing default-charset
connection controls failed there too and a raw TCP probe received zero greeting bytes. The underlying
server/network cause is unknown. No real JSON storage or OBProxy success is claimed.
Accepted server names, collations, storage/coercibility, national/JSON/CLOB combinations and
version/proxy/reset support still require the corresponding real-server matrix. This delivers
local client-encoding behavior, not all P1-08.

The GB2312 extension has local byte-exact client/result mapping tests for both modes and strict
rejection of a GBK-only character. It uses OceanBase 4.4.2 charset IDs 24/86 and .NET code page
20936. No real 4.4.2.2 GB2312 tenant or collation roundtrip was available, so this does not close
the live deployment portion of the charset matrix.

The EUCKR extension has local byte-exact client/result mapping tests for both modes and strict
validation of OceanBase's EUC-KR byte ranges. It uses OceanBase 4.4.2 charset IDs 19/85 and .NET
code page 51949. No live tenant roundtrip was available; UJIS, EUCJPMS and the remaining newer names
remain candidates because the available .NET code page does not reproduce OceanBase's full UJIS table.

The single-byte fallback extension has local byte-exact automatic-fallback fixtures and five-asset
consumption. Explicit read-only MySQL 4.4.2.2 checks pass all four families in sync/async mode with
ASCII aliases, preserving values, HEX, collation and coercibility4 under the server's existing
SQL_MODE. No objects or SQL_MODE were changed. These generated-literal checks do not exercise a
real 1295-triggered automatic fallback, DBCS, other mode/collation/version profiles or OBProxy.
Retained failures and exact local verification are in the
[module record](compatibility.md).

The subsequent DBCS extension adds all seven strict mappings with trail5C-adjacent quote/backslash
samples, GB18030 four-byte values and HKSCS supplementary values. Explicit direct MySQL checks
pass14/14 generated-literal cases plus a connection control, with ASCII aliases and unchanged
SQL_MODE. This extends local automatic-fallback support, not actual server1295/proxy or every
SQL_MODE/collation/version profile. See its
[module record](compatibility.md).

The complex-national extension adds exact synthetic input bytes for all fourteen non-default names,
both prepare paths, sync/async and five-asset consumption. Explicit Oracle4.4.2.2 array-input
checks pass36/48: ASCII, latin1, DEC8, TIS620, GBK, both GB18030 revisions and HKSCS/HKSCS31,
each with NCHAR/NVARCHAR2, sync/async, exact read-back and SQL NULL. Each case rolls back and drops
its generated table. That historical run had12 failures at Open before DDL. The later
[failure investigation](compatibility.md) identifies four as an overly strict
driver UTF8MB3 state check; those four now pass, making this group40/48. The remaining8 are SJIS/Big5:
session switching makes subsequent fixed ASCII statements fail900, independently reproduced with
official Connector/J2.4.14. No substitution of a different input encoding is added for those families.
These observations do not establish other versions, proxies, stored complex output, standalone
national or live pool-reset compatibility. Retained initialization failures and final local gates
are in the [module record](compatibility.md).
