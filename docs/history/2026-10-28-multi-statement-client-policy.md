# P1-01 文本多语句客户端门禁（2026-10-28）

## 根因及修复

两种真实直连服务端的握手均未声明 `CLIENT_MULTI_STATEMENTS`。旧代码虽然根据
`Allow Multi Statements=true` 在客户端握手置位，却在执行时要求服务端也声明，
导致已显式启用的文本多语句在发送前被拒绝：初始实库16项中12失败、4默认关闭项通过。

固定参考 Connector/J 2.4.14（`6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`）的
`AbstractConnectProtocol.initializeClientCapabilities` 第316行直接根据
`allowMultiQueries` 设置客户端位，不要求服务端 greeting 对应位。

最小修复只将 `OceanBaseCommand.Start` 的这项门禁改为检查已发送的 Requested 位。
Requested/Server/Effective 三态及交集诊断保持既有含义；参数、Prepare、游标、
SchemaOnly/KeyInfo、Oracle 输出等其他拒绝组合不扩展。默认关闭仍在发送前拒绝。
不具备服务端多语句能力的目标在显式启用后仍可返回正常服务端错误，不自动降级或重放。

## 测试与走查

- 本地回归扩充为 sync/async × 服务端声明/未声明。修复前2通过、2失败，
  原因与实库相同；修复后相关门禁专项13/13通过。
- 双模式直连16/16通过：两个准确结果、完整消费后原会话复用、未读 reader
  提前释放后 Broken/Close/Open 新会话、中途已确认错误停止第三条语句，以及默认关闭无写入。
- 未读普通 reader 仍遵循会话销毁契约；初版用例错误期待其自动排空，按已有
  架构契约修正测试，未更改 reader 行为。
- 五资产Release构建零警告/错误；DI14/14通过；完整单测8140/8140通过、
  0跳过，耗时8分49秒。
- 独立走查无待修发现。

日志/TRX 在 ignored `TestResults/multi-statement-*`。Windows/.NET10.0.14、
双模式直连、TLS Disabled；不证明代理、TLS 或未支持的混合执行组合。
