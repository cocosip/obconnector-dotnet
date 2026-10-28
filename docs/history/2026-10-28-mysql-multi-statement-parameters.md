# MySQL 参数化文本多语句（2026-10-28）

## 范围与证据

P1-01/W3：显式 `Allow Multi Statements=true` 后，MySQL 文本多语句支持位置
标量输入：普通数字、字符串、二进制和 NULL。先快照参数，再读取可信会话上下文，
完整生成有界 SQL，沿 COM_QUERY 多结果路径执行；不尝试服务端准备或自动重放。
此功能不依赖 `AllowClientSidePrepareFallback`。

官方 Connector/J 2.4.14 固定源码
`6adc311ba5db8b2f19f98f3e6e4734667fdfdda1` 的
`OceanBaseConnection.internalPrepareStatement` 有客户端准备路径；已有官方 jar
对照确认两种模式、`useServerPrepStmts=false/true` 均以客户端准备执行参数化多语句。
本轮另测旧引号转义：Java 脚本先切换 `NO_BACKSLASH_ESCAPES` 时，随后含引号/
反斜线的字符串绑定在当前 MySQL 服务端返回 `1064/42000`，不能直接取消原单语句保护。

MySQL 新路径以严格客户端编码生成 hex 内容，使用
`REPLACE('x','x',_<trusted charset> X'<hex>')` 保持普通字面量的 collation 和
coercibility=4；普通 `_charset X` 使用默认 collation，CAST/CONVERT 会产生
coercibility=2，直接 COLLATE 则为0，均不能替代当前契约。
官方 jar 实库验证了 general_ci/bin/unicode_ci 三种上下文及 NO_BACKSLASH_ESCAPES、
ANSI_QUOTES 的组合，含引号、反斜线、Unicode、分号、绑定标记和 NUL，值与编码均保真。

解析字符集必须在脚本内保持稳定。上述表达式在脚本切到 latin1 时会将不可表示字符
转换成 `?`，因此字符串绑定脚本在发送目标 SQL 前拒绝可观察的 SET NAMES/CHARSET/
CHARACTER、SET 中的客户端/连接字符集及 collation 变量（保守地包括读取）、quoted SET
标识符，以及 CALL/DO/PREPARE/EXECUTE/DEALLOCATE/RESET。单引号系统变量标识符的
三个可疑形式已由当前服务端拒绝1064；ANSI_QUOTES双引号形式可被接受，客户端已拦截。
不承诺静态扫描能够识别存储函数/触发器任意隐式字符集副作用；这类脚本不在支持范围。

保留拒绝：显式 Prepare、曾准备后改为多语句的命令、游标、SchemaOnly/KeyInfo、
输出、stream、时间和复杂输入。Java实库另外确认两模式的Date/Time/Timestamp绑定可执行，当前.NET路径仍拒绝，保留为后续真实开发缺口。其他组合继续按依据分类，本模块
不代表整个 P1-01/W3 已关闭。Oracle 已有输入路径保留，MySQL 单语句 fallback
沿用原转义和会话策略。

## 实施与验证

1. 新正常路径6项首先全部因原 MySQL 参数门禁 RED，再补共享入口与安全字符串路径。
2. MySQL 多语句不准备，参数在任何上下文 await 前快照；原始 SQL 仍用于会话/缓存
   分类。四种引号模式必须对绑定位置达成一致，执行注释及歧义输入仍拒绝。
3. 重用公开 DLL 消费夹具、双模式实库事务及默认关闭用例，补强拒绝与编码矩阵。

当前日志位于 ignored `TestResults/mysql-multi-parameters/`：

- 相关单测232/232：正常输入、两种EOF、快照晚修改、确认错误后复用，
  charset/动态执行拒绝、SQL歧义、严格hex编码及64MiB逻辑包预算。
  普通M/m注释另3项RED→GREEN，仅拒绝真正的版本执行注释前缀。
- 公开DLL消费者24/24：Oracle/MySQL、代表性三资产、同步/异步、TCP/OB20。
- 双模式显式实库多语句组78/78，新增MySQL54项：UTF8MB4三个collation、
  SQL_MODE切换、普通/顺序读取、重复执行、NUL/Unicode/RAW/NULL、
  literal collation/coercibility、默认关闭无副作用、中途错误、未提交可见性、回滚，
  以及其余12种已支持客户端字符集的严格实际字节及两组结果。
- 五资产主解决方案Release零警告/错误；DI14/14。
- 未启用实库时22配置通过、155数据库测试跳过；真实数据库/证书测试不进入CI。
- 最终完整默认单测8102/8102，0失败、0跳过，耗时8分16秒；独立最终走查无待修发现。

Java probe及安全语法对照保存在 ignored `TestResults/java-gap-probe/`。
连接信息沿已有 user-secrets 经 stdin 提供，不写入文档和产物。
本地OB20消费者不是实际OB20/OBProxy/TLS验收，完整版本/平台/隐式副作用矩阵仍开放。
