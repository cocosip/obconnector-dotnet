# Oracle 参数化文本多语句（2026-10-28）

## 范围与依据

P1-01/W3：显式 `Allow Multi Statements=true` 后，Oracle 文本多语句支持普通
命名标量输入（数字、字符串、二进制、NULL），包括跨语句重复引用及重新赋值执行。
复用 `ParameterBindingPlan` 的精确绑定位置与 `ClientPreparedSql` 的类型、转义、
编码及逻辑包预算校验；先完整生成文本，再开始执行，使用 COM_QUERY。
不新增编码器、解析器、配置或依赖，不受 `AllowClientSidePrepareFallback` 控制。

官方 Connector/J 2.4.14 固定源码提交
`6adc311ba5db8b2f19f98f3e6e4734667fdfdda1` 的
`OceanBaseConnection.internalPrepareStatement` 支持客户端准备路径。
官方 jar 显式直连两种模式，启用 `allowMultiQueries`，分别设置
`useServerPrepStmts=false/true`，四项均实际使用 `ClientSidePreparedStatement`，
返回两组结果，并保留数字及含引号、反斜线、Unicode、分号和绑定标记的字符串。
这同时证明 MySQL 参数化多语句也是尚未实现的客户端缺口。
当前模块先补 Oracle；MySQL 字符串受脚本中 SQL_MODE/字符集变化影响，
不能直接取消现有单语句字面量保护，后续另定安全范围。

保持拒绝：显式 Prepare（包括曾准备后改为多语句的命令）、服务端游标、
SchemaOnly/KeyInfo、输出参数、stream、时间和复杂输入。
现有 Oracle 字符串拒绝 NUL，转义后不超过 4000 编码字节；RAW 不超过
2000 字节。默认关闭时在发送脚本前拒绝，不产生脚本副作用。
原始 SQL 仍用于会话状态/缓存/DDL 分类，不改变未知结果、事务或池隔离策略。

## 实施与验证

1. 六项正常用例先因原参数拒绝全部 RED，再在共享 Start 入口放开受限形状。
   最初测试夹具的 DbCommand 转换编译错误不计入 RED。
2. 将 Oracle 普通输入在执行开始前完整转换为已有字面量，并清空内部绑定计划
   的待发送参数；公有参数集合、命名、方向和值不变，普通文本结果路径复用。
3. 补拒绝边界、重复执行、公开 DLL 及显式直连，再同步当前矩阵并独立走查。

当前验证日志位于 ignored `TestResults/oracle-multi-parameters/`：

- 相关单测 47/47，含新增 10 项；正常多结果/ExecuteScalar/累计更新数，
  重复绑定与重新赋值，unsupported 输入未发送且 stream 未读取，
  Prepare 拒绝后原准备命令仍可用。
- 五资产主解决方案 Release 零警告/错误。
- 公开 DLL 消费者 12/12：代表性三资产、同步/异步、普通协议/OB20，
  验证 COM_QUERY、Unicode 字符串、累计更新数及下一条命令可用。
  初始夹具遗漏 OB20 初始化请求编号和 Oracle 会话状态回报，修正后通过；
  不属于产品缺陷，也不计入功能 RED。
- 双模式显式直连多语句组 24/24，新增 Oracle 参数 8 项：普通/顺序读取、
  字符串/RAW/NULL、两组结果、重复执行、默认关闭无副作用、中途错误不执行后缀、
  事务未提交可见性及实际回滚；连接保持复用。
- DI 14/14；未显式启用实库时 22 配置通过、152 数据库测试跳过。
- 独立实现走查无待修发现，要求补齐的当前支持文档已更新。
- 最终完整默认单测 8,063/8,063，零失败/跳过，耗时 8 分 18 秒；
  普通用户上下文运行，保留 TNS 文件替换测试。
- 最终消费者复跑 12/12；独立最终走查覆盖生产实现、消费者清理和当前文档，
  无待修发现。

Java 对照及日志位于 ignored `TestResults/java-gap-probe/`，连接信息沿已有
user-secrets 经 stdin 传入，不记录在文档或产物。真实数据库、证书/TLS 不加入
默认或 tag CI。OB20 消费者是本地协议夹具，不代表真实 OB20/代理已验收。
