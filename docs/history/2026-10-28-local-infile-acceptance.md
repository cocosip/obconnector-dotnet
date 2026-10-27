# 2026-10-28 LOCAL INFILE 实库验收

本模块把已有的 LOCAL INFILE 协议路径接入 Acceptance console，没有新增协议、自动授权、文件系统读取、压缩/OB20 组合或取消契约。

## 交付内容

- `localInfileProfile` 默认关闭，只接受 MySQL 模式且要求连接串显式 `Allow Local Infile=true`。
- `streams.local-infile.sync` 和 `streams.local-infile.async` 使用公开 `LocalInfileStreamFactory`，上传固定两行 UTF-8 CSV 到随机表。
- 两个 case 都检查服务端文件名回调、影响行数、按顺序读取的两行内容和同一连接上的后续查询。
- 表复用 `CaseContext.TableAsync` 的既有清理逻辑；报告只记录对象名和清理结果，不记录连接串、CSV 或凭据。
- Acceptance 文档改为说明目标是 `net10.0`；安装的 .NET 11 SDK 可以构建该目标，运行仍使用 .NET 10 runtime。

## 证据

| 门禁 | 结果 |
| --- | --- |
| 配置/选择单测 | `AcceptanceLocalInfileTests`：5 passed，0 failed/skipped；覆盖 profile 默认/显式关闭、Oracle 拒绝、缺少 capability 拒绝和有效 MySQL 选择 |
| 真实 MySQL streams profile | 用户提供的直连 OceanBase MySQL 租户；OceanBase 4.4.2.2、Windows、`.NET 10.0.14`、TLS disabled；`streams` 组 4/4 passed（binary sync/async + LOCAL INFILE sync/async），0 failed，0 cleanup failures；4 张自有随机表均 removed |
| profile opt-out | 未启用 profile 时 LOCAL INFILE sync/async 均在连接前 skipped；该配置没有通过任何 case，runner exit code 2 |
| Oracle 防误用 | `mode=Oracle` 且启用 profile 在配置解析阶段 rejected，未建立连接 |

真实 profile 使用进程级环境变量传递连接信息，临时配置和报告未进入版本库。上述实库证据只覆盖直连 MySQL 4.4.2.2；不推导 OBProxy 策略、代理后端、大文件、压缩/OB20 组合或中断行为。

## 保留边界

协议单测继续覆盖未配置 factory、空 factory、读取失败、取消、最终响应失败和未知结果会话销毁。真实验收不重复这些 loopback 场景，也不把本地回环证据当作服务端互操作证据。
