# MySQL 过程与函数的 FetchSize 提示（2026-10-28）

## 范围与依据

P1-05/W5：`CommandType.StoredProcedure` 的 MySQL 过程及单 ReturnValue 函数
接受正命令 `FetchSize`。提示值保留，CALL/SET 的有效游标 fetchSize 为零；
普通结果沿既有路径流式读取，内部输出 SELECT 不分页、不公开为结果集。
不新增过程结果游标分页，SchemaOnly/KeyInfo、UseCursorOffset、INOUT stream
及不兼容参数方向继续拒绝。

官方 Connector/J 2.4.14 的固定源码提交
`6adc311ba5db8b2f19f98f3e6e4734667fdfdda1` 中，
`JDBC4ServerCallableStatement.execute` 在 MySQL 模式使用 `setInOutParamsOnServer`
并单独获取输出；`JDBC4CallableStatement` 的函数路径也保留 fetch 提示。
本机官方 jar 的显式实库对照在 `useServerPrepStmts=true`、`useCursorFetch=true`
时验证了提示 1/Int32.MaxValue：过程两组实际结果 7/9、OUT=8、INOUT 后缀，
函数返回 42，提示不变，后续 SELECT 1 可复用连接。对象在 finally 中清理，
连接信息从已有 user-secrets 经 stdin 传入，不记录实际连接信息。

## 实施与验证步骤（已完成）

1. 在已有协议夹具上增加正提示、多结果/提前关闭、原子输出、错误及函数回归，
   先确认原代码的明确拒绝，再修正共享入口。
2. 只移除正提示拒绝，将 MySQL routine 的有效 fetchSize 归零；保留能力门禁。
3. 复用公开 DLL 消费和显式实库夹具，覆盖 Prepare、重复执行、输出及连接复用。
4. 更新公共契约与矩阵，构建、完整默认单测、相关消费者及实库验收，走查后提交。

实库与 TLS/证书测试不加入默认 CI。Java 对照记录位于 ignored
`TestResults/java-gap-probe/mysql-routine-fetch-hint.log`。

## 当前验证证据

- 新增正常路径 36 项首先全部因原入口的明确 FetchSize 限制而失败；
  修正后过程/函数相关单测 100/100，含新增正提示 42 项及原零提示覆盖。
  首次测试夹具编译时 DbCommand 转换错误已修正，不计入功能 RED。
- 五资产主解决方案及独立 SecurityTests 的 Release 构建均零警告/错误。
  SecurityTests 只构建，未执行证书/TLS；共享消费者保留原安全矩阵入口。
- 公开 DLL 消费者 48/48，新增 24 项正提示；代表性三资产、同步/异步、
  普通协议/OB20、两种 EOF 行为，验证 CALL 不请求游标及关闭时输出收集。
- MySQL 显式直连过程/函数组 30/30，新增正提示 24 项，准备/重复执行、
  完整结果/提前关闭、OUT/INOUT/ReturnValue、提示不变及后续查询均通过。
- DI 14/14；默认未启用实库时 22 项配置通过、149 项数据库测试跳过。
- 最终完整默认单测 8,053/8,053，零失败/跳过，耗时 8 分 19 秒；
  在普通用户上下文运行，保留 TNS 文件替换用例。
- 独立最终走查无待修发现；同步/异步、准备及输出完成路径均已复核。

本次测试记录位于 ignored `TestResults/mysql-routine-fetch-hint/`。
当前证据不证明真实 OB20、OBProxy、TLS、其他版本或独立运行时矩阵。
