# 2026-10-28 Command/Batch 结果限制直连验收

本次为 P1-05 增加 acceptance console 结果矩阵，不改变 `OceanBaseCommand` 或 `OceanBaseBatch`
的协议实现。每个模式分别执行一个包含两行结果的第一项和一个后续批次项：

- `SingleRow` 只暴露第一结果的第一行，并隐藏后续行和批次项；
- `SingleResult` 保留第一结果的后续行，但隐藏后续批次项；
- 两种行为均在同步/异步路径完成排空，随后在同一连接执行 `SELECT 1`。

## 结果

- 本地 acceptance catalog 回归：1/1 通过；
- MySQL 直连 OceanBase 4.4.2.2：`batch` 组 3/3 通过；
- Oracle 直连 OceanBase 4.4.2.2：`batch` 组 3/3 通过；
- 合计新增双模式同步/异步结果限制 4 项，清理失败 0。

运行使用 `.NET 11.0.100-rc.3` SDK、`.NET 10.0.14` runtime、TLS Disabled；连接信息仅通过
进程环境变量提供，没有写入仓库或报告。
