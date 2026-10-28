# P0-01/P0-02 DataSource 关闭与未完成 Open（2026-10-28）

## 缺口与实现

凭据提供器和池化发现发生在 session 登记前；非池化 Open 也不会登记到池。
过去 DataSource.Dispose 只销毁池，因此这两类 Open 在无限连接超时下可能长期等待。

DataSource 现在维护关闭令牌，仅链接到逻辑 Open 的生命周期。Dispose 先将源标记为
不可用并销毁池，再在锁外取消未完成 Open；成功 Open 的链接已释放，既有 Open/Busy
连接仍由借用者持有。调用方取消保持原令牌优先，源关闭返回 ObjectDisposedException。
不支持忽略取消且永久阻塞的同步应用回调，沿用 provider/resolver 的既有契约。

独立走查发现源已标记关闭但池退休尚未结束时，关闭令牌尚未取消，可能错误返回
其他 Open 异常。新增受控退休回归先复现该窗口，再改为在源锁内读取 disposed 状态。

## 验证

- 凭据/发现 × 同步/异步 × 池化/非池化八项先 RED（两秒 watchdog）后 GREEN。
- 另补活动 Busy 借用者（两项）、取消回调跨线程重入及池退休窗口回归。
- 相关连接/提供器/路由/物理池单测：160/160 通过；DI：14/14 通过。
- SDK 10.0.403，五资产 Release 构建零警告、零错误。
- 双模式真实直连基础连接 6/6；新增关闭矩阵 8/8，通过正在读取的 reader、
  源关闭后继续执行 SQL、最终 Close 释放和拒绝重开验证生命周期。
- 完整 Connections 双模式直连矩阵：128 通过、4 跳过、0 失败。跳过为两个
  `alternateDatabase` 用例及专用认证插件、禁用自动提交端点用例；未配置这些能力。
- 独立走查的一项问题已通过 RED→GREEN 修复；新增实库用例及指标隔离补充走查无待修发现。
- 完整单测首轮发现旧 `ResetFailureDestroysSessionInsteadOfReturningIt` 监听进程级
  指标导致计数 2 而非 1，停止该轮后用测试自身 AsyncLocal 范围隔离测量，保留原断言。
  专项通过；完整复跑 8138/8138 通过、0 跳过，耗时 8 分 55 秒。

实际运行环境为 Windows/.NET 10.0.14；直连 profile 明确使用 TLS Disabled。
日志/TRX 保留在 git-ignored `TestResults/source-open-shutdown-*`。
完整 Connections 日志为 `TestResults/pool-source-lifecycle-live.*`。

## 本地验收配置

用户授权将两套连接存入既有 UserSecretsId `obconnector-dotnet-integration-tests`，
密钥采用示例中的 `ConnectionStrings:MySql` 和 `ConnectionStrings:Oracle`。
本地 `tests/OceanBaseConnector.IntegrationTests/integration-tests.json` 仅引用密钥名，
已由 Git 忽略，后续显式验收自动读取。仓库文档与提交不保存地址、用户或密码。

这不是 OBProxy/TLS/认证插件、长期资源曲线或完整故障矩阵的验收。
