# 2026-10-28 MySQL 函数 ReturnValue acceptance

在现有 MySQL 函数 ReturnValue integration 覆盖之外，acceptance console 增加了公开 ADO.NET
路径的同步/异步两项：随机创建函数，使用 `CommandType.StoredProcedure`、一个
`ParameterDirection.ReturnValue` 和一个 Input 参数执行，断言结果为 42，最后删除函数。

## 结果

- 本地 `AcceptanceRunnerTests.CatalogHasUniqueNamesAndCoversEveryGroup`：1/1 通过；
- 真实 OceanBase 4.4.2.2 MySQL 直连，Windows，`.NET 11.0.100-rc.3` SDK + `.NET 10.0.14`
  runtime，TLS Disabled：`routines` 同步/异步 2/2 通过；
- 对象创建成功后才登记清理，报告 `CleanupFailures=0`。

Oracle 模式没有新增该 case；函数协议范围仍以 MySQL Connector/J 参考和已有实现为准。
函数与 Input/Output 混用、FetchSize、SchemaOnly/KeyInfo 等边界没有扩大。
