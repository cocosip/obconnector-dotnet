# Oracle 多语句国民字符输入（2026-10-29）

开发基线：`7fc4d85`。沿用 master，本地提交，不推送。

## OceanBase 和官方客户端依据

官方 OceanBase Connector/J 2.4.14、源码 `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`：
`BasePrepareStatement.setNString` 选择 `OBNStringParameter`；其文本写入支持 `n'...'`，
配置 `nCharacterEncoding` 时输出逐 UTF-16 单元的 `u'\hhhh...'`。后者只含 ASCII SQL，
可以表达客户端字符集之外的国民字符。`setObject(String, Types.NCHAR/NVARCHAR)` 则调用普通
`setString`，不能把这个 JDBC 声明当作 `setNString` 的协议证据。

同一 OceanBase 4.4.2.2 Oracle 直连租户，实际 Java `ClientSidePreparedStatement` 多语句证明：

- UTF-8、windows-1252、GB18030，默认及显式 UTF-16BE national 配置六种组合通过；
  Unicode 字面量写入的 NVARCHAR2/NCHAR 内容由独立 UTF-8 连接实际读取核对，六种组合均通过。
- national 表达式元数据为 NCHAR；两种国民字符参数生成同类字面量，不承诺二进制参数的表达式元数据等价性。
- 0/1/2000/2001/4000/4001/32767 个汉字的文本长度通过；不能照搬普通 VARCHAR2 的 4000 编码字节限制。
- 引号、反斜杠、类似 Unicode 转义的原始文本、NUL、控制字符及补充字符均保真。
- 空字符串为 SQL NULL；NCHAR 填充按 Unicode 字符计算，一个补充字符占一个字符、两个 .NET UTF-16 单元。

## 实现和保留契约

- Oracle 多语句接受显式 NChar/NVarchar2 Input、typed NULL、空值和声明 Size。
- 复用已有严格 Unicode、模式、方向及零 Precision/Scale 校验；所有国民字符逐 UTF-16 单元转义，
  输入内容不能成为 SQL 引号、分号或绑定标记。生成前限制单个字面量展开大小，生成后仍受整体 SQL 包预算约束。
- 文本字面量不执行未使用的二进制 national 参数编码；二进制参数路径保留既有会话编码校验。
- 普通 Oracle 字符串的 NUL/长度限制、单语句 client fallback、Prepare/cursor/输出/stream/复杂输入限制保持。
- national 结果仍依赖可信会话字符集报告，仅保留原有 UTF-8/UTF-16 解码范围。
  后续结果没有新报告时继续拒绝解码，不根据可能与实际编码不符的列 collation 猜测。
  本模块没有关闭 national 存储/输出、复杂 national 类型或其他字符集的整体缺口。

## 测试和诊断过程

- 新增单测初始 5 项 RED、3 项既有约束通过；公开 DLL 消费者 12 项全部 RED；实库新增 38 项中
  36 项 RED、2 项本地拒绝边界通过。原失败来自未开放的 provider 白名单。
- 包预算测试首次编译出现 `long` 到 `int` 的 CS1503，改为显式 checked 转换后才执行上述 RED；不把编译错误计作功能 RED。
- 首次实库尝试 240 项中 208 通过、32 失败：12 项被未使用的二进制 national 编码拦住；
  20 项遇到既有的后续结果缺少可信 national 状态。保存 `national-live-final.trx`，此记录不是最终 GREEN。
  前者按真实输入路径补 RED 后修复；后者保留既有 `NationalCharacterResultsRequireFreshFactsAfterUnreportedResults`
  安全回归，调整新增用例以独立 UTF-8 读取器核验实际存储、首结果表达式类型和后续数字结果。
- 在完整旧单测运行期间尝试重建测试时遇到正在使用的 `xunit.abstractions.dll` 文件锁，产生
  MSB3026/MSB3027/MSB3021；等待该次进程结束后再构建，不把该失败构建或旧产物运行计作最终门禁。
- 续接复核重新构建后，`OracleNationalMultiStatementsDoNotUseBinaryEncodingForUnicodeLiterals`
  同步/异步两项均 RED；同步和异步实际请求路径补 `!clientMultiStatement` 后，相关单测 413/413。
  首次复核误以文件名过滤 partial class，35/35 仅覆盖 helper；纠正方法名过滤后确实得到 93 通过/2 RED。
- 最终公开 DLL 消费者相关组 120/120、双模式直连回归 240/240（其中多语句组 202/202、新增 national 38 项）、
  DI 14/14，均零失败、零跳过；五目标解决方案 Release 构建零警告、零错误。
- 新增实库验证五种写入字符集（utf8mb4/ascii/latin1/utf8mb3/gb18030）、同步/异步及普通/顺序读取；
  独立 UTF-8 连接验证实际 NCHAR/NVARCHAR2 存储、NCHAR 补空格、NULL、表达式元数据，
  另验证 0/1/2000/2001/4000/4001/32767 长值、本地拒绝无副作用及服务端失败的已确认前缀/未执行后缀。
- 最终完整默认单测 8,128/8,128，零失败、零跳过，耗时 8 分 16 秒；沙箱外运行以避开已确认的
  File.Replace 沙箱限制，不启用实库或 SecurityTests。旧产物的 8,128/8,128 改存
  `national-unit-full-initial.*`；当前通过证据为 `national-unit-full-final.*`。
- 一次独立最终走查无 Critical/Important/Minor 问题；确认 Unicode/包预算及真实存储断言有效。
  文本输入跳过未使用二进制编码、保留 fresh-facts/UTF-8/UTF-16 输出限制的裁定通过复核。
  原生 Oracle/MySQL 全面语义、复杂存储、二进制表达式类型等价和其他真实部署不计入本模块关闭范围。
- 当前 GREEN 产物为 `national-unit-contract-final.trx`、`national-consumer-contract-final.trx`、
  `national-live-contract-final.trx`、`national-di-contract-final.trx`、`national-unit-full-final.trx`；
  五目标构建为 `national-build-final.log`。旧失败记录保留，不改名冒充通过。

本地证据：ignored `TestResults/multi-national/*.trx` 和日志；官方 Java 探针、包装脚本及日志为
ignored `TestResults/java-gap-probe/MultiNationalProbe.java`、`RunMultiNationalOracle.ps1`、
`multi-national-oracle.log`。使用现有 user-secrets，经 stdin 传入凭据，不打印连接信息。

## 未关闭范围

新增能力仅 Oracle。实库证据范围为 Windows/.NET 10、OceanBase 4.4.2.2 双模式直连、TLS Disabled。
真实 OB20/OBProxy/TLS/KDC、其他版本/平台和完整资源/故障矩阵仍开放；合成 OB20 消费者不是实环境验收。
