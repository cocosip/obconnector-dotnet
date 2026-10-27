# OB20/trace 显式验收

验收通过公共 `Negotiated` / `Frame` 诊断及正常查询结果，检查配置对应的实际协议。默认不运行，普通单测和CI只使用合成服务端，不依赖真实环境。

## 选择profile

在独立验收配置中设置 `ob20TraceProfile`，并在私密连接配置中显式启用 `Use OB20=true`。每个profile有 `.sync` / `.async` 两个 `matrix.ob20-trace-negotiation.<profile>` case；仅匹配的两个执行，其余跳过。无效profile或冲突选项在解析/解析连接配置时失败，尚未联网。

| profile | Enable Full Link Trace | 期望实际帧层 | trace / modern | 额外断言 |
| --- | --- | --- | --- | --- |
| `ob20` | false | OB20 | false / false | 写帧不得含trace extra-info |
| `trace-legacy` | true | OB20 | true / false | 查询写帧至少一次携带legacy trace extra-info |
| `trace-modern` | true | OB20 | true / true | 查询写帧至少一次携带modern trace extra-info |
| `declined-ob20` | true | MySQL | false / false | 验证请求OB20/trace被拒绝后的普通MySQL通信，无OB20帧 |
| `declined-trace` | true | OB20 | false / false | OB20启用、trace拒绝；写帧不得含trace extra-info |

所有profile禁止压缩；保留原TLS配置，不做降级。若部署没有所选协议或trace策略，启用的case失败，不自动改为另一个成功profile。TLS握手失败同样保留失败。

配置示例只引用私密连接来源，不包含连接值：

```json
{
  "mode": "MySql",
  "connectionStringEnvironment": "OB_ACCEPTANCE_CONNECTION_STRING",
  "ob20TraceProfile": "trace-modern",
  "groups": ["faults"]
}
```

该配置的私密连接需同时请求OB20与Full Link Trace。现有两个example文件省略profile，继续默认跳过。`--list` 不解析凭据、不连接。运行方法见[验收README](../tests/OceanBaseConnector.Acceptance/README.md)。

## 断言与边界

- 复制配置并关闭池化，强制新物理握手；不借用旧池会话，不创建数据库对象或修改服务器设置。
- 只接收当前逻辑执行流的诊断，按listener实例和ChannelId匹配，要求恰好一次、六字段类型和值均正确的协商事件。
- Open之后才开始统计命令帧；完成三次固定 `SELECT 1`（Oracle加 `FROM DUAL`），要求结果为1且实际帧层双向通信。同步命令注册调用方取消，异步命令传递token。
- 活跃trace还要求实际发送extra-info，检查写帧modern标志。三次查询允许第一份响应的服务器策略启用后续请求；若没有采样/控制产生发送数据，明确失败。只观察extra-info标志，不读取或存储其内容。
- 捕获器只保留固定标量和布尔汇总，不保留事件字典、trace ID、payload、SQL、端点或凭据。连接、命令、Activity和订阅均随case释放，失败/取消也执行释放。

成功消费查询结果验证响应主体/校验路径；读Frame事件单独不证明主体有效，写Frame也不证明服务器收到。trace写帧证明本地发送路径接受了数据，不证明collector收到、服务器持续采样、完整链路导出、真实OBProxy兼容或性能预算。TLS/auth矩阵仍是独立占位；本模块不实现OB20压缩/reroute，也不关闭P2-06整项。

## 当前真实环境结果

在用户提供的直连 OceanBase 4.4.2.2 MySQL/Oracle 租户上，Windows/.NET 10.0.14、TLS Disabled
运行了 `declined-ob20` 的同步/异步用例；两种模式均通过连接控制和两个 profile 用例（各 6 passed、0
failed，其他 case 按配置跳过）。两种模式的 `ob20` profile 均按预期失败，诊断显示请求了 OB20 但实际
`Layer=mysql`；因此该环境没有提供 OB20 实际帧或 trace 发送策略。客户端保留显式失败，不把服务端
拒绝伪装成 OB20 成功。该结果只证明普通 MySQL fallback 的真实路径，不能证明 OB20/trace、OBProxy 或
collector 兼容性。

实现入口：[执行case](../tests/OceanBaseConnector.Acceptance/DatabaseCases.Ob20Trace.cs)、[标量捕获](../tests/OceanBaseConnector.Acceptance/Ob20TraceCapture.cs)。公共事件语义见[协议诊断](protocol-diagnostics.md)。
