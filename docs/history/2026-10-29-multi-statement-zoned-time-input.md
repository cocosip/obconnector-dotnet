# OceanBase Oracle 多语句时区时间输入

日期：2026-10-29。开发基线：`23a1dab`。沿用当前 master，本地提交，不推送。

## OceanBase 依据和行为

官方 Connector/J 2.4.14 源码 `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`：
`OBTIMESTAMPTZParameter.writeTo` 输出 typed TIMESTAMP 字面量，
`OBTIMESTAMPLTZParameter.writeTo` 也有文本路径；`DataTypeUtilities.TIMESTAMPTZToString`
使用名称优先于偏移，缩写不作为独立文本输入提示。官方 Java 在同一 Oracle 租户的
`useServerPrepStmts=false/true` 多语句中均接受 TZ/LTZ 输入；检查实际 statement 类型后
确认两种选项最终都采用客户端文本路径，不能算作二进制验收。

对照发现 LTZ 的 `CAST(... AS TIMESTAMP WITH LOCAL TIME ZONE)` 默认精度是 6，
会把 `.123456789` 舍入为 `.123457000`；显式 `TIMESTAMP(9)` 保留全部纳秒。
命名地区 `America/New_York` 在当前租户返回 `1882/HY000`，Java 同样失败；
数字时区名称、负的不足一小时偏移在文本路径可用。数字名称优先于数值偏移，文本
写入不带缩写；不是按原生 Oracle 或本机时区库推测行为。

追加相同带名称值的 .NET 二进制对照时，原本假定两条路径完全等价的四项断言失败，
代码为 `1882/HY000`（保存 `zone-live-parity-final.trx`，136 通过/4 失败）。随后显式
调用 Java `serverPrepareStatement`，确认实际类为 `ServerSidePreparedStatement`：
非空数字时区名称同样返回 1882，无名称的偏移值通过。这是当前服务端二进制路径的
限制，不能把请求 prepare 选项当作成功二进制证据。撤回错误的跨路径等价性断言；
本轮没有清空二进制名称或修改原编码来强行通过。该失败及独立 Java 对照保留，
不将其混入本模块文本支持或声称二进制带名称形状可用。

## 实现范围

- `ClientPreparedSql` 在 Oracle 文本多语句接受 `DateTimeOffset`、
  `OceanBaseTimestampWithTimeZone`、`OceanBaseTimestampLocalTimeZone`，
  支持推断/显式 provider 类型和两种 typed NULL。
- 继续调用 `PreparedParameters.Encode` 做类型、模式、范围、精度及禁止舍入检查，
  生成 SQL 仍受共享逻辑包预算约束。
- TZ 生成 TIMESTAMP 字面量，按 Java 使用名称优先于数值偏移；缩写是结果元数据，
  不作为文本输入提示。名称中的单引号按 SQL 内容转义，交由服务端验证时区身份。
- LTZ 用 `FROM_TZ` 固定值的来源时区，再显式 CAST 为实际精度的 LTZ；即使前面的
  语句切换会话时区，后面同一参数仍代表原来的时间，而不是重新解释当地字段。
- 移除 Oracle 在 `Start` 中清空参数的提前替换，保留冻结参数到原有公共执行路径，
  让同步/异步均执行现有的当前会话时区匹配检查；错误身份在目标脚本发送前拒绝。

没有新增公共 API、配置或依赖。MySQL、单语句 client-prepare fallback、输出、
Prepare/cursor、stream 及复杂输入仍保留现有拒绝边界。

## 测试和代码走查

- 修改产品代码前：新增单测 4 失败、1 校验保护通过；公开 DLL 消费者 12 失败；
  Oracle 实库 16 失败、2 保护用例通过。失败原因均为旧时区参数限制。
- 首轮 GREEN：新增单测 5/5、公开 DLL 消费者 12/12、Oracle 实库 18/18。
- 扩展实库：新增时区输入/身份保护共 22/22；覆盖同步/异步、缓冲/SequentialAccess、
  精度 0/3/7/9、纳秒、`-00:30`、`+14:00`/`-14:00`、数字名称优先、typed NULL、
  实际表列存储、多结果、同连接后续查询及拒绝后的零副作用。脚本确实改变时区到
  `-03:30`，恢复来源时区后两次存储字段和纳秒均与原值相同。
- 双模式多语句组 112/112，加原有类型往返 28/28，共 140/140，零失败、零跳过。
- 公开 DLL 消费者相关组 48/48，覆盖三个实现资产、同步/异步及 TCP/合成 OB20。
- DI 14/14；五目标完整解决方案 Release 构建零警告、零错误。
- 最终完整默认单测 8,116/8,116，零失败、零跳过，耗时 8 分 16 秒。
- 独立走查未发现产品代码问题；共享路径使重复绑定同一流先到达已有
  `NotSupportedException` 保护，旧测试仍期待 `ArgumentException`。首次完整单测
  复现同步/异步两项失败后中止；同步异常断言，并保留流位置为零及后续连接复用检查，
  定向 7/7 通过，随后完整默认单测取得上述 8,116/8,116；没有把中断运行计作通过。

追加诊断时曾误用异常属性 `Number`，导致一次探针编译失败；改为公开的
`ServerErrorCode` 后取得上面的 1882 结果，未为该诊断改产品 API。

走查边界：命名地区/DST 正向验收需要服务端时区数据；不加入客户端 DST 解析。
后续 LTZ 结果集没有可信时区报告时继续失败关闭，沿用已有多结果快照契约和回归，
不以本模块绕过。以上均保留为明确的服务端/结果集边界。

本地证据：ignored `TestResults/multi-zones/*.trx`、`TestResults/multi-zone-*.log`；
官方 Java 对照源码/包装脚本/日志为 ignored
`TestResults/java-gap-probe/MultiZoneProbe.java`、`RunMultiZoneOracle.ps1`、
`multi-zone-oracle.log`。沿用 user-secrets，通过 stdin 向 Java 传入凭据；
不在源码、文档或日志中保存连接信息。默认单测在沙箱外运行，以避免已确认的
沙箱 `File.Replace` 拒绝访问；不启用 SecurityTests 或隐式实库访问。

## 未关闭范围

本轮实库只证明 Windows/.NET 10、OceanBase 4.4.2.2 Oracle/MySQL 直连、TLS Disabled
下记录的形状；新时区能力仅 Oracle。命名地区、真实 OB20/OBProxy/TLS、其他版本/
平台及完整资源/故障矩阵仍未验收；P1-01 和全部缺口矩阵没有因此整体关闭。
