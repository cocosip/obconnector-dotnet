# A 阶段续接记录（2026-10-18）

依据：[架构设计](obconnector-dotnet-architecture-design.md)、[开发方案](obconnector-dotnet-development-plan.md) A01–A07。用户已授权核查、续接当前阶段、验证并在当前分支提交。

## 范围与执行顺序

- [x] 工程：保留产品四目标；全部测试及消费宿主使用 net10.0；迁移 slnx，更新 CI 和文档；复用已有 CPM 与统一 Version，不引入第二套版本来源。
- [x] A05：先修复未提交代码的编译阻断，运行现有测试；用协议真实序号、认证切换种子终止符、session-track 嵌套长度、能力缺失和密码脱敏回归暴露问题，再修复共享协议路径。
- [x] A06：先编写环回状态机测试；实现物理会话连接超时/取消、独占租约、未读响应失效、同步/异步关闭、异常映射。A 阶段不发送 SQL，不预建 B 阶段 statement/reader 对象。
- [x] A07：用真实物理会话替换集成测试占位符；覆盖四种模式/拓扑的连接、模式不符与 TLS 名称拒绝，保留缺环境跳过及强制关口失败规则。
- [x] 验证：restore、四目标 Release build、全部测试、format、pack/包内容检查；更新进度证据，完成独立代码审查后提交。

## 边界与决定

- 当前已有 A01–A04 提交及未提交 A05，均在当前 master 上续接，不创建/切换分支。
- 测试运行目标遵循用户要求改为 .NET 10；netstandard2.0 消费样例改为 .NET 10 宿主并显式引用基线资产，产品仍构建四目标。旧运行时执行矩阵留给 B09/R01。
- 合成握手不代表兼容版本已验证；未知端点拒绝，未有实库矩阵前所有互通结论保持未验证。G0/G1 不因单测通过关闭。
- A05 → A06：成功认证返回模式/版本/能力/状态快照，由会话统一拥有传输和清理；A06 → A07：集成测试只使用会话，不复制协议。
- 初次还原因沙箱 Windows TLS 凭证失败；已在授权提升后成功还原，不修改 NuGet 信任或签名策略。

## 核查结果与验证证据

- 基线：A01–A04 已由 GLM 提交；CPM 和根 Version 已存在，问题是 sln/测试目标未统一及 A05 未提交半成品不可编译。
- 本次协议修复：收发共享序号、嵌套 session-state 长度、切换 seed NUL、无插件 native 回退、SSLRequest/认证响应能力/字符集/包上限一致、251–253 字节 DNS 属性 lenenc、拒绝非法长度整数、丢弃可能回显密码的服务器错误文本。OceanBaseException 保留服务端码、SQLSTATE 及跨目标 InnerException。
- A06：独占 lease、未读响应销毁、全程连接预算、同步 TCP 取消与 wait handle 释放、关闭取消、成功发布与取消共锁；仅 Connecting 状态允许连接预算关闭 socket。
- 独立审查及修复后复核通过：native 回退、同步取消、长属性、成功/取消竞态、可信模式边界均已核对，未发现新重要问题。
- `dotnet restore OceanBaseConnector.slnx`：成功。沙箱的 NuGet/Schannel 凭证受限，正常 Windows 提升环境还原和 TLS 测试成功；没有弱化 TLS/包签名验证。
- `dotnet build OceanBaseConnector.slnx -c Release --no-restore`：四个产品目标及全部 net10.0 测试/消费工程成功，0 警告、0 错误。
- `dotnet test OceanBaseConnector.slnx -c Release --no-restore --logger trx --results-directory TestResults`：UnitTests 158 通过、ConsumerTests 5 通过、IntegrationTests 4 跳过，退出码 0；TRX 位于本地忽略目录 `TestResults/`。
- `OB_REQUIRE_INTEGRATION=1` 下单独执行 OpenClose 关口：因四种连接串全部缺失失败，退出码 1，证实关口不会把缺环境折算成通过。该次是预期的环境门槛失败，不是真实互通测试。
- `dotnet format OceanBaseConnector.slnx whitespace --no-restore --verify-no-changes` 与 `git diff --check`：通过。
- `dotnet pack src/OceanBaseConnector/OceanBaseConnector.csproj -c Release --no-build --no-restore`：成功。XML/ZIP 断言验证 CPM、csproj 无局部依赖/产品版本、全部测试项目 net10.0，包版本来自根 Version=0.1.0-alpha，恰含 netstandard2.0/net8.0/net9.0/net10.0 四个产品 DLL。包 README/许可证等正式发布元数据仍属 R01，pack 有缺 README 的提示。

## 未关闭的边界与下一步

- G0/G1 缺真实租户、确切 OceanBase/OBProxy 版本、TLS/认证矩阵；Oracle 当前只有合成/环回证据。没有伪造兼容白名单。
- 未设置 Oracle 状态位的连接当前一律拒绝，MySQL 实际连接暂未开放；需以真实模式位证据实现版本白名单，再跑 A07。ExpectedMode 只能校验，不可覆盖检测结果。
- A04 私有 CA/真实证书名/跨平台验收与 A05 插件实测保持待验证，A07 阻塞。Windows/Linux CI 已更新，远端未运行；本次未运行旧 .NET 8/9 运行时测试。
- 未开始 B 阶段公开 ADO.NET 对象、SQL、reader、事务，不发布或推送 NuGet 包。

## 后续目标框架修订（2026-10-18）

按用户补充要求，核心产品在上述四目标基线上增加 `netstandard2.1`，现在为五目标；本页原有编译、测试和包内容记录保留为当时证据。最新依赖矩阵见架构设计 §2.3，最新验证结果见进度台账；DI 仍为三个现代目标，所有测试及消费宿主仍为 net10.0。
