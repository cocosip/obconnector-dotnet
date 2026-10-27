# 驱动完整性清单与剩余开发

更新：2026-10-28。已提交产品基线`85e6fe6`；P2-01 DataAdapter/CommandBuilder 已按范围审计移除；当前OB20/trace验收是未提交草稿，专项88/88通过，尚未完成完整门禁。
日常查看[具体功能矩阵](obconnector-dotnet-progress.md)；本文件保留原23项审计编号（其中P2-01已移除）和剩余工作包，不再重复交付流水。

## 23项缺口矩阵

“已实现范围”是已有代码和本地证据；“仍缺开发”与“仍欠验收”分开。P2-01 已从当前产品范围移除，其余大项仍不能据本表宣称整体关闭。

| ID | 模块 | 已实现范围 | 仍缺开发 / 明确不支持 | 仍欠验收 |
| --- | --- | --- | --- | --- |
| P0-01 | 连接池 | 容量/等待/预热/探活/回收/清池/指标、重入和通知取消修复 | 其他并发/Dispose交错、长期资源与shutdown范围 | 持续资源上限、完整部署矩阵 |
| P0-02 | 会话隔离 | reset代际、状态SQL退休、Oracle自动提交、reader/LOB/cursor/XA隔离 | 任意隐式副作用、完整资源/中断/交错覆盖 | 代理reset、外置LOB真实中断、完整借用者隔离 |
| P0-03 | Ambient | 单资源PSPE、取消/晚回调/终态保护 | 分布式提升和durable prepare不支持；完整失败覆盖待补 | 实际事务管理器、timeout/in-doubt和代理故障 |
| P0-04 | Unknown outcome | 分类、销毁/新租约、16项受控网络断言、应用核对示例 | 完整发送/执行/取消故障边界；不重放未知写 | 服务器/代理/协调器全故障矩阵 |
| P0-05 | 活动连接恢复 | 连接ERR置Broken、旧事务失效、显式Close/Open | 无透明迁移/自动重连/replay，先定契约；补完整重建 | 节点/代理断连和恢复 |
| P0-06 | 池路由隔离 | resolver身份、Open快照、闲置退休和借出保留 | 其他边界/压力覆盖；无活动迁移/DNS-IP身份保证 | 单上游受控路由已通过，独立节点/代理HA仍缺 |
| P1-01 | 多语句 | opt-in文本多结果 | 参数/Prepare/cursor/Oracle输出组合拒绝，扩展范围待定 | 中途错误/排空/版本/代理 |
| P1-02 | 压缩/OB20 | 标准MySQL压缩、普通OB20及trace | 压缩OB20、proxy reroute 当前明确拒绝；除非取得客户端协议依据，不列为默认开发项 | 真实压缩/OB20/代理及损坏/取消 |
| P1-03 | LOCAL INFILE | opt-in应用流上传、安全边界 | 大输入/取消边界回归待补 | 实际上传策略、代理和大文件 |
| P1-04 | TLS/mTLS | 服务器回调、客户端证书、池隔离、撤销选项 | 发现实际差异后补轮换/平台回归 | 企业CA、负向握手、过期/轮换、平台撤销 |
| P1-05 | Command/Batch | SchemaOnly/KeyInfo/结果限制、MySQL过程、Batch元数据 | 完整行为/结果形状矩阵，现有拒绝组合保留 | 权限/目录/prepare副作用/锁/游标 |
| P1-06 | Oracle输出/游标 | Prepare、OUT/RETURNING、输出提示、文本/二进制cursor列 | Input/InputOutput REF CURSOR 没有 Java 输入绑定依据，当前发送前拒绝；输出offset/数组正FetchSize也拒绝 | 真实方向/分页/资源/版本/代理 |
| P1-07 | 复杂类型 | 嵌套二进制结果、LOB/cursor结果、命名LOB内容/匿名locator输入、DDL失效 | 命名LOB定位器回绑、复杂cursor输入、文本复杂结果及未解析RETURNING身份没有足够协议依据，当前拒绝；其他形状按服务端矩阵决定 | DDL、真实复杂SQL、资源生命周期和更多部署profile |
| P1-08 | 类型/字符集 | NUMBER/FLOAT、scale、常见扩展类型、客户端编码、DBCS fallback、CLOB转码 | 其余national存储/输出、扩展类型/metadata；UTF8MB3/SJIS/Big5 Open失败待解决 | JSON/CLOB转换/存储、完整字符集/版本/代理；保留失败 |
| P1-09 | HA/发现 | TCP重试/健康缓存、TNS/OCP、组/权重/黑名单/上限 | 其他发现/策略边界；TCP健康不等于数据库健康 | 实际发现服务/节点/代理故障 |
| P1-10 | Schema | 双模式目录、DataTypes、原子参数派生、FLOAT/INOUT | 包内PL/SQL参数类型拒绝，实际目录差异回归待补 | 权限、大小写、重载、版本、代理 |
| P1-11 | 凭据轮换 | 每次Open取值、过期/取消、版本退休、脱敏 | 实际provider/plugin差异回归待补 | 凭据服务、plugin/KDC、拒绝/刷新/轮换 |
| P2-02 | Statement cache | opt-in idle LRU、上下文/DDL/reset代际失效 | 更多reprepare/资源/负载回归 | DDL、资源/延迟和代理 |
| P2-03 | 批处理优化 | 顺序Batch、Oracle array binding、MySQL INSERT rewrite | multi-send/pipelining、独立bulk 没有当前 Java/OceanBase 契约，明确不列为待实现 | 部分失败/事务/触发器/警告/限额与吞吐 |
| P2-04 | 诊断 | 日志/Activity/Meter、池/阶段/慢命令/协议/I/O/源流 | 其他资源等待不是已定义产品契约，不列为待实现 | 真实负载、资源曲线和监听开销 |
| P2-05 | TFM/生态 | 五资产、Windows实际运行时nupkg协议子集 | 完整平台/API矩阵、Framework/ORM支持范围 | 非Windows、完整功能和实库消费者 |
| P2-06 | 真实兼容性 | 验收工具、双模式直连和网络/XA/路由专项 | TLS/auth占位；OB20/trace替换占位仍在草稿；发布审查 | OBProxy、TLS/auth、允许零日期SQL_MODE、完整资源/故障/版本/平台 |

## 八个工作包

| 包 | 覆盖ID | 下一步交付重点 |
| --- | --- | --- |
| W1 池与会话 | P0-01、P0-02、P0-06 | 完整并发/资源/隐式状态与代理reset |
| W2 事务与恢复 | P0-03、P0-04、P0-05 | 发送/执行/取消边界、活动连接重建和协调器故障 |
| W3 协议选项 | P1-01、P1-02、P1-03 | 已有协议路径的真实上传/压缩和组合验收；OB20压缩/reroute 先保持拒绝 |
| W4 信任与发现 | P1-04、P1-09、P1-11 | 企业TLS/plugin/KDC、轮换、实际发现与HA |
| W5 命令与类型 | P1-05、P1-06、P1-07、P1-08 | 已有输出/复杂类型/编码路径的真实验收；输入游标及无依据复杂形状保持拒绝 |
| W6 目录与生态 | P1-10 | 真实目录/权限/派生 |
| W7 性能与诊断 | P2-02、P2-03、P2-04 | statement cache、已有批处理路径、负载与诊断开销；不扩展 multi-send/bulk 或未定义等待点 |
| W8 运行时与发布 | P2-05、P2-06 | 显式验收profile、承诺部署/运行时和发布证据 |

## 关闭标准与发布边界

每项功能须具备：明确支持/拒绝契约、源码、对应回归、成功构建、完整单测、一次独立最终走查、同步矩阵及本地提交。适用的实库/代理/运行时证据另行验证，不能用合成回环替代。

正式发布仍缺：完整reset/资源隔离、Oracle未分类失败闭合、真实OBProxy和TLS、完整unknown/XA/活动连接故障报告，以及声明TFM的包消费证据。此前局部通过不取消这些门槛。

不自动重放未知写，不静默降级TLS，失败会话不回池。分布式ambient提升、透明活动会话迁移、通用durable coordinator没有现成承诺，扩展前须明确范围。真实连接仅用于显式验收，普通单测/CI不依赖用户环境。

## 按需查阅

- [当前具体功能矩阵、草稿和门禁](obconnector-dotnet-progress.md)
- [交付状态摘要](obconnector-dotnet-delivery-status.md)
- [详细审计、协议依据、场景/源码/关闭标准及15.1—15.107历史](history/obconnector-dotnet-completeness.md)
- [逐模块测试/走查/失败与提交记录](history/obconnector-dotnet-progress.md)
- [兼容性及已记录限制](compatibility.md)

日常更新矩阵和当前开发项，不再向本文件追加逐次交付记录；详细证据只在需要核查时打开。
