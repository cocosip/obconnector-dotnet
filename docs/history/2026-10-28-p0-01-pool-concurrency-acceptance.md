# 2026-10-28 P0-01 连接池受控并发直连验收

本次为 P0-01 增加最小真实并发矩阵，不修改连接池协议或容量实现。每个兼容模式创建一个
私有 `OceanBaseDataSource`，设置 `MaxPoolSize=2`，启动 4 个 worker；每个 worker 重复 3 次
打开逻辑连接、执行只读 `SELECT 1`、关闭连接。同步和异步路径分别执行。

## 结果

- 本地 acceptance catalog 回归：1/1 通过；
- MySQL 直连 OceanBase 4.4.2.2：`connection` 组 5/5 通过；
- Oracle 直连 OceanBase 4.4.2.2：`connection` 组 5/5 通过；
- 新增同步/异步池并发 4 项均通过，清理失败 0。

运行使用 `.NET 11.0.100-rc.3` SDK、`.NET 10.0.14` runtime、TLS Disabled；连接信息仅通过
进程环境变量提供，没有写入仓库或报告。该结果只覆盖有界短时并发，不替代长期负载、
Close/Open 与 Dispose/reset 交错、真实代理和跨平台验收。
