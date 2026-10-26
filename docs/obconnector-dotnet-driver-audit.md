# OceanBase .NET 驱动完整性走查报告

**最近复核：** 2026-10-26；基于master/d7a5354的Batch元数据及数据源条目创建；全部23项及八工作包仍开放。
**当前模块：** P1-05/W5；完整性第15.60节、报告第55节。
**当前验证：** 完整单位6198通过（保留此前6147及新增51，8分7秒）；受影响单位269、消费者162通过（新增72），均零失败/跳过。
Release全解决方案0警告/错误（2.39秒）；4C#限定格式验证退出0，保留既有workspace加载警告。
**本次修复：** Batch开放SchemaOnly/KeyInfo；数据源Batch不再落入框架CreateDbBatchCommand的NotImplemented入口。
保留批次预留、同会话目录、目标不执行、懒启动/提前关闭、精确取消、CloseConnection租约和源连接所有权。
原12RED、消费者36/36及后续夹具失败全部保留；当前未新增实库/OBProxy、企业TLS、跨平台或独立运行时验收。
**仓库：** `D:\dotnet-code\obconnector-dotnet`。
**初次走查：** 2026-10-24，.NET `06d58e1`；Java 固定参考 `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1` / Connector/J 2.4.14。
**目标和结论级别：** 为 MySQL/Oracle 双模式定义可执行的完整驱动清单；源码、本地 fixture、包消费和真实互操作分别记载，不互相替代。


**阅读入口：** 第55节记录Batch元数据及源条目创建，第54节记录诊断卸载修复和默认642项回归，第53节保留池通知交接及原崩溃检查点；第52节记录本次协议帧诊断，第51节保留二进制游标列与嵌套所有权，第50节保留黑名单/尝试上限，第49节保留发现分组/策略/权重，第48节保留OCP HTTP适配，第47节保留TNS文件适配，第46节保留TCP健康缓存，第45节保留TCP重试，第44节保留连接级ERR，第43节保留Closed回调修复，第42节保留national复杂成员，第41节保留Oracle输出提示，第40节保留事务响应丢失验收，第 39 节保留外置 LOB 验收，第 38 节保留内联资源验收；第 37 节保留此前文档交付与未完成验收；[第 1.1 节](#11-本次评审摘要与剩余交付清单)列剩余工作；[第 1.2 节](#12-明确缺失和受限组合实施入口)列实施入口；第 3/4 节列当前能力和全部缺口；第 8 节定义发布门槛。第 36 节为当前 reader 租约修复；第 35 节保留 XA 池隔离验收；第 34 节保留自动提交模块；第 33 节保留文档与 RED 检查点；第 32 节为已提交 NUMBER 模块；第 31 节保留慢命令诊断交付；第 30 节保留建连阶段诊断交付；第 29 节保留 Oracle Prepare 交付；第 28 节为开发前文档检查点；第 27 节为已提交字符集模块；第 25 节保留归一化开发前快照，第 12/14/17 节和基线第 15 节保留原始失败、修复及跳过，不作为当前未实现声明。

此前池路由和 TLS 验证分别见[池路由记录](obconnector-dotnet-progress.md#2026-10-24-p0-06-pooled-resolver-routing)及 [TLS 记录](obconnector-dotnet-progress.md#2026-10-25-p1-04-tls-server-validation-callback)。当前双模式直连证据不代表 OBProxy、企业 TLS、跨平台或独立运行时验收。

## 1. 结论先行

当前 .NET 驱动已经超过“只有连接和简单 SQL”的阶段，但仍不能宣称是完整的生产级驱动。最关键的事实如下：

| 问题 | 结论 |
| --- | --- |
| Java 驱动有没有连接池 | 有。Connector/J 有 `OceanBasePoolDataSource`、`OceanBasePooledConnection`、`Pools`/`Pool`，覆盖池注册、借用/归还、最小/最大容量、空闲和生命周期清理以及池事件。 |
| .NET 当前有没有连接池 | 有基础物理会话池，默认 `Pooling=false`。显式启用后可复用已认证的 `PhysicalSession`，支持最大容量、等待超时/取消、借用前 `COM_PING`、空闲/总生命周期回收、显式清池和借出会话归还退休，归还时执行 reset；可识别的用户 session-state/temporary-object 语句会销毁会话。Closed 回调重开的池归属已有修复与回归，真正多线程压力仍缺证据。 |
| .NET 连接池是否完整 | 不是。配置归一化及共享容量/清池身份已有本地实现；完整 session reset、压力和真实 OceanBase/OBProxy reset 兼容性仍未闭合。池指标、最小池预热及失败回滚、空池 static registry 清理已有本地实现。 |
| 当前最大的生产风险 | 会话状态隔离、unknown outcome、ambient 本地事务的故障矩阵、活动连接故障恢复和真实代理/TLS/跨平台矩阵仍不完整。 |
| 是否已经达到 Java/.NET 成熟驱动水平 | 没有。现有实现可以继续开发和做受控试用，但 README 和包元数据应继续保持 experimental/未完成真实兼容性验证的表述。 |

本报告共追踪 **23 项缺口：P0 六项、P1 十一项、P2 六项**。其中有功能缺失、已实现功能的组合限制、源码推导的正确性问题及真实验收缺口，不能合并称为“全部尚未实现”。每项触发场景、源码/测试入口及关闭标准见[详细追踪表](obconnector-dotnet-completeness.md#14-缺口到场景源码和验收的追踪表)。第一版明确排除的能力也要有公开契约，不能把成熟驱动的所有可选特性都当成无条件发布要求。

### 1.1 本次评审摘要与剩余交付清单

**当前文档复核日期：2026-10-26；当前开发基线：`master/e0d1d30`。** 本次协议帧诊断见第52节，二进制游标列保留于第51节，此前OCP HTTP适配见第48节，TNS文件适配保留于第47节，端点TCP健康缓存保留于第46节，TCP重试保留于第45节，连接级ERR保留于第44节，此前Closed回调见第43节，national成员见第42节，Oracle输出提示见第41节，事务响应丢失验收及核对示例见第40节，已提交外置 LOB 验收见第 39 节，已提交内联资源验收见第 38 节；第 37 节保留此前文档复核与未提交草稿的失败记录。已提交 reader 租约隔离见第 36 节，原始失败、源码入口、实际回归和剩余矩阵均见能力基线第 5.1.7/15.41 节。 池配置身份和普通结果字符集均已提交，分别见第 26、27 节；Oracle 独立 Prepare 已提交 `d6a9169`，见第 29 节；阶段诊断已提交 `b2fce9d`，见第 30 节；慢命令诊断已提交 `5ea71b2`，见第 31 节；Oracle 未知精度 NUMBER 修复已提交 `4810907`，见第 32 节；XA 池隔离专项见第 35 节；Oracle 自动提交模块见第 34 节；W1 RED 与文档检查点保留于第 33 节，未交付时的文档检查点保留于第 28 节。第 25 节保留池修复前发现及文档快照。此前会话识别、statement cache、ambient 单资源及初始化取消、复杂类型可观察 DDL 自动失效保留，第 20 节原取消缺口及第 21 节修复证据不改写。Java 参考 HEAD 本次再次核实为 `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`。第 12、14、17 节保留早期文档快照，W1—W8 清单表示当前欠项。

**当前基线最近已提交修复：** `0ec3e1c` 修复连接级服务器ERR退休/unknown边界，见第44节；`917e9cc` 的Closed回调修复保留于第43节。**此前reader修复：** 旧 CloseConnection reader 延迟关闭按原逻辑 Open 代数隔离，不再因物理会话被复用而关闭新租约；普通 reader 与 Batch 四个同步/异步入口共同核对，见第 36 节。**此前自动提交模块：** Oracle 新连接及池 reset 后增加显式自动提交初始化，原实库四个隐式写失败均已同名通过；reset 后才设置，不提交遗留写入。最终回归见第 34 节，契约见[第 5.1.5 节](obconnector-dotnet-completeness.md#515-oracle-自动提交基线已确认缺陷与待实现契约)。原始失败与 RED 检查点保留；完整资源/XA/并发隔离、OBProxy 和其他部署矩阵仍开放。

Java 和 .NET 的连接池都已存在；.NET 默认 `Pooling=false`。会话识别通过保守销毁可识别的状态会话来避免复用；本次自动提交与池隔离专项已有真实双模式直连证据，见第 34 节。**专项通过不等于完整 reset 或全部借用者隔离已验收。** 无法静态识别的函数/触发器副作用、完整资源与 XA、并发/长期运行及其他服务器/代理 reset 矩阵仍须验收。

以下是完整驱动当前的执行清单。编号沿用原 23 个 ID，不增加平行清单；每项具体触发场景、源码/测试入口及关闭标准见[详细追踪表](obconnector-dotnet-completeness.md#14-缺口到场景源码和验收的追踪表)，工作包依赖见[第 16 节](obconnector-dotnet-completeness.md#16-剩余交付工作包)。

| 工作包及缺口 | 当前剩余工作 | 对使用者的影响 | 最低验收产物 |
| --- | --- | --- | --- |
| W1 池与会话：P0-01、P0-02、P0-06 | 保留 reader 原租约关闭保护及同物理会话复用专项（第 36 节）；保留 Oracle fresh/reset 自动提交初始化及失败销毁/真实直连回归；补部署矩阵；保留已有容量、预热、探活、清池、路由身份及会话识别；补完整状态/资源/XA 隔离、并发与长期运行；保留配置归一化及原始 PAM 因子身份（第 26 节），补部署验收，定义 shutdown 范围 | 配置身份已统一；Closed事件内满池Open已有第43节专项；完整状态恢复、其他日志重入/并发和资源上限尚缺完整证明 | 等效配置共池/共享容量/清池及身份隔离报告；双借用者状态矩阵；满池/取消/清池/Close/Open/Dispose/reset 交错与资源曲线；双模式直连及 OBProxy reset 报告 |
| W2 事务与恢复：P0-03、P0-04、P0-05 | 保留已交付的单资源/终态/身份、自动初始化取消及池/协调器恢复回归；保留第40节真实响应丢失/单容量池恢复和[结果核对指南](unknown-outcome-recovery.md)；保留第44节连接级ERR/显式重建本地回归，补其他故障状态机及真实重建矩阵 | 断线后应用可能无法确认写入或提交结果，必须核对业务状态；不得盲目重试 | 故障注入矩阵、in-doubt/超时结果、会话丢弃及重建示例；证明未知写无自动重放；分布式提升明确拒绝 |
| W3 协议：P1-01、P1-02、P1-03 | 标准压缩、opt-in 文本多语句和应用流 LOCAL INFILE 已有；补组合/错误矩阵、OB20 压缩契约与实现、reroute | 可选协议组合仍受限，不能把标准压缩等同于 OB20 压缩；代理重定向未交付 | capability/版本依据、分片/序号/校验/解压限额、多结果中途错误、上传取消及真实代理报告 |
| W4 信任与发现：P1-04、P1-09、P1-11 | TLS 回调/客户端证书隔离、凭据快照/轮换已有；保留第45节TCP重试/退避及共享预算、第46节端点TCP健康缓存；补真实健康/故障验收、其余本地策略覆盖及发现边界（全局黑名单/尝试上限见第50节，分组/权重见第49节，TNS/OCP适配见第47/48节），以及真实企业认证验证 | 企业 CA、撤销、短期凭据或节点变更部署尚不能依据本地 fixture 获得兼容承诺 | 企业 CA/mTLS/撤销、插件/KDC/凭据过期刷新失败、地址更新/错误身份、Windows/Linux 报告 |
| W5 命令与类型：P1-05、P1-06、P1-07、P1-08 | 保留 SchemaOnly、KeyInfo 和 signed DecimalScale；保留 MySQL StoredProcedure 执行与输出本地回归，补真实部署矩阵、Oracle 输出方向/结果形状、复杂LOB/cursor、其他national边界及复杂整列RETURNING身份及扩展类型/其余字符集；保留严格普通结果解码/宽度（第 27 节）并补完整部署矩阵；保留自动 DDL 失效回归并补真实验收 | 目录可推导参数不等于过程可执行；复杂结果、目录键和扩展编码不能按基础 SELECT 的通过结果推定支持 | 双模式过程/输出与 Command/Batch/REF CURSOR 行为表、NULL/精度/溢出/编码/资源释放、真实目录及 Prepare 行为/锁报告 |
| W6 目录与生态：P1-10、P2-01 | 常用 Schema、DataTypes、参数推导、同步 adapter/有界命令生成已有；补真实目录/权限/版本和 CRUD/并发验收 | 生成 UPDATE/DELETE 要求完整可见行键；复杂形状需显式命令，推导不扩大过程执行支持 | 双模式目录/权限/重载/参数方向，实际 CRUD/乐观并发/事务/锁、独立运行时报告 |
| W7 性能与诊断：P2-02、P2-03、P2-04 | 有限 idle statement cache 已本地实现，补真实 DDL/reprepare/资源/负载验收；选择并实现承诺的 rewrite/multi-send/bulk 优化；保留九阶段建连/探活直方图；保留慢命令阈值报告；保留第52节协议帧诊断；补其他资源等待与性能验收 | 当前 Batch 逐项执行，性能和诊断完整性尚不能按成熟驱动承诺 | 缓存容量/失效/关闭、部分失败/取消/包限额，吞吐/延迟/峰值内存对照及脱敏/监听开销报告 |
| W8 平台与发布：P2-05、P2-06 | 明确承诺的服务器/代理版本、平台/TFM/ORM 范围；保留第54节诊断卸载修复与默认642项回归；补独立运行时、matrix.* 真实案例、包来源/许可和发布检查 | 包含五种 TFM 资产不等于五个运行时已独立验证，实验性标签仍须保留 | 每个承诺 profile 的包消费、实库/代理/TLS/认证/故障报告；保留失败及跳过原因、XA 外部协调器恢复证据 |

上述工作中，W1/W2 的隔离和失败处理优先；W3—W6 按实际部署与公共 API 承诺交付，W7 的优化以测量为依据，W8 随所有模块持续积累证据。依赖及关闭要求以详细基线第 16 节为准；负责人和日期尚未分配，本文不把建议写成排期承诺。

**仍须确定的支持范围：** OB20 压缩的客户端版本/协商，完整OCP/TNS策略及分组方式（基本TNS/OCP适配已实现），批量优化方式，统一 shutdown，以及正式承诺的 TFM/平台/ORM 范围。分布式 ambient 提升当前明确不支持；EF Core provider 是独立生态工程。这些边界需公开记录，不能默认为已经实现或已经排除。Java 2.4.14 也拒绝压缩 OB20，不能把它列为现成的 Java 移植能力。

**当前证据入口：** 第 31 节及能力基线第 15.36 节记录慢命令诊断；第 30 节及能力基线第 15.35 节记录建连阶段诊断；第 29 节及能力基线第 15.34 节记录 Oracle 独立 Prepare；第 28/15.33 节保留开发前文档复核；第 27/15.32 节记录已提交结果字符集模块；第 26/15.31 节保留池身份模块；第 24 节及[第 15.30 节](obconnector-dotnet-completeness.md#1530-2026-10-25-mysql-存储过程执行本地交付)保留上一过程模块的失败、修复与最终门禁。第 22/15.28 节为上一 DDL 模块证据；第 19 节缓存竞争、第 18 节会话识别与全部历史结果保留，不能替代当前验证。真实资源负载、OBProxy 和独立运行时矩阵仍缺。

**完整驱动的完成定义：** 每个承诺能力必须同时具备公共契约、对应实现、可复现回归和要求的真实环境证据；明确不支持的组合应在发送前拒绝并有文档。23 项是跟踪项，包含缺实现、受限实现与欠验收，并非 23 个已复现缺陷。现有 Oracle nested-table column 失败、全部跳过和第 8 节发布门槛继续保留，本次文档交付不关闭任何完整缺口。

### 1.2 明确缺失和受限组合：实施入口

以下是当前源码可直接定位的功能限制，用于从 W1—W8 汇总进入具体开发。它不是新增缺口编号，也不是新复现的故障清单；完整 23 项及验收要求仍见第 4 节和详细基线第 14 节。

| 触发场景 / 归属 | 当前行为及影响 | 源码入口 | 补齐与验收要求 |
| --- | --- | --- | --- |
| Oracle 未开启显式事务的写入，P0-02 / W1 | 已显式初始化并验证，原 fresh/reset 隐式写失败现已通过；完整隔离矩阵仍开放 | [HandshakeFlow.Identity](../src/OceanBaseConnector/Internal/Session/HandshakeFlow.Identity.cs)、[PhysicalSession.ResetCore](../src/OceanBaseConnector/Internal/Session/PhysicalSession.cs) | 身份确认/reset 后显式初始化并验证；失败销毁，保留 MySQL 拒绝契约；真实证明隐式提交、显式回滚和健康会话复用，详见[基线第 5.1.5 节](obconnector-dotnet-completeness.md#515-oracle-自动提交基线已确认缺陷与待实现契约) |
| 等效连接配置的顺序、默认值或布尔文本不同，P0-01 / W1 | 已按有效配置统一普通池身份；公共 Open/Close/失败清理/ClearPool 保留原始空 PAM 因子，不能依赖公开字符串重解析；Closed事件内同满池Open先完成捕获归还，其他日志重入仍受限（第43节） | [builder](../src/OceanBaseConnector/OceanBaseConnectionStringBuilder.cs)、[PoolKey 与 registry](../src/OceanBaseConnector/Internal/Session/PhysicalSessionPool.cs)、[ClearPool](../src/OceanBaseConnector/OceanBaseConnection.Pooling.cs) | 按有效类型/默认值生成内部身份，同时保留秘密、策略引用和私有源边界；覆盖共享容量、清池及原池失败清理，保留旧发现及[当前契约](obconnector-dotnet-completeness.md#514-普通池的有效配置身份) |
| MySQL `CommandType.StoredProcedure`，P1-05 / W5 | 已本地实现 CALL/私有输出计划；按集合顺序绑定，ReturnValue、FetchSize、SchemaOnly/KeyInfo 拒绝，提前关闭排空响应；输出按 SELECT 元数据解码，见第 24 节 | [OceanBaseCommand.Start](../src/OceanBaseConnector/OceanBaseCommand.cs) | 保留同步/异步、输出、初始化取消/断线/超时、畸形响应、Batch 重用及默认策略实库回归；补真实 TLS/代理、版本、资源与独立运行时矩阵 |
| Oracle 独立 Prepare 与输出 FetchSize，P1-06 / W5 | 独立Prepare及输出正FetchSize提示已有实现；内部输出不分页，返回cursor独立分页（第41节）；输出offset与数组正FetchSize仍拒绝 | [OceanBaseCommand.Start](../src/OceanBaseConnector/OceanBaseCommand.cs) | 保留独立准备、DML 拆分元数据、错误/取消、cursor/stream/array/池 reset 回归；继续补 fetch/结果形状与真实部署矩阵，不承诺服务端无法提供的形状 |
| REF CURSOR 输入或列值，P1-06 / W5 | 参数Input/InputOutput仍拒绝；普通二进制列和嵌套所有权已有本地实现（第51节），文本及真实SQL验收仍缺 | [OracleCommandPlan](../src/OceanBaseConnector/Internal/Sql/OracleCommandPlan.cs)、[ResultColumn](../src/OceanBaseConnector/Internal/Protocol/ResultColumn.cs) | 补参数方向与 文本cursor列及二进制列真实验收 的所有权契约；验证父 reader/statement/事务/连接提前结束、取消及池归还，不泄漏或误用 cursor ID |
| object/collection 含LOB/cursor及受限national/RETURNING形状，P1-07 / W5 | 标量/嵌套及有界national方向编码已有；LOB/cursor、其他存储编码、无上下文匿名普通national和复杂整列RETURNING身份仍受限；复杂列要求二进制 | [ComplexType](../src/OceanBaseConnector/Internal/Values/ComplexType.cs)、[ResultColumn](../src/OceanBaseConnector/Internal/Protocol/ResultColumn.cs) | 按成员类型补双向绑定、NULL、嵌套、编码和资源释放；循环或超限类型保持确定拒绝；与服务端 nested-table column 失败分开验收 |
| 类型 DDL 后再次使用描述，P1-07 / W5 | 已有 `ClearComplexTypeCache`；本次增加 Oracle 文本 CREATE/ALTER/DROP/RENAME/TRUNCATE 目标发送时的自动失效，之后须重新加载描述并重建绑定值；外部或不透明 DDL 仍须显式清理 | [OceanBaseConnection.ComplexTypes](../src/OceanBaseConnector/OceanBaseConnection.ComplexTypes.cs)、[ComplexTypeDictionaryTests](../tests/OceanBaseConnector.UnitTests/ComplexTypeDictionaryTests.cs) | 明确可观察 DDL、外部变更和手动清理的边界；保证旧描述拒绝、新描述完整发布、活动结果快照稳定；自动失效的完整本地/真实证据分别验收，不能由 statement cache 的存在推定 |
| OB20 压缩或 reroute，P1-02 / W3 | 压缩帧和 reroute 标志拒绝；标准 MySQL 压缩是另一条已实现路径。固定 Java 2.4.14 也拒绝压缩 OB20 | [Ob20Packet](../src/OceanBaseConnector/Internal/Protocol/Ob20Packet.cs)、[Ob20Stream](../src/OceanBaseConnector/Internal/Protocol/Ob20Stream.cs) | 先取得客户端链路、版本、capability 与校验规范；再实现并用真实代理验证，详见[协议前提](obconnector-dotnet-completeness.md#163-ob20-压缩的客户端协议前提) |
| 节点故障或拓扑变化，P0-05、P1-09 / W2、W4 | 已有地址列表与 resolver，候选切换限 Open 阶段 TCP 失败；TCP整轮退避已有本地实现（第45节）；端点TCP健康缓存已有本地实现（第46节），已有内置TNS文件适配（第47节）；已有OCP HTTP地址适配（第48节），分组/策略/权重见第49节，全局黑名单/尝试上限见第50节，本地覆盖及活动连接迁移仍缺 | [EndpointSelector](../src/OceanBaseConnector/Internal/EndpointSelector.cs)、[PhysicalSession](../src/OceanBaseConnector/Internal/Session/PhysicalSession.cs) | 第44节已交付连接级ERR退休与显式Close/Open本地契约；保留有界TCP健康/全候选fallback/代数隔离，补完整故障矩阵，发现适配和透明恢复分别定义范围；真实节点/代理故障中不得重放未知写或迁移未完成事务 |
| 高吞吐 DbBatch，P2-03 / W7 | 逐条执行，Oracle array binding 已有；尚无通用 rewrite/multi-send/bulk 优化 | [OceanBaseBatchReader](../src/OceanBaseConnector/OceanBaseBatchReader.cs) | 选择目标模式和优化方式，验证部分失败、受影响行数、输出顺序、事务与取消；以吞吐、延迟和峰值内存实测决定交付 |
| 排查慢建连/慢查询，P2-04 / W7 | 已有操作 span、日志、池指标和九阶段建连/探活直方图；慢命令阈值报告已有本地实现；协议帧诊断已有本地实现；其他资源等待及负载验收仍缺 | [DriverDiagnostics](../src/OceanBaseConnector/Internal/DriverDiagnostics.cs)、[ConnectionLog](../src/OceanBaseConnector/Internal/ConnectionLog.cs) | 保留已实现的阶段耗时、慢命令阈值及协议帧诊断契约，补其他资源等待及负载验收；覆盖脱敏、监听关闭开销和应用回调异常；不能记录口令、认证载荷或敏感参数 |

**现有实现的验证工作仍是主任务：** 池容量和清理、完整借用者隔离、PSPE/unknown outcome、TLS/凭据轮换、SchemaOnly/KeyInfo、DataTypes/参数推导、DataAdapter 和 statement cache 均须按第 1.1 节取得相应真实报告。不能因本表集中列出待开发入口，就遗漏已有模块的压力、故障、权限和独立运行时验收。

**范围决定单列：** 分布式 ambient 提升当前明确拒绝，显式 XA 不等于自动提升或 durable coordinator；EF Core provider 属独立工程；OB20 压缩、发现适配、批量优化和正式平台承诺按第 1.1 节冻结范围。负责人、期限及性能 SLA 尚未指定，本文不虚构承诺。

**历史文档核对记录：** 配置归一化池模块见第 26/15.31 节；自动提交模块见第 34/15.39 节，当前 XA 池隔离验收见第 35/15.40 节。以下过程模块报告为历史结果：单测 4414、消费者 2558、DI 14 通过；Integration 9 通过/76 NotExecuted，适用 MySQL 实库套件 74 通过/5 跳过。上一 DDL 模块的 4347/2478、9/72 及 ambient 的 4291/2478 是历史结果，保留于第 22、21 节。该旧模块记录的 11 个加载程序集哈希和时间戳未变。旧实库 MySQL 60、Oracle 79 通过/1 失败及 CI 平台配置保留，仍不等于完整平台/代理矩阵。

### 1.3 完整文档交付索引与本次复核

| 评审问题 | 文档入口 | 已整理内容 |
| --- | --- | --- |
| 到底缺哪些功能、先做什么 | 本报告第 1、3、4、7 节 | 当前能力、23 个缺口 ID、用户影响和优先级；区分缺实现、组合受限与欠验收 |
| 每项如何落地并验收 | [能力基线第 5 节](obconnector-dotnet-completeness.md#5-分模块要求和验收标准)、[第 14 节](obconnector-dotnet-completeness.md#14-缺口到场景源码和验收的追踪表) | 功能契约、双模式差异、具体触发场景、源码/测试入口及关闭标准 |
| 如何拆分完整驱动交付 | [能力基线第 16 节](obconnector-dotnet-completeness.md#16-剩余交付工作包) | 八个工作包，每个缺口只归属一个主包；依赖、开发项、验证产物和范围决定 |
| 哪些已经通过、哪些仍失败或跳过 | [进度台账](obconnector-dotnet-progress.md)、[兼容性记录](compatibility.md)、能力基线第 15 节 | 已交付模块及原始失败、修复、跳过和真实环境边界；保留历史，不以当前摘要覆盖 |
| 何时能称为完善驱动 | 本报告第 8 节及能力基线第 7.3、15.1、16.2 节 | 承诺范围内实现、回归、实库、代理、运行时与包消费证据齐备；排除能力须明确说明理由 |

前次文档检查点以干净的 `1bb13cb` 为基线，核对池、过程拒绝及其他限制，并复核 DDL 模块最终 TRX：单测 4347、消费者 2478、DI 14 通过，Integration 9 通过/72 NotExecuted。以上是既有报告，不是本次执行。第 23 节保留过程开发检查点，第 24 节保留过程模块交付；此前 Oracle 自动提交和 XA 池隔离验收分别见第 34、35 节，已提交 reader 修复见第 36 节，本次文档检查点见第 37 节，不能用历史状态覆盖当前交付。完整驱动仍未完成，负责人、日期、性能 SLA 与待定支持范围继续开放。

## 2. 证据规则

本报告使用三种证据等级：

1. **源码/本地回归**：代码路径存在，并由单元测试、消费者测试或 loopback 协议 fixture 覆盖。
2. **包和运行时消费**：目标 TFM 能编译、加载和调用公共 API。
3. **真实互操作**：实际 OceanBase、OBProxy、TLS/KDC 或故障环境中的运行结果。

只有第 3 级证据可以把“未验证”升级为真实兼容。mock、自签名 `SslStream`、TCP loopback、协议 fixture 和包消费测试不能证明真实 SQL、目录、锁、拓扑、认证或代理行为。

初次走查以源码调用链发现 P0-06 resolver/池路由问题及 P1-05 SchemaOnly 执行语义。P0-06 随后已有同步/异步复现与本地修复，详见第 5 节和进度台账；SchemaOnly 已交付 Prepare-only 路径及本地回归，真实副作用/锁/准备形状验收仍待完成。真实拓扑兼容不能由这些 loopback 回归推定。

现有真实数据库记录为 Windows/.NET 10、直连 OceanBase 4.4.2.2、TLS disabled：MySQL 模式 60 通过，Oracle 模式 79 通过、1 个 nested-table column 失败。OBProxy、其他版本、完整 TLS/plugin、KDC、故障注入和非 Windows 运行仍未闭合。

## 3. 当前能力矩阵

| 模块 | 状态 | 已有能力 | 主要缺口或边界 |
| --- | --- | --- | --- |
| 连接与配置 | 部分实现 | 连接字符串、超时、多地址、RoundRobin、模式确认、地址 resolver、空池 registry 清理 | 其余本地策略覆盖及发现边界（全局黑名单/尝试上限见第50节，分组/权重见第49节）、真实健康/故障及完整版本/代理矩阵；有效配置归一化和TCP健康缓存已有本地实现 |
| 物理连接池 | 部分实现 | 物理会话复用、最大容量、等待超时/取消、最小池预热及失败回滚、带 MySQL autocommit 校验的 `COM_PING`/reset、idle/total lifetime、空池 registry 清理、池指标、`ClearPool`/`ClearAllPools`/source `Clear` | 完整资源 reset、广泛并发/长期运行、真实 reset 兼容性 |
| 会话隔离 | 部分实现 | `COM_RESET_CONNECTION`、MySQL `COM_INIT_DB`、reader 消耗后可复用、prepared statement generation 失效、未释放 REF CURSOR/LOB 归还时销毁、可识别的用户 session-state/temporary-object 语句归还时销毁 | 不可识别的 session-state、XA 和失败丢弃的完整矩阵 |
| ADO.NET 核心 | 部分实现 | `DbConnection`、`DbCommand`、`DbParameter`、`DbDataReader`、provider factory、`CloseConnection`、`SequentialAccess`、`SingleRow`、`SingleResult`、仅 Prepare 目标的 `SchemaOnly`、有界键/隐藏列 `KeyInfo`、同步 DataAdapter/单表命令生成 | 真实目录/CRUD、生成形状与可见键限制、完整 schema metadata、SchemaOnly 真实准备行为与形状矩阵 |
| .NET 8+ 资产 | 部分实现 | `DbDataSource`、`DbBatch`、savepoint、异步事务、GSSAPI 代码路径 | 现代 TFM 与旧 TFM 的完整行为矩阵、标准 `DbBatch` 生态验收 |
| 认证 | 部分实现 | native、old、clear、SHA-2、Ed25519、PAM、GSSAPI 路径 | 真实插件成功/拒绝矩阵、KDC、短期凭据服务及轮换实库验收 |
| TLS | 部分实现 | 默认平台信任库和主机名校验；可选服务器验证回调及按引用隔离的普通池；池化/直连 `ClientCertificate` 和 source 证书快照；可选撤销检查 | 真实企业 CA/轮换/撤销和跨平台语义、真实 mTLS 矩阵 |
| 基础 SQL | 已实现 | 文本执行、server prepare、参数绑定、多结果、流式读取、取消、超时 | 真实版本/代理/大数据量边界仍需验收 |
| 多语句 | 部分实现 | 显式 `Allow Multi Statements=true` 的文本多结果路径、默认关闭；Oracle 匿名块和程序 DDL 的内部分号按单个程序单元处理 | 真正的多语句参数/Prepare/cursor/Oracle 输出组合和真实服务器错误矩阵；程序单元非法尾部由服务端解析 |
| 压缩与 OB20 | 部分实现 | 显式 Use Compression 的标准 MySQL 压缩；基础 OB20、校验和、extra info、trace 结构 | OB20 压缩 frame、proxy reroute 及真实压缩矩阵 |
| LOCAL INFILE | 部分实现 | `Allow Local Infile=true` 才请求 `CLIENT_LOCAL_FILES`；应用通过 `LocalInfileStreamFactory` 提供流；同步/异步上传后读取最终 OK，失败会销毁物理会话 | 真实 OceanBase/OBProxy 互操作、代理策略、超过 64 MiB、服务器错误和中断恢复 |
| 本地事务 | 部分实现；本地单资源模块 | MySQL/Oracle 隔离级别、savepoint；PSPE 单资源、终态/身份及自动初始化取消/失败清理回归 | 真实模式/版本/代理/事务管理器故障和恢复示例；分布式提升明确拒绝 |
| Unknown outcome | 部分实现 | 异常可标记未知执行结果，驱动不自动 replay | 发送/响应/commit/rollback 故障状态机和真实故障验证 |
| XA | 部分实现 | Start/End/Prepare/Commit/Rollback/Recover，MySQL/Oracle 分支 | coordinator crash recovery、普通池与 XA 的完整隔离验证 |
| MySQL 过程 | 部分实现 | 集合顺序 Input/Output/InputOutput、显式 Prepare、多结果及关闭排空输出 | PS_MULTI_RESULTS 必需；ReturnValue/FetchSize/SchemaOnly/KeyInfo/INOUT stream 拒绝；真实 TLS/代理/版本/资源矩阵仍缺 |
| Oracle 输出与游标 | 部分实现 | PrepareExecute、独立 Prepare、OUT/RETURNING、REF CURSOR、LOB、数组绑定 | cursor参数方向、文本cursor列及二进制列真实验收、输出offset/数组正FetchSize及其余结果形状；正命令提示已接受但内部输出不分页 |
| 复杂类型 | 部分实现 | object/collection 标量及嵌套成员、类型缓存和数组绑定 | LOB/cursor、其他national存储/匿名结果和复杂整列RETURNING身份、循环结构、文本结果；可观察 DDL 自动失效本地实现已有，外部/不透明 DDL 须显式清理，真实 DDL 验收仍缺 |
| 类型与字符集 | 部分实现 | 常用数值、时间、YEAR、BIT、JSON、zero-date、Oracle 时间/interval；普通结果严格字符集映射/宽度 | 客户端/national 编码、其他字符集/排序规则、扩展 type code/metadata、精度及完整实库矩阵 |
| HA 与恢复 | 部分实现 | Open 阶段 TCP 候选地址重试、注入式 resolver | 活动连接迁移、透明重连、退避、reroute、安全重试 |
| Schema 元数据 | 部分实现 | Oracle 20/MySQL 18 个集合、静态 DataTypes 及自动参数推导有本地回归 | 实库字典、权限/版本/代理和类型/DDL 矩阵；推导不扩大过程执行支持 |
| 批处理与准备缓存 | 部分实现 | `DbBatch` 顺序执行、Oracle array binding、opt-in 有限物理会话 idle LRU | rewrite、multi-send、bulk，以及真实缓存/资源/负载验收 |
| 诊断 | 部分实现 | `ILogger`、`ActivitySource`、`Meter`、脱敏日志、池生命周期与九阶段建连/探活指标及慢命令阈值报告 | 协议 debug、其他资源等待与负载验收 |
| 真实兼容性 | 部分验证 | 直连 4.4.2.2 的有限记录，包含 Oracle 失败 | OBProxy、TLS/plugin、版本、平台、网络故障和 XA 恢复矩阵 |

### 3.1 除连接池之外的待开发范围

跨命令 statement cache 已有 opt-in 本地实现和初步回归，真实 DDL/reprepare、资源/负载/代理验收仍缺。DataAdapter/CommandBuilder 已有本地填充、映射/事件、CRUD 和乐观并发实现；自动 UPDATE/DELETE 要求完整可见行键，复杂形状需显式命令，真实 CRUD/锁/目录及运行时验收仍缺。KeyInfo 已有单基表键/隐藏列与复杂查询 unknown 的本地契约，真实目录验收仍缺。自动参数推导已通过完整本地回归和独立走查，真实目录互操作继续开放。`DataTypes` 已完成本地实现及全量/五资产回归，真实服务器类型、DDL、编码和版本/代理范围仍须验收。协议侧缺 OB20 压缩/reroute，发现侧保留TCP整轮重试/固定退避及端点TCP健康缓存，仍缺内置其余本地策略覆盖及发现边界（全局黑名单/尝试上限见第50节，分组/权重见第49节，TNS/OCP适配见第47/48节）和真实健康验收；Oracle 侧还有限定游标方向/结果形状、复杂成员的缺口；可观察 DDL 自动失效已有本地实现，外部/不透明 DDL 仍须显式清理且真实 DDL 未验收。

凭据提供/轮换已有本地实现：每次 Open 的认证快照、版本退休、过期和取消规则已通过最终回归。真实短期凭据服务和认证插件轮换仍须验收。

已有实现但尚未闭合的是：会话隔离、事务/unknown outcome 的故障矩阵，SchemaOnly 的真实准备行为与元数据形状，扩展类型/字符集，批量性能与诊断，以及真实版本/代理/TLS/认证/平台验收。常用 Schema 目录、标准 MySQL 压缩、客户端证书池化、清池和池指标均已有本地实现，不应重新列为完全缺失。

完整分类见[详细基线第 4.1 节](obconnector-dotnet-completeness.md#41-除连接池外具体还缺什么)；凭据契约清单见第 5.5 节，DataTypes/参数推导/KeyInfo 见第 5.10.1 节。部分可选能力是否进入首个正式版本尚未确定，不能把未确认的范围决定写成已排除。

## 4. 缺口清单和交付优先级

### P0：不完成就不能称为完整驱动

| ID | 缺口 | 当前问题 | 完成条件 |
| --- | --- | --- | --- |
| P0-01 | 连接池生命周期 | 指标、显式清池及旧会话退休已接入，Closed回调重开归属及同满池清理链已有回归（第43节）；内部配置身份已统一，保留原始空 PAM 因子和旧池清理快照；多线程压力、完整 reset 和真实服务器行为仍未闭合 | 等效配置共享池、容量和清池，不合并凭据/策略/私有源身份；生命周期可观测，清池不破坏活动工作或容量上限，并通过压力及真实服务器验收 |
| P0-02 | 完整 session reset | Oracle fresh/reset 初始化及原失败实库回归已通过；旧 reader 延迟 Close 的逻辑租约隔离已修复并有本地/消费者/直连证据（第 36 节）；持久内联 LOB/REF CURSOR 同端点阶段及外置 LOB 完整读取/本地中断验收通过（第 38/39 节）；prepared statement 按物理会话 generation 失效；注释/多语句/不透明调用识别及目标发送销毁标记已有本地回归，完整状态/资源/并发和真实 reset 仍缺 | 先通过 Oracle fresh/reset 隐式提交可见性、初始化失败销毁及容量恢复；保留第 18 节识别与复用回归；事务、变量、临时对象、prepared statement、reader/cursor、LOB、XA 和 reset 失败丢弃均有真实 cross-borrower 测试 |
| P0-03 | Ambient transaction | PSPE 单资源/终态、初始化与晚回调身份及取消/失败清理已有本地交付 | 保留本地回归与原始 RED；补真实故障矩阵、业务结果核对和重建示例；分布式提升明确拒绝 |
| P0-04 | Unknown outcome | 发送后断开、取消、提交/回滚响应丢失时无法总是判定服务端结果；受控双模式直连及新租约/核对专项见第40节，完整故障矩阵仍缺 | 明确发送前/发送后/响应中/commit/rollback 状态；写操作不自动重放；异常给出恢复动作 |
| P0-05 | 活动连接恢复 | 连接建立后的故障恢复契约和真实矩阵不完整；无透明迁移/自动重连 | 第44节已交付连接级ERR状态与显式重建本地回归，补真实故障；定义只读、幂等写、非幂等写边界，活动事务不被静默迁移；不要求自动重放未知写 |
| P0-06 | 池路由身份隔离 | 此前普通池误共享不同 resolver 的会话，已复现并本地修复；每次 Open 路由快照会清理旧端点闲置会话 | 保留引用身份、快照/预热、取消/预算、并发清理回归；补真实拓扑、代理/节点故障和跨平台报告 |

### P1：常见生产和 ADO.NET 能力

| ID | 缺口 | 目标 |
| --- | --- | --- |
| P1-01 | 多语句 | 保留显式启用的无参数文本多结果；明确参数/Prepare/cursor/Oracle 输出组合的拒绝，并验证真实服务器错误边界 |
| P1-02 | 压缩/OB20 | 标准 MySQL 压缩已有本地实现；先确认客户端 OB20 压缩的链路/版本/协商契约，再补实现；reroute 及真实服务器/代理验收仍缺 |
| P1-03 | LOCAL INFILE | 只允许应用提供的流，禁止驱动读取任意本地路径 |
| P1-04 | TLS/mTLS | 自定义服务器验证回调、池化客户端证书隔离和 source 快照已有本地实现；真实企业 CA/轮换/撤销/跨平台和 mTLS 矩阵仍待完成 |
| P1-05 | ADO.NET 行为 | 保留 MySQL StoredProcedure 参数方向、输出和多结果实现及回归（第 24 节）；保留 KeyInfo 键/隐藏列与复杂形状 unknown 契约及回归，补真实目录和完整 schema/parameter metadata、Batch/游标行为表；保留 `SchemaOnly` Prepare-only及第55节Batch元数据/源条目创建回归；补真实准备行为验收，以及明确的 adapter 策略 |
| P1-06 | Oracle 输出/游标 | 完整参数方向、文本cursor列及二进制列真实验收、PrepareExecute 结果形状 |
| P1-07 | 复杂类型 | 保留已有嵌套 object/collection、显式/可观察 DDL 自动失效；保留第42节有界national实现；补LOB/cursor、其他national、复杂整列RETURNING身份和真实DDL验收 |
| P1-08 | 类型/字符集 | 保留已实现的 DecimalScale 及普通结果严格字符集/宽度；补其余编码、扩展类型码/metadata、客户端/national 及完整真实矩阵，见第 27 节 |
| P1-09 | HA/发现 | 保留TCP重试/退避及端点TCP健康缓存；其余本地策略覆盖及发现边界（全局黑名单/尝试上限见第50节，分组/权重见第49节）、OBProxy拓扑及真实健康/故障验收仍缺 |
| P1-10 | Schema | 常用目录/限制参数及 DataTypes 静态映射已有本地实现和最终回归；自动参数推导已有本地交付，真实目录/权限/版本/代理矩阵待验收，见 [Schema 契约](schema.md) |
| P1-11 | 凭据提供/轮换 | 每次 Open 快照、全口令替换、版本退休、过期/取消及脱敏已有本地实现和最终回归；真实插件/凭据服务/KDC 验收仍缺 |

### P2：生态、性能和运营增强

| ID | 缺口 | 目标 |
| --- | --- | --- |
| P2-01 | `DbDataAdapter`/`DbCommandBuilder` | 保留本地填充、映射/事件、原始值/NULL、行键/并发和事务回归；补双模式真实 CRUD/锁/目录与运行时报告；生成值回填及复杂形状限制须公开 |
| P2-02 | statement cache | 本地 idle LRU、容量/淘汰/关闭、epoch 和 reset generation 已实现；补真实 DDL/reprepare、资源/负载/代理验收 |
| P2-03 | 批处理优化 | rewrite、multi-send、bulk 与 partial failure 结果模型 |
| P2-04 | 诊断 | 保留池/建连和慢命令阈值报告；补协议 debug、其他资源等待及负载验收 |
| P2-05 | TFM/包生态 | 每个目标TFM的consumer、运行时和发布矩阵；定位报告53/完整性15.58的默认.NET10.0.12合并消费者CLR崩溃，条件通过不能代替默认验收 |
| P2-06 | 真实兼容矩阵 | 扩展范围可分期；承诺支持的版本、直连/OBProxy、模式、TLS、认证、平台和故障报告始终是发布门槛 |

## 5. 连接池专项结论

### 5.1 当前实现路径

- `OceanBaseConnection.Open`/`OpenAsync` 从匹配配置的 `PhysicalSessionPool` 租用物理会话。
- 新建会话完成 TCP、TLS、认证和 OceanBase 身份确认；池内复用会话先执行 `COM_PING`。
- 逻辑连接关闭时，健康且非 XA 的物理会话执行 reset；MySQL 模式随后恢复初始 database。
- 达到最大容量时，后续 Open 等待归还；等待支持超时和取消。
- 空闲 lifetime、总 lifetime 和 `Min Pool Size` 的保留规则已存在；首次或后续租借前会补足 `Min Pool Size`，预热失败仅回滚本次新建且仍闲置的会话，不能销毁已被其他 Open 借出的会话。
- reader 未完成、LOB/cursor 活跃、XA 专用、reset 失败或生命周期过期的会话必须销毁，不得回池。
- 未释放的 Oracle REF CURSOR 会设置归还销毁标记；最后一个 cursor 释放后才清除该标记。
- 未释放的 LOB 或 temporary LOB 句柄会设置归还销毁标记；句柄释放后才允许池化 reset。
- 可识别的用户 `SET`、`ALTER SESSION` 和临时对象语句会设置永久归还销毁标记；当前不依赖未验证的服务器 reset 语义。
- reset 成功后必须递增物理会话 generation；旧 prepared statement 只能被本地丢弃，不能再次发送关闭或执行请求。

### 5.2 与成熟 Java 池的差距

Connector/J 的池实现包含池注册、最小/最大容量、生命周期调度、连接/语句事件和运营基础设施。当前 .NET 已有容量、等待、预热、探活、惰性回收和指标；以下内容仍需补齐或明确取舍：

1. 连接字符串归一化和进程退出行为；
2. session variable、临时表/对象、statement、cursor、LOB、XA 的完整 reset；
3. 保留建连/探活阶段指标，保留慢命令阈值报告，补协议 debug、其他资源等待及负载验收；
4. 在已有 Closed 回调重开回归之外，补多线程 Close/Open、池等待取消、借出连接与 data source disposal 的资源归属压力验证；
5. 真实 OceanBase 与 OBProxy 上的 reset、预热、超时和并发验证。
6. 保留普通全局池已有的 resolver 身份隔离与路由快照失效规则，补真实拓扑和代理故障验收（P0-06）；不重复开发已交付的本地隔离。

普通池已按区分大小写的原始连接字符串、resolver 和凭据提供者的引用身份、TLS 验证回调引用身份，以及客户端证书引用和 SHA-256 DER 指纹共同索引；resolver、回调和证书的引用身份不会因对象自定义值相等而合并。等价但顺序、别名不同的字符串仍可能产生多个池，是后续容量/复用效率问题。数据源保留私有池及 Build 时捕获的证书副本。每次逻辑 Open 只解析一次地址快照，移除不再匹配的闲置端点后再预热/借用；发现失败不回退，已借出连接不迁移。并发发现使用各自快照，不承诺全局拓扑版本排序；真实拓扑仍须验收。

显式清池关闭闲置会话，使借出会话和清池前的在途会话归还后销毁；借出会话继续计入容量，活动命令不会被中断。`ClearAllPools()` 只清理普通 registry 池的快照，私有源使用 `source.Clear()` 或针对源连接的 `ClearPool`，清理后仍允许重新 Open。P1-11 把凭据提供者引用加入普通池身份和清池匹配，返回的秘密及版本不进入可观测池标识。

`ClearPool` 按原配置及对象引用匹配，不读取证书内容，可清理同一证书对象的旧指纹池。清池不是强制终止所有活动连接的进程 shutdown；具体契约见[架构设计](obconnector-dotnet-architecture-design.md#explicit-pool-clearing)。

本轮池走查对上述生命周期风险做了代码复核，当前状态如下：

| 检查项 | 当前状态 | 仍需补的证据 |
| --- | --- | --- |
| idle lifetime 与 total lifetime 混用 | 已修正；归还路径只检查 total lifetime，候选闲置会话才检查 idle lifetime，并保留 `Min Pool Size` | 长时间借用、并发借还和时钟边界压力回归 |
| 旧 `Open` 完成覆盖新一轮打开 | 已有 per-open cancellation 和 generation；过期完成会被拒绝，清理只处理同一 generation；Open 失败和 Close 归还使用原池/key 快照，Closed 回调更换配置重开不再混用池 | 多线程 Close/Open 压力回归和活动事务交错 |
| Close 不能打断池等待 | 已有 `OpenCancellation`，同步/异步池等待使用该 token | 等待、Close、重新 Open 的并发回归 |
| 静态 registry 无清理 | 空池通过 `ReleaseIfEmpty` 移除并 Dispose；普通池已有显式 `ClearPool`/`ClearAllPools`，私有源有 `Clear`/Dispose；Dispose 后借出会话关闭时直接销毁，不发送 reset。没有统一强制关闭活动会话及禁止后续 Open 的 shutdown API | 非空池和长时间运行进程的生命周期报告；是否需要统一 shutdown 须单独确定契约 |

因此这些项目不再作为“已确认的当前 bug”报告；它们是已有保护但验证尚未闭合的池生命周期验收项。

### 5.3 LOCAL INFILE 的当前实现和边界

- `Allow Local Infile` 默认是 `false`；关闭时不发送 `CLIENT_LOCAL_FILES`，收到服务端 `0xfb` 会拒绝并按 unknown outcome 销毁本次物理会话。
- 开启后，应用必须在 `Open` 前设置 `OceanBaseConnection.LocalInfileStreamFactory`。工厂收到服务端文件名这一不透明值，返回的可读流由驱动释放；驱动永远不按该名称访问本地路径。
- 上传使用同步和异步两条路径，按 64 KiB 分块，发送空包结束并读取最终 OK。工厂缺失、空返回、不可读、读取异常、取消、超过默认 64 MiB 上限或最终响应异常都会使物理会话进入 unknown outcome 并销毁。
- `LocalInfileTests` 已覆盖默认拒绝、capability、同步/异步上传、文件名传递、工厂错误、读失败、取消、空终止包和最终 OK；这些是 loopback 协议证据，不是真实数据库兼容性证明。

仍需真实 OceanBase/OBProxy 验收 `LOAD DATA LOCAL INFILE`、代理安全策略、服务器错误、长文件、超限和中断恢复。LOCAL INFILE 也不等于普通参数流上传，两者的 API 和风险边界不能合并。

### 5.4 连接池验收矩阵

每一项都必须有“借用者 A 污染状态 → 归还 → 借用者 B 观察基线”的测试：

| 状态 | B 应观察到的结果 |
| --- | --- |
| 本地事务/自动提交 | 无活动事务，自动提交回到基线 |
| session variables | provider 修改过的变量恢复 |
| database/schema | 回到连接字符串初始值 |
| prepared statement | 无旧命令、旧 schema 或旧 statement 引用 |
| reader/cursor | 未读协议流不会进入 B；无法清理就销毁会话 |
| LOB/临时 LOB | stream、locator、临时对象按契约关闭/释放 |
| XA | branch 未完成时不得进入普通池 |
| reset 失败 | 物理会话从池移除，不可再次借用 |

## 6. Java 对照结论

Connector/J 的静态模块对照显示，Java 已把以下职责分开：连接入口、地址/发现、TLS/认证、会话/事务、查询/游标、类型/字符集、LOB/复杂类型、目录、HA、池、XA、诊断和发布测试。当前 .NET 对应模块大多已有骨架，但差距集中在契约闭合和真实验收，而不是缺少一个单独的 `Pool` 类。

Java 池证据位于参考仓库的 `src/main/java/com/oceanbase/jdbc/OceanBasePoolDataSource.java`、`OceanBasePooledConnection.java` 及 `internal/util/pool/Pool.java`、`Pools.java`。`Pool` 读取最小/最大容量并使用调度器维护，`Pools` 管理池注册和 executor。该结论已在本轮核对源码与 commit；未运行 Java 池负载测试，也不推定其所有行为都应原样移植到 ADO.NET。

对 .NET 的正确目标是复用 ADO.NET 语义，不复制 JDBC 类名：

- 用 `DbConnection`/`DbDataSource` 表达逻辑连接和数据源；
- 用 `PhysicalSession` 表达可复用物理会话；
- 用明确的 generation、租约和 reset 状态隔离 session、transaction、reader、LOB 和 XA；
- 用 OceanBase 模式/能力矩阵表达 MySQL/Oracle 差异；
- 用异常的 confirmed failure、unknown outcome 和 confirmed success 字段表达恢复边界。

## 7. 推荐实施顺序

| 阶段 | 内容 | 退出标准 |
| --- | --- | --- |
| G0 | 冻结公共 API、兼容矩阵、状态机和错误分类 | 每个缺口都有状态、测试入口和明确“不支持/未验证”边界 |
| G1 | 完整 session reset 和 XA 隔离 | cross-borrower、并发、取消、失败丢弃和 XA 隔离通过 |
| G2 | ambient transaction、取消和 unknown outcome | 保留 PSPE 及初始化取消的本地交付；真实响应丢失/故障矩阵及应用恢复指引仍待完成 |
| G3 | 多语句、压缩、LOCAL INFILE、OB20 reroute/压缩 | capability 协商、旧服务器拒绝和真实服务器通过 |
| G4 | mTLS、信任/撤销、认证插件、HA/发现 | TLS/KDC/OBProxy/故障注入矩阵通过 |
| G5 | ADO.NET 行为、Oracle 输出、复杂类型、字符集和 Schema | 公共 API、模式差异和结果形状矩阵通过 |
| G6 | statement cache、batch 优化和诊断 | 资源上限、淘汰、性能和可观测性基线通过 |
| G7 | TFM、跨平台、包和发布验收 | 所有对外声明都有可复现报告 |

## 8. 发布门槛

在以下条件全部满足前，版本只能作为实验性驱动发布：

1. 池 reset 和 cross-borrower isolation 通过；
2. Oracle 真实验收中的失败已分类并处理，不能以 skip 掩盖；
3. 至少一个直连和一个 OBProxy 真实 profile 通过；
4. 至少一个真实 `VerifyFull` TLS profile 通过，mTLS/KDC 是否支持必须单独声明；
5. unknown outcome、XA crash recovery 和活动连接故障边界有报告；
6. 声明支持的每个 TFM 有 package consumer 和运行时证据；
7. README、连接字符串、公共 API、异常、测试和兼容性文档对同一能力给出一致结论。

## 9. 走查入口和参考文件

| 用途 | 文件 |
| --- | --- |
| 本报告 | [obconnector-dotnet-driver-audit.md](obconnector-dotnet-driver-audit.md) |
| 详细能力基线与验收要求 | [obconnector-dotnet-completeness.md](obconnector-dotnet-completeness.md) |
| 公共 API 和配置契约 | [obconnector-dotnet-architecture-design.md](obconnector-dotnet-architecture-design.md) |
| 历史验证、失败和剩余工作 | [obconnector-dotnet-progress.md](obconnector-dotnet-progress.md) |
| 真实兼容性和已知限制 | [compatibility.md](compatibility.md) |
| Java Connector/J 模块对照 | [obconnector-j-architecture-outline.md](obconnector-j-architecture-outline.md) |
| 每个缺口的场景、源码、测试入口与验收要求 | `docs/obconnector-dotnet-completeness.md` 第 14 节，共 23 个 ID |
| TLS、客户端证书和撤销检查 | `src/OceanBaseConnector/Internal/Transport/TlsHandshakeFlow.cs`、`TlsUpgrade.cs`、`src/OceanBaseConnector/OceanBaseConnection.cs`、`tests/OceanBaseConnector.UnitTests/Transport/TlsComponentsTests.cs`、`ProviderConnectionTests.cs` |
| 物理会话池 | `src/OceanBaseConnector/Internal/Session/PhysicalSessionPool.cs` |
| 物理会话 reset/状态 | `src/OceanBaseConnector/Internal/Session/PhysicalSession.cs` |
| 连接池回归 | `tests/OceanBaseConnector.UnitTests/PoolingTests.cs`、`tests/OceanBaseConnector.UnitTests/Session/PhysicalSessionPoolTests.cs` |
| Close/Open 归属修复回归 | `tests/OceanBaseConnector.UnitTests/PoolOwnershipTests.cs` |
| 真实数据库入口 | `tests/OceanBaseConnector.IntegrationTests/`、`tests/OceanBaseConnector.Acceptance/` |

## 10. 明确不应偷换的结论

- Java 有连接池，不等于 Java 的每个 JDBC API 在本项目都已经对齐。
- .NET 有基础池化，不等于已经完成成熟连接池的状态隔离和运营能力。
- 本地 TLS、loopback 和 mock 不等于真实 OceanBase/OBProxy/TLS/KDC 兼容。
- OceanBase MySQL/Oracle 是兼容模式，不应写成原生 MySQL/Oracle 服务器支持。
- 服务端不支持 nested-table column，不能被记录成驱动已经支持该形状；procedure collection 参数是另一个能力。
- 未完成的功能必须明确拒绝或标记未验证，不能让深层协议错误替代公共契约。
- `SchemaOnly` 当前只 Prepare 目标，禁止目标执行及 fallback；这不代表准备阶段没有解析、权限检查、元数据锁或任何服务器行为，真实矩阵仍需验收。
- DataAdapter 的形状限制、EF Core provider 尚缺、没有透明 replay 与核心连接正确性是不同层级；明确的版本支持范围是验收依据。

## 11. 本次文档交付和后续执行

初次文档交付整理了能力基线、23 项可追踪缺口、Java 对照、源码/测试入口、实施依赖、实库矩阵和发布门槛。后续开发已补充 P0-06 的池路由隔离与快照生命周期，保留复现、修复、独立走查和验证记录；尚未完成的真实矩阵继续保留为缺口。

完整清单见[能力与缺口基线](obconnector-dotnet-completeness.md)第 4、5、14、15 节。后续每个 ID 关闭时须记录提交、回归、命令/退出码、运行时/服务端 profile、失败/跳过和报告位置；当前未分配负责人/日期。已有开发进度台账及历史任务编号保持原样。

2026-10-25 DataTypes 交付时基线更新至 `5146b62`，包含 DataTypes、客户端证书池化隔离、source 证书快照、显式清池/退休、标准 MySQL 压缩、Schema 常用目录和凭据提供/轮换。DataTypes 的本地交付和新增集合数量见第 15.14 节。连接池的容量、预热、清理和指标已具备；完整隔离、并发/长期运行及真实兼容性仍开放。23 个缺口继续逐项验收，局部模块交付不等于完整驱动验收。

后续执行按[剩余交付工作包](obconnector-dotnet-completeness.md#16-剩余交付工作包)分为八组，覆盖全部 23 项：每组列出尚需实现的内容、必须保留的行为及验收产物。负责人、日期和承诺支持范围仍待项目分配；本报告不把建议阶段写成已确认排期。上述文档整理只检查链接、编号和 diff；随后的标准 MySQL 压缩开发、测试和独立走查单列于[模块记录](obconnector-dotnet-completeness.md#157-2026-10-25-标准-mysql-压缩)。

此前另补充[OB20 压缩协议前提](obconnector-dotnet-completeness.md#163-ob20-压缩的客户端协议前提)：Java 2.4.14 同样拒绝压缩 OB20，固定版本的 OBProxy 客户端响应代码注明该链路不支持；服务端有代理能力位控制的压缩路径，不能据此直接推定通用客户端支持。该项继续开放，先确认客户端链路、版本及协商契约，再实施与验收。


2026-10-25 后续已扩展 Schema 常用目录，具体契约见 [schema.md](schema.md)，开发、测试与独立走查见[模块台账](obconnector-dotnet-progress.md#2026-10-25-p1-10-schema-catalog)。这不关闭真实字典兼容矩阵，也不将此前真实六集合结果扩展解释为新目录已验收。

本次文档交付补全明确缺失/部分实现/欠验收的分类、凭据获取/过期/并发刷新/池退休/取消和脱敏清单，以及 DataTypes/参数自动推导/KeyInfo 的分别验收要求。已复核现存 Schema TRX，保留所有历史失败、跳过、缺失报告及 23 个 ID；未修改产品代码或关闭任何工作包。

DataTypes 修复前的 3791 通过/1 失败及消费者筛选遗漏保留于[历史检查点](obconnector-dotnet-completeness.md#1513-2026-10-25-datatypes-未提交开发与文档交付检查点)，已由[第 15.14 节](obconnector-dotnet-completeness.md#1514-2026-10-25-datatypes-本地交付)的完整单测 3792、消费者 66（含实际 16 项）通过结果取代。23 个 ID、8 个工作包及历史实库失败/跳过均保留；自动参数推导和真实验收继续开放。

此前完整文档复核另明确两项容易混淆的边界：Oracle NUMBER 结果 metadata 可表示负 scale，开发基线 `5146b62` 的 byte Scale 参数 API 却不能显式声明负 scale；新增 DecimalScale 已完成本地回归和独立走查，原消费者失败及筛选遗漏保留在历史记录中；真实声明矩阵仍待验收。过程目录与过程执行也不同：当前 CommandType.StoredProcedure 只走 Oracle PrepareExecute，MySQL 不具备该执行入口。自动推导在已提交基线尚缺，工作区进展以以下检查点为准。均沿用现有 ID 追踪，不改变 23 项缺口和八个工作包。

有符号 DecimalScale 模块交付、消费者 fixture 根因、最终146项实际覆盖和历史报告见[第15.16节](obconnector-dotnet-completeness.md#1516-2026-10-25-有符号-decimal-scale-本地交付)。

此前文档检查点（历史快照）以 `2bbecac` 及其未提交工作区为准：自动参数推导已有三种同步/异步签名、唯一目录签名定位、类型属性映射、同会话/事务预留、取消及集合成员原子发布；相关子集 132 项通过，新增五资产消费者仅编译，Release 构建仍有 1 个 xUnit2013 警告。报告逐项保留此前 RED/失败，不把上一模块全量结果沿用为本模块结果。最终 package/overload、事务/权限、并发和不完整类型身份等边界，以及完整回归、消费者和最终独立走查仍待完成，详见[第15.17节](obconnector-dotnet-completeness.md#1517-2026-10-25-参数推导开发与完整文档检查点)。该次只修改文档；真实矩阵及全部 23 项/八个工作包继续开放。

后续自动参数推导已完成本地交付：新增 81 项单测和 20 项五资产消费者；完整单测 3900、完整消费者 2328、DI 14 通过，Integration 9 通过/72 跳过。修复类型身份配对、浮点/YEAR 声明丢失及 fixture 清理问题，完整回归、独立源码/契约走查和格式检查通过。详情及全部历史报告见[第15.18节](obconnector-dotnet-completeness.md#1518-2026-10-25-自动参数推导修复与验证)。真实目录/权限/声明/版本/OBProxy 验收仍缺，不关闭整个 P1-10、任何工作包或完整驱动目标。




## 12. 当前交付快照（2026-10-25）

本节为 SchemaOnly 开发前的历史快照；其中执行语义已由后续模块修复，当前契约见第 3、4 节及 API 文档。

本次只整理走查文档，源码基线为 `557a05a`，未继续实现 SchemaOnly 或其他模块。以下是对当前源码及已有报告的复核，不是新一轮测试执行，也不替代第 4 节全部 23 项缺口和详细基线第 14 节的逐项验收。

### 12.1 关键结论与当前证据

| 结论 | 本次复核入口 | 当前处置与验收要求 |
| --- | --- | --- |
| Java 有连接池 | 参考仓库 `OceanBasePoolDataSource`、`OceanBasePooledConnection`、`internal/util/pool/Pool` 和 `Pools`；HEAD 与参考版本一致 | Java 源码事实已确认；未运行 Java 池或将 JDBC API 直接视为 ADO.NET 要求 |
| .NET 有基础池化，默认关闭 | [配置默认值](../src/OceanBaseConnector/OceanBaseConnectionStringBuilder.cs)、[租用/归还与容量](../src/OceanBaseConnector/Internal/Session/PhysicalSessionPool.cs)、[reset/销毁状态](../src/OceanBaseConnector/Internal/Session/PhysicalSession.cs) | P0-01/02/06 继续追踪完整隔离、并发/长期运行及真实双模式/代理验收；不能因已有 Pool 类关闭这些缺口 |
| SchemaOnly 仍发送并执行目标 SQL | [命令同步/异步执行](../src/OceanBaseConnector/OceanBaseCommand.cs)、[reader 隐藏行](../src/OceanBaseConnector/OceanBaseDataReader.cs)、[现有测试](../tests/OceanBaseConnector.UnitTests/TextCommandTests.cs)中的 `SchemaOnlyReturnsMetadataWithoutRowsAndKeepsConnectionUsable` | 现有测试服务端收到查询并返回数据行，只证明调用者看不到行及连接后续可用。P1-05 还需证明目标不执行，或明确公开执行限制；副作用函数、锁和大结果成本尚无专门实库证据 |
| KeyInfo、DataAdapter/CommandBuilder 尚缺 | [命令行为允许集合](../src/OceanBaseConnector/OceanBaseCommand.cs)、[factory 能力与 null 返回](../src/OceanBaseConnector/OceanBaseProviderFactory.cs) | P1-05/P2-01；补实现或作明确版本范围决定，不能由已有 reader schema 推定完整支持 |
| 自动参数推导已有本地交付 | [公开入口](../src/OceanBaseConnector/OceanBaseCommand.Parameters.cs)、[目录加载](../src/OceanBaseConnector/OceanBaseConnection.Parameters.cs)、[签名映射](../src/OceanBaseConnector/Internal/Sql/RoutineParameterDerivation.cs) | P1-10；保留成功替换/失败保留的契约，补真实目录/权限/重载/版本/代理验收；MySQL StoredProcedure 执行仍未因此增加 |
| 标准 MySQL 压缩已有，OB20 压缩/reroute 仍缺 | [标准压缩流](../src/OceanBaseConnector/Internal/Protocol/MySqlCompressionStream.cs)、[OB20 压缩拒绝](../src/OceanBaseConnector/Internal/Protocol/Ob20Packet.cs)、[reroute 标志拒绝](../src/OceanBaseConnector/Internal/Protocol/Ob20Stream.cs) | P1-02 三项分别追踪；先确认客户端链路及协商契约，见[协议前提](obconnector-dotnet-completeness.md#163-ob20-压缩的客户端协议前提) |
| Oracle 游标方向和复杂成员有限制 | [Oracle 输出计划](../src/OceanBaseConnector/Internal/Sql/OracleCommandPlan.cs)、[复杂成员类型](../src/OceanBaseConnector/Internal/Values/ComplexType.cs) | P1-06/07；Input/InputOutput REF CURSOR 等组合明确拒绝，扩展时须同步所有权、NULL、取消和释放矩阵 |
| Batch 按命令顺序执行 | [Batch](../src/OceanBaseConnector/OceanBaseBatch.cs)、[逐个启动命令的 reader](../src/OceanBaseConnector/OceanBaseBatchReader.cs) | P2-02/03；顺序执行和 Oracle array binding 已有，跨命令缓存、rewrite/multi-send/bulk 不应被写为已实现 |
| 五个目标资产不等于五种独立运行时通过 | [项目目标](../src/OceanBaseConnector/OceanBaseConnector.csproj)、[CI](../.github/workflows/ci.yml)、已有消费者报告 | P2-05/06；本次重新读取报告，未执行跨运行时、跨平台或真实 OceanBase/OBProxy 测试 |

### 12.2 已有报告复核与发布边界

重新读取 `TestResults/derive-parameters/` 的四份 `*-verified.trx`，按每个 `UnitTestResult` 的 outcome 统计：单测 3900 通过，消费者 2328 通过，DI 14 通过，Integration 9 通过/72 NotExecuted。消费者中的 `EveryAssetDerivesRoutineParametersWithSignedScaleAndAtomicFailure` 实际 20 项，覆盖五资产、同步/异步及双模式 fixture。报告和日志位于忽略目录，未纳入文档交付；原执行命令、失败与格式警告保留于[第 15.18 节](obconnector-dotnet-completeness.md#1518-2026-10-25-自动参数推导修复与验证)。

当前仍保留真实验收的 MySQL 60 通过、Oracle 79 通过/1 失败及其环境限制；本次没有重跑或扩展这些结论。Integration 跳过不能视为真实兼容通过。源码复核也没有证明所有编解码分支或未测并发组合正确。

交付内容包括：能力矩阵、23 项有稳定 ID 的缺口、触发场景/源码/测试/关闭条件、Java 模块对照、八个工作包及其依赖、测试矩阵、发布门槛和历史证据。下一步按[工作包](obconnector-dotnet-completeness.md#16-剩余交付工作包)逐项实施及验收；W1/W2 的隔离和失败契约优先，未达到发布门槛前维持 experimental。负责人、日期和首个正式版本的可选能力范围仍待项目确定，本文不代替这些范围决定。

## 13. SchemaOnly 模块（2026-10-25）

开发起点 `557a05a`；同步/异步 SchemaOnly 仅 Prepare 单条目标查询，读取准备时列元数据，禁止目标执行、fetch、参数上传及客户端 fallback。参数名称/顺序/数量仍需匹配，值不参与绑定验证。FetchSize/offset hint 不启用游标；prepared 元数据可复用，空 reader 立即完成 exchange，CloseConnection 保留原会话关闭契约。命名复杂列可能查询目录，准备阶段仍可能解析、检查权限和取得元数据锁。

独立走查发现的已释放 LOB 值仍被校验问题已用 RED 复现并修复；相关单测 30、完整单测 3928、完整消费者 2348（含新增 20 项）、DI 14 通过，Integration 9 通过/72 跳过；构建 0 警告/0 错误，格式及本地 pack 通过。原并行运行保留五项网络/时序失败，其 22 项子集和完整重跑均通过，未改产品/断言，不据此宣称长期负载时序已验收。详细契约、历史失败和最终结果见[第 15.20 节](obconnector-dotnet-completeness.md#1520-2026-10-25-schemaonly-元数据专用路径)。P1-05 的 KeyInfo、其他行为和真实准备/结果形状/代理矩阵继续开放。

## 14. 当前完整文档交付（2026-10-25）

本节保留 KeyInfo 开发前的文档交付快照；后续本地实现与验收边界见第 15 节，不以本节的“仍缺”覆盖新实现。

本次以已提交的 `de15ec2` 为源码基线，整理完整驱动所缺的开发、验证和范围决定。面向长期运行、并发借用连接的服务；未约定吞吐、延迟和并发 SLA。本文没有把所有未验收项都认定为已复现缺陷，也没有把 Java 的可选功能直接变成 .NET 的发布承诺。

### 14.1 文档组成与使用顺序

| 文档 | 解决的问题 |
| --- | --- |
| 本报告第 3、4 节 | 当前有什么、缺什么、先做什么；保留 P0 六项、P1 十一项、P2 六项 |
| [完整性基线第 5 节](obconnector-dotnet-completeness.md#5-分模块要求和验收标准) | 每个模块的功能契约、模式差异及验收要求 |
| [逐项追踪表](obconnector-dotnet-completeness.md#14-缺口到场景源码和验收的追踪表) | 每个 ID 的触发场景、影响、源码/测试入口及关闭标准 |
| [八个工作包](obconnector-dotnet-completeness.md#16-剩余交付工作包) | 剩余开发、依赖和必须交付的验证产物；每个 ID 有且只有一个主工作包 |
| [API 设计](obconnector-dotnet-architecture-design.md)、[Schema 契约](schema.md) | 公开 API、配置、参数及模式/TFM 限制 |
| [进度台账](obconnector-dotnet-progress.md)、[兼容性记录](compatibility.md) | 已提交实现、历史执行命令、失败/跳过和真实部署证据 |
| [Java 结构对照](obconnector-j-architecture-outline.md) | 参考实现模块来源；不作为 Java 运行验收或 .NET 兼容性证明 |

### 14.2 当前仍需补齐的能力

下表是评审索引，完整的 23 项要求仍以第 4 节和逐项追踪表为准；一个类别可以横跨多个 ID。

| 类别 / ID | 源码事实与实际影响 | 剩余交付及最低验收 |
| --- | --- | --- |
| 连接池与隔离：P0-01/02/06 | Java 有内置池；.NET 默认关闭池化，已有容量、等待/取消、预热、清池/退休、路由身份隔离及指标。完整状态 reset、并发和长期运行证据仍不足，不能宣称成熟池化 | 双借用者验证事务/变量/临时对象/reader/statement/LOB/cursor/XA；无法恢复则销毁；补满池、Dispose/reset/Close/Open 交错、资源上限及双模式/代理实库报告 |
| 事务与故障恢复：P0-03/04/05 | 本地 ambient 和 unknown-outcome 分类已有；分布式提升被拒绝，活动连接无透明迁移。响应丢失后盲目重试可能重复写入 | 发送前/后、响应中、commit/rollback 丢包、超时/in-doubt/提前关闭故障矩阵；异常恢复指引和应用重建示例；未知写禁止自动 replay |
| 协议选项：P1-01/02/03 | 文本多语句、标准 MySQL 压缩和应用流 LOCAL INFILE 已有；组合受限，OB20 压缩/reroute 被拒绝 | 保留显式授权及发送前拒绝；补中途失败/取消、包与解压上限、上传及代理矩阵；OB20 压缩先取得[客户端协议依据](obconnector-dotnet-completeness.md#163-ob20-压缩的客户端协议前提) |
| 信任、认证和发现：P1-04/09/11 | TLS 回调、客户端证书隔离、凭据提供/轮换已有本地实现；内置 OCP/TNS、健康缓存/退避及真实企业身份矩阵不足 | 企业 CA/mTLS、撤销/轮换、plugin/KDC、短期凭据过期/刷新失败；发现更新、错误身份/节点/代理故障、总预算和跨平台报告 |
| ADO.NET 行为：P1-05 | Command 拒绝 KeyInfo；Batch 仅接受 CloseConnection/SequentialAccess。SchemaOnly 已改为目标 Prepare-only，不能再列为“仍执行目标” | 补主键/唯一键、组合键、来源/别名/连接/表达式及隐藏键列契约；不从单列 flags 臆造唯一性或可更新性；行为组合和真实准备/锁/结果形状验收 |
| Oracle 与类型：P1-06/07/08 | 部分 REF CURSOR 方向、cursor-valued column、PrepareExecute 组合、复杂 LOB/cursor/national 成员、外部 DDL 缓存失效和扩展字符集/类型仍不足 | 方向/NULL/形状/精度/溢出/编码及资源所有权矩阵；保留已完成的嵌套类型和有符号 DecimalScale 回归；服务端不支持的形状单独记录 |
| 目录与生态：P1-10、P2-01 | DataTypes、常用目录和参数推导已有；Factory 的 DataAdapter/CommandBuilder 返回 null，DataSet 更新和命令生成工具无法使用。过程目录不等于 MySQL StoredProcedure 执行支持 | 补目录/权限/重载/大小写/空集合/版本/代理验收；adapter 若纳入版本则实现 CRUD/并发冲突，若排除则正式公开范围决定；EF Core provider 另行验收 |
| 性能与运维：P2-02/03/04 | 无跨命令 statement cache；Batch 顺序执行，已有 Oracle array binding；建连/探活分段和慢查询/协议诊断不足 | 有限缓存的失效/淘汰/statement close；承诺批量路径的部分失败/取消/事务/包上限及性能对照；诊断脱敏、单位和开销 |
| 运行时与发布：P2-05/06 | 五个 TFM 资产不等于五种独立运行时；六个 matrix.* 仍为 DeferredReason 占位。历史 Oracle 实库失败和 cleanup failure 保留 | 确定支持 profile；实际 nupkg consumer、版本/模式/直连与代理/TLS/认证/平台/故障/XA 恢复报告；承诺范围缺证据仍阻塞发布 |

本次重读了 Command/Batch 行为白名单、ResultDbColumn、provider factory、池配置/reset、OB20 拒绝路径、项目/CI 和 Acceptance 的六个占位项；Java 本地 HEAD 仍为上述参考提交，PoolDataSource 调用池实现。其余模块沿用已有源码走查及记录，本次没有重新逐行审计所有 codec、SQL 或并发路径。

### 14.3 已有报告复核与交付边界

以下数字来自本次重新解析 `TestResults/schema-only/` 下已有 TRX 的每个 UnitTestResult，属于历史执行结果复核，没有在本次运行测试：

| 报告 | 实际记录 |
| --- | --- |
| unit-recheck.trx | 3928 通过，0 失败/跳过 |
| consumer-verified.trx | 2348 通过，0 失败/跳过；其中 SchemaOnly 新增消费者方法实际 20 项 |
| di.trx | 14 通过 |
| integration.trx | 9 通过、72 NotExecuted；不计为新实库互操作证据 |
| unit-verified.trx / timing-recheck.trx | 原并行运行 3923 通过/5 失败；五个方法的 22 项独立子集通过，原失败保留 |

同时重读已有 Release 构建日志的 0 警告/0 错误及格式日志的 workspace-load warning；未将日志重读表述为本次构建或格式检查通过。源码、链接、编号及文档结构检查另记在[本次文档复核](obconnector-dotnet-completeness.md#1521-2026-10-25-schemaonly-交付后完整文档复核)。

后续先完成 W1/W2 的隔离与失败契约，再按 W3-W7 补功能及矩阵，W8 持续积累发布证据。每个 ID 都需留下实现提交、回归、命令/退出码、运行环境、失败/跳过及报告位置；负责人、日期和首个正式版本可选范围仍未分配。本次文档不关闭任何 ID、工作包或完整驱动目标。

## 15. KeyInfo 模块（2026-10-25）

从 `de15ec2` 开发：在同一会话/事务中 Prepare 原目标、读取有界参数化键目录，再执行目标或返回 SchemaOnly 元数据。单基表且能证明保留行语义的 SELECT 优先完整主键、否则非空唯一键，缺失成员追加为隐藏尾列；原 CommandText/Prepare 缓存不变。FieldCount 包含隐藏列，VisibleFieldCount 保留原投影数量。可空单列 UNIQUE 与行键分别标记，组合成员不误标单列唯一。复杂或不确定形状保持原 SQL/unknown，不虚构来源、表达式或更新性；详细边界见 [API 契约](obconnector-dotnet-architecture-design.md#key-information-and-hidden-columns)。

独立 review_keyinfo_design 发现的 Oracle 非唯一支撑索引/约束成员与 owner 身份、NQ 引号、参数表达式动态元数据三项 Important 已用 RED 复现并修复；最终产品/测试走查无剩余 Critical/Important，审阅者未代替主任务运行完整套件。最终完整单测 4013、专项 85、完整消费者 2368（含新增五资产实际 20 项）及 DI 14 通过；Integration 9 通过/72 NotExecuted。构建 0 警告/0 错误；完整证据见[第 15.22 节](obconnector-dotnet-completeness.md#1522-2026-10-25-keyinfo-键元数据与隐藏列)。原沙箱 TLS/PFX 117 项失败、重叠构建警告、筛选零项及所有 RED/修复记录保留。

真实字典 SQL/权限/DDL/锁、版本/OBProxy、跨平台及独立运行时矩阵仍开放；整个 P1-05、23 个缺口和八个工作包不因本子模块关闭。

## 16. DataAdapter / CommandBuilder 模块（2026-10-25）

从 `d7e8b5a` 开发 P2-01：五资产新增 typed adapter/builder 和 factory 入口，基于 System.Data.Common 完成同步填充、DataSet 映射、AddWithKey、原始值/NULL、行事件、顺序 CRUD、ContinueUpdateOnError 和 DBConcurrencyException；生成命令继承源事务/超时，不自动跨行提交或重试。自动 UPDATE/DELETE 需要完整可见行键，未知来源、隐藏键和上下文相关复杂绑定需显式命令；自动命令不回填 identity/default。MySQL 限定库名不被基类丢弃，可空 UNIQUE 不会被误作行键。公开 API 和缓存/引号边界见 [adapter 契约](obconnector-dotnet-architecture-design.md#data-adapters-and-command-generation)。

独立 review_data_adapter 找到 Scale=0 错误声明小数秒精度的问题，两个实际 RED 复现并通过清除声明修复；最后产品/测试复核无剩余 Critical/Important。最终完整单测 4047（新增实际 34）、完整消费者 2388（新增实际 20）、DI 14 通过，Integration 9 通过/72 NotExecuted，均退出 0；边界子集 94 和消费者子集 21 通过。最终构建 0 警告/0 错误、十文件格式退出 0（保留 workspace-load warning）；本地双包/五资产哈希及嵌入 README 已核对，不构成独立运行时验收。结果和所有原失败见[详细记录第 15.23 节](obconnector-dotnet-completeness.md#1523-2026-10-25-dataadapter--commandbuilder)。较早章节的尚缺只描述当时快照。

未新增真实 OceanBase/OBProxy、权限/DDL/锁、跨平台或独立运行时证据，23 个缺口和八工作包均不因本地子模块而整体关闭。

## 17. 完整文档交付与当前待办（2026-10-25）

本节为会话识别子模块开发前的文档快照，所引行号和“未修复”描述对应 `4dd20a7`；当前模块及证据见第 18 节和详细基线第 15.24 节。

本次按已提交源码 `4dd20a7` 整理，工作负载假设是长期运行、多个请求并发借还连接的服务。Java 本地参考 HEAD 已重新核对为 `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`。没有约定并发量、吞吐或延迟 SLA，不能据此承诺性能。本次只改文档；DataAdapter 是最近交付的功能模块，会话隔离仍为下一项待修工作。

### 17.1 文档覆盖与事实结论

Java 驱动已有内置连接池：`OceanBasePoolDataSource` / `OceanBasePooledConnection` 提供入口，`Pools` / `Pool` 处理注册、容量、借还、探活、reset 和清理。已静态核对这些调用；未运行 Java 池测试。.NET 当前也已有可选基础池化，默认 `Pooling=false`，并非仍然完全没有连接池；最大容量、等待/取消、预热、探活、生命周期、清池、路由/证书/凭据身份隔离和指标都有本地实现。**完整会话隔离、并发压力和真实 reset 验收尚未完成，当前仍是实验性驱动。**

| 评审需要回答的问题 | 完整内容所在位置 |
| --- | --- |
| 当前有何能力、什么完全没有、什么只完成一部分？ | 本报告第 3、4 节；[详细基线第 4.1 节](obconnector-dotnet-completeness.md#41-除连接池外具体还缺什么) |
| 每个问题的场景、影响、源码/测试依据和关闭标准是什么？ | [全部 23 项追踪表](obconnector-dotnet-completeness.md#14-缺口到场景源码和验收的追踪表)，保留 P0 六项、P1 十一项、P2 六项 |
| 最终功能契约、模式差异和最小验收是什么？ | [分模块要求](obconnector-dotnet-completeness.md#5-分模块要求和验收标准)、[公开 API/配置](obconnector-dotnet-architecture-design.md)、[目录](schema.md)、[凭据](credentials.md) |
| 开发依赖、实施顺序、要留下什么证据？ | [W1—W8 工作包](obconnector-dotnet-completeness.md#16-剩余交付工作包)，23 项各有一个主归属；本报告第 7、8 节 |
| 已做到哪里，哪些通过、失败或跳过？ | [进度台账](obconnector-dotnet-progress.md)、详细基线第 15 节和 [真实兼容记录](compatibility.md)；历史失败和跳过不被后续本地通过覆盖 |
| Java 对照是否等于 .NET 必须复制全部实现？ | 本报告第 6 节及 [Java 模块图](obconnector-j-architecture-outline.md)；按 ADO.NET 语义确定范围，Java 静态源码不等于互操作证据 |

### 17.2 除连接池外仍需完成的内容

| 工作类别 | 当前未闭合内容 | 对应 ID / 工作包 |
| --- | --- | --- |
| 事务与失败恢复 | ambient 超时/in-doubt/关闭竞态、commit/rollback 响应丢失、unknown-outcome 状态及应用重建契约；不自动重放未知写入 | P0-03/04/05，W2 |
| 协议选项 | OB20 压缩/reroute 尚缺；文本多语句、标准 MySQL 压缩和应用流 LOCAL INFILE 已实现但组合、失败及真实代理矩阵不足 | P1-01/02/03，W3 |
| 信任、发现和凭据 | 内置 OCP/TNS 或等价发现范围、健康状态/退避；TLS/mTLS、撤销/轮换、凭据服务/plugin/KDC 的真实验收 | P1-04/09/11，W4 |
| ADO.NET、Oracle 和类型 | Command/Batch/游标行为矩阵；REF CURSOR 方向及结果形状；复杂类型 LOB/cursor/national 成员、外部 DDL 缓存失效；扩展 metadata/类型码/字符集 | P1-05/06/07/08，W5 |
| 目录和生态 | 常用 Schema、DataTypes、参数推导、KeyInfo、DataAdapter/CommandBuilder 已有本地实现；真实目录/权限/DDL/生成 SQL/锁/并发与运行时证据仍缺。生成值回填及复杂查询更新限制须公开 | P1-10、P2-01，W6；键元数据与 W5 联动 |
| 性能和运维 | 跨命令 statement cache 明确缺失；rewrite/multi-send/bulk 范围与实现；建连/探活分段、slow-query 和协议诊断；压力与内存上限报告 | P2-02/03/04，W7 |
| 运行时和发布 | 独立运行时的实际 nupkg 消费、Windows/Linux、真实版本/模式/直连与 OBProxy/TLS/认证/网络故障及 XA crash recovery；六个 matrix.* 占位仍需实现 | P2-05/06，W8 |

以上不是“全部重写”。已实现能力应保留回归并补验收；明确拒绝的组合只有在正式纳入支持范围后才扩展。分布式提升、透明迁移、自动重连、EF Core provider 以及额外发现/性能路径不能因 Java 有类似模块就默认承诺。可选范围、负责人、日期及性能目标仍待确定；承诺范围内的安全、隔离和真实兼容证据仍是发布门槛。

### 17.3 优先处理：P0-02 的具体识别缺口

当前 [PhysicalSession.cs](../src/OceanBaseConnector/Internal/Session/PhysicalSession.cs) 第 304—314 行仅对开头固定前缀进行比较。因此前置注释、ALTER/CREATE 多词关键字之间的注释或非单空格、后续多语句、MySQL 可执行注释及不透明过程调用都可能漏掉“归还时销毁”的标记。[OceanBaseCommand.cs](../src/OceanBaseConnector/OceanBaseCommand.cs) 第 254 行又在长度、多语句、绑定等校验之前标记，Prepare 或未发送的拒绝命令可能造成额外销毁。现有 [PoolingTests.cs](../tests/OceanBaseConnector.UnitTests/PoolingTests.cs) 第 17—19 行只为该状态理论提供无注释 SET/临时表两例。

具体输入、影响、统一修复边界和验收要求已补入[详细基线第 5.1.1 节](obconnector-dotnet-completeness.md#511-当前会话状态识别的具体缺口)。这是源码走查发现，尚未实施修复或新增回归；不能表述为已复现实库污染，因为 `COM_RESET_CONNECTION` 对具体状态的恢复能力仍需实库验证。优先补 A 执行/归还、B 借用后的物理身份及基线验证，兼顾普通查询和无执行命令的安全复用；随后完成 W1/W2 的资源、并发及故障矩阵。P0-01/02/06 仍归属 W1，不因本次文档整理关闭。

### 17.4 本次核实的证据和未检查范围

本次重新读取当前池化配置/状态标记/命令校验路径、factory、Oracle PrepareExecute/扩展 metadata 拒绝边界、两包 TFM、Acceptance 占位、Java 池入口/借还/reset/清理，并解析以下已有报告的逐条 UnitTestResult。其余 codec、事务、目录及并发分支沿用前次走查记录，本次没有重新逐行走查全仓库。

| 已有本地报告，位于 `TestResults/data-adapter/` | 本次重新解析的结果 |
| --- | --- |
| `unit-verified.trx` | 4047 Passed，0 Failed/NotExecuted |
| `consumer-verified.trx` | 2388 Passed，0 Failed/NotExecuted |
| `di.trx` | 14 Passed |
| `integration.trx` | 9 Passed、72 NotExecuted；不增加真实服务器验收证据 |
| `temporal-actual-red.trx` | 2 Failed，作为已修问题的原始失败保留；修复记录见第 16 节 |

已有构建日志记录 0 警告/0 错误，格式日志仍有 workspace-load warning；包清单声明两包五资产及嵌入 README 与当时工作区一致，五资产仅在 .NET10 host 加载。本次未重跑构建、单测、格式、打包或实库测试。历史包内 README 对应原交付内容，不因本次更新 README 就成为新包验证结果。

本次只验证文档的相对链接/锚点、围栏、23-ID 及工作包覆盖、历史记录保留和 diff。未新增 Java 运行、真实 OceanBase/OBProxy、跨平台、独立运行时或压力报告。完整驱动目标、23 项和八工作包均保持开放；后续每项仍需实现提交、失败与通过回归、独立走查及对应验收证据。

## 18. 会话状态识别与发送边界模块（2026-10-25）

从 `4dd20a7` 补 P0-02 本地识别子模块。注释/词间空白、后续语句、可执行注释、匿名/带标签块及不透明过程不再依赖固定前缀；MySQL 用户变量和命名锁按保守规则销毁。Prepare、发送前拒绝、KeyInfo/SchemaOnly 元数据及长参数上传不提前标记，目标发送前才落地持久标记。公共 API/配置不变，五资产共用同一执行边界；[详细契约](obconnector-dotnet-architecture-design.md#pooled-session-state-after-user-sql)。

独立 review_session_state 找到命名锁的普通标识符/表与 CTE 清单误判、表达式 FROM 漏判，均有真实失败回归并修复；最后源码复核无剩余 Critical/Important。保留的 Minor 是嵌套 CTE 锁同名声明可保守销毁，已公开代价。SQL_MODE 引号反斜杠也会保守销毁；词法识别不证明任意存储函数、触发器或真实 reset 安全。

最终完整单测 4188 通过（实际新增 141、未移除旧例），DI 14 通过，Integration 9 通过/72 NotExecuted；均退出 0。完整消费者 2408 通过，实际新增 20 项五资产用例、无旧例移除，退出 0；均在同一 .NET10 host 运行。Release 构建 0 警告/0 错误，11 文件格式退出 0（保留 workspace-load warning）；本地双包/五资产哈希与 README 已核对，不构成独立运行时证据。过程中的全部失败及修复、最终命令/报告见[第 15.24 节](obconnector-dotnet-completeness.md#1524-2026-10-25-会话状态识别与发送边界)。此前第 17 节及第 5.1.1 节描述修复前文档快照；本地实现不关闭整个 P0-02、23 项或八工作包。

## 19. 跨命令 statement cache 模块（2026-10-25）

开发起点 `6d399fd`。`Statement Cache Size` 默认 0，可配置 0..1024；每个物理会话缓存 idle prepared IDs，按精确绑定后 SQL 复用并按归还顺序淘汰。命令持有期间保持独占，容量仅限制 idle cache，应用持有的命令仍须 Dispose。正常参数执行或显式 Prepare 才使用 server prepare，不改变普通文本 SQL 的选择。完整契约见 [API](obconnector-dotnet-architecture-design.md#prepared-statement-cache)。

共享目标发送边界负责上下文失效，成功 ChangeDatabase 也使缓存和仍被命令持有的旧 epoch 失效；reset/传输关闭忘记旧 IDs。完整结果和匹配参数数目才可进入缓存；执行错误、未读结果、元数据新准备、Oracle PrepareExecute/重写 KeyInfo 的 owner 保留原保护。新单测覆盖独占/重复归还、LRU、两模式 reset、DDL/会话/取消、SchemaOnly、部分元数据/参数数目、游标终止及新流参数；五资产消费者专项 20 通过。以下为租约竞争修复前的历史结果：完整单测 4235（实际新增 47、无旧例移除）、DI 14 通过，Integration 9 通过/72 NotExecuted，均退出 0；Release 构建 0 警告/错误，14 文件格式验证退出 0（保留 workspace-load warning）。本地包两个包/各五资产哈希和 README 匹配；完整消费者 2428 通过、0 失败/跳过、退出 0，实际新增 20 项且无旧例移除，期间未重建加载的程序集；五资产只在同一 .NET10 host 运行，不是独立运行时证据。此段记录竞争修复前的阶段证据；过程失败、fixture 修正和最终证据见[第 15.25 节](obconnector-dotnet-completeness.md#1525-2026-10-25-跨命令-statement-cache)。

提交前另复现同步/异步两例租约窗口：A 在检查 epoch 后等待连接锁，B 先执行 DDL，A 仍上传旧 ID 参数。原 RED 两例实际为预期 PREPARE(22)、收到 LONG_DATA(24)。共享 EnsurePrepared/Async 现于独占租约内重新检查 Database、Generation、CacheEpoch，失配关闭/重准备；49 项专项通过。独立增量复核无剩余可操作问题；修复后最终完整单测 4237 通过（新增 49）、完整消费者 2428 通过（新增 20），无旧例移除、0 失败/跳过，均退出 0；DI 14、Integration 9 通过/72 NotExecuted，退出 0。最终 Release 构建 0 警告/错误，15 文件格式检查退出 0（workspace-load warning 保留）；首次新增格式检查的一处空白诊断/退出 2 和修正过程留在详细记录。最终本地包的五资产哈希及嵌入 README 匹配。原报告另存 before-epoch-race-*，五份原始失败报告的失败例均对应最终通过；消费者运行期间未重建程序集，仍只证明 .NET10 host 上的五资产，不是独立运行时。

独立 review_statement_cache 的源码/测试/契约走查无 Critical/Important；Minor 拒绝措辞已澄清为本地发送前拒绝，服务端拒绝已发送 DDL 仍保守失效。审阅者未运行测试。两模式合成对照均验证 8 个命令只需 1 次 prepare，默认关闭则 8 次；修复前 TRX 整个测试时长 MySQL 141.84→26.81 ms、Oracle 158.71→20.00 ms，含人为 15 ms prepare 延迟及握手开销，不是真实 SQL 性能承诺；外部 DDL/函数/触发器没有依赖追踪，驱动不自动 replay。真实 DDL/reprepare、资源/负载、版本/OBProxy 和独立运行时仍须验收。此前第 12/14/17 节的提交前/修复前快照保留；第 1.1 节为当前阅读入口，不能用其旧缺失描述覆盖新模块。23 项和八工作包均继续开放。

## 20. Ambient 单资源事务模块（2026-10-25）

**本节是完整走查文档检查点，不是模块交付声明。** 已提交 HEAD 为 `f15716e`；工作区存在未提交的 ambient 实现及测试。第 1.1 节汇总全部八工作包，详细基线第 4、14 节覆盖全部 23 项缺口，既有第 12—19 节及详细第 15.1—15.25 节保留历史状态。

原 volatile enlistment 会允许多个数据库连接各自本地提交，也可能在协调器提交通知抛异常后再次通知而掩盖未知提交。工作区改用协调器单资源槽（PSPE）：接受后再初始化，第二资源/提升发送事务 SQL 前拒绝，确认 COMMIT、可信拒绝和未知结果分别报告 Committed、Aborted、InDoubt；通知不再捕获于数据库操作 catch 中。独立走查发现 Initialize 等待期间 Close/Open 的竞态，双模式真实线程交错 2 RED 后已通过原 Open generation 在 exchange 锁内复核修复，晚回调再绑定原本地事务；池 A/B 回归核对同一物理会话。完整行为见 [ambient API](obconnector-dotnet-architecture-design.md#ambient-transactions)。

**剩余 Important：自动 OpenAsync 登记初始化忽略取消令牌。** `OceanBaseConnection.cs` 的 OpenAsync 调用同步 EnlistTransaction，`AmbientEnlistment.Initialize` 转入 `OceanBaseConnection.Transactions.cs` 的 BeginAmbientTransaction，后者传 `CancellationToken.None`。控制 SQL 等待响应期间取消 OpenAsync 无法中断此阶段；Command Timeout=0 时可无界等待。这是已核实的源码缺口，尚无专门实际取消 RED/GREEN，不能由当前 4285 项通过推定已支持。最小修复为自动登记内部路径携带 opening token，保留公开同步 API；验收需双模式实际阻塞回包、调用者 token、连接/池/协调器槽清理和无 replay，再验证五资产消费者。scope timeout 可能等同步 Initialize 返回再回滚，不能替代 wire cancellation。

| 已有报告（本次重读，非新运行） | 结果与限制 |
| --- | --- |
| 原始失败 | `initial-red.trx` 6 失败；`initialization-race-red.trx` 2 失败，均保留 |
| 专项和全量单测 | `pool-green.trx` 52 通过；`unit-verified.trx` 4285 通过、0 失败/跳过，相对已提交缓存模块新增 48 项、无旧例移除 |
| 消费者 | `consumer-focused.trx` 40 通过：五资产 × 两模式 × 同步/异步 × 确认/丢失 COMMIT；实际两个 TCP fixture、公共 API、第二资源无事务 SQL、无 replay。全量消费者未完成 |
| DI / Integration | DI 14 通过；Integration 9 通过、72 个单独 NotExecuted，未启用实库；保留跳过，不能只看汇总 Counters |
| 构建 / 格式 | 已有 Release build 0 警告/错误；9 个 C# 文件格式验证保留 workspace-load warning；不证明取消缺口已修复 |
| 包 / 真实环境 | 本模块最终包哈希/嵌入 README 尚未验证；无新增 OceanBase/OBProxy/跨平台/独立运行时证据 |

独立 reviewer 未运行测试，已核对终态、竞态修复、池身份和消费者可信性；仍有上述 Important，故不能记为复核通过。原始产物位于忽略目录 `TestResults/ambient-single-phase/` 及根目录对应日志，需要另行归档才能交接。详细命令、报告、触发条件和关闭标准见 [第 15.26 节](obconnector-dotnet-completeness.md#1526-2026-10-25-ambient-单资源事务开发与完整文档检查点)。

本次同步 README、兼容性、API、完整基线和进度台账，逐项区分功能缺失、受限实现、开发中与欠真实验收。下一步先修复并回归自动初始化取消，再完成本模块最终全量消费者、构建/测试/格式、独立复核及包验证；之后继续真实事务故障、结果核对/重建和 W1—W8 验收。此前 2428 全量消费者与旧包仅验证上一个模块；同一 .NET10 host 加载五资产不等于五运行时。历史 Oracle nested-table column 失败、跳过及全部 23 项继续开放。

## 21. Ambient 单资源事务与初始化取消本地交付（2026-10-25）

开发基线 `f15716e`。PSPE 单资源登记、可信提交/拒绝/未知终态、初始化 Open generation 与原本地事务身份已闭合本地路径；第二数据库资源在其本地事务 SQL 前拒绝；后续提升明确拒绝并中止原本地事务，未知写/提交无 replay。第 20 节保留原取消缺口及当时未提交状态；本节是修复后的交付，不覆盖此前失败/警告/跳过。

独立走查的两个 Important 均有实际复现：Initialize 等协调器时 Close/Open 误用新会话（双模式 2 RED）现于获取 exchange 的同一锁内复核原 generation；自动 OpenAsync 初始化忽略取消（双模式 × 两条控制 SQL 的 4 RED）现把有效 opening token 传至同步 Initialize 的事务 exchange。取消响应等待后清本地/ambient 状态、丢弃会话、释放池容量并保留 exact caller token；成功 Open 后取消/释放原 CTS 不影响 commit。scope timeout 仍不能替代 wire cancellation；显式同步登记没有取消参数。

原始 6 RED、初始化竞态 2 RED、取消 4 RED 均保留并对应最终通过。取消测试最初因 scope Dispose 早于断线清理而中止的日志保留，修正测试清理后才取得 4 个可退出的真实超时失败。五资产取消消费者实际使用同 scope/事务和 maxpool1/wait1s 配置，第一次控制回包挂起后取消，第二条物理连接重新初始化并执行精确 SQL；Close/ROLLBACK/restore/reset、晚到 rollback 及 ClearPool→EOF 验证池/协调器清理。独立 reviewer 未运行测试，源码/测试复核无剩余可操作 Critical/Important/Minor。

最终 Release build 0 警告/错误；九文件格式验证退出 0，保留 workspace-load warning。全量单测 4291、完整消费者 2478、DI 14 通过，0 失败/跳过，均退出 0；Integration 9 通过/72 个单独 NotExecuted，未启用实库。相对上一已提交模块实际新增 54 单测/50 消费者，无旧例移除。完整消费者期间未重建加载的程序集；五资产仍只在同一 .NET10 host 运行。最终本地 no-build pack 退出 0，两包各五 DLL 哈希及嵌入 README 与构建/工作区匹配，未发布。原 4285 单测/40 专项消费者是取消修复前历史，50 专项消费者则包含新取消测试，均保留。

命令、最终报告、原始失败和验收边界见[第 15.27 节](obconnector-dotnet-completeness.md#1527-2026-10-25-ambient-单资源事务与初始化取消本地交付)。产物位于忽略目录 TestResults/ambient-single-phase 及根目录对应日志，需要独立交接时另行归档。本地交付不提供 durable prepare/跨资源原子提交或分布式恢复，也不关闭 P0-03/04/05 的真实 OceanBase/OBProxy/事务管理器故障、跨平台/独立运行时和长期负载验收；全部 23 项/八工作包及既有 Oracle 失败/历史跳过保留。

## 22. 复杂类型可观察 DDL 自动失效（2026-10-25）

开发基线 `96f544d`。Oracle 文本 CREATE/ALTER/DROP/RENAME/TRUNCATE 的目标发送边界增加独立元数据代际；绑定、字典加载/发布及新结果快照均拒绝旧上下文。支持默认关闭 statement cache 的场景，Prepare/发送前失败不失效；已发送但被服务端拒绝的 DDL 保守失效。重载描述并重建值后才能绑定，原返回值仍可读。外部或不透明 DDL 沿用显式清理，不提供数据库变更通知或自动 replay。

8 个确认的旧描述 RED 后取得专项 25 通过；新增边界进入全量单测 4347 通过（实际新增 56、移除 0），五资产专项消费者 40 通过。完整消费者 2478 通过/0 失败/0 跳过，10 分 32 秒，无用例新增/移除；运行期间 11 个程序集哈希和时间戳均未变。最终 Release build 0 警告/错误，10 文件格式退出 0（workspace warning），DI 14 通过，Integration 9 通过/72 NotExecuted；两本地包各五 DLL 和 README 校验一致，未发布。独立源码/测试 reviewer 无 Critical/Important；最终文档复核的一处 Minor 活跃摘要旧措辞已修正，无剩余可操作问题。reviewer 未自行运行测试。初始夹具失败及原始报告保留，见[详细交付记录](obconnector-dotnet-completeness.md#1528-2026-10-25-复杂类型可观察-ddl-自动失效)。

P1-07 的 LOB/cursor/national 成员和真实 DDL/锁/隐式提交/代理矩阵仍未闭合；本地 fixture 与同一 .NET10 host 的五资产消费不建立真实部署或独立运行时承诺。23 项/八工作包、历史失败和跳过全部保留。

## 23. 完整文档交付与 MySQL 过程开发检查点（2026-10-25）

本次按完整文档要求交付：第 1 节汇总差距和影响，第 3 节列能力矩阵，第 4 节保留全部 23 个缺口（P0 六项、P1 十一项、P2 六项），第 5/6 节给出连接池和 Java 对照，第 7/8 节定义实施顺序及发布门槛。[详细基线](obconnector-dotnet-completeness.md)第 5、14、16 节分别提供模块要求、逐项源码/验收追踪及八工作包；README、API、Schema、兼容性和进度台账同步指向当前边界。历史记录不作为当前已完成声明。

已提交源码仍为 `master` / `1bb13cb`，Java 固定参考 HEAD 本次核实为 `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`。Java 内置池和 .NET opt-in 物理池均存在；.NET 默认 `Pooling=false`，完整 reset/资源/并发/真实隔离矩阵继续开放。开发中的 MySQL 过程执行不新增缺口 ID，归 P1-05/W5；其调用、输出及关闭行为见[草案契约](obconnector-dotnet-architecture-design.md#mysql-stored-procedures-development-checkpoint)。已提交基线的“过程执行缺失”不能再描述成工作区完全没有代码，也不能因为工作区有代码而宣布已交付。

本次读取既有报告：相关单测 53 通过；专项消费者 80 例中 40 通过、40 失败，全部失败位于本地证书 PKCS#12 加载，尚未进入 TLS/过程交互。4 个直连 OceanBase MySQL 模式过程测试曾通过，但它们早于最新测试修改及移除 AllowMultiStatements 配置；默认策略实库执行未重验。最近新增单测、现有 Integration 断言调整、扩展 DEFINER 语法及提前关闭 Batch 行数修复尚缺最终全量门禁与增量独立复核。最初过程拒绝、DDL/DEFINER、提前关闭和所有失败报告均保留，详见[第 15.29 节](obconnector-dotnet-completeness.md#1529-2026-10-25-mysql-过程开发与完整文档检查点)。

本轮只修改文档、核对当前源码和既有 TRX；没有重新执行构建/测试/打包、真实数据库或 Java，也没有提交或发布。过程模块的消费者失败、最终包与复核未闭合，不能套用第 22 节的全量通过记录。Oracle nested-table column 的历史失败与全部跳过保留；五资产在同一 .NET10 host 上加载不等于五种独立运行时验收。所有 23 项和八工作包继续开放；范围决定、负责人、日期及性能 SLA 未批准部分仍标待定。

## 24. MySQL 存储过程执行（2026-10-25）

开发起点 `1bb13cb`，补齐 P1-05/W5 的 MySQL StoredProcedure 执行子模块。集合顺序绑定 Input/Output/InputOutput，prepared SET/CALL 和私有 SELECT 同一租约，完整响应后发布输出；关闭 reader 排空输出，Batch 不执行后续项。要求 PS_MULTI_RESULTS，ReturnValue、FetchSize、SchemaOnly/KeyInfo 和 INOUT stream 拒绝；会话归还时退休，CALL 不进 idle cache。输出按真实 SELECT 元数据解码，业务执行和输出获取失败须分别考虑，不能盲目重试，详见[契约](obconnector-dotnet-architecture-design.md#mysql-stored-procedures)。

原过程拒绝、提前关闭、DDL/DEFINER 和 Batch 行数失败均保留。增量走查又发现旧 closed reader 的 RecordsAffected 会覆盖重执行后的 BatchCommand 行数，双模式/同步异步 4 RED 后已修复。专项 67 通过；新增初始化取消/断线/超时与私有输出列数/多结果错误。独立源码/测试 reviewer 无剩余可操作问题，未自行运行测试。完整消费者 2558 通过、0 失败/跳过，11 分 15 秒；新增 80、无移除，11 个加载 DLL 哈希/时间戳不变。文档旧摘要入口已纠正，最终门禁结果和原 RED 对应均已核对。

全量单测 4414、DI 14 通过；Integration 无 opt-in 为 9 通过/76 NotExecuted。默认多语句关闭下实库专项 5 通过；适用 MySQL 套件 74 通过/5 跳过（含 9 配置回归），直连 4.4.2.2、Windows/.NET10/TLS disabled。原未过滤整套的 19 个 Oracle-only no-data 错误及 1 个过时 null 登记断言失败保留；纠正后以显式过滤运行适用套件，不推定 Oracle 兼容。两个本地包各五 DLL 和 README 已核对，未发布；首次格式错误和修正记录保留。

第 23 节为开发检查点，40 个证书加载失败已有同资产常规环境 80 通过对照，未改驱动或信任库；不当作真实 TLS 证据。完整命令、原始失败、跳过与交付边界见[第 15.30 节](obconnector-dotnet-completeness.md#1530-2026-10-25-mysql-存储过程执行本地交付)。所有 23 项和八工作包继续开放，历史 Oracle 失败及完整真实部署/独立运行时验收保留。

## 25. 完整走查文档交付与池配置身份（2026-10-25）

**源码快照：`master` / `c2fe307`；本次交付为文档更新。** MySQL 过程已在该提交交付，不能再按第 23 节的开发检查点认定为未实现。池配置归一化目前只有实施方案，没有产品或回归测试修改。完整驱动目标、全部 23 个缺口及八个工作包仍未关闭。

### 25.1 文档覆盖与阅读顺序

| 评审所需内容 | 当前入口 | 覆盖范围 |
| --- | --- | --- |
| 完整缺口、优先级、使用影响、依赖和实施顺序 | 本报告第 1.1、1.2、3、4、7 节；[工作包](obconnector-dotnet-completeness.md#16-剩余交付工作包) | P0 六项、P1 十一项、P2 六项，分别归入 W1—W8；每项区分缺实现、受限组合和欠验收 |
| 可操作的功能契约、源码和测试入口、完成标准 | [能力基线第 5 节](obconnector-dotnet-completeness.md#5-分模块要求和验收标准)、[逐项追踪表](obconnector-dotnet-completeness.md#14-缺口到场景源码和验收的追踪表) | 池/隔离、事务/恢复、协议、信任/发现/凭据、命令/Oracle/类型、目录/生态、性能/诊断及平台/发布 |
| Java 对照及连接池真实状态 | 本报告第 5、6 节；[池配置发现](obconnector-dotnet-completeness.md#513-普通池配置身份尚未归一化) | Java 有内置池；.NET 有默认关闭的物理会话池；配置身份、完整 reset、压力和实库验收仍有欠项 |
| 公共 API、配置和当前限制 | [架构/API](obconnector-dotnet-architecture-design.md)、[Schema](schema.md)、[凭据](credentials.md)、[README](../README.md) | 已实现调用方式、参数/结果形状、资源所有权和明确拒绝的组合；待开发目标不能作为现有 API 使用 |
| 已验证、失败、跳过和发布门槛 | [进度台账](obconnector-dotnet-progress.md)、[兼容性](compatibility.md)、基线第 15 节、本报告第 8 节 | 原始失败及修复报告、真实部署边界、五资产与独立运行时的区别、实验性发布约束 |

除连接池外，仍须补齐事务未知结果与故障恢复、OB20 压缩/reroute、发现/健康退避、Oracle 输出与复杂成员、扩展类型/字符集、批处理优化和诊断；已有 TLS/凭据、目录/参数推导、adapter、缓存等模块仍欠相应真实环境、负载或运行时验收。具体条目和验收产物已在第 1.1/1.2 节逐包列出，不把“已有实现但未验收”写成“完全未实现”。

分布式 ambient 提升目前明确拒绝；EF Core provider 是独立工程。OB20 客户端协议、发现方式、批量优化策略、shutdown 和正式平台/ORM 承诺仍需冻结范围。负责人、期限、性能 SLA 未指定，文档不虚构排期或性能指标。

### 25.2 本轮确认的池配置缺口

普通池按 builder 输出文本与策略引用身份索引。现有 DLL 的无网络探针确认：同样键值改变顺序、省略与显式默认 `Port=2881`、`Pooling=true` 与 `TRUE`，均产生不同池；`Host` 与 `Server` 的单纯别名变化已统一，仍共池。三类分池和一个反例均已记录，未声称发生真实服务器超额建连。

这使有效配置相同的普通连接不能保证共享容量，ClearPool 也只匹配对应文本。**P0-01 / W1 应实现内部配置归一化**，并覆盖领取、归还/失败清理及清池路径；同时保留凭据、resolver、TLS 策略/证书及私有 DataSource 边界。触发场景、源码、临时使用约束、实现边界和回归/实库关闭标准见[基线第 5.1.3 节](obconnector-dotnet-completeness.md#513-普通池配置身份尚未归一化)。该发现纳入原 P0-01，不增删原有编号。

### 25.3 本轮证据及未完成检查

本轮重新核对 .NET HEAD、连接配置/registry/清池调用链和相关拒绝分支；Java 参考 HEAD 仍为 `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`。已读取 `OceanBasePoolDataSource` 的池领取入口及 `Pool` 的最小/最大容量、闲置队列和回收路径，没有编译或运行 Java。无网络探针使用当前 Release/net10.0 DLL，仅创建并移除空池，不读凭据、不启动数据库连接；它不是正式单元测试或实库压力报告。

既有 TRX 按逐用例 outcome 重新统计：单测 4414、消费者 2558、DI 14 通过；无 opt-in Integration 9 通过/76 NotExecuted；适用 MySQL 实库 74 通过/5 NotExecuted。以上均为过程模块原报告复核，**不是本轮重新运行测试**。保留原未过滤失败、证书加载失败、Oracle nested-table column 及清理失败；此次没有补齐或覆盖这些真实验收。

本次只新增最小身份探针及文档结构/链接/编号/历史保留检查；未重新执行 build、完整测试、pack、真实数据库、OBProxy、TLS/KDC、并发/长期压力或独立运行时矩阵。未修改产品代码、提交或发布。优先闭合 W1 配置身份与会话隔离、W2 事务失败契约，再按第 7 节推进其余工作；每个已承诺能力均须满足第 8 节及基线第 16.2 节才能计为完整交付。

## 26. 普通池配置归一化（2026-10-25）

开发起点 `master/c2fe307`；普通池现在按有效配置统一身份，等效顺序/类型表示/显式默认值共享容量和清池。所有文本、秘密、策略引用/证书指纹及私有 source 边界保留，公开序列化不变。P0-01/W1 仍未整体完成；[契约](obconnector-dotnet-completeness.md#514-普通池的有效配置身份)、[完整证据](obconnector-dotnet-completeness.md#1531-2026-10-25-普通池配置归一化)。

独立走查发现公开序列化丢失 quoted-empty PAM 因子，4 个真实 RED 后改由原始 builder 捕获 Open/ClearPool/失败身份，另 2 个 Close discard RED 后补 DetachForClose 快照。保留 Closed-before-cleanup 事件契约；原回调立即重开期望改为验证共享容量超时、finally 归还及 Close 后复用，不称为绕过上限的修复。回调内同步重开同满池在等待为 0 时可无限等待，应移到 Close 完成后；任意重入仍属 W1 待收敛。最终增量源码 review 无剩余可操作问题，reviewer 未跑全量。

最终 build 0 警告/错误；相关292、完整单测4494及DI14通过；无 opt-in Integration9通过/78逐用例NotExecuted。真实专项6通过，双模式Connections26通过/3跳过，限已配置直连及Windows/.NET10；不新增代理/TLS/PAM服务/独立运行时声明。完整消费者2638通过/0失败/0跳过，用时11分30秒；单测及消费者按名称/ID多重集均实际新增80、无移除。两个本地包各五DLL及README逐字节核对，format退出0保留workspace warning和原错误，未发布。冻结脚本首次glob误选依赖副本已纠正，未重建/重启原消费者；最终11个文件哈希/时间戳保持运行早期样本值，修改时间均早于消费者启动；不将运行早期样本称为预运行冻结。全部原始失败、两例契约修订、用例清单及包验证见 result-validation.json。历史失败和全部23项/八包保留。

## 27. 普通结果字符集（2026-10-25）

开发起点 master/abd0077，P1-08/W5。普通字符串结果与MySQL ColumnSize使用共享严格collation映射，覆盖ASCII、MySQL latin1/CP1252、GBK、GB18030-2005、UTF16/LE及已列明UTF8族；binary63、JSON/CLOB、可信national及客户端UTF8边界保留。[公共契约与全部ID](obconnector-dotnet-architecture-design.md#result-character-sets)、[完整证据及原失败](obconnector-dotnet-completeness.md#1532-2026-10-25-普通结果字符集)。全部23项和八工作包继续开放。

初始84RED与reviewer发现的GBK单字节80/FF的2RED、GB18030旧映射的2RED均有原报告；shared decoder修正语法及A8BC/8135F437映射，不做全局provider注册。独立全表核对包括latin1全部256字节及GB18030有效四字节1,087,996项；仅已修正的两处差异。reader首轮64失败属于Oracle NUMBER fixture类型，最终相关524通过。最终Minor为测试失败清理路径漏等服务Task，已修正并复核；最终评审结论以完整基线记录为准。

真实4.4.2.2 MySQL直连专项6通过，每项8组合，TLS disabled；UTF16/LE直接SET结果字符集1231/42000失败保留，最终使用NULL保留表达式原编码并断言string类型和内容。不能扩展为Oracle、OBProxy、TLS或全collation兼容。旧Int32/Int64断言错误、测试源码编译失败和沙箱证书导入失败保留。NuGet只完成本地缓存restore，在线漏洞审计未完成。

最终完整单测4915、消费者2798、DI14通过，零失败/跳过；消费者耗时13分36秒，11个运行前冻结DLL哈希/时间戳保持一致。消费者实际新增160无移除，单测422新增/1负例替换，净增421，按名称/ID多重集核对。正常用户复跑通过，沙箱原单测137失败/4778通过、消费者846失败/1952通过分别保留；未修改TLS或证书信任以规避环境问题。Release solution及最终两个测试项目增量build为0警告/错误，format退出0保留workspace warning。

最终独立review无剩余Critical/Important/Minor。两个本地包各5DLL与输出及README/LICENSE/NOTICE精确匹配，Standard声明CodePages而现代runtime提供；eng内容/依赖检查通过，未运行该脚本独立缓存restore/nupkg消费者。临时脚本根路径错误及错误的全TFM依赖假设已修正并保留原失败。344本地链接、三张23ID表/八工作包和既有历史段落均检查通过。

所有证据保存在忽略TestResults/character-sets及根日志，需另行归档。未发布；五资产仅在同一.NET10 host消费，完整服务器/代理/独立运行时、客户端编码、national扩展、其他charset/type code仍须按W5/W8验收。

## 28. 完整文档交付与 Oracle 独立 Prepare 检查点（2026-10-25）

本次按用户要求整理完整文档，以已提交的 `master/6b357f0` 为基线。完整清单沿用第 4 节的 **23 项（P0 六项、P1 十一项、P2 六项）** 和能力基线第 16 节的八工作包；每项的触发场景、源码/测试入口及关闭标准见[追踪表](obconnector-dotnet-completeness.md#14-缺口到场景源码和验收的追踪表)。当前摘要和历史记录分别保留，不以文档完成宣布驱动完成。

| 读者需要作出的判断 | 当前结论与入口 |
| --- | --- |
| 连接池是否漏做 | Java 和 .NET 都已有连接池。.NET 默认 `Pooling=false`；有效配置归一化、容量、等待/取消、预热、探活、回收、清池和路由隔离已有本地实现；完整 reset/资源/XA 隔离、并发长期负载及真实服务器/代理验收继续开放。见第 5 节及 P0-01/02/06。 |
| 除连接池外还要开发什么 | Oracle 独立 Prepare/输出 fetch 组合、REF CURSOR 输入/列值、复杂 LOB/cursor/national 成员、OB20 压缩/reroute、发现/健康退避、批量优化和诊断细分仍有缺项；协议与发现等先冻结目标支持范围。见第 1.2 节、P1-02/06/07/09、P2-03/04。 |
| 哪些已有实现而欠验收 | 事务/unknown outcome、TLS/认证/凭据、SchemaOnly/KeyInfo、目录与参数推导、adapter、statement cache、字符集等按第 1.1 节逐项补真实故障/权限/版本/代理/运行时证据；不能全部记为未实现。 |
| 如何实施、何时关闭 | 八工作包的依赖与最低产物见[能力基线第 16 节](obconnector-dotnet-completeness.md#16-剩余交付工作包)。优先 W1/W2 的隔离与结果安全；每项保留契约、实现、回归、独立走查和必要实库报告，发布还须满足第 8 节。负责人、日期和 SLA 尚未指定。 |

**当前开发现场。** Oracle 输出/过程路径仍在 `OceanBaseCommand.Start` 拒绝独立 `Prepare/PrepareAsync` 和正 `FetchSize`；普通 Prepare 已有实现。工作区只有新增的 `OracleStandalonePrepareTests.cs`，尚无产品修改。现存 `initial-red.trx` 的 12 项全部因该明确拒绝失败，覆盖同步/异步、两种 EOF 策略和三类命令形状；不能写成已通过或已交付。后续准备无执行副作用、statement/cursor 所有权、取消/错误/重开及真实验证要求见[第 15.33 节](obconnector-dotnet-completeness.md#1533-2026-10-25-完整文档复核与-oracle-独立-prepare-检查点)。其他 P1-06 方向及结果形状继续独立跟踪。

**本次复核证据。** 重新读取固定 Java HEAD 及池入口、.NET Prepare 拒绝、REF CURSOR 方向、复杂成员白名单、OB20 拒绝和六个 `matrix.*` 延迟占位。读取上模块现存 TRX：单测 4915、消费者 2798、DI 14 通过，无 opt-in Integration 9 通过/79 NotExecuted，MySQL 直连字符集专项 6 通过。以上是既有执行报告，不是本次测试；新增 12 RED 不被旧全绿覆盖。原 Oracle nested-table column 失败、沙箱 PFX 失败、在线 NuGet 审计未完成及所有历史跳过均不改写。

**交付边界。** 本次只更新两份审计文档及进度台账，校验链接/锚点、23-ID 三表一致性、八包唯一归属、历史原文、编码/换行与 diff。没有重跑构建、单测、实库或 Java 测试，没有提交或发布；现有回归文件保留原状。完整驱动、23 项和八工作包继续开放，正式支持范围和真实兼容矩阵仍须逐项闭合。

## 29. Oracle 独立 Prepare（2026-10-25）

从 master/6b357f0 开发，P1-06/W5。独立 Prepare/PrepareAsync 只发 COM_STMT_PREPARE，保留命令独占 ID，下次执行交给既有 OracleCursorOwner；cursor 可以跨命令 Dispose。普通命令与数组均携带 retained ID，stream 只在执行时发送 Oracle piece。重复 Prepare 可复用；后续隐式执行重新准备，不新增持久 Oracle 缓存。详见[公共契约](obconnector-dotnet-architecture-design.md#oracle-standalone-prepare)和[完整原始失败/验证记录](obconnector-dotnet-completeness.md#1534-2026-10-25-oracle-独立-prepare)。

真实 RETURNING 暴露输入参数/纯 OUT 列拆分，现以 Oracle DML + 精确输出列/总数 + 纯 OUT 校验；PL、INOUT/ReturnValue 不放宽。原 12 RED、RETURNING 4 RED、split-procedure 2 RED、array 4 RED 和 fixture 失败均保留。独立走查的两项 Important 已进入修正：命令锁统一上下文检查和转移，确定性竞争回归捕获原 NRE；三个 DML 负例通过，并以刻意移除保护的 3 失败 mutation 证明覆盖。首轮全量的 2 失败属于旧 Prepare 必须拒绝的断言修订，保留原报告。

当前专项 context-green 44、review-green 50、消费者 60 通过；最终完整单测 4963、DI 14 通过，无 opt-in Integration 9 通过/82 NotExecuted。Oracle 实库最终含准备后数组的 20 项通过；完整消费者 2858 通过/0 失败/0 跳过，耗时 12 分 23 秒。11 个运行前冻结程序集哈希/时间戳不变；单测新增 48、消费者新增 60，按名称/ID 多重集核对均无移除。两项走查 Important 已修正并完成回归与全量验证，无剩余已确认问题。两个 no-build 本地包各五 DLL/文档/依赖组验证通过；没有执行新缓存 restore 或独立 nupkg 消费者。实库为 Oracle4.4.2.2 直连、TLS disabled、Windows/.NET10；五资产消费者非五个独立运行时。正 FetchSize 输出执行、REF CURSOR 输入/列值与其他缺口不开放，历史失败/跳过及全部 23 项、八工作包继续保留。

## 30. 建连阶段诊断（2026-10-25）

从 `master/d6a9169` 开发，P2-04/W7。现有 Meter 新增九阶段秒直方图，只含固定 phase/outcome 标签；可选阶段省略、TCP 重试/预热逐次记录、池复用与实际等待/探活边界均有回归。现有操作 span、指标及旧 pool.wait 聚合语义保留；linked-budget 阶段取消和逻辑 Open 超时分别记录。[公共契约](obconnector-dotnet-architecture-design.md#connection-phase-diagnostics)、[完整证据与原始失败](obconnector-dotnet-completeness.md#1535-2026-10-25-建连阶段诊断)。

独立 reviewer 找出池等待包含清理监听器耗时，按实际影响列 Important；同步/异步两项 RED 后补单调结束快照，GREEN 两项通过。此前静态字段顺序、fixture SSL/time-zone、方法组编译和沙箱 PFX 失败均保留。正常用户早期专项 78 通过。首次全量单测 5003 通过/2 超时失败保留；同程序集相关方法单独 6 通过，消费者结束后完整单测 5005 通过（208.5 秒），未排除或放宽旧例，首次超时根因尚未确认。消费者 2863 通过（732.6 秒），DI 14 通过；Integration 未 opt-in 为 9 通过/83 NotExecuted，实库双模式直连诊断 8 通过。Release 构建 0 警告/错误、限定文件格式检查退出 0（workspace-load warning 保留）。单测新增 42、消费者新增 5，名称/ID 无移除；原始单测失败均映射最终同名通过。启动前冻结 11 DLL 不变，两个本地包各五 DLL/文档/依赖组验证通过。未新缓存 restore、独立 nupkg 消费或发布。实库为 4.4.2.2 direct、TLS disabled、Windows/.NET10；五资产非五独立运行时。无其他已确认新增产品缺陷或延后 Minor。真实服务器、五资产消费者与完整回归不能互相替代；P2-04 的协议 debug、slow-query、其他资源等待及负载/平台验收继续开放，全部 23 项及八工作包不关闭。


## 31. 慢命令诊断（2026-10-25）

从 `master/b2fce9d` 开发，P2-04/W7。默认关闭的 Slow Query Threshold 毫秒阈值保留于配置快照和池身份；复用现有 exchange 时钟，对 execute/cursor.fetch 的完整生命周期完成报告一次，包括 reader 消费及错误。Warning1102、四标签计数器、现有 span 标记独立工作，不记录 SQL/参数/凭据/异常文本；不提供服务器 CPU 耗时或周期性挂起告警。[公共契约](obconnector-dotnet-architecture-design.md#slow-query-diagnostics)、[原始失败和证据](obconnector-dotnet-completeness.md#1536-2026-10-25-慢命令诊断)。

初始25RED、两次测试编译错误、first-green160pass/6fail（缺 User ID、PFX、实际重复 wait）和 focused-user173pass/7time-zonefixturefail 全部保留；校正后的 focused-green184pass。既有 wait 测试按每次 invocation 契约验证所有 elapsed 样本，仍验证超时及容量；未减少产品等待或指标。独立走查无确认新增缺陷或延后Minor，检查配置/池身份、reader/REF CURSOR、错误/清理/三通道隔离和脱敏，并确认 wait 测试修正。完整单测5049（217.0秒）、完整消费者2873（762.0秒）、DI14通过，无opt-in Integration9通过/84NotExecuted；专项184、五资产诊断25、双模式直连诊断12通过。Release构建0警告/错误，限定格式检查退出0（workspace-load warning保留）。单测新增44、消费者新增10，名称/ID无移除；原始失败均映射最终同名通过。启动前冻结11DLL不变、两本地包各5DLL/文档/依赖组验证通过。实库4.4.2.2 direct/TLSdisabled/Windows/.NET10/SELECTonly；五资产非五独立运行时。未新缓存restore、独立nupkg消费、在线漏洞审计或发布。reviewer未执行门禁或判断部署/负载/无限阻塞回调，仍按公开契约与剩余矩阵验收。完整驱动、23 项及八工作包仍未关闭，真实平台/代理/TLS/负载与协议 debug 等缺口继续跟踪。

## 32. Oracle 未知精度 NUMBER 结果与 W1 新发现（2026-10-25）

从master/5ea71b2开发，P1-08/W5。实库绑定整数7的结果读取被precision0/scale-1约束错误拒绝；真实NUMBER(*,-1)表和CAST也返回相同元数据，不能安全区别未知标记。修复仅让Oracle precision未知结果保留精确载荷，继续检查Oracle有效数字/指数范围；已知precision、MySQL和参数声明约束保留。Schema仍回报服务端元数据；系数/值scale按实际文本表示，40由原4/-1变为40/0，数值不变。[公共契约](obconnector-dotnet-architecture-design.md#oracle-number-results-with-unknown-precision)、[完整失败与验证](obconnector-dotnet-completeness.md#1537-2026-10-25-oracle-未知精度-number-结果读取)。

新单测6RED/3pass后专项84通过。完整单测首次5057通过/1既有初始TCP超时；原路由专项5通过后完整5058通过，未修改预算或将原因归为已证实环境问题。受影响消费者40（非完整套件，含新增五资产5例）、DI14、Oracle实库4通过；无opt-in Integration9通过/86NotExecuted。构建0警告/错误，格式空白修正后限定检查退出0；workspace warning及原始失败保留。按单测名称新增12/移除3、净增9，明确保留改名/期望修正；原6RED均最终同名通过。两次独立走查及消费者增量复核无确认新增缺陷；reviewer未执行门禁或部署验收。五资产非五独立运行时，未打包或发布。

W1另确认Oracle全新连接及pool reset后的隐式写不可见，自动提交初始化仍待修；旧显式事务回滚通过不能证明其后隐式提交。初始池套件12/3/1、装配修正14/4/1、Oracle探针4失败与declared-metadata2通过均保留，详情和忽略草稿见15.37。完整池隔离、OBProxy/TLS/跨平台/长期负载及所有23项、八包继续开放；本次数值修复不掩盖或关闭该事务风险。

## 33. 完整文档复核与 Oracle 自动提交 RED（2026-10-25）

当前已提交基线 `master/4810907`；Java 参考 HEAD 复核为 `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`。完整能力总表、23 项场景/源码/验收追踪、八工作包及依赖均保留。新增的最高优先发现是 Oracle fresh/reset 自动提交基线缺失，纳入 P0-02/W1 并与 W2 联合验收；没有另起编号。完整问题、影响、源码、Java 对照、修复顺序和最低验收见[基线第 5.1.5 节](obconnector-dotnet-completeness.md#515-oracle-自动提交基线已确认缺陷与待实现契约)，本次文档记录见[第 15.38 节](obconnector-dotnet-completeness.md#1538-2026-10-25-完整走查文档与-w1-red-检查点)。

新增 Oracle 初始化回归共 18 项，filtered Release 运行退出 1，0 通过/18 失败/0 跳过；其中 2 项因未发送 SET 等待 fixture 超时，16 项因 Open 未产生预期失败而失败。它证明初始化路径缺失，不能写成已验收异常响应处理。共享身份 fixture 调整及恢复的 PoolIsolationTests 均未提交；整理后的实库套件仅已构建，尚未重跑，未补完 reset 失败/取消/超时和帧回归，生产实现尚未修改。本次未运行全量门禁或独立最终代码走查，不以旧 NUMBER 模块的通过结果替代当前 RED。

文档检查保留第 12—32 节及基线第 15.1—15.37 节历史原文，核对本地链接/锚点、三表 23 项、八包唯一归属、编码/换行和 diff；本次未提交、推送或发布。全部完整缺口、真实 OBProxy/TLS/认证/跨平台/独立运行时/长期资源验收继续开放。完成文档不代表驱动达到生产交付标准。

## 34. Oracle 自动提交与真实池隔离（2026-10-25）

从master/4810907开发P0-02/W1。Oracle身份确认后显式设置自动提交，reset先回滚清理再设置；确认完整后发布Open/新generation。共享普通OK校验拒绝错误计数、缺Autocommit、事务（含0x2000）、多结果和cursor/output状态；错误/取消/Close/超时销毁会话并释放容量，MySQL登录拒绝契约保持。建连合并SET元数据、reset清除旧元数据，增加一个往返的成本已公开。完整契约见[架构](obconnector-dotnet-architecture-design.md#oracle-autocommit-baseline)，失败、命令和验证见[基线15.39](obconnector-dotnet-completeness.md#1539-2026-10-25-oracle-自动提交与真实池隔离)。

原Open18RED、reset18RED、只读状态4RED、CS0103、沙箱344/32、首次全量5130/4及格式/核验脚本错误均保留。旧MySQL/Oracle脚本按实际协议补齐，未放宽断言/预算或删除失败例。最终全量单测5134通过（新增76/移除0，原失败同名通过）、完整消费者2898通过，DI14、no-optin9通过/93跳过；Release与consumer构建0警告/错误，限定格式0且workspace warning保留。实库池从18/4/1变为22/0/1，整个Connections加OracleNumber52通过/4跳过，仅双模式4.4.2.2直连/TLSdisabled/Windows/.NET10。新增20项公共五资产案例证明拒绝后真正新socket/认证/SQL可用，ClearPool在恢复之后执行，不能掩盖容量泄漏。

独立走查发现生命周期/metadata覆盖欠项，补12+8项后已复核；新增consumer和旧fixture及公共契约增量走查无剩余可操作问题。reviewer未执行门禁或独立实库。消费者冻结11DLL不变；两个本地包各5DLL及文档/依赖组核对；两个standalone样例×三个真实现代runtime共6项smoke通过，不能等同完整运行时/部署矩阵。完整资源隔离、XA/隐式副作用、并发/长期负载、OBProxy/企业信任/其他平台与正式发布继续开放；所有23ID、八包及第12—33节/基线15.1—15.38历史保留。未push或发布。

## 35. XA 池隔离与 prepared 分支恢复验收（2026-10-25）

本模块从 master/ffadc7a 补 P0-02/W1 验收，生产实现原本存在且新增用例首次即通过，没有新增生产修复。新增 28 项本地协议回归：双模式、同步/异步、七阶段专用会话关闭，旧 socket EOF 无 reset/隐式 XA 完成；单容量新握手和 SQL 在清池前成功；同逻辑连接重开后十二个旧资源 API 拒绝。新增 20 项真实用例：idle/commit/rollback 之后仍销毁专用会话，替代借用者自动提交和本地事务正常；prepared 分支关闭后可由新会话 Recover、显式完成，再次恢复无该 XID且行数正确。契约与源码入口见[基线 5.1.6](obconnector-dotnet-completeness.md#516-xa-专用会话的池隔离)，完整命令与边界见[15.40](obconnector-dotnet-completeness.md#1540-2026-10-25-xa-池隔离与-prepared-分支恢复验收)。

首次及最终全量单测均 5,162 通过，新增 28 项、旧测试源码无移除；Connections + XaTests 首次及最终均 76 通过、4 项配置跳过；无 opt-in 9 通过、95 NotExecuted。Release solution/integration 构建 0 警告/错误，限定格式退出 0 且保留 workspace warning。独立 reviewer 要求完善失败清理的顺序、异常聚合、非池连接和服务 Task 收尾，均已补齐并复核；无剩余可操作问题。仅两个测试文件及文档变更，未重跑消费者/DI/包/运行时样例，上一模块结果不算本轮门禁。

只证明 OceanBase 4.4.2.2 双模式直连、Windows/.NET10、TLS disabled 专项。真实 active/suspended 断开存续、响应丢失、外部 coordinator crash、完整资源/并发/代理/企业 TLS/平台矩阵仍缺，全部 23 ID、八工作包及历史第 12—34 节保留，完整驱动尚未完成。未 push 或发布。

## 36. 旧 reader 延迟关闭与池租约隔离（2026-10-25）

从 master/00b3976 继续 P0-02/W1，并关联 P1-05。确认缺陷：读完 CloseConnection reader 后显式 Close/Open 同一逻辑连接，池复用同 PhysicalSession，旧 reader Dispose 会关闭新租约。原本地四项及真实直连四项均失败；修复在命令启动/Batch 预留时于连接锁内捕获逻辑 Open generation，所有四个 reader 关闭路径在 DetachForClose 同锁比较引用和 generation。公开 Close 不附加条件；当前租约 CloseConnection 行为保留。源码与触发/关闭契约见[能力基线 5.1.7](obconnector-dotnet-completeness.md#517-closeconnection-reader-的逻辑租约隔离)。

新增八项本地覆盖双模式同步/异步及 Batch 释放 reservation 后的回调窗口，二十项消费者覆盖五资产公共 API 同 socket reset/ping/新 SQL；实库四项断言同物理对象及 generation 递增，旧 reader 关闭后新 SQL 正常。Batch 受控去掉代数比较后四项失败，生产文件原字节恢复；不是原始 RED。独立 reviewer 核对生产路径无遗漏，并要求 fixture 独立执行清理/观察两个 Task/聚合主异常，已修正复核。

最终完整单测 5,170、消费者 2,918、DI 14 通过；无 opt-in Integration 9 通过/96 NotExecuted；Connections + XaTests + RefCursorTests + LobTests 87 通过/4 配置跳过；Release solution 0 warning/error，十文件 scoped format 退出 0，保留 workspace warning。初始 fixture CS0266、零测试过滤、消费者首轮 10 通过/10 Oracle timezone fixture 失败及修正过程完整记录于[15.41](obconnector-dotnet-completeness.md#1541-2026-10-25-旧-reader-延迟关闭与池租约隔离)。

真实结果仅覆盖 OceanBase4.4.2.2 双模式直连、Windows/.NET10、TLS disabled；消费者五资产使用一个 .NET10 host。没有重跑包消费/独立运行时/非 Windows/代理/企业 TLS 或完整数据库套件。完整资源 reset、任意并发/长期负载、XA 故障与协调器恢复仍开放；23 ID、八工作包、历史第 12—35 节及全部失败/跳过保留。未 push 或发布。

## 37. 完整走查文档与资源验收状态复核（2026-10-25）

本次按用户要求交付完整走查文档，已提交代码基线为 `master/6c45173`。23 个缺口、优先级、当前能力、Java 对照、触发影响、源码/测试入口、验收条件及八工作包依赖保留；详细文档见[能力基线](obconnector-dotnet-completeness.md)，本次快照和原始报告见[第 15.42 节](obconnector-dotnet-completeness.md#1542-2026-10-25-完整走查文档交付与资源验收检查点)。不是继续新增功能，也不宣布完整驱动交付。

Java 固定 HEAD 再次核实，四个池相关类存在；.NET 的基础池、容量/清理和已提交自动提交/XA/reader 子模块不能继续描述为完全缺失。尚缺内容仍包括完整资源/reset/并发隔离、事务未知结果与故障恢复、OB20/reroute、发现、受限 Oracle/ADO.NET/类型形状、批量优化及真实代理/信任/运行时矩阵。按原 ID 区分缺实现、组合受限及欠验收，不另建平行问题编号。

原始 TRX 复核：上一模块完整单测 5,170、消费者 2,918、DI14 通过，无 opt-in 集成 9 通过/96 NotExecuted，限定真实套件 87 通过/4 NotExecuted。这些是既有报告，本次未重跑。另一个原有未提交资源草稿为 24 通过/4 失败：持久 LOB20 通过，REF CURSOR8 中 unopened/disposed4 通过，partial/complete 同步/异步4 失败。错误包含重新握手超时及测试端连接关闭，原因待确认；不得把草稿算作已提交验收、把失败认定为产品缺陷，或合并声称当前工作区全绿。资源验收的最低条件见[第 5.1.8 节](obconnector-dotnet-completeness.md#518-readerlob-与-ref-cursor-的剩余验收)。

本次只修改三份文档，保留原测试草稿；第 12—36 节及基线第 15.1—15.41 节历史保留。执行文档结构、链接/锚点、缺口/工作包、编码和 diff 检查；未重新构建、运行数据库/测试套件、打包或发布。23 项与八工作包仍开放，负责人、期限、性能 SLA 和尚待确认的支持范围不作虚构承诺。

## 38. 持久 LOB 与 REF CURSOR 池隔离验收（2026-10-25）

从 master/6c45173 继续 P0-02/W1，只增加验收测试，无生产/API/依赖变更。[当前契约](obconnector-dotnet-completeness.md#518-readerlob-与-ref-cursor-的剩余验收)与[完整失败、修正及门禁](obconnector-dotnet-completeness.md#1543-2026-10-25-持久-lob-与-ref-cursor-的同端点池隔离验收)保留全部证据。新增本地二十八项与真实 Oracle 二十八项；同端点 MaxPoolSize=1，活动持久 LOB 句柄销毁会话、全部释放的只读句柄允许同会话 reset/复用；旧读写/截断/绑定/释放不影响下一租约。非池观察连接核对持久值的完整文本或字节内容。REF 四阶段仍受不透明调用退休保护，不能证明独立 cursor 计数或释放后复用。

原本地24pass/4fail来自partial TCP reset与complete提前等待statement-close的fixture假设；修正后28pass。原实库8pass/20fail是patient-data长度错写十一，实际十二；修正后28pass，完整内容观察者断言再纳入最终相关115pass/4配置跳过。最终完整单测5198pass，无opt-in9pass/98NotExecuted；Release solution/观察者项目构建0warning/error，两文件格式和verify退出0，workspace warning保留。原5170单测及91实库case均保留；上述失败均最终同名通过，不称为生产RED。

独立走查发现长度不能识别等长覆写，已补非池完整值断言并复核，无剩余可操作问题；reviewer未运行门禁。新增20实库LOB全部inline=True，本地文本partial可能预读；不证明外置locator网络分块/取消。实库仅既有4.4.2.2双模式直连、Windows/.NET10、TLSdisabled，资源新例仅Oracle。消费者/DI/包/独立运行时/代理/企业TLS/全库套件未重跑；不覆盖历史失败。基线15.1—15.42及本报告12—37保留，23IDs/八包仍开放，本地提交后继续完整资源/故障/并发及W2验收；未push或发布。

## 39. 外置持久 LOB 读取与池生命周期验收（2026-10-25）

从master/a426623继续P0-02/W1，仅测试与文档，无生产/API/配置/依赖修改。[契约与剩余验收](obconnector-dotnet-completeness.md#518-readerlob-与-ref-cursor-的剩余验收)，[原始失败与完整证据](obconnector-dotnet-completeness.md#1544-2026-10-25-外置持久-lob-读取与池生命周期验收)。独立非池连接准备并释放临时种子，池连接普通SELECT返回实际外置BLOB100000字节/CLOB220000UTF8字节；同步/异步四项完整内容匹配，全部释放后同物理会话reset/复用且generation递增、SQL正常。四项首轮即通过，日志均inline=False，未修改数据库全局阈值。

本地十八项在peer已收到DBMS_LOB.READ、响应未返回时执行caller取消/Close/句柄或流释放/原预算超时；同端点单容量替代会话重新认证，新SQL先于ClearPool，旧资源及迟到Dispose不影响替代租约。caller原token、非unknown分类及超时预算断言保留。首轮14pass/4fail是Close错误期待OCE，源码核对后断言非unknown OceanBaseException，corrected18pass，不是生产RED。独立走查主体通过，指出pending异常诊断丢失与预算断言不足；已补异常聚合、两项回归及五秒完成上限，无延后项。reviewer未执行门禁。

最终完整单测5218pass（走查调整前5216另存）、相关双直连119pass/4配置NotExecuted、noopt9pass/99NotExecuted；Release solution0警告/错误，四文件限定格式及verify0，原workspace警告保留。原5198单测/119相关实库case保留，新20本地/4实库，原Close失败均最终同名通过。实际外置读取实库证据不等于真实中途故障/取消；完整并发/长期资源/其他版本代理TLS平台仍缺。消费者/DI/包/独立运行时/全库套件未重跑，原15.1—15.43/报告12—38及服务器失败保留，23IDs/八包继续开放。本地提交，不push/发布。

## 40. 事务响应丢失与池恢复验收（2026-10-25）

从master/4c15e3c继续P0-04/W2，无生产/API/依赖修改。[契约](obconnector-dotnet-completeness.md#521-提交回滚响应丢失与应用核对)、[完整失败和门禁](obconnector-dotnet-completeness.md#1545-2026-10-25-事务响应丢失与池恢复验收)、[应用核对指南](unknown-outcome-recovery.md)。新本地16和真实16覆盖双模式/同步异步/提交回滚/无或部分回包。测试中继先读真实服务器响应再向客户端丢弃；独立非池观察者核对已提交/已回滚结果。unknown分类、Broken、旧事务失效、单容量同端点替代、新隐式写/显式回滚及迟到Dispose保护先于Clear。无自动replay；跨会话目标次数区分正常新事务ROLLBACK。

首构建CS0266/CS1503及relay-first2失败保留；前者缺显式转换，后者C#变长十六进制转义令命令字节变0x3c，修正后2通过。原失败报告同时暴露停止监听/下一Accept竞态；独立reviewer要求仅在停止且Accept边界处理，已补两项正反回归，最终relay4通过，无延后项；不是生产修复。reviewer未运行门禁。指南补强时误用不存在的Enlist属性，final编译CS0117失败保留；改用启用异步流的BCL TransactionScope.Suppress后重新编译0警告/错误。不声称示例应用事务或协调器已验证。

最终完整单测5234pass，扩展相关套件160pass/4配置NotExecuted（其中4项为本地fixture，非实库），noopt13pass/100NotExecuted；Release solution/guide编译0警告错误，四文件格式/verify0，既有workspace警告保留。原5218单测/123相关case保留，新16unit/16real/4fixture明确区分。真实范围仍4.4.2.2双模式直连Windows/.NET10/TLSdisabled；只注入服务器响应已产生后的丢包，不外推服务器/协调器crash、真实代理/TLS或完整发送中断。消费者/DI/包/独立运行时/全库未重跑。全部23ID/八包及15.1—15.44/报告12—39历史保留，W2/完整驱动未关闭，不push/发布。

## 41. Oracle 输出执行与 FetchSize 提示（2026-10-25）

从master/a07957e继续P1-06/W5，在共享Start接受Oracle文本OUT/RETURNING、过程/函数正FetchSize提示，显式/隐式准备都适用。内部输出仍cursorFlag0并在执行响应中读取，不发FETCH、不声称输出分页；REF CURSOR reader独立指定页大小。输出offset和ExecuteArray正提示仍发送前拒绝，普通查询cursor/offset及MySQL过程原限制保留。[公共契约](obconnector-dotnet-architecture-design.md#oracle-output-fetch-hint)、[源码与完整证据](obconnector-dotnet-completeness.md#1546-2026-10-25-oracle-输出执行与-fetchsize-提示)。固定Java参考6adc311的非SELECT/internal结果区分与本实现一致，但不当作运行证据。

新24项原代码RED均命中明确拒绝；最小生产调整后26GREEN，扩展聚焦57通过。新46本地项覆盖准备所有权、返回cursor独立分页、空/INOUT、多结果/提前关闭、已知错误、取消/超时及无参数过程；五资产新增30consumer验证普通/OB20/trailing响应，保留原子输出与错误分类。新20Oracle实库验证同步异步/显式隐式准备/五命令形状、真实写入与三行cursor独立page1。完整5280unit、相关510consumer、扩展58real均通过，无opt-in13通过/101NotExecuted。构建0warning/error，七C#限定format/verify0且保留workspace警告。源码独立走查无可操作发现，reviewer未执行门禁。

实库仅4.4.2.2双直连Windows/.NET10/TLSdisabled，新增20仅Oracle；五资产消费者共用.NET10host。未重跑全量消费者/DI/包/独立运行时/完整数据库，不证明代理/企业TLS/其他部署或完整输出故障矩阵。内部输出分页、REF CURSOR输入/列值与其他P1-06形状并未实现，全部23ID/八包及15.1—15.45/本报告12—40历史和失败保留。只本地master提交，无push/发布。

## 42. 复杂类型 national 成员与方向编码（2026-10-25）

从master/a1dddde继续P1-07/W5。NCHAR/NVARCHAR2命名/嵌套成员、工厂数组和PL输出接入；复杂输入用connection，存储成员用查询确认的AL16UTF16，PL输出用结果sessionnational。实库字节证伪初始scalar假设，严格Unicode/预算/NULL/freshness/原子输出保留。[公共契约](obconnector-dotnet-architecture-design.md#complex-national-members)、[原始失败与完整证据](obconnector-dotnet-completeness.md#1547-2026-10-25-复杂类型-national-成员与方向编码)。

新增46unit/40consumer/56real（48正向+8RETURNING拒绝边界）。完整5326unit、相关180consumer通过；实库65项64pass/1fail，保留既有nested-table表列服务器拒绝，不称全绿。原16RED、初始编码12fail/4pass、expanded RETURNING8fail、consumer序号40fail、首轮完整unit3fail、后续RSA1fail、沙箱串行139fail和相关real2fail原样保留。最终完整单测在沙箱外禁用collection并行，未改RSA/TLS超时或断言；RSA专项10pass未独立复现原等待阶段失败，不能称其根因已修复。三条旧national拒绝case按新契约迁移；旧objectfixture调整DDL/load顺序后通过，未放松产品身份或freshness。

独立review修复NLS拒绝前未读EOF导致误销毁健康连接，mutation4fail验证；复核无其他可操作源码发现，reviewer未执行门禁。noopt13pass/105NotExecuted，Release0警告/错误、18C#格式/verify0保留workspace警告。其他national存储编码、无上下文匿名普通非NULL national、复杂整列RETURNING身份、LOB/cursor/cycle仍开放。五资产同一.NET10host；实库仅Oracle4.4.2.2直连/Windows/TLSdisabled/DBAL16UTF16。未跑全consumer/DI/包/独立运行时/全库。23IDs/八包及历史12—41/15.1—15.46保留，只本地master提交，不push/发布。

## 43. Closed 回调满池重开与清理资源释放（2026-10-25）

从master/80ddfbe修复P0-01/W1。Closed事件内池化Open先完成捕获清理链，再遵守原容量/reset/探活借用；同/异逻辑连接及嵌套A.Close→B.Close→A.Open均覆盖。CAS/TCS共享exactly-once清理和异常，完整mask链防递归，保留旧资源快照、提前Closed事件、外部异步回滚交错及reader generation。独立走查发现并修复scope delegate及跨await局部delegate保留旧资源，在发布共享完成前释放。[公共契约](obconnector-dotnet-architecture-design.md#pooled-close-callbacks)、[全部原始失败和最终证据](obconnector-dotnet-completeness.md#1548-2026-10-25-closed-回调中的满池重开与清理资源释放)。

最终完整5418unit、相关233consumer通过；新增92unit/40consumer/16real。原173consumer过滤漏新40，且未包含旧reader20，纠正后233，不重复旧错误覆盖声明。memory修复前实库74pass/1NotExecuted，新16通过；最终实库复验74fail/1NotExecuted，两profile端点初始greeting前关闭，独立无凭据/SQL的TcpClient读取也为0；不称最终实库通过。skip为alternateDatabase未配置。noopt相关9pass/12NotExecuted；Release0警告/错误，9C#限定format/verify0保留workspace警告。原16产品Timeout RED、fixtureEOF/隔离默认/编译失败、初次5414全量及memory4fail→3pass1fail→4pass均保留；最后4pass后按review前移共享完成发布。随后全量5417pass/1既有同步partial游标重开握手超时，原方法8+scope18重查26pass，同二进制完整复跑5418pass；未确认该超时根因，不称已修复。两条旧TLS容量测试由超时名称迁移为ClosedCallbackReopensEquivalentPoolAfterReturningOriginalSession，仍校验同socket/容量。

该专项仅4.4.2.2双模式直连/Windows/.NET10/TLSdisabled；五资产共用.NET10host。完整reset/隐式副作用/并发/长期资源、其他日志回调满池重入、代理/企业TLS/独立运行时仍开放。历史nested-table/RETURNING/RSA问题未因此关闭。未重跑全consumer/DI/包/完整实库或发布；历史12—42及基线15.1—15.47不改写，全部23ID/八工作包仍开放。本地master提交，不push。

## 44. 连接级服务器ERR与显式连接重建（2026-10-25）

从 `master/917e9cc` 开发，归属 P0-05/W2，并覆盖 P0-02/P0-04 的失败会话及 unknown 边界。固定 Java 参考仍为 Connector/J2.4.14 `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`；FailoverProxy 对 SQLSTATE 前缀08及精确70100/1927的分类提供源码依据，不意味着本驱动实现了 Java 的 failover 策略。

共同 `CommandExchange.CheckPacket` 入口将 ordinal `08*` 或精确 `1927/70100` 识别为连接级失败。先验证完整响应边界，再 Break，再 Complete(error) 释放交换；保留错误码/SQLSTATE及通用脱敏消息。普通 `1317/70100` 查询中断、`1927/HY000`、`1064/42000` 不按此规则分类为fatal，保留原清理/退休语义；干净普通命令的邻居回归仍可继续执行，脏piece或cursor错误原有销毁规则保留。失效连接不回池；旧事务 Connection=null、再次执行拒绝，显式 Close/Open 才重新获取凭据、发现地址及新租约。不自动重放、发送 Batch 后缀或迁移事务。[公共契约](obconnector-dotnet-architecture-design.md#connection-level-server-errors)。

unknown采用 `connectionFailure && 当前_sqlSent`，不用整个交换的_anySqlSent，也不限08007：代理连接错误不能证明已发送业务未执行。Prepare、业务前显式executesSql:false的目录请求和piece确认不标记业务unknown；已确认父响应后的显式非执行请求不反向污染父操作。Control隔离/autocommit、COMMIT/ROLLBACK、ChangeDatabase等保留原执行标记；FETCH不能只按opcode推断，原execution仍待完成的cursor和独立REF CURSOR交换不同。

独立走查发现Oracle piece确认的旧分支会在Break后再次ResponseComplete，覆盖可信错误为code0；改为互斥分支。另对完整裸协议ERR读完后取消的竞态，PacketChannel仅在closed、无activePayload且未启用OB20/压缩时接受已消费边界；已关闭帧协议仍保守失败，不能因Broken跳过校验。坏帧不保留不可信code/state，取消的帧协议仍按原unknown/cancellation映射处理。

| 原始报告 | 结果与证据范围 |
| --- | --- |
| fatal-server-error-red.trx / green.trx | 原始最小同步/异步2失败（Expected Broken / Actual Open）后2通过 |
| expanded.trx | 初始扩展34通过/12失败；夹具请求序列、reader预读及Batch缺少会话变量报告造成；原报告保留 |
| expanded-v3.trx | 修正夹具后58通过；expanded-v2日志中的ReceiveAsync参数编译失败保留 |
| expanded-red.trx | 用917e9cc精确生产文件复跑同58条：48失败/10通过；finally恢复当前生产字节，证明原缺陷与普通ERR/坏帧控制 |
| piece-red.trx | 独立走查发现的piece错误码丢失：2失败，Expected2013/Actual0；修正互斥清理分支 |
| cancel-red.trx | 解析前取消3失败/3通过，其中piece2已通过，另1同步API无callerToken故未真正取消；移除该无效theory行，最终仅保留有效裸取消3条 |
| focused.trx | 66通过，含后来删除的无效同步callerToken行，且不含原最小2条；不作最终总数依据 |
| unit-final.trx | 首次全量5484通过/1既有partial-cursor夹具失败，服务器tail写reset阻断替代连接accept；原报告保留 |
| recheck.trx | 67新增及原cursor八种阶段共75通过，包含夹具修正后的partial路径 |
| unit-accepted.trx | 最终完整5485通过=5418基线+新增67；旧单测不删减、不排除 |
| consumer-final.trx / consumer-accepted.trx | 原142通过/30失败均为Oracle必选探测夹具假设；改为optional-probe后同172条全部通过，产品二进制不变 |
| real-final.trx | 仅真实CommitRollbackAndDisposeSemantics双profile×同步/异步4条，全部首次Open初始greeting前失败，未执行fatalERR或事务行为 |
| noopt-final.trx | 限配置及相同事务选集9通过/2NotExecuted，不代表全量实库 |

上述表内文件前缀均为 `fatal-server-error-`，原始TRX、日志及manifest在忽略目录TestResults/fatal-server-errors，交接须另存。新增67单测=原最小2+双模式/同步异步/池开关/三种fatal码24+普通邻居6+prepare/commit/rollback/ChangeDatabase8+流式首行前后4+Batch2+OB20/压缩边界8+父响应已确认2+resolver/旧reader2+piece2+有效裸取消3+关闭帧取消4。新消费者80=五资产×双模式×同步异步×三fatal码60，加五资产×同步异步×标准/OB20的piece20；172还包含既有piece20、reader20、压缩TCP/TLS40及OB20故障12。同一.NET10host不代表五独立运行时。

首次完整单测的当前夹具根因有精确服务器栈：PooledResourceIsolationTests在partial分支读第一行后Close，服务器尚在写终止包而reset；随后无法接受替代连接，后者读greeting超时。采用RunContinuationsAsynchronously的pageSent，在服务器尾包写完后通知partial客户端再Close；客户端仍不读第二行/尾包，原资源退休/旧对象/新租约断言保留，不吞异常、不放宽超时。该证据只解释当前失败，历史15.48及更早超时仍保留原未确认结论。

最终Release0警告/错误，6C#限定format/verify通过并保留workspace加载警告。独立最终代码/测试走查未发现新可操作问题，reviewer未运行门禁；文档复核收紧普通ERR保留清理语义及显式非执行metadata边界。真实端点独立无凭据/SQL探针读8字节返回0，耗时5035ms；当前四条实库回归受阻，不把本地ERR注入称为真实服务器故障验收。未改服务器/全局配置、未增加API/选项/依赖。完整发送、并发/长期资源、真实OBProxy/TLS/认证/平台/独立运行时/协调器/包验收均未闭合；历史national、nested-table、RETURNING及RSA问题保留。全部23ID及八包开放，仅本地master提交，不push/发布。

## 45. TCP 建连重试预算与退避（2026-10-25）

**开发基线与范围。** `master/0ec3e1c`，P1-09/W4。新增 `Connect Retry Count`（默认0，0..10，额外完整候选轮）及 `Connect Retry Interval`（默认1，1..60秒，即使count0仍验证）。所有五资产均提供 builder 属性/常量、规范序列化、不可变配置及端点/凭据复制；策略进入有效池身份，省略与显式默认值等效。[公共契约](obconnector-dotnet-architecture-design.md#connect-retry-policy)。未新增依赖。

**行为。** 每次物理建连选取一次 Sequential/RoundRobin 起点；整列表 TCP 失败后才等待，轮内候选之间不等待，最多候选数×(count+1)次，最终失败不额外等待。仅 Attach 前 IOException 内含 SocketException 可重试。TCP一旦成功，greeting/TLS/auth/identity/初始化/业务错误直接返回；没有活动会话自动重连、事务迁移或SQL replay。每次逻辑Open只获取一次凭据/解析一次地址，预热及各轮复用同一快照。

**共享预算及走查修复。** count>0把逻辑Open纳入既有共享预算，包含池等待/预热/探活、各TCP及退避、握手及自动ambient初始化。因此Pool Wait Timeout即便更长或为0仍受Connection Timeout约束；count0原等待语义不变，Connection Timeout0仅取消时间上限，不取消次数上限。独立走查发现无callbacks的MinPool2原先每条物理连接重置超时；普通池/数据源×同步异步4RED均在Timeout2、两次1200ms greeting下错误成功，现共享令牌修复并检查后续显式Open恢复容量。应用同步回调仍须及时返回并响应取消。

**证据。** 最终完整单测5549通过（新增64：重试61、池身份3），相关五资产消费者174通过（新增52）；Release全解决方案及修正后消费者构建0警告/错误，7C#限定格式验证通过并保留既有workspace警告。五资产在同一.NET10host消费；无凭据/无SQL探针5022ms读0greeting，本模块没有新增实库互操作验收。

**原始失败。** 最小6RED均未知关键字；补配置后2通过/4TCP失败，随后首轮70通过、扩大113通过。保留CS1729私有端点构造误用、消费者CS0103局部Expect误用；首个相关消费者174条中162通过/12失败，均path2finally在DataSource Dispose后再次Clear，修正为自有源仅Dispose、普通连接才静态清池后同174条全通过，五个产品DLL不变；以及并行build/test导致3个MSB3026占用警告；之后顺序构建。预热4RED按预期未抛Timeout。首个125专项121通过/4失败，均新增双候选夹具在Listen前Accept；添加listening信号后保留同名用例和原断言。文档脚本首次默认GBK读取UTF8失败，没有文件写入，随后显式UTF8。沙箱首次NuGet审计NU1900，正常环境后续构建成功；不将在线依赖审计记作通过。

**边界与后续。** RR专项覆盖首次Open的全轮顺序，不声称旋转后所有起点已穷举。五资产消费者同一.NET10host，非五独立runtime。OCP/TNS或等价发现适配、健康缓存、代理拓扑/reroute、真实故障/负载/企业信任及跨平台/独立运行时/发布验收仍缺；23ID及八工作包不关闭。原有实库握手前失败保留，本模块本地TCP夹具不替代实库互操作。只本地master提交，无push或发布。

## 46. 端点TCP健康缓存（2026-10-26）

**开发基线与配置。** `master/bc84ba1`，P1-09/W4。新增 `Host Recheck Seconds` / `OceanBaseConnectionStringBuilder.HostRecheckSeconds` / `HostRecheckSecondsKey`，默认0（关闭），范围0..3600秒；五资产均保留规范序列化、不可变快照、端点/凭据复制和有效池身份。省略与显式0等效，不同值隔离普通池。[公共契约](obconnector-dotnet-architecture-design.md#endpoint-tcp-health)。没有新增依赖。

**候选与反馈。** 每次物理Open先取既有Sequential/RoundRobin起点，再按近期TCP失败稳定分组：未记录近期失败的候选在前，近期失败候选在后；所有候选和重复地址均保留，全部失败时仍可尝试恢复。顺序在本次物理Open及其重试轮次内冻结，TTL到期不会修改已选轮次。只记录Attach前IOException内含SocketException且预算未取消的失败，包括最后一次失败；TCP成功立即清除该端点记录。greeting/TLS/认证/身份/SQL错误及取消或已到期预算不污染TCP记录。它不证明服务器、租户或业务健康，也不触发活动连接迁移和SQL replay。

**容量、归属及代次。** 每个缓存最多1024个精确host/port键（ordinal，不合并大小写或DNS别名），用单调时钟并惰性过期；满时淘汰最早失败。普通非池连接按自己的selector寿命保存，非池数据源按共享selector保存；普通池的匹配借用者/预热共享池缓存，私有数据源池独立。配置、resolver/凭据/TLS策略的既有池身份隔离不变。对池缓存，Clear、凭据版本退休和Dispose替换缓存对象；非池缓存随selector存活，清池API不清其记录；旧借用者/在途尝试继续其生命周期，迟到成功或失败不能写入新代次，预留容量后再Clear再创建的竞态也与新缓存隔离。空普通池registry回收会销毁缓存，不为了保留健康记录延长空池寿命；没有进程级全局地址表或后台探测。

**回归与走查。** 公共路径覆盖同步/异步、MySQL/Oracle、同一非池连接重开、两个普通池借用者、私有池/非池数据源，以及普通池/私有源MinPoolSize2预热；覆盖TTL、显式0、全部失败后恢复、最后失败、取消、greeting/TLS/auth/identity终止边界、清池交错和策略隔离。内部单测验证稳定轮转/重复项、成功清除、已冻结顺序、1024上限、1500地址变更、4000并发操作及Clear/凭据代次。成功清除、完整轮转与预留后构造竞态主要为内部单测证据。五资产消费者验证公共选项和第二次物理建连选择，不将资产数当独立运行时数。

**验证。** 最终完整单测5620通过（新增71：健康缓存69、池身份2），相关五资产消费者226通过（新增52），均零失败/跳过；Release全解决方案0警告/错误，12C#限定格式验证通过并保留既有workspace警告。五资产同一.NET10host消费；本模块没有新增实库互操作验收。

受限首轮完整单测5480通过/140失败：109为PFX导入、5为Schannel凭据、20为VerifyFull预期不匹配、5为silent TLS等待超时、1为既有异步拓扑超时；相关消费者146通过/80失败，80项均在TestCertificateFactory的PFX导入。相同10个TLS/清池用例普通用户环境通过后，保持源码、夹具、预算和五资产二进制不变，完整单测5620通过（9分21秒）、同226消费者通过（6分31秒）。所有初次失败均同名通过，原报告不删除；拓扑超时原因尚未确认，不声称修复其根因。完整5549单测基线及174消费者基线全部保留。报告位于忽略目录TestResults/endpoint-health，执行日志为根目录TestResults-endpoint-health-*.log，应单独归档。

**保留原始失败。** 初始18项未知关键字RED；只加配置后2通过/16失败，重复Open仍先尝试失败地址；首轮84通过、扩大126通过。故意删除RetireSessions换缓存行时六个Clear/轮换用例全部失败，随后恢复原始源码，六项加八项策略隔离和一个预算边界共15项通过。原超时夹具曾假定loopback拒绝耗时，独立走查指出跨平台不成立；现从首次失败TCP计量回调开始监听并延迟1200ms，使Connection Timeout1在失败反馈前到期。该用例证明本地预算/反馈边界，不是实际网络TCP超时或Linux运行证据。首次受限全量的TLS/PFX环境失败及普通用户复测另见最终验证，不删除原报告。

**参考与后续。** Java固定参考`6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`的`loadBalanceBlacklistTimeout`默认50秒，仅作源码先例；.NET默认0并保留全候选fallback。OCP/TNS或等价发现适配、proxy拓扑/reroute、真实节点/代理故障与TCP健康策略验收、企业信任、长期负载、跨平台/独立运行时及发布矩阵仍开放。此前探针和直连报告均为历史；本模块没有新增实库互操作。23ID及八工作包不关闭；仅本地master提交，不push或发布。


## 47. TNS文件地址发现（2026-10-26）

**开发基线与接口。** `master/9dc3f06`，P1-09/W4。新增五资产公共不可变 `OceanBaseTnsAddressResolver(filePath, netServiceName, expectedServiceName = null)`，接入既有 `IOceanBaseAddressResolver`、连接 AddressResolver 与现代数据源 builder。每次逻辑 Open 重新读取文件，发现失败不得借用池内旧会话；不更改凭据、database、TLS 或既有池归属。[公共契约与示例](obconnector-dotnet-architecture-design.md#tns-file-discovery)。没有新增依赖。

**支持与限额。** 显式路径在构造时转为绝对路径；标准及外层括号 alias、逗号别名、大小写不敏感别名匹配、注释、单双引号及双写引号均支持。整个文件检查语法、重复别名与 IFILE；仅选中描述符检查支持语义。DESCRIPTION 接受直接 ADDRESS 或单个 ADDRESS_LIST，以及可选 CONNECT_DATA/SERVICE_NAME；地址仅 TCP，保留顺序/重复项及 IPv4/IPv6。严格 UTF-8（可有开头 BOM）、最多1MiB实际字节、16层描述符、4096个节点与别名合计、256地址及65536规范输出字符。未知选项、策略/权重/分组、DESCRIPTION_LIST、TCPS、重复字段及非法地址均显式失败，不静默扁平化。SERVICE_NAME 如存在，必须提供精确 ordinal 匹配的期望值；配置期望也要求文件含该字段。这只核对文件元数据，不验证真实服务器或租户身份。

**文件更新、重试与取消。** 调用方必须用完整文件替换发布；不保证原位写入的快照一致性。读取句柄允许 ReadWrite/Delete，共享更新并不保证每次并发 Open 成功。仅打开时的 FileNotFoundException、Win32共享/锁冲突0x80070020/21最多尝试5次，间隔4次25ms可取消等待；持续缺失/锁定仍失败。访问拒绝、目录不存在及读取/解码/解析失败不重试，无旧地址缓存回退。100ms是配置等待总量，不是原生文件IO的硬截止时间。异步入口将可能阻塞的打开移出调用线程，读取和发布检查取消；操作系统阻塞调用不能强制中止，既有逻辑Open守卫拒绝迟到结果。预取消/有限等待取消测试不等于全面的实际中途文件读取取消验收。

**回归与原始失败。** 保留初始缺少公共类型12RED及12GREEN、PORT附加候选注入1RED，以及地址/编码/别名/服务/精确限额、重复地址、并发替换、删除文件失败与实际Open更新回归。400k字符重复别名曾分配8,995,488字节，现提前检查计数/重复并有分配限额回归；空引号别名/键3RED后统一拒绝。File.Move覆盖在受限及普通用户环境均失败；改File.Replace后仍观察到短暂共享冲突及FileNotFound，不能归为仅沙箱问题。先补共享等待，再以缺失文件预算/取消2RED补齐同一有限打开重试，专项99项通过；其后新增的三个空名称案例纳入最终全量。Windows共享冲突用例有平台守卫，不是Linux证明。全部原始报告保留于忽略目录TestResults/tns-discovery，根目录TestResults-tns-discovery-*.log应另行归档。

**最终验证。** 最终完整单测5687通过（新增67），相关五资产消费者290通过（新增64），均零失败/跳过；Release全解决方案0警告/错误（23.62秒），5C#限定格式验证通过并保留既有workspace加载警告。单测9分13秒，消费者6分57秒；五资产在同一.NET10host运行，无新增实库互操作验收。

**边界与后续。** 五资产消费者在同一.NET10host运行，验证公共类型/构造、同步/异步及两模式下直连/普通池，现代三资产另含私有池和非池数据源；文件删除后失败、恢复为新地址后连接新服务，不把资产数当独立运行时数。本模块没有新增真实数据库、代理或网络文件系统互操作证据。OCP、完整TNS策略/分组、proxy拓扑/reroute、真实健康/故障/负载、企业信任、跨平台/独立运行时及发布验收仍开放。全部23ID及八工作包保持开放；只本地master提交，不push或发布。


## 48. OCP HTTP地址发现（2026-10-26）

**开发基线与接口。** `master/5cbe5c9`，P1-09/W4。新增五资产公共不可变 `OceanBaseOcpAddressResolver(httpClient, server, netServiceName, expectedServiceName = null)`，使用既有 AddressResolver/UseAddressResolver。调用方持有并配置并发安全的HttpClient（HTTP认证、TLS、proxy及redirect），驱动不销毁它，也不传送数据库口令。每次逻辑Open执行一次GET到固定 `/api/v2/obproxy/loadBalanceInfo?appName=<转义名称>`，请求no-cache/no-store，失败不使用旧池地址。[公共契约与示例](obconnector-dotnet-architecture-design.md#ocp-http-discovery)。无新增产品依赖。

**HTTP与信任边界。** server须为HTTP(S)origin，禁止userinfo、非根path、query与fragment；app名称非空并trim。只接受HTTP200、JSON/+json、未指定或UTF-8 charset、没有残留Content-Encoding的响应。返回RequestUri存在且不同则拒绝；调用方应禁用自动重定向，该检查不能撤销handler已跟随的请求。response/request/stream在成功、错误和取消时均释放，HttpClient保留。驱动无HTTP重试/缓存/磁盘回退；调用方handler仍控制自己的缓存/重试/transport行为。实际最多1MiB字节，最多读取上限+1，并提前拒绝过大Content-Length。Send和读取传token并在解析/发布前检查；ResponseHeadersRead下HttpClient.Timeout不单独覆盖整个body，直接调用须传取消token，连接Open继续使用既有统一预算。任意handler/stream忽略取消时不能强制中断，逻辑Open拒绝迟到结果；单次有界长字符串/空白CPU扫描不承诺即时中断。

**JSON与地址模型。** 严格UTF-8（开头BOM可选）及JSON语法，最多16层value/4096个value节点（含root、属性值、数组元素），下一key分配前检查预算；全部对象decoded key按ordinal拒绝重复，__type全局拒绝。root.data须含精确ordinal NET_SERVICE_NAME及DESCRIPTION；可选successful/success必须true、status必须数值200，其他envelope元数据只解析/限额不用于路由。data/descriptor未知成员拒绝。DESCRIPTION只接受单个ADDRESS_LIST组、可选CONNECT_DATA.SERVICE_NAME及可选OBLB=ON标记；ADDRESS数组只含TCP/HOST/PORT，PORT为十进制整数数值或数字字符串。保留地址顺序与重复，通过安全引号转换复用TNS的256地址/65536规范字符及端口/IPv6校验。SERVICE_NAME必须与显式期望精确匹配，期望也要求字段；两种名称检查均是发现元数据绑定，不确认实际服务器/租户身份。完整OCP组、权重、blacklist、显式策略及重试字段仍拒绝，不静默扁平化；.NET既有端点选择策略继续适用，未移植Java全局load-balance引擎。

**原始失败、修正与回归。** 初始公共类型12RED后12GREEN。扩展68项66通过/2失败：首个__type被平台JSON-to-XML reader隐藏成属性；精确node夹具错误少算successful/status两个value，4084改4082（4096总量）。专用JSON11项3通过/8失败、结构9项3通过/6失败，证实平台reader接受部分非法数字、缺失1/2/3闭括号及object/array尾逗号；因此放弃该平台映射，改为有界严格JSON解析。新解析87GREEN后，数字路径Take跳空白导致11项7通过/4失败（合法status后空白及三种非法number），改为Raw token后98GREEN，最终扩展101GREEN。现有一项测试对照框架JsonDocument检查313个固定/确定性随机metadata片段；这只是局部语法回归，不是所有JSON输入的形式证明。其余覆盖选定HTTP状态、媒体/UTF8/redirect、精确byte/node/depth/endpoint/canonical边界、服务/地址注入、并发32请求、Send及body取消、声明过大body不读流及释放、实际Open的错误/idle/新端点。原报告保留于忽略目录TestResults/ocp-discovery，日志为根目录TestResults-ocp-discovery-*.log，应另行归档。

**最终验证。** 最终完整单测5788通过（新增101），相关五资产消费者354通过（新增64），均零失败/跳过；Release全解决方案0警告/错误（14.57秒），5C#限定格式验证通过且无诊断。单测9分14秒，消费者7分20秒；五资产同一.NET10host运行，没有新增真实OCP/网络HTTP或实库互操作验收。

**剩余工作。** 五资产消费者在同一.NET10host验证公开API、同步/异步、双模式、直连/普通池，现代三资产加私有池和非池数据源；HTTP503时拒绝借用idle，恢复200并更新地址后实际连接第二个协议服务并执行命令。HTTP本身由受控HttpMessageHandler提供，不是实际OCP部署/网络HTTP、企业TLS、Linux或独立运行时证据。OCP/TNS完整策略/分组/权重、proxy拓扑/reroute、真实HA/负载/认证及发布矩阵仍开放。全部23ID与八工作包保留，整体驱动仍未完成；只本地master提交，不push/发布。

## 49. 发现分组、策略与权重（2026-10-26）

**范围与契约。** 从master/1416c55实现P1-09/W4的TNS/OCP分组选择，沿用既有公共resolver API。内部每次发现独立产生immutable DiscoverySnapshot，保留地址、策略、权重和组边界，经普通/私有池、预热及非池路径原样传至physical Open；public Resolve/Async仍返回声明顺序的规范membership字符串，不编码策略。自定义string-only包装不会保留内置策略。shared resolver不保存last-response字段。

组按声明顺序；description OBLB_GROUP_STRATEGY=ROTATION接受但不是轮转组。组内支持ROTATION声明顺序、RANDOM Fisher-Yates、SERVERAFFINITY正权重无放回选择，零权重按声明顺序保留fallback。description策略向组继承，组策略/ON覆盖；OBLB/LOAD_BALANCE=ON无显式策略时默认RANDOM，没有策略/ON则保留连接LoadBalance的组内行为。LOAD_BALANCE_STRATEGY与OBLB_STRATEGY等价，别名重复拒绝；组ON可覆盖继承策略为RANDOM。RoundRobin每条physical route仅取一次counter，复用于各组取模。WEIGHT仅在有效SERVERAFFINITY下合法，默认1，0..Int32.MaxValue，每组至少一个正值，64bit总和；保留重复地址。详见[公共契约](obconnector-dotnet-architecture-design.md#discovery-group-selection)。

健康偏好在策略之后、每组内部应用当前pool-generation缓存；健康零权重可以先于近期TCP失败的正权重，但后组不能跨越前组。每次物理建立仅选择一次，跨TCP重试轮冻结。只在Attach前TCP连接失败推进；TLS/认证/身份错误终止、不重放SQL。所有组合计256地址/65536规范字符及既有byte/node/depth限制保留。每次逻辑Open发现一次、在idle复用前验证；权重/组序/策略变更不主动重分配membership仍有效的idle连接，Clear后新physical才重新选择。

**原始失败与修正。** 实际Open的32项RED全部失败于旧策略拒绝，基础实现32GREEN。池健康回归4RED证实策略初稿读取selector缓存而池反馈写入generation缓存；改用会话当前缓存后通过。alias2RED证实LOAD_BALANCE_STRATEGY未支持，现支持并拒绝重复别名。新增冻结retry夹具最初8秒不足以覆盖Windows六次拒绝TCP：扩展报告252通过/2超时失败，调整夹具预算为30秒，不修改生产预算。测试开发还修正LoopbackServer不实现disposable的编译用法，改显式Stop并等待Completion。独立源码走查发现Framework逐连接new Random的同tick seed集中风险；改默认共享加锁随机源，内部CountingRandom仅用于确定性冻结retry测试，复核确认关闭。没有把统计抽样当成流量比例承诺。

旧策略拒绝测试有意演进为正/负契约：TnsAddressResolverTests.UnsupportedPoliciesProtocolsAndIncludesAreNeverIgnored映射至PoliciesProtocolsAndIncludesAreValidated；OcpAddressResolverTests.ExplicitPoliciesAndUnknownDescriptorMembersCannotBeFlattened映射至SupportedPoliciesAndUnknownDescriptorMembersAreValidated；MultipleGroupsAndAddressPoliciesCannotBeSilentlyIgnored映射至GroupShapesAndAddressPoliciesAreValidated。原参数行保留；已支持策略/多组改为声明membership断言，空组改FormatException，未知策略/权重无有效策略/协议仍拒绝。基线比对显式使用这三个名称映射，不声称旧测试名逐字保留。

消费者门禁最初筛选误包含整个Ob20ConsumerTests类；仅列举测试确认实际2587项（该类2029项而非目标TCP重试52项）后主动取消，session89911退出1且确认无遗留testhost，不计为通过。原取消日志、列表及consumer-scope-correction.json保留。纠正为精确建连重试方法后，运行前仅列举确认610项，复用同一冻结产物；不是因静默日志或观察超时而重启。

**验证。** 最终完整单测5879通过（新增91），相关五资产消费者610通过（新增256），均零失败/跳过；Release全解决方案0警告/错误（12.86秒），12C#限定格式验证退出0，保留既有workspace加载警告。单测9分40秒，精确消费者8分15秒；五资产同一.NET10host运行，没有新增真实OCP/网络HTTP、数据库或独立运行时验收。

证据在忽略目录TestResults/discovery-groups与根日志TestResults-discovery-groups-*.log，需另行归档。新增测试覆盖确定性抽样/大权重/零值/重复/覆盖/别名/跨组限额/health边界、并发32OCP响应快照、权重变更的idle保留和Clear后重建、跨retry冻结及TCP成功后失败不推进；五资产消费者覆盖双模式/同步异步/直连普通池及现代私有池/非池源，普通池和私有池MinPoolSize=2验证每次Open只发现一次的预热关联。五资产都在同一.NET10host；受控HttpMessageHandler和loopback协议服务不是实际OCP/网络HTTP、真实数据库或独立运行时证据。

**仍需完成。** 显式blacklist/发现侧retry、其他策略边界、proxy拓扑/reroute、真实HA/负载/企业认证及独立平台/运行时/包发布验收继续保留。23ID及八工作包均开放，整体目标未完成；不新增依赖/公共API/配置，不改服务器/全局状态，只本地master提交，不push/发布。

## 50. 发现黑名单与TCP尝试上限（2026-10-26）

基于master/888bd1c，P1-09/W4，详见[公共契约](obconnector-dotnet-architecture-design.md#discovery-blacklist-and-attempt-ceiling)与[完整证据](obconnector-dotnet-completeness.md#1555-2026-10-26-发现黑名单与tcp尝试上限)。description全局NORMAL/RETRYDURATION及TIMEOUT、共享实际TCP上限、eligible组内阈值重试/一次fallback/可取消退避已实现；同tick逐事件、窗口/过期probe、策略与pool-generation隔离，1024条目/16384事件有界驱逐。两字段均无保留旧行为；任一有则默认cap120/NORMAL/TIMEOUT50秒，DURATION毫秒，严格拒绝未知/畸形/重复/本地覆盖。

独立走查发现cap0与MinPoolSize补建优先级冲突；以部分预热会话被丢弃后的真实Open回归验证。明确cap0跳过补建但继续membership/expiry/probe/credential，允许现有idle；无idle零TCP失败，非零恢复预热。全局cap每条physical独立，逻辑Open期限共享；Attach后错误不推进、不重放未知写。缓存驱逐可能放行额外探测，不提供持久黑名单保证。

最终完整单测5980通过（新增101），相关五资产消费者738通过（新增128），均零失败/跳过；Release全解决方案0警告/错误（17.80秒），16C#限定格式及验证退出0，保留既有workspace加载警告。单测11分47秒，精确消费者15分30秒；五资产同一.NET10host运行，没有新增真实OCP/网络HTTP、数据库或独立运行时验收。

原始RED44/GREEN44、状态33及所有扩展失败保留；malformed公共异常包装断言与旧策略正向测试演进均记录于15.55。五资产消费者同一.NET10host覆盖双模式/同步异步/普通与私有预热/非池，非实际OCP/HTTP网络/实库/OBProxy/企业TLS/独立运行时证据。本地策略覆盖、proxy/reroute及完整真实/负载/发布验收继续开放，23ID/八工作包未整体关闭。本地master提交，无push/发布。

## 51. Oracle 二进制游标列与嵌套所有权（2026-10-26）

从master/75d3751开发P1-06/W5；详见[完整性15.56](obconnector-dotnet-completeness.md#1556-2026-10-26-oracle-二进制游标列与嵌套资源所有权)
和[公共API契约](obconnector-dotnet-architecture-design.md#oracle-cursor-valued-columns)。二进制prepare metadata中的163
转移父statement所有权，逐ID发布与未访问释放、顺序跳过、后代持有祖先和共享4096预算均有本地回归。
KeyInfo原始/重写语句、SchemaOnly、typed getter及协变数组、输出结果后普通cursor列的发布边界已有专项。
独立走查发现均已修复并保留原始失败；完整父响应结束前不能读取子cursor，Dispose活动命令仍取消，不静默改变既有语义。

**父查询分页修复。** 固定Java在prepare列含CURSOR时强制NO_CURSOR；本实现同步、异步和最终KeyInfo metadata统一将父FetchSize作为提示，execute flag0、无父FETCH，子cursor独立分页。UseCursorOffset在参数上传/目标执行前拒绝，SchemaOnly仍忽略提示。新增8正数提示/4offset回归原始12RED全部失败；修复后相关218通过。此前6044全量报告保留为pre-fetch-hint，不作为修复后验收。测试初次编译的DbCommand属性错误已修正，不计RED证据。

**验证。** 最终完整单测6056通过（新增76），相关五资产消费者561通过（新增160），均零失败/跳过；父FetchSize修复后相关218通过。
单测8分20秒，消费者1分18秒。Release全解决方案0警告/错误（14.66秒）；15C#限定格式及验证退出0，保留既有workspace加载警告。
独立源码/契约/证据走查无未处理阻塞发现；76DLL及25源码/项目哈希在两项最终门禁期间不变。
五资产消费者在同一.NET10host运行，不代表五个独立运行时；新实库入口1失败于greeting前，未执行SQL。

固定Java公共ResultSet的专门CURSOR分支提供四字节binary解码源码依据；generic codec返回null不是完整公共调用链。
新集成入口已直接运行构建DLL，oracle-direct 1失败/0跳过，Open/HandshakeFlow70在greeting前断开；未执行SQL，不能作为普通列/嵌套互操作通过。最初项目命令因NuGet导入路径未执行测试，无TRX的exit0不计通过。
本地wire与同一.NET10host上的五资产不替代真实数据库/OBProxy/TLS/平台/独立运行时或负载验收。
文本cursor列、输入方向、其他输出/复杂形状及完整验收仍缺，23ID/八工作包继续开放。原15.1—15.55/报告12—50历史保留。

## 52. 协议帧诊断（2026-10-26）

基于master/e0d1d30，P2-04/W7。新增标准DiagnosticListener OceanBaseConnector.Protocol，Frame/Error/Boundary
仅提供固定scalar只读字典；mysql/compressed/ob20共享本地ChannelId，并按listener实例区分不同程序集。
覆盖握手、普通命令、reset/probe、流式读取、同步/异步。读Frame为完整头部观察，早于校验/完整body；
写Frame为下一层stream接受，不代表flush、TCP/server交付或SQL成功。不输出SQL、payload/auth/extra-info、
用户名/端点或异常对象/文本。公共订阅、字段类型及边界详见[协议诊断契约](protocol-diagnostics.md)。

复用已安装DiagnosticSource，无新增依赖、provider公共API或配置。禁用无eventdictionary分配；回调同步、
异常隔离、恢复Activity.Current、抑制同线程递归，驱动不保存队列。创建listener的应用回调异常会使本程序集
协议诊断禁用，但不得破坏连接。观察者必须及时返回且不重入；不承诺任意应用副作用可恢复。

独立走查发现：外部Close先发生时closed/disposed guard漏报，改为独立Interlocked错误去重；缓冲续帧头部
异常在Dispose时误记incomplete，现StartFrame统一保留实际异常。4续帧RED保留；最初public cancel误接受
server同名listener事件，原4pass不作为证明，按client greeting ChannelId筛选后4全部RED。另2boundaryRED
证明非法响应边界缺少事件；增加观察但不改变原错误/连接/未读payload行为。首批6无事件RED及全部报告保留。

首轮最终全量6122通过/1失败，失败为LOB夹具把30位小数的NUMBER声明成scale0；输出行解码提前拒绝，
客户端销毁连接与server尾部EOF/OK写入竞态。原始失败及同产物4项调查通过均保留，后者不证明路径正确。
新增精确错误断言得到3通过/1失败，确认原用例走命令初始化错误而非LOB amount校验；夹具按数值写入实际
scale后，93项LOB/诊断相关回归通过。仅修正两处测试文件，不吞IO错误、不放宽产品数值校验。

夹具修正后另一轮全量6122通过/1失败，为既有DriverDiagnostics off分配断言观测98352bytes；
该Start调用不经过新协议诊断路径，相关产品/原测试未修改。同冻结产物下分配3项及完整诊断相关192项
均通过，但不足以证明该次异常分配的根因或修复；原始报告保留，零分配断言不放宽，负载/分配调查仍开放。

第三轮全量6122通过/1失败，为异步CloseCallbackScope立即GC的弱引用断言；同产物整类18项调查通过。
源码机制允许Task完成内联恢复测试时，最外层清理生产者栈尚未退出；未采集GC root栈，不能把该解释
当作已证明的具体根。测试改为5秒预算内让出执行并重复GC，保留最终弱引用断言及scope/context KeepAlive。
临时故障注入保留清理字段，4项均在预算后失败；原产品源码逐字节恢复后89项相关回归通过。产品释放逻辑
未改，故障注入不在交付源码中；该检查证明有界等待仍检出持续字段泄漏，不证明生产内存/负载预算。

最终完整单测6123通过（新增67），相关五资产消费者341通过（新增170），均零失败/跳过；LOB/诊断专项93通过，scope/诊断相关89通过及三轮各4项scope通过。
Release全解决方案0警告/错误（6.84秒）；11C#限定格式验证退出0，保留既有workspace加载警告。
独立源码/契约/证据走查无未处理阻塞发现；最终单测期间76DLL及36源码/项目哈希不变；消费者341通过使用的26项provider/hostDLL哈希与最终产物相同。
五资产消费者在同一.NET10host运行，不代表五个独立运行时；无新增实库、真实OBProxy/企业TLS、跨平台或生产负载验收。

五资产普通/压缩、双模式、同步异步和普通池Max1两次借用验证auth切换、reset、probe以及source/filter/write
回调失败；另用既有OB20公共执行fixture验证inner/outer身份相关。均在同一.NET10host；无新增真实数据库、
代理或TLS/跨平台/独立运行时验收。原15.1—15.56/报告12—51历史保留；其他资源等待/性能及完整23ID/八包
继续开放。原始TRX/log在忽略目录TestResults/protocol-diagnostics及根TestResults-protocol-diagnostics-*.log，需另行归档。


## 53. 2026-10-26 池等待通知后的取消交接

基于master/ffceec8，归属P0-01/W1。容量为1，两个调用方等待，实际取得释放通知的调用方在
下一次TakeCandidate前取消；旧EndRent只减少renters，另一调用方即使已有idle/空槽仍等待。
现租借退出时在_gate内检查idle或sessions+opening低于上限并通知下一等待者。通知不等于
领取连接；原TakeCandidate仍在同一锁内执行唯一领取/预留，不新增容量或FIFO承诺。
完整触发、源码/测试入口、[公共契约](obconnector-dotnet-architecture-design.md#pool-wait-handoff)和
证据见[完整性15.58](obconnector-dotnet-completeness.md#1558-2026-10-26-连接池通知后的取消交接)。

创建容量四组合先4RED，公共双模式/同步异步八组合先8RED；最小修复后同名12GREEN。
扩展24项单测包含失败reset、普通/私有源；64项消费者包含五资产、双模式和混合等待。
公共夹具还断言取消者Closed、原会话/单认证复用、probe/reset/两次SQL和第三Open仍受容量限制。
创建容量用例仅证明槽位交接，不将未打开的replacement描述成新TCP。独立走查确认核心修复，
指出夹具Drain短路问题；已改为同时观察两任务并用finally保证owner、池和server清理，未吞非取消错误。

默认配置完整单测6147及通知交接消费者64通过，相关982项通过，均零失败/跳过；全解决方案
Release0警告/错误（11.67秒），5C#限定格式验证退出0并保留workspace加载警告。76DLL/522源码
项目与实际最终门禁冻结值一致。修复前五资产同选择对照为568通过/64预期超时失败。

原完整632项消费者默认三轮在263/256/225项后CLR中止，crash收集261项后访问异常、仅禁PGO
374项后仍CLR中止，均不能计通过。新64项在这五轮尚无结果。转储中有ExecutionEngineException
及JIT相关线程，但无ExceptionStream，具体故障线程和根因未定。单route10和三组110通过。
同冻结资产仅测试子进程禁用分层编译时632通过；默认配置本模块64通过。它们不替代失败的
默认合并门禁，不将分层编译关联写成已修复根因；P2-05/W8继续保留，详见完整性15.58。
临时基线对照后已逐字节恢复池源码/79项build文件并核验冻结哈希，无故障注入/JIT workaround
或运行时配置改动进入产品。本地保存通知修复不等于生产发布许可。

日志/TRX/149MB转储保留TestResults/pool-wakeup-handoff及根同名前缀日志，需另行归档。
没有新增真实数据库/OBProxy、企业信任、跨平台或独立运行时验收；全部23ID/八包继续开放。

## 54. 2026-10-26 可卸载协议诊断线程状态修复

继续调查第53节的默认消费者崩溃。串行集合仍160项后失败；当前唯一ThreadStatic是诊断重入
标记。独立链接源码复现后，以实际五资产和独立子进程进一步得到5control正常/5线程退出
损坏其他ALC静态对象的RED。新回归要求旧ALC收集、退出前Marker正常、退出/GC/压力后
新ALC的16个GC-static盒仍正确；子进程隔离潜在堆损坏，超时终止并排空输出。

ProtocolDiagnostics改用ThreadLocal<bool>，保留物理线程同步重入保护，避免产品collectible
ThreadStatic槽。修改后同名10项GREEN；默认原632加新增10项共642通过、零失败/跳过
（2分37秒），未关闭分层编译/PGO/OSR。完整单测6147通过（保留此前6123及新增24，零失败/跳过，8分11秒）。
Release solution0警告/错误3.33秒；8C#格式验证退出0，既有workspace警告保留。
没有新增产品依赖、运行时配置或服务器改动。源码与子进程夹具的独立走查未发现阻断项，
最终测试报告/文档/冻结哈希已由独立只读复核核对，无剩余阻断项；reviewer未运行构建或测试。[完整证据及限制](obconnector-dotnet-completeness.md#1559-2026-10-26-可卸载诊断线程状态损坏与默认回归恢复)。

第53节及15.58原始失败、转储、原池比较和条件通过全部保留；已确认此状态损坏缺陷及默认
组合回归恢复，不把所有历史native故障逐一归为同因。五资产仍仅一个Windows/.NET10host，
其他平台、独立运行时、包、实库/OBProxy/企业信任、并发/长期负载仍开放；23ID/八包不关闭。

## 55. 2026-10-26 Batch 元数据与数据源条目创建

基于master/d7a5354，补P1-05/W5的Batch SchemaOnly/KeyInfo，复用Command元数据与同会话键目录，
保留批次预留直到reader关闭、隐藏列、懒启动/跳过后缀、异常归属/取消与CloseConnection原租约。
公共测试另确认框架私有DbBatchWrapper.CreateDbBatchCommand直接抛NotImplementedException；
DataSource现直接创建持有自有连接的原生Batch，每次执行Open，reader/scalar/nonquery关闭归还，
失败/Dispose清理；保留外部Connection/Transaction/Prepare拒绝及源timeout/ambient契约。
没有新增包装类、公共类型、依赖或运行时配置；普通连接所有权保持原路径。

新增51单位及72公共消费者；受影响单位269、消费者162均零失败/跳过。
初始12NotSupported RED、消费者36通过/36源创建失败及后续夹具259/2、265/4、138/24保留。
夹具修正分别针对未入集合条目、tracked时区报告、销毁源会话的最后pending close；不放宽驱动校验。
消费者失败finally已修为取消后await服务器，保留非取消错误。独立只读源码走查无剩余产品阻断项；
完整单位6198通过（新增51，8分7秒），Release0警告/错误2.39秒；4C#格式验证退出0并保留workspace警告。
524源码/项目及1266Release DLL哈希未变；最终证据/文档及冻结哈希已独立只读复核，无剩余阻断项；reviewer未运行构建/测试。完整触发、测试入口、门禁与边界见[完整性15.60](obconnector-dotnet-completeness.md#1560-2026-10-26-batch-元数据与数据源条目创建)及[公共契约](obconnector-dotnet-architecture-design.md#batch-metadata)。

三现代资产在同一Windows/.NET10host运行，不是三个独立运行时；没有新增实库、真实OBProxy、
企业TLS、跨平台或长期负载验收。Batch SingleRow/SingleResult、其他命令/游标组合和23ID/八包继续开放。
