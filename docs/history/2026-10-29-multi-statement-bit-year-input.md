# MySQL 多语句 BIT/YEAR 输入（2026-10-29）

## OceanBase / 官方客户端依据

对照 OceanBase Connector/J 2.4.14、源码 `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`：
`BigDecimalParameter.writeTo` 使用 `toPlainString()`，`ShortParameter.writeTo` 使用
`String.valueOf(value)` 写入数值文本，没有要求新的 BIT/YEAR CAST 或原生数据库专属语法。

OceanBase 4.4.2.2 MySQL 直连 Java 探针实际使用 `ClientSidePreparedStatement`。
三组样本覆盖 BIT(1)/(9)/(64) 的 0、1、511、2^63、UInt64 最大值，及 YEAR 的
0、1901、2155；重复两次插入后再插入 SQL NULL，更新计数为 3，独立连接读取存储字节和年份均精确。
Java 第三行是 SQL NULL，不是绑定 typed NULL；绑定 typed NULL 由下述 .NET 测试覆盖。
BIT(1) 目标列接收值 2 时返回 `1406/22001`，已确认前缀在事务内可见、后缀未执行，
独立连接不可见，回滚后为空。探针创建的表已通过独立连接清理。

## 实现与契约

- 仅将显式 `OceanBaseDbType.Bit` / `Year` 加入 opt-in 文本多语句的 provider 白名单。
  复用已有 UInt64/UInt16 精确数值字面量，不经过浮点或有符号收窄。
- 继续复用 `PreparedParameters.Encode` 发送前校验：仅 MySQL；BIT 值必须为 UInt64，
  Size=0 表示 64 位，或明确 1～64 位，Precision/Scale 为 0；YEAR 值必须为 UInt16，
  仅 0 或 1901～2155，Precision 为 0/4，Scale 为 0。typed NULL 也校验声明。
- 表达式保持数值表达式；目标 BIT/YEAR 列负责转换。不能承诺 `SELECT ?` 表达式具有
  BIT/YEAR 元数据。普通单语句 client fallback 仍拒绝这两个显式 provider 类型。
- 不需要字符串 literal-state 查询；保留参数快照、展开 SQL 包预算、opt-in、Prepare/cursor/
  输出/复杂输入拒绝，以及失败、事务和不重放契约。没有新增 API、配置或依赖。

## 测试与走查

- 正式单测 RED：16 失败 / 4 通过 / 20 总数，0 跳过；12 项字面量及 4 项执行正例均命中
  原 provider 白名单拒绝；既有模式/fallback 及非法值/声明拒绝检查通过。
- GREEN 专项单测 20/20；公开 DLL 新消费者 12/12，覆盖三类代表资产、同步/异步及
  TCP/合成 OB20，检查精确文本、typed NULL、非法值发送前拒绝、更新计数及连接复用。
  扩大多语句消费者回归 132/132，零失败/跳过。
- 新增 MySQL 实库 28/28：24 项为六组样本 × 同步/异步 × buffered/sequential，独立连接
  分别按文本及 prepared 路径读取 BIT/YEAR 列元数据及完整值，再读取两个数值表达式结果集。
  另 2 项验证非法值/typed-NULL 声明不执行脚本前缀，2 项验证严格模式下目标 BIT(1) 溢出的
  已确认前缀/未执行后缀、连接复用、独立不可见和回滚。
- 测试编写期间先修正 UInt64 不能传入 signed-long 断言的编译错误；首轮实库 4 通过/24 失败，
  失败均为存储核对后对数值表达式使用严格 `GetDecimal` 的测试转换错误。改用现有
  OceanBaseDecimal/`Convert.ToDecimal` 精确断言模式后 28/28；没有改变产品 getter 契约。
- 最终门禁：完整默认单测 8445/8445（8 分 21 秒）、扩大相关单测 5519/5519、
  公开 DLL 多语句消费者 132/132、双模式多语句/流实库回归 361/361（本模块新增 28 项）、
  DI 14/14，均零失败/跳过。完整单测在沙箱外执行，避开已确认的 Windows File.Replace 沙箱限制；
  不启用实库或 SecurityTests。
- 独立最终走查无 Critical/Important/Minor 遗留。五目标解决方案最终顺序 Release 构建
  零警告/错误。此前与完整单测重叠的重建曾因 xUnit DLL 文件锁报 MSB3027/MSB3021；
  等原单测进程成功结束后顺序重建通过，没有重启单测或修改产品代码掩盖文件锁。

本地证据位于 ignored `TestResults/multi-statement-bit-year/`；Java 探针及脱敏日志位于
ignored `TestResults/java-gap-probe/MySqlBitYearProbe.java`、`RunMySqlBitYear.ps1`、
`mysql-bit-year.log`。凭据使用现有 user-secrets/stdin，不写入源码、文档或输出。

## 未关闭范围

新增能力仅 MySQL；实库证据限定 Windows/.NET 10、OceanBase 4.4.2.2 直连、TLS Disabled。
合成 OB20 不代表真实 OB20/OBProxy 验收。其他版本/平台、真实 TLS/auth/代理、更多目标转换、
故障及资源矩阵仍开放；P1-01 与完整缺口矩阵不因本模块关闭。
