# OceanBase .NET scope audit against Connector/J

审计基线：OceanBase Connector/J 2.4.14，commit `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`；
当前 .NET `master`。本表把旧的 23 个 ID 从“交付缺口”改为证据分类。Java 代码路径只作为能力事实，
不把 JDBC API 直接复制成 ADO.NET API；ADO.NET 的连接、命令、参数、reader、事务和物理连接池是本项目明确保留的核心。

| ID | 旧分类 | Java/OceanBase 对照 | 当前处置 | 代码动作 |
| --- | --- | --- | --- | --- |
| P0-01 | 连接池 | Java 有 `OceanBasePoolDataSource`/`Pool`；.NET 有物理会话池和 reset | 必须保留 | 保留并继续验证并发、资源和 reset |
| P0-02 | 会话隔离 | Java 池归还会 reset；OceanBase 会话状态会影响复用 | ADO.NET 正确性要求 | 保留；不把未覆盖矩阵写成新功能 |
| P0-03 | Ambient transaction | Java 有本地事务/XA；`DbConnection.EnlistTransaction` 是 ADO.NET 入口 | 有效但范围受限 | 保留单资源和明确拒绝提升 |
| P0-04 | Unknown outcome | SQL/提交响应丢失是数据库客户端安全问题，Java 也区分连接故障 | 核心安全契约 | 保留；禁止未知写自动重放 |
| P0-05 | 活动连接恢复 | Java failover 有边界；透明迁移不是当前 Java/.NET 默认契约 | 明确拒绝自动迁移 | 保留显式重建/退休，不实现透明 replay |
| P0-06 | 池路由身份 | Java 有池/HA 路由；池配置和 endpoint 身份必须隔离 | 连接池必要组成 | 保留 |
| P1-01 | 多语句 | Java `allowMultiQueries` 和测试存在 | Java/OceanBase 能力 | 保留 opt-in 文本路径，保留受限组合拒绝 |
| P1-02 | 压缩/OB20 | Java 有标准压缩、OB20 和 trace；压缩 OB20/reroute 需独立协议条件 | 保留已有协议，拒绝无依据组合 | 不实现猜测性 OB20 压缩/reroute |
| P1-03 | LOCAL INFILE | Java 有 `LocalInfileInterceptor`、`CLIENT_LOCAL_FILES` | Java/OceanBase 能力 | 保留显式授权、应用流和安全边界 |
| P1-04 | TLS/mTLS | Java 有 TLS/socket plugin；客户端证书是部署能力 | 有效部署能力 | 保留；企业 CA 只作为验收矩阵 |
| P1-05 | ADO.NET 命令行为 | Java 有 prepare/call/metadata，但没有 ADO.NET 同名 API | ADO.NET 核心与可选 API 分开 | 保留 DbConnection/Command/Reader/SchemaOnly/KeyInfo；逐项审查 DbBatch |
| P1-06 | Oracle 输出和游标 | Java `registerOutParameter`、`getComplexCursor`、四字节 cursor ID | 输出能力有效 | 保留 OUT/结果列；Input/InputOutput 只保留发送前拒绝，不列为缺失 |
| P1-07 | 复杂类型 | Java 有 object/collection、LOB、cursor 成员和 array binding | Java/OceanBase 能力 | 保留已有有证据形状；复杂 cursor 输入等无证据形状继续拒绝 |
| P1-08 | 类型/字符集 | Java `ColumnType`、结果协议和 charset 处理覆盖对应类型 | Java/OceanBase 能力 | 保留协议已有映射；未证实的扩展 metadata 不新增 |
| P1-09 | HA/发现 | Java 有 failover、TNS、OCP、blacklist/group 选择 | Java/OceanBase 部署能力 | 保留已有实现；真实代理/拓扑只算验收 |
| P1-10 | Schema/参数元数据 | Java 有 `DatabaseMetaData`、Oracle dictionary 和参数描述 | Java/OceanBase 能力 | 保留实际目录与参数推导；不把权限矩阵当新 API |
| P1-11 | 凭据提供/轮换 | Java 有 `CredentialPlugin`；.NET provider 是 ADO.NET 适配 | 有证据的扩展 | 保留；真实 plugin/KDC 是部署验收 |
| P2-01 | DataAdapter/CommandBuilder | Java 没有对应 API；这是可选 ADO.NET 生态，不是 OceanBase 协议核心 | **已移除** | 已删除 public 类型、factory 入口、单元/consumer 测试和当前文档入口；CRUD 使用显式命令 |
| P2-02 | statement cache | Java `cachePrepStmts`/prepared cache 存在 | Java/OceanBase 能力 | 保留有限 cache 和失效规则 |
| P2-03 | 批处理优化 | Java 有 batch rewrite/Oracle array binding；没有已确认的 multi-send/bulk 契约 | 分拆 | 保留 rewrite/array binding；删除“multi-send/bulk 待实现”目标和占位 |
| P2-04 | 诊断 | Java 有 logger、slow/protocol `TimeTrace`；.NET DiagnosticListener/Meter 是 API 适配 | 有效运维能力，但不扩张范围 | 保留已有诊断；删除“其他资源等待”未定义目标 |
| P2-05 | TFM/生态 | Java 没有 .NET TFM；属于发布验证，不是 OceanBase 功能 | 重新分类 | 从功能缺口移到发布矩阵，不删除核心代码 |
| P2-06 | 真实兼容性 | Java 测试和真实 OceanBase 也需要版本/代理/认证矩阵 | 验收门槛，不是代码缺口 | 保留验收记录，不新增功能代码 |

## 明确不再开发的形状

- REF CURSOR Input/InputOutput：Java 没有输入绑定路径，当前 .NET 发送前拒绝。
- 复杂 cursor 输入、命名 locator 回绑、文本复杂结果和未解析复杂 RETURNING：没有足够的
  Java/OceanBase 输入协议或形状证据，继续显式拒绝。
- OB20 压缩、proxy reroute、multi-send/bulk 和其他资源等待：没有当前可复用的协议或公共契约，
  从“待实现缺口”中删除。

## 已执行的删除批次

P2-01 有明确的 public 类型、provider factory 分支、单元测试、consumer 测试和文档入口，而且
Connector/J 没有对应能力。现已删除这些入口，并保留 `DbConnection`、`DbCommand`、参数、reader、
事务、batch 和物理连接池；删除不影响 ADO.NET 核心连接路径。
