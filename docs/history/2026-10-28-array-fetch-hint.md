# Oracle 数组执行的 FetchSize 提示（2026-10-28）

## 依据与实施范围

P1-06/W5：补齐 Oracle `ExecuteArray` 对正命令 `FetchSize` 的接受。
`FetchSize` 仍表示游标分页提示，数组 DML/PL 的内部批次结果不请求游标，
不改变数组行数、不分页批次状态、不发送 `COM_STMT_FETCH`，属性值保持不变。
显式事务、参数形状、逐项结果、输出原子发布及未知结果契约保持原样。
`UseCursorOffset` 仍发送前拒绝。

官方 Connector/J 2.4.14 源码固定为
`6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`：

- `JDBC4ServerPreparedStatement.executeBatchInternal` 为批次构造 fetchSize=0 的
  `Results`，数组绑定的 iterationCount=2，不用命令提示代替数组行数。
- `AbstractQueryProtocol.executePreparedQueryArrayBinding` 的 PrepareExecute 路径
  对非 SELECT 使用 `CURSOR_TYPE_NO_CURSOR`。
- 本机显式 Oracle 直连对照：Java `setFetchSize(1)` 和 `Integer.MAX_VALUE` 均可
  执行三项数组 INSERT，属性不变，事务内实际三行、回滚后零行并可复用连接。
  Java 的各项更新计数为 `SUCCESS_NO_INFO`，不将其误记为精确逐项计数。

## 实施与验证步骤（已完成）

1. 复用本地数组协议夹具，增加同步/异步、独立 Prepare/隐式准备、1/最大提示矩阵；
   先验证当前明确拒绝导致失败，再移除共享命令入口的一条过严限制。
2. 保留原零提示及失败路径；原正提示拒绝用例改为 offset 拒绝，保留发送前检查。
3. 增加公开 DLL 消费回归及显式实库矩阵，核对批次结果、回滚、无 Prepare 副作用。
4. 更新公共契约及当前矩阵，构建、默认单测、相关消费者/实库回归，独立走查后提交。

连接信息仅从已有 user-secrets 读取；实库入口和证书测试不加入默认 CI。
本机 Java 对照产物位于 ignored `TestResults/java-gap-probe/`。

## 当前证据

- 新增本地 8 项首先全部以原有 `Array binding does not support FetchSize` 拒绝失败；
  修正后数组/输出提示相关 134/134 通过。
- 五资产及主解决方案 Release 构建零警告/错误。
- 公开 DLL 数组消费者 36/36，通过代表性三目标资产的同步/异步、普通协议、
  OB20 和非法尾帧矩阵；包括新增提示的 18 项，保留原零提示的 18 项。
  核对请求无游标、iterationCount=2、逐项错误、RETURNING 分组和失败时旧输出保留。
- Oracle 显式直连数组组 12/12（新增提示 8 项、既有 4 项），未使用 TLS；
  覆盖 Prepare 无写入、实际行数、逐项成功、提示不变、回滚及后续查询。
- 最终完整默认单测 8,011/8,011，零失败/跳过，耗时 8 分 22 秒；DI 14/14。
  默认未启用实库时 22 项配置测试通过、146 项数据库测试跳过。
- 独立最终走查无遗留发现；修正 README 的旧数组提示限制说明，并同步当前矩阵。
  同时按已有 TRX 更正直连记录的 640/18→643/12、目录失败重命名和报告路径；
  清理旧 TLS consumer 在 CI 复跑、Oracle 失败未分类等过期措辞及 Java 提交号笔误。

本次运行证据位于 ignored `TestResults/ci-pool-shutdown/array-*`。

这些不证明真实 OB20、其他服务器版本、代理或非 Windows 兼容性。
