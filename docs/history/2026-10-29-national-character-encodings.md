# 显式 national 字符集的标量绑定与结果解码（2026-10-29）

基线 `f011212`，继续在 master 开发；本地提交，不推送。对应 P1-08，不能关闭全部缺口。

> 本文保留标量模块 `74f6497` 的证据范围。后续 PL 复杂输出的实库验收见
> [复杂编码验收记录](2026-10-29-complex-national-encoding-acceptance.md)，不改写本模块当时的验证结果。

## 功能与协议依据

官方 OceanBase Connector/J 2.4.14，源码 `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`：
`BasePrepareStatement.setNString` 选择 `OBNStringParameter`，后者的 `writeBinary` 使用配置的 national
编码（否则使用客户端编码）；`RowProtocol.getCurrentEncoding` 对 NCHAR/NVARCHAR2 优先使用配置的
national 编码；`AbstractConnectProtocol` 初始化 national 状态并发送 `SET NCHARACTER_SET_CONNECTION`。
这支持显式编码选择，不构成原生 Oracle/MySQL 全功能承诺。

独立 Java 实库探针覆盖 ascii/latin1/gbk/gb18030 × 普通 server prepare/Oracle PrepareExecute，八种组合通过。
全部实际为 `ServerSidePreparedStatement`，写入 NVARCHAR2/NCHAR 后通过独立 UTF-8 连接检查实际存储和填充，
原连接的文本/二进制结果读取也通过。初次探针复用了被 Connector/J 修改的 Properties，导致后续连接沿用 ascii；
每次连接改用 clone 后通过。初始失败日志保留，不归因于服务端或 .NET。

## 实现范围

- `NationalCharacterSet` 复用既有严格 `ClientCharacterSet.Parse` 映射；保留 UTF8/UTF8MB4/UTF16 既有行为，
  新接受显式 utf8mb3、ascii、latin1、gbk、gb18030、gb18030_2022、dec8、tis620、sjis、big5、hkscs、hkscs31。
- standalone NCHAR/NVARCHAR2 二进制输入、national 普通结果及共享 resolver 的 PL complex 输出使用该映射。
  latin1 采用项目已有的 OceanBase/MySQL CP1252 映射；.NET 实库验证包含 `é€`，没有照搬 Java ISO-8859-1 别名。
- `ClientCharacterSet.GetDecoder` 委托底层严格解码器，保留多字节字符跨缓冲区状态；不新增映射或依赖。
- 输入仍优先 national，否则使用 connection；结果优先 national，否则使用 results，最后只允许既有 Unicode
  collation fallback。unknown/binary 缺少可用事实、畸形字节、不可表示输入继续拒绝。
- 后续结果仍要求新的可信会话状态，stored complex national 仍要求独立查得 AL16UTF16；这些边界不扩展。

## 本轮验证

| 检查 | 结果与范围 |
| --- | --- |
| 首轮核心 RED | 正式输入契约 24 项中 20 失败、4 通过：12 项显式编码被拒绝，8 项多字节分块解码失败 |
| 相关单测 | 544/544，包括十二种固定预期字节、sync/async、text/binary、buffered/sequential、NCHAR/NVARCHAR2、NULL、不可表示输入无执行及连接复用；另含 stored/PL 输出方向隔离与 fresh-facts 拒绝 |
| 实际 DLL 消费者 | 336/336，三个代表资产由 .NET 10 host 加载，验证标量绑定/流式读取及 PL 输出；不能外推所有独立运行时 |
| Oracle 直连新矩阵 | 48/48，十二种设置 × 两种二进制执行模式 × sync/async；独立 UTF-8 连接检查实际 NCHAR/NVARCHAR2 存储，再验证原连接及独立连接的 text/binary、buffered/sequential、NCHAR 填充、引号、反斜杠、NUL、长值和 NULL |
| 双模式既有实库回归 | 多语句及 TypeRoundTrip 230/230，零失败、零跳过；与新矩阵分别记录 |
| DI | 14/14 |
| 五目标解决方案 Release | 成功，零警告、零错误；后续仅重建 integration 项目修正会话名称断言，零警告/错误 |
| 完整默认单测 | 8344/8344，零失败、零跳过，8 分 24 秒；沙箱外本地运行以避开既有 File.Replace 限制，不启用实库或 SecurityTests |
| 独立最终走查 | 一次独立最终走查，无 Critical/Important/Minor；完整单测门禁已通过，未留下待修问题 |

初次 decoder 测试使用空输入 `Decoder.Convert`，触发 custom encoding 默认实现的空缓冲区异常；改为实际
StreamReader 使用的 `GetChars(..., flush)` 契约后再运行，正式 RED 为上表 20 失败/4 通过。
首轮实库 44 通过/4 失败：服务端把 utf8mb3 national 设置报告为 utf8mb4，失败发生在名称断言，尚未执行写入。
按已有服务端归一契约修正断言后，48/48 通过。显式 utf8mb3 映射的 mb3 输入限制由固定会话单测验证；
归一后的实库不能当作服务端保留独立 mb3 national 会话的证据。

当前实库范围：Windows/.NET 10、OceanBase 4.4.2.2 Oracle 直连、TLS Disabled。连接客户端编码保持 UTF-8，
只在专用连接上设置 national 编码；SJIS/Big5 national 通过不推翻此前同名客户端 Open 初始化失败。
PL complex 新编码只有合成协议/公开 DLL 回归，未新增其真实存储/过程矩阵；其他平台、OBProxy、真实 OB20、
压缩/TLS/KDC、全量资源/故障/版本矩阵仍开放；未运行 SecurityTests。

产物为 ignored `TestResults/national-encoding/*.trx` 和同名前缀日志；RED、初始失败及最终通过分别保留。
Java 探针为 ignored `TestResults/java-gap-probe/NationalEncodingProbe.java`、`RunNationalEncodingOracle.ps1`、
`national-encoding-oracle*.log`。凭据取自既有 user-secrets，不写入报告或输出。
