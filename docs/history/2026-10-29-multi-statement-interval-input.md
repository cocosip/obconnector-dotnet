# OceanBase Oracle 模式多语句 INTERVAL 输入

日期：2026-10-29。基线：`25603a9`；在现有 master 工作区开发，本地提交，不推送。

## 能力依据与实现

官方 Connector/J 2.4.14，源码固定 `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`：
`OBINTERVALYMParameter.writeTo` 和 `OBINTERVALDSParameter.writeTo` 分别生成
`INTERVAL '...' YEAR(9) TO MONTH`、`INTERVAL '...' DAY(9) TO SECOND(9)`。
本机官方 Java 包在同一 Oracle 租户上完成参数化多语句对照，客户端/服务端 prepare
选项各两种区间值通过；这证明 OceanBase 已有该能力，不是原生 Oracle 的推测性补齐。

`ClientPreparedSql` 现接受文本多语句中的 `OceanBaseIntervalYearToMonth` 和
`OceanBaseIntervalDayToSecond`，支持推断类型、显式类型及显式类型 NULL。
沿用 `PreparedParameters.Encode` 校验模式、类型、范围、精度及禁止舍入，再按有效的
Precision/Scale 生成数字字面量。复用既有绑定位置、重复命名参数及逻辑包预算。
没有新增公共 API、依赖或配置。

MySQL INTERVAL 输入、普通单语句 client-prepare fallback、输出、Prepare/cursor、
stream、时区时间和复杂类型组合仍保持原来的拒绝边界。

## RED、GREEN 与走查

- 产品代码修改前：字面量与同步/异步命令三项因未支持 INTERVAL 失败；
  公开 DLL 消费者 12 项、真实 Oracle 四项均因同一限制在发送目标 SQL 前失败。
  首次负向单测复用了已属于集合的参数，产生额外夹具错误；改用参数快照后，
  正确 RED 为三失败、一通过，没有修改产品行为来迁就夹具。
- 相关源码回归：5,172/5,172，通过，零跳过。
- 真实 Oracle 扩展区间矩阵：12/12，通过，覆盖同步/异步、缓冲/SequentialAccess、
  正负区间、纳秒、声明精度覆盖、默认零值结构体、typed NULL、实际表列存储、
  多结果消费和同一会话复用；包含 YEAR(0)/DAY(0) TO SECOND(0) 的实际解析。
- 双模式完整多语句组：90/90，通过，零跳过（包含上述新增 12 项）。
- DI：14/14，通过。
- 最终公开 DLL 消费者：36/36，通过，零跳过，包含新增区间和既有双模式标量多语句。
- 完整默认单测：8,111/8,111，通过，零失败、零跳过，沙箱外运行耗时 8 分 14 秒。
- 五目标完整解决方案 Release 构建：零警告、零错误。
- 独立代码走查没有产品代码待修项；消费者失败清理需等待服务任务的细节已修正。

沙箱内完整单测出现
`TnsAddressResolverTests.ConcurrentReadersUseCompleteSnapshotsAndMissingFilesHaveNoFallback`
的 `File.Replace` 拒绝访问；同一测试单独运行仍失败，且无读线程、无打开句柄的独立
文件替换探针也返回 `0x80070005`。同一单测在沙箱外 1/1 通过，确认是执行环境限制；
未修改 TNS 产品或夹具代码。已中止有该失败的沙箱内完整运行，改在沙箱外重跑完整门禁，
结果为上述 8,111/8,111；失败记录和环境归因保留，不将中断运行计为通过。

首次并行 MSBuild/共享编译器启动没有及时输出；使用
`-m:1 -nr:false -p:UseSharedCompilation=false` 完成构建，没有改产品或仓库构建设置。

本地证据为 ignored `TestResults/multi-intervals/*.trx` 和
`TestResults/multi-interval-*.log`；Java 源码、包装脚本及日志位于 ignored
`TestResults/java-gap-probe/MultiIntervalProbe.java`、`RunMultiIntervalOracle.ps1`、
`multi-interval-oracle.log`。凭据从原有 user-secrets 获取，经 stdin 传给 Java，
不进入源码、命令行参数、文档或日志。

## 保留边界

真实数据库证据限本机 Windows、.NET 10、OceanBase 4.4.2.2 双模式直连、TLS Disabled；
新增区间能力仅 Oracle 模式。消费者覆盖三个实现资产和同步/异步、TCP/合成 OB20，
不等于真实 OB20/OBProxy/TLS 或独立运行时验收。P1-01 和全部工作包不因本模块关闭。
