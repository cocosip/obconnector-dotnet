# 多语句时间参数输入

日期：2026-10-28

## 交付

`Allow Multi Statements=true` 的文本客户端参数化现在支持已有时间参数编码契约中的 MySQL `DATE`、`DATETIME`、`TIMESTAMP`、`TIME`，以及 Oracle `DATE` 和 `TIMESTAMP`。实现复用 `PreparedParameters.Encode(..., validateOnly: true)` 的模式、类型、范围和精度校验，再生成模式对应的 SQL 字面量；Oracle DATE 使用 `TO_DATE`，Oracle TIMESTAMP 使用 `TIMESTAMP` 字面量。Oracle 多语句路径现在正确传入多语句标志。

时区时间、LOB、INTERVAL、stream、复杂类型、输出参数和 Prepare/cursor 仍按矩阵契约拒绝。

## 验证

- `ClientPreparedSqlTests.MultiStatementTemporalLiteralsUseModeSpecificSql`：MySQL/Oracle 字面量、精度和负 TIME。
- `TextCommandTests.MySqlTemporalMultiStatementsUseTextLiterals`：同步/异步 MySQL 文本执行。
- `TextCommandTests.OracleTemporalMultiStatementsUseDateAndTimestampLiterals`：同步/异步 Oracle 文本执行。
- 上述 5 项针对性测试通过；产品五目标 Release 构建通过。完整单测复核仍受既有 loopback 夹具时序失败影响，未将其计为本功能失败。
