# national 复杂输出的显式编码直连验收（2026-10-29）

基线 `74f6497`，P1-07/P1-08/W5。前一模块已实现严格映射，本轮扩展实库证据，产品实现保持不变。
沿用 master，仅本地提交，不推送。

## 能力与场景

前一模块的 [OceanBase/Java 编码依据](2026-10-29-national-character-encodings.md)确认显式 national 编码选择。
本轮不从原生 Oracle 推断复杂类型支持，而是在已配置 OceanBase 4.4.2.2 Oracle 直连租户实际创建
对象类型、集合类型和过程，绑定及读取实际值；以成功执行作为对应形状的 OceanBase 证据。

保留 utf8mb4/utf16，加入前一模块的十二种映射：utf8mb3、ascii、latin1、gbk、gb18030、gb18030_2022、
dec8、tis620、sjis、big5、hkscs、hkscs31。每项先设置并查询会话 national 名称；utf8mb3 按服务端既有行为
归一为 utf8mb4，不把它记作独立 mb3 会话。连接的普通客户端编码保持 profile 的默认 UTF-8。

| 实库场景 | 组合及断言 | 结果 |
| --- | --- | --- |
| 命名对象/集合输入及 OUT/INOUT | 14 设置 × NCHAR/NVARCHAR2 × sync/async × object/collection；输出成员、NULL、普通 NUMBER 保真；命名对象另有真实表列的二进制 buffered/sequential 读取 | 112/112 |
| 嵌套对象/集合 OUT/INOUT | 同一 112 组合；保留根/父/子类型身份、NULL 集合成员、普通字符串空格及 NUMBER | 112/112 |
| 匿名 national 数组 DML | 同一 112 组合；显式事务、两次迭代、两种 NULL 位置、实际表读取并回滚 | 112/112 |
| 未解析复杂整列 RETURNING | 14 设置 × NCHAR/NVARCHAR2 × sync/async；发送后保持旧输出、报告 unknown outcome 并置 Broken，仍然拒绝该形状 | 56/56 |

共 **392/392，零失败、零跳过**，1 分 35 秒；原 utf8mb4/utf16 的 56 项保留，新增 336 项。
补充普通连接编码及根类型身份断言后，最终连同前一模块标量 48 项复验 **440/440**，零失败、零跳过，1 分 49 秒。
样本复用既有固定映射文字，涵盖 CP1252 euro、GB18030 修订映射、补充字符、HKSCS 两个版本和 DBCS 尾字节 5C。
NCHAR(4) 的测试输入显式补齐至四个 Unicode 标量，避免把 UTF-16 长度误作字符数；本轮不据此新增
服务端 NCHAR 自动填充承诺，实际标量填充已由前一模块独立验证。

## 验证与走查

本轮首个扩展实库运行即通过，没有驱动修复或伪造 RED；没有新增公共 API、编码映射或依赖。
原有严格字节及畸形输出、结果新状态、AL16UTF16 字典来源和输出原子发布回归仍是必要边界。

| 门禁 | 本轮结果 |
| --- | --- |
| 五目标解决方案 Release | 成功，零警告、零错误；补充连接编码及根类型身份断言后，integration 项目再构建成功，零警告/错误 |
| 相关单测/公开 DLL 消费者/DI | 544/544、336/336、14/14，均零失败、零跳过 |
| 完整默认单测 | 8344/8344，零失败、零跳过，8 分 21 秒；沙箱外避开既有 File.Replace 限制，不启用实库或 SecurityTests |
| 独立最终走查 | 一次独立走查，无 Critical/Important/Minor；矩阵计数、字节/身份/清理及证据边界均无待修问题 |

产物为 ignored `TestResults/complex-national-encodings/`。连接仅通过显式 integration profile 和既有
user-secrets 获取；不打印凭据、连接字符串或未经筛选的实库异常。

## 保留边界

本轮是 Windows/.NET 10、OceanBase 4.4.2.2 Oracle 直连、TLS Disabled、Oracle PrepareExecute 的验收。
标量普通 prepare 见前一模块，本轮不扩展复杂输出的其他执行协议。stored complex national 仍要求字典
明确报告 AL16UTF16；本轮普通表存储读取仅命名对象，不能外推嵌套表列或其他数据库存储编码。
SJIS/Big5 national 输出通过不证明同名客户端 Open 初始化可用。

复杂 cursor 输入、文本复杂结果及未解析 whole-column RETURNING 继续拒绝；未知/畸形编码及没有新可信
会话状态的后续结果不放宽。其他版本/平台、OBProxy、真实 OB20/压缩/TLS/KDC 和完整资源/故障矩阵未验收，
未运行 SecurityTests。这些实际通过只关闭本记录的编码/形状验收，不关闭全部 23 项缺口或 W5。
RETURNING 拒绝检查不核对该未知写最终是否持久化；它验证旧输出保留、Broken 和测试对象清理，不代替业务结果核对。
