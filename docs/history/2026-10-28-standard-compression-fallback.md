# 2026-10-28 标准 MySQL 压缩降级验收

本次核查并修正 P1-02 标准 MySQL 压缩与 Connector/J 的协商差异。标准压缩帧实现本身已有本地回归；本次补齐服务端未声明 `CLIENT_COMPRESS` 时的真实双模式行为。

## 参考依据

固定 Java Connector/J 2.4.14 参考 `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`：

- `AbstractConnectProtocol` 在 `useCompression=true` 且服务端没有 `CLIENT_COMPRESS` 时清除压缩选项，不发送 capability；
- 只有服务端声明 capability 时才请求 `CLIENT_COMPRESS`；
- 认证完成后，`compressionHandler` 才切换到七字节 MySQL 压缩帧。

此前 .NET 在 capability 构造阶段直接抛出异常，并且 framing 只看连接串选项；这会把 Java 的普通协议降级错误地变成连接失败。

## 实现与测试

- `ClientCapabilities` 仅在服务端声明 `CLIENT_COMPRESS` 时加入该 capability；
- `HandshakeFlow` 仅在该 capability 实际生效时启用压缩流；服务端不支持时保持普通 MySQL framing；
- 保留 `Use Compression=true;Use OB20=true` 的联网前拒绝，以及服务端支持时的原压缩路径。
- 单元测试先在旧代码下复现 2/2 RED，再以新契约验证 fallback、普通查询、同步/异步和压缩 capability 选择；受影响非 TLS 压缩/诊断筛选 `122/122` 通过。
- 新增 `Protocol/CompressionTests`，对每个选中的非 OB20 profile 运行同步/异步 `SELECT 1`。

## 真实直连结果

| profile | `Use Compression=true` | 结果 |
| --- | --- | --- |
| MySQL 直连 | 服务端不声明 `CLIENT_COMPRESS` | Open 和 `SELECT 1` 通过，普通协议降级 |
| Oracle 直连 | 服务端不声明 `CLIENT_COMPRESS` | Open 和 `SELECT 1 FROM DUAL` 通过，普通协议降级 |

真实集成测试为 4/4 通过（双模式 × 同步/异步）。探针没有建表、写入或修改服务端配置；凭据只通过当前进程传递。

## 当前边界

本次关闭的是“服务端不支持压缩时错误失败”的客户端差异，不代表当前租户已经验证压缩帧。真实压缩帧、OBProxy、压缩 TLS、跨平台和大负载仍需具有对应 capability 的环境；OB20 压缩和 proxy reroute 继续明确拒绝。
