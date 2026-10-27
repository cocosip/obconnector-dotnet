# 2026-10-28 Schema 直连验收

本次针对 P1-10 Schema 目录和 P1-05 相关目录行为，在用户提供的 OceanBase 直连环境上复核。
连接信息只通过当前进程环境变量注入，未写入仓库、报告或文档。

## 运行边界

- SDK：`.NET 11.0.100-rc.3`；目标框架：`net10.0`；运行时：`.NET 10.0.14`。
- profile：MySQL 直连和 Oracle 直连，TLS Disabled，`createObjects=true`。
- Oracle profile 额外启用 `Use Oracle Prepare Execute=true` 和 `Use Lob Locators=true`。
- 连接串不要同时包含 `SslMode` 和 profile 的 `tlsMode`：二者是同一规范键的别名，合并时会按设计拒绝冲突；单独连接串仍可使用任一别名。

## 结果

| 范围 | 结果 | 说明 |
| --- | --- | --- |
| Release 全解决方案构建 | 通过，0 警告 / 0 错误 | 使用 .NET 11 SDK 构建 net10.0，不需要 .NET 10 SDK |
| Schema integration（双模式） | 10 通过，2 失败 | 通过项覆盖集合形状、表/列、未知对象、键/索引/FK、MySQL 与 Oracle 目录路径 |
| Oracle `CREATE VIEW` | 服务端拒绝 `1031/HY000` | 不是客户端 Schema 查询失败；当前租户不能用该 DDL 建立验收对象 |
| Oracle `CREATE SYNONYM` | 服务端拒绝 `1031/HY000` | 同上，未增加客户端 workaround |
| Oracle Schema acceptance console | 2 通过，0 失败 | 集合和建表/列目录路径通过 |

最初的失败表现为 `DROP VIEW`/`DROP SYNONYM` 清理失败，原因是测试沙箱在 DDL 成功前就登记了清理动作。
最小修正是让 `DatabaseContext` 支持延迟登记，Schema 用例只在 `CREATE` 成功后登记清理。
修正后失败点回到实际 `CREATE` 行，清理失败不再遮蔽服务端原因。独立探针还确认不存在对象的清理错误分别为
`942/42S02` 和 `600/HY000`。

## 当前结论

双模式 Schema 目录实现本身已经有真实直连证据；本次 10 个通过项可计入 P1-10/P1-05 的实库进度。
Oracle Views/Synonyms 的 DDL 互操作仍受该 OceanBase 租户能力限制，不能据此判定驱动目录实现缺陷，也不能把两个用例记为通过。
权限、大小写、版本、OBProxy 以及其他真实目录矩阵仍未关闭。

## 2026-10-28 CommandBehavior 元数据补验

在同一双模式直连 profile 上新增并运行 `MetadataBehaviorTests`，共 4 项通过：

- MySQL/Oracle 各一项 `SchemaOnly`：准备结果元数据但不上传输入流，流位置保持为 0；
- MySQL/Oracle 各一项 `KeyInfo`：投影不含主键时追加隐藏主键列，`VisibleFieldCount` 保持原结果形状。

补验首先在旧代码下复现 MySQL `KeyInfo` 缺陷：OceanBase MySQL 目录返回小写表名，而查询结果元数据保留大写对象名，大小写敏感匹配导致隐藏键列不追加。修复为仅在 MySQL 模式对 schema/table 来源标识使用不区分大小写匹配；Oracle 仍保持大小写敏感。受影响 KeyInfo 单测 97 项通过，Release 全解决方案构建 0 警告/0 错误。

这组用例证明了当前两个直连租户上的基本准备/目录路径，不关闭权限、目录锁、副作用、组合键/唯一键、版本或 OBProxy 矩阵；SchemaOnly 的真实目标 SQL 不执行仍以协议路径和本地回归为准，当前实库用例直接验证了不上传输入参数。
