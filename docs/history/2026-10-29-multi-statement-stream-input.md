# 多语句二进制流输入（2026-10-29）

## OceanBase / 官方客户端依据

对照 OceanBase Connector/J 2.4.14、源码 `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`：
`StreamParameter.writeTo` 在文本路径写 binary introducer 及源流转义字节；
`OBStreamParameter.writeTo` 在 Oracle 文本路径写 quoted hex，而非调用 binary `writeBinary`。

既有 Java 探针在两个模式各通过 known/unknown length × 0/256/1024 共 6 项，实际使用
`ClientSidePreparedStatement`、一次执行两条带参数 INSERT、两个更新计数及独立连接验证存储。
本轮 Oracle BLOB 探针另通过 known/unknown length × 0/2000/2001/4000/32767/65536 共 12 项。
这证明 2000 字节不是 Oracle 流文本输入的通用边界，不据原生 Oracle/MySQL 全功能推测实现。

## 实现与契约

- 仅扩展 opt-in 文本多语句的二进制 Input 流。复用既有 DbType/可读性/Size/Precision/Scale 校验，
  参数快照及 Stream 引用身份约束；同一个源不能占两个绑定位置，Oracle 重复名称也一样。
- 先用 NULL 占位验证全部 SQL/参数和非流字面量，再从当前位置以最多 8192 字节读取到 EOF。
  同步调用 Read，异步调用带命令预算 token 的 ReadAsync；复用源流诊断计时，不使用 Task.Run。
- MySQL 使用 X'HEX'，不会因脚本中的 SQL_MODE 切换改变字节。Oracle 使用官方 Java 的 'HEX'，
  由 RAW/BLOB 目标做隐式转换；用于表达式时保留 quoted-hex 文本形状，不承诺等同 byte[] 的
  HEXTORAW 表达式元数据。原 byte[] 的 2000 字节 RAW 字面量限制不变。
- 所有流的十六进制展开、其余字面量及 SQL 共享 64MiB 逻辑包上限；源失败、非法返回计数、
  超限和本地取消不会发送目标 SQL，不误报未知执行。初始化响应已完成时会话可继续使用。
- 不读取 Length、不回卷、不释放调用者流。取消时不合作的源仍持有 busy 租约，直到 Read 返回；
  不保证强制中断任意同步阻塞源。不会自动重放。
- 单语句 server-prepared long-data 路径不变；array binding、实际 client-prepare fallback、
  多语句 Prepare/cursor/输出/SchemaOnly/KeyInfo 及无协议依据的复杂输入仍拒绝。
  MySQL 混合字符串的稳定解析字符集规则、Oracle national Unicode 字面量规则仍保留。

## 测试与走查

- 正式 RED：32/32 在原 Stream 拒绝处分支失败。取消测试先修正为观察实际执行失败，避免
  等待基线根本不会进入的源读取。实现后补上引用相等但非同一对象，以及混合 string/national 回归。
- 首轮 GREEN 有 4 项测试误将未绑定 SELECT 1 预期为 SELECT (1)，修正 wire 断言；未改产品。
- 最终相关单测 **547/547**，含新增 40 项；双模式 sync/async、空流/短读/非 seek、当前位置/EOF、
  SQL/参数快照、先校验后读取、非法 count、源中途失败、聚合展开预算、正确 caller token、
  command cancel/timeout、合作与不合作 async 源、会话复用和引用身份。
- 实际 DLL 多语句消费者 **96/96**，其中新增 24 项，三类代表资产、sync/async、TCP/合成 OB20。
  首轮有 6 项 MySQL OB20 fixture 请求编号错误，按已有初始化差异修正后全部通过，未改产品。
- 新增实库 **46/46**：双模式 sync/async，0/1/2000/2001/4000/65536/1048576 字节，
  Oracle RAW/BLOB、MySQL VARBINARY/LONGBLOB，两个独立源、当前位置、typed NULL、更新计数、
  独立连接逐字节校验存储，MySQL 两种初始 SQL_MODE 及中途反转；另 4 项源失败验证本连接
  和独立连接均无副作用。Oracle 空 RAW 为 SQL NULL，MySQL 空二进制仍为空字节。
- 双模式既有多语句及 StreamTests 实库回归 **253/253**；DI **14/14**；五目标 Release
  构建零警告、零错误。消费者三类实现路径均在当前 .NET10 host，不外推完整运行时矩阵。
- 独立最终走查无 Critical/Important/Minor 遗留；审查者只读核对实现、全部新增文件及已保存门禁证据，未执行测试或实库。完整默认单测 **8384/8384**，零失败、零跳过，耗时 8分23秒；在沙箱外运行以避开已知 File.Replace 沙箱限制，未启用实库或 SecurityTests。

证据为 ignored `TestResults/multi-statement-stream/` 的 TRX/log；Java 探针在 ignored
`TestResults/java-gap-probe/`，含 `LargeStreamProbe.java`、`RunLargeStreamOracle.ps1` 和
`large-stream-oracle.log`。凭据只经既有 user-secrets/stdin 传递，原始 stderr 不展示。

## 未关闭范围

仅 Windows/.NET10、OceanBase 4.4.2.2 双模式直连、TLS Disabled；不代表真实 OB20/OBProxy/TLS、
其他版本/平台或完整资源故障矩阵。源会被消费且 SQL 先物化，不能当作无界流式传输保证。
本模块不能关闭 P1-01 的所有形状，也不关闭整体缺口目标；未推送。
