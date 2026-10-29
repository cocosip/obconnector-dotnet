# Oracle 大二进制客户端字面量（2026-10-29）

## OceanBase / 官方客户端依据

基线为 `5e0d295`。对照 Connector/J 2.4.14、源码
`6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`，`OBByteArrayParameter.writeTo`
在文本路径写 quoted hex，没有 2000 字节上限；这与 binary 参数路径分开。

Java 实际 `ClientSidePreparedStatement` 的两个 INSERT 及独立连接 BLOB 存储校验，
通过 0/2000/2001/4000/32767/65536 六种长度。另以 `HEXTORAW(?)` 保持 .NET
既有表达式，通过同样六种长度；最终单 SELECT 的 RAW 类型/完整字节与多 INSERT
独立 BLOB 存储探针通过上述六种长度及 1MiB，共七种长度。

`RAW(32767)` DDL 在当前租户返回 `910/42000`；`RAW(2000)` 接受空值和 2000 字节，
2001 字节仍失败。Java 报 `25137/22001`，.NET 实库收到 `25137/22003`：
Java `ExceptionFactory` 对 SQLSTATE 类 22 构造 `OceanBaseDataTruncation`，其父类
`DataTruncation(read=false)` 使用 22001；.NET 保留 ERR 包原始 SQLSTATE，不做该映射。
不能把 Java 异常展示的 SQLSTATE 直接作为服务端原始值断言。

## 实现与契约

- 从共享 `ClientPreparedSql` 删除 Oracle byte[] 大于 2000 字节的一条拒绝分支。
  同时覆盖 opt-in 文本多语句和普通 opt-in client-prepare fallback，不增加 API 或依赖。
- byte[] 仍使用 `HEXTORAW('HEX')`，保留 RAW 表达式类型；空字节在 Oracle 为 SQL NULL。
  不改成 Stream 使用的 quoted-hex 文本表达式。
- 复用声明校验、参数/字节快照、单个 hex 分配上限和所有重复绑定累计的 64MiB SQL 上限。
  普通字符串的 4000 escaped encoded bytes 限制保留。
- fallback 仍只在完整验证的 `1295/HY000` 自动准备失败后发生；显式 Prepare/cursor/输出
  等既有拒绝保持。真正由实库触发 1295 的 fallback 未验证，本轮由合成服务器验证。
- `RAW(2000)` 等目标列限制仍由服务器决定；支持大 BLOB/RAW 表达式不等于扩展 RAW 列容量。

## 测试与走查

- 正式 RED 为 14 失败/1 通过，14 项均命中原有 2000 字节 guard；测试编译时的状态码
  byte 转换在正式 RED 前修正。产品实现只有删除该 guard。
- helper 覆盖普通/多语句、2001/65536/1MiB、RAW SQL 身份、单值及重复命名累计预算；
  协议覆盖同步/异步及 fallback 准备期间调用者修改数组/声明后仍使用原快照。
- 首轮 Consumer 为 126 通过/12 失败：原 fixture 只接受一个 OB20 帧，大请求按产品既有
  64KiB 缓冲拆帧，fixture 错将首帧当完整请求。修正共享 fixture 的逐帧校验/重组及响应
  sequence，未修改产品 OB20 代码；新 Consumer 用真实 DLL 验证 65536 字节重复绑定、
  typed NULL 和 fallback，覆盖三类代表资产、同步/异步、TCP/合成 OB20。
- 首轮实库 281 通过/2 失败，仅 RAW 列失败测试错误预期 Java 的 22001；按上文归因改为
  服务端原始 22003。新实库 30 项覆盖七种长度、同步/异步、buffered/sequential、
  独立连接逐字节 BLOB 存储、RAW 元数据/字节、typed NULL，以及事务中的已执行 prefix、
  未执行 suffix、独立连接不可见和 rollback 清理。
- 最终门禁全部通过；相关单测 215/215、DI 14/14、双模式多语句/流实库
  283/283（新增大二进制 30/30）、五目标 Release 零警告/错误已通过，均零跳过。
  Consumer 扩大回归 **1171/1171**（含新增 24 项），零失败、零跳过，耗时 7分11秒；
  完整默认单测 **8399/8399**，零失败、零跳过，耗时 8分29秒；
  使用沙箱外执行避开已知 File.Replace 沙箱限制，未启用实库或 SecurityTests。
- 独立最终走查已完成，无 Critical/Important/Minor。审查覆盖全部 tracked/untracked 差异、
  共享 fixture 和执行路径、文档与保存证据；审查者只读，未运行测试或实库。
  真正实库 1295 fallback、真实部署与并发内存压力仍不作通过声明；最终完整单测已通过，满足本轮本地提交门禁。

证据在 ignored `TestResults/oracle-large-binary/`。Java 探针在 ignored
`TestResults/java-gap-probe/`，包括 `LargeByteArrayProbe`、`LargeRawFunctionProbe`、
`LargeRawExpressionProbe`、`LargeRawColumnProbe`、`RawColumnLimitProbe` 及对应安全 wrapper/log。
凭据只使用既有 user-secrets/stdin，不在文档或输出中保存连接信息。

## 未关闭范围

仅 Windows/.NET10、OceanBase 4.4.2.2 Oracle 直连、TLS Disabled；既有回归组另含 MySQL。
合成 OB20 不证明真实 OB20/OBProxy；不覆盖 TLS、其他版本/平台或完整资源/故障矩阵。
本模块不关闭 P1-01 所有形状或整体缺口目标；未推送。
