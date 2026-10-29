# 范围依据与清理决策

核对日期：2026-10-29。.NET 基线 `d4d5685`；Java Connector/J **2.4.14**，固定提交
`6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`。本次在线读取 OceanBase 官方文档仓库
V4.3.5，固定提交 `2a5ae3a23a51729d065cec716508eb26cb064106`。文档版本与历史实库
4.4.2.2 是两个维度，不能相互替代，也不代表支持所有版本。

## 范围规则

1. 产品是一个 OceanBase ADO.NET provider，支持 MySQL/Oracle **租户兼容模式**，不追求原生数据库全功能。认证后的状态确定模式，配置只验证期望，不改变租户。
2. 官方文档说明服务端能力；Java 源码说明参考客户端路径；本地源码说明 .NET 实现；实库报告决定具体部署是否验收。四者不能互相替代。
3. JDBC/ADO.NET 按语义适配。Java 没有 DataAdapter/DbBatch/DiagnosticListener 不是删除理由；Java 有 NClob/SQLXML 方法或 MariaDB 选项也不是 OceanBase 支持证明。
4. 连接、命令、参数、reader、精确类型、本地事务、错误和资源安全是核心；池/缓存/批处理、LOB/复杂类型/XA、发现/协议扩展保留有依据的可选子集。
5. SQL/DDL/PL包、权限、备份、复制、分区、优化器是服务端能力。驱动负责发送、绑定、解码和返回错误，不给每项语法增加模拟实现或专用 API。
6. 未配置 TLS、OB20 等属于欠验收；确定拒绝的组合保留校验。不能通过删测试、改 skip、降安全等级来关闭缺口。

## 官方文档依据

以下为 OceanBase 官方仓库原文，本次核对使用以上固定提交。文档并非完整的4.4.2.2实库支持证明。

| ID | 文档 | 范围约束 |
| --- | --- | --- |
| O1 | [兼容模式][O1] | 租户模式不可切换，社区版仅 MySQL |
| O2 | [MySQL 兼容性对比][O2] | 类型、视图、语法有差异；版本相关数组/向量/位图不能默认列已支持 |
| O3 | [Oracle 兼容性概述][O3] | SQL/PL/目录/对象支持有边界，服务端管理功能不等于驱动 API |
| O4 | [Oracle SQL 类型][O4] | LONG、LONG RAW、BFILE、NCLOB 明确不支持；ROWID/UROWID、BLOB/CLOB、精确时间/区间有效 |
| O5 | [Connector/J][O5] | 一个驱动识别两种模式，共享协议，主要提供连接/执行/调用/事务 |
| O6 | [XA 事务][O6] | OceanBase 是资源管理器；全局决定和恢复由应用/事务管理器负责 |
| O7 | [SSL 配置][O7] | TLS 是需服务端配置和证书的部署能力，当前未开不等于不存在 |
| O8 | [XMLType][O8] / [SDO_GEOMETRY][O9] / [ANYDATA][O10] | 服务端专用类型有独立限制，不自动承诺 .NET 专用 codec；ANYDATA 不可当普通持久列 |

## Java 源码依据

路径相对固定版本 [obconnector-j][J0] 的 `src/main/java/com/oceanbase/jdbc/`。结构解读见
[Java 参考分析](obconnector-j-architecture-outline.md)，它不是 .NET 验收证明。

| ID | 路径 / 行号 | 核实事实 |
| --- | --- | --- |
| J1 | `internal/protocol/AbstractConnectProtocol.java:823–825,908–926,1778–1785` | 认证识别模式；压缩/OB20选择；PrepareExecute与offset有模式/选项/版本条件 |
| J2 | `OceanBaseConnection.java:776–801,1614–1615`；`BasePrepareStatement.java:972–974,1034–1035` | 共享执行；SQLXML明确拒绝；NClob setter只是Clob别名 |
| J3 | `internal/ColumnType.java:69–118`；`internal/com/read/resultset/rowprotocol/BinaryRowProtocol.java:1608–1611` | 模式类型；ROWID字符串、MySQL GEOMETRY原始byte[] |
| J4 | `JDBC4ServerPreparedStatement.java:819–832`；`JDBC4ServerCallableStatement.java:206–232,284–303` | Oracle数组绑定有门槛；标量OUT/INOUT有效，不等于REF CURSOR输入有效 |
| J5 | `util/DefaultOptions.java:470–490,553–560`；`internal/protocol/AbstractQueryProtocol.java:729–732,1287–1326` | multi-send确有实现；bulk说明针对MariaDB且执行点注明服务端不支持 |
| J6 | `extend/datatype/ComplexUtil.java:126–140,190–240`；`BinaryRowProtocol.java:2621,2632` | LOB读侧不等于writer支持；复杂输入只支持具体成员类型 |
| J7 | `internal/io/input/Ob20PacketInputStream.java:228–238`；`internal/protocol/OceanBaseCapabilityFlag.java:102` | 此基线拒绝压缩OB20；reroute常量不代表执行实现 |
| J8 | `OceanBaseXaResource.java:162–180,258–287,299–312,346–396` | MySQL XA/Oracle DBMS_XA；Java自身forget/timeout/joins亦有边界 |
| J9 | `credential/CredentialPlugin.java:52–68`；`AbstractConnectProtocol.java:553–555,593–595`；认证SPI注册 | 建连取凭据不是活动连接实时轮换；客户端插件存在不证明两模式服务器支持 |
| J10 | `internal/util/constant/HaMode.java:53–58`；`internal/protocol/AuroraProtocol.java:83–86` | 继承Aurora/Galera机制不属于OceanBase必需范围 |
| J11 | `OceanBasePoolDataSource.java`、`internal/util/pool/`、`internal/failover/`；`UrlParser.java:280–291` | 池/reset与有限failover、TNS/OCP部署入口；不是透明活动会话迁移 |
| J12 | `util/DefaultOptions.java:189,258,401–428,548–551`、`internal/protocol/tls/` | TLS/keystore/CA/hostname配置有实现，仍须实际部署验收 |
| J13 | `internal/protocol/AbstractQueryProtocol.java:2390–2433` | LOCAL INFILE有应用流/授权及批处理限制 |

纠正旧结论：**multi-send是Java已有的可选优化，.NET缺失，不是无依据功能**。
MariaDB `COM_STMT_BULK_EXECUTE` 则不进入当前目标，两者不能合并排除。
Java没有同名DataAdapter不能证明其无用；它维持已移除状态是当前产品选择显式命令的范围决定。

## 清理计划与结果范围

先整理能力、缺失和验收文档，再清理源码及所有调用方/测试，最后构建五资产、默认单测、DI、消费者和文档检查。
保持当前分支，不提交、不推送；本次不补开发缺失功能。

| 动作 | 范围 | 原因与兼容性 |
| --- | --- | --- |
| 删除A01公开演示类型 | `TargetSurface.cs` 的 `OceanBaseModernApis`、`OceanBaseTargetSurface` | 空类/条件常量仅供脚手架测试，实际DataSource/Batch已有；外部引用者须移除调用，这是public API删除 |
| 同步资产测试 | 删除常量单测，修改TargetSurfaceIsolationTests及netstandard sample | 保留并加强真实DataSource/Batch/factory边界验证 |
| 删除零引用方法 | AuthSwitchRequest.IsAuthSwitch、ByteCursor.SkipLengthEncodedInteger、HandshakeResponsePacket.BuildAuthSwitchResponse | 全仓库无调用；实际认证/读取实现保留 |
| 删除零引用UTF-8快捷属性 | MySqlInsertRewrite.TupleBytes/BaseBytes | 活动批处理已用会话编码的GetTupleBytes/GetBaseBytes |
| 修正验收负例的测试收尾 | PackageLiveConsumerTests | 已验证损坏行导致提前关闭；仅负例成功断言后容许两种预期socket断连，不改变产品行为 |
| 保留测试专用入口 | PhysicalSessionPool.TryRent | 测试用其验证预热/销毁/所有权，不是零引用 |
| 保留实际可选功能与拒绝保护 | TLS/认证、OB20、LOB/复杂类型、XA、发现、池、unknown outcome | 均有实际实现；不能据未验收整块删除 |

未发现产品源码TODO/FIXME/NotImplementedException式空实现。NotSupportedException主要是合法的模式、方向、协议和安全边界。
组合缺口在[缺失矩阵](obconnector-dotnet-completeness.md)明示，不能宣称全部补齐。

## 文档收敛

[文档目录](README.md)列当前唯一入口。旧23项/八包退为历史索引，不再当产品需求或完成率。
`history/README.md`只保留一份精简索引；重复阶段快照、delivery-status和pause-checkpoint入口已删除。
历史安全套件未完整执行记录及真实TLS欠验收继续保留在[兼容性](compatibility.md)和[缺失矩阵](obconnector-dotnet-completeness.md)中。

[O1]: https://github.com/oceanbase/oceanbase-doc/blob/2a5ae3a23a51729d065cec716508eb26cb064106/zh-CN/700.reference/100.oceanbase-database-concepts/300.multi-tenant-architecture/200.compatibility-mode.md
[O2]: https://github.com/oceanbase/oceanbase-doc/blob/2a5ae3a23a51729d065cec716508eb26cb064106/zh-CN/100.learn-more-about-oceanbase/500.compatibility-with-mysql.md
[O3]: https://github.com/oceanbase/oceanbase-doc/blob/2a5ae3a23a51729d065cec716508eb26cb064106/zh-CN/100.learn-more-about-oceanbase/400.compatibility-with-oracle/100.overview-of-compatibility-with-oracle.md
[O4]: https://github.com/oceanbase/oceanbase-doc/blob/2a5ae3a23a51729d065cec716508eb26cb064106/zh-CN/100.learn-more-about-oceanbase/400.compatibility-with-oracle/200.sql-data-type.md
[O5]: https://github.com/oceanbase/oceanbase-doc/blob/2a5ae3a23a51729d065cec716508eb26cb064106/zh-CN/700.reference/1300.drivers/500.oceanbase-connector-j.md
[O6]: https://github.com/oceanbase/oceanbase-doc/blob/2a5ae3a23a51729d065cec716508eb26cb064106/zh-CN/700.reference/100.oceanbase-database-concepts/800.transaction-management/100.transaction/900.xa-transactions.md
[O7]: https://github.com/oceanbase/oceanbase-doc/blob/2a5ae3a23a51729d065cec716508eb26cb064106/zh-CN/700.reference/800.configuration-items-and-system-variables/100.system-configuration-items/300.cluster-level-configuration-items/20800.ssl_client_authentication.md
[O8]: https://github.com/oceanbase/oceanbase-doc/blob/2a5ae3a23a51729d065cec716508eb26cb064106/zh-CN/700.reference/500.sql-reference/100.sql-syntax/300.common-tenant-of-oracle-mode/300.basic-elements-of-oracle-mode/100.built-in-data-types-of-oracle-mode/1100.xml-date-type-of-oracle-mode/100.xml-data-type-overview-of-oracle-mode.md
[O9]: https://github.com/oceanbase/oceanbase-doc/blob/2a5ae3a23a51729d065cec716508eb26cb064106/zh-CN/700.reference/500.sql-reference/100.sql-syntax/300.common-tenant-of-oracle-mode/300.basic-elements-of-oracle-mode/100.built-in-data-types-of-oracle-mode/1200.spatial-data-type-of-oracle-mode/100.spatial-data-type-overview-of-oracle-mode.md
[O10]: https://github.com/oceanbase/oceanbase-doc/blob/2a5ae3a23a51729d065cec716508eb26cb064106/zh-CN/700.reference/600.pl-reference/300.pl-oracle/1400.pl-system-package-oracle/27800.anydata-type-oracle/100.anydata-type-overview-oracle.md
[J0]: https://github.com/oceanbase/obconnector-j/tree/6adc311ba5db8b2f19f98f3e6e4734667fdfdda1
