# 2026-10-28 过程参数推导直连验收

本次补齐 P1-10 自动参数推导的真实 MySQL 过程签名证据，并复跑现有 Oracle FLOAT 推导用例。连接信息只通过当前进程环境变量注入，未写入仓库、报告或文档。

## 运行边界

- SDK：`.NET 11.0.100-rc.3`；目标框架：`net10.0`；运行时：`.NET 10.0.14`。
- profile：MySQL 直连和 Oracle 直连，TLS Disabled，`createObjects=true`。
- Oracle profile 额外启用 `Use Oracle Prepare Execute=true` 和 `Use Lob Locators=true`。

## 结果

| 范围 | 结果 | 说明 |
| --- | --- | --- |
| MySQL `DeriveParameters` | 2/2 通过 | 同步/异步；真实创建并清理过程，验证 IN、OUT、INOUT 的顺序、方向、INT/VARCHAR 映射、长度和 DBNull 初值 |
| Oracle FLOAT/NUMBER 推导 | 4/4 通过 | 现有同步/异步、过程/函数矩阵；验证输入/输出/INOUT、函数返回值和精确绑定 |

本次没有修改过程推导协议或扩大函数/过程执行范围；真实测试只验证目录读取和参数发布。过程对象均使用随机 `OBI_` 名称，并在创建成功后登记清理。

## 当前结论

MySQL 和 Oracle 两个直连租户的基本过程目录/参数推导路径均有真实证据。权限、重载/包、版本、目录差异、OBProxy 和其他插件环境仍未关闭；不把直连通过扩大为完整 P1-10 或发布兼容性结论。
