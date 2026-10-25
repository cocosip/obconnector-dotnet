# Unknown outcome 与应用核对

内置TNS/OCP的全局黑名单和TCP尝试上限只作用于新物理连接；cap0可复用已验证idle并跳过最低池补建，无idle则零TCP失败。TCP成功后不因TLS/认证/身份错误继续候选，更不重放已发送业务或恢复旧事务。策略与池代数隔离、一次fallback和共享上限见[契约](obconnector-dotnet-architecture-design.md#discovery-blacklist-and-attempt-ceiling)及[本次证据](obconnector-dotnet-completeness.md#1555-2026-10-26-发现黑名单与tcp尝试上限)。

内置TNS/OCP发现现保留组、策略与权重，仅在新建物理连接时选择候选；顺序跨本次TCP重试冻结，健康偏好不跨组。权重/组序变更不会主动重分配仍符合地址membership的idle会话，需Clear后重新建连。它不恢复旧事务或重放未知写入；发现失败仍不得回退旧快照。见[分组选择契约](obconnector-dotnet-architecture-design.md#discovery-group-selection)与[本次验证](obconnector-dotnet-completeness.md#1554-2026-10-26-发现分组策略与权重)。


`OceanBaseException.IsExecutionOutcomeUnknown=true` 表示请求可能已经执行，客户端没有得到足够的最终确认。它不表示执行失败或已回滚。网络断开、超时、取消，以及 COMMIT/ROLLBACK 回包丢失都可能进入该状态；取消调用者的令牌也不能撤销已经完成的服务器操作。

驱动不会自动重放这类 SQL。失败传输销毁、事务失效；连接池释放容量并允许新租约，不提供事务迁移。对旧事务再次 Commit/Rollback 会拒绝；Dispose 也不是补偿操作。显式本地事务与 ambient/XA 的恢复协议各自独立，下面只讨论普通本地事务。

## 建连重试与业务重放的边界

`Connect Retry Count` / `Connect Retry Interval` 只控制物理会话 Attach 前的 TCP 失败与整轮候选等待。TCP 成功后，握手、TLS、认证、身份初始化及业务错误不自动重试；活动连接断开仍须显式 Close/Open 并核对 unknown outcome。开启重试不会恢复旧事务，也不证明原写入已失败。[完整配置与预算契约](obconnector-dotnet-architecture-design.md#connect-retry-policy)。

`OceanBaseOcpAddressResolver`在重新Open时取得新的HTTP地址快照；HTTP/解析失败不回退旧地址，服务名称只绑定发现元数据。HTTP client的认证/TLS/redirect由调用方控制；它不重建旧事务或重放不确定写入。[OCP HTTP契约](obconnector-dotnet-architecture-design.md#ocp-http-discovery)。

`OceanBaseTnsAddressResolver`在显式重新Open时读取新文件地址；发现失败不能退回旧池地址。文件SERVICE_NAME匹配仅核对元数据，不确认真实服务器身份。它不会恢复旧事务、迁移活动连接或重放SQL，unknown outcome仍须按本指南核对。[TNS文件发现契约](obconnector-dotnet-architecture-design.md#tns-file-discovery)。

`Host Recheck Seconds`仅改变后续物理建连的候选偏好：近期TCP失败靠后、全部地址仍可fallback，TCP成功即清除记录。它不验证业务健康，不恢复旧事务，也不改变unknown outcome核对规则。池Clear和凭据退休隔离池缓存的旧尝试反馈；非池缓存随selector存活，不由清池API清除。已借出连接继续原工作。[TCP健康缓存契约](obconnector-dotnet-architecture-design.md#endpoint-tcp-health)。

## 连接级服务器ERR

可信SQLSTATE以08开头，或精确1927/70100时，驱动将物理会话置为Broken并销毁。已发送执行的unknown标志保守为true，不表示服务器一定执行或一定回滚；业务前Prepare/目录/piece确认保持当前非执行标记。普通查询取消1317/70100不属于该分类。错误码和SQLSTATE不替代业务核对依据。

对同一个逻辑连接须先Close再Open；Open才重新获取凭据、发现地址和新租约，旧事务不能恢复或迁移。完整裸协议ERR在解析前取消时仍可保留身份；已关闭OB20/压缩传输无法补足边界证据，仍保守失败，不绕过校验。[精确边界与本地证据](obconnector-dotnet-completeness.md#1549-2026-10-25-连接级服务器错误与显式连接重建)不代表真实节点/代理故障已经验收。

## 在第一次发送前建立核对依据

应用在操作开始前生成并持久保存稳定的 `operation_id`，不要为一次重试生成新 ID。预先建立自己的操作表，例如两种模式均可使用：

```sql
CREATE TABLE application_operations (
    operation_id VARCHAR(64) PRIMARY KEY,
    outcome VARCHAR(32) NOT NULL
)
```

在**同一个数据库事务**中写业务数据和操作标记，标记的 `outcome` 为应用定义的完成状态，然后提交。标记必须与全部业务写入原子提交，不能在事务外补写。唯一键用于阻止重复操作标记；它不能代替对业务操作、并发重试及外部副作用的设计。

捕获 unknown-outcome 异常后保存操作 ID、异常分类和核对状态，释放旧连接及事务，交给核对流程。不要对旧连接重新 COMMIT，也不要立即重新执行业务 SQL。日志避免记录连接字符串、密码和完整业务参数。

## 用新租约核对已提交标记

下面是现代 TFM 的查询示例；`connectionString` 使用应用正常的认证/TLS策略。新连接不得重新加入原来失败的 TransactionScope，核对必须读取权威库的最新已提交数据，不能依赖延迟副本或旧事务快照。

```csharp
using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using OceanBaseConnector;

static async Task<string?> ReadOperationOutcomeAsync(
    string connectionString, string operationId, CancellationToken token)
{
    using var suppression = new TransactionScope(
        TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
    await using var connection = new OceanBaseConnection(connectionString);
    await connection.OpenAsync(token);
    using var command = connection.CreateCommand();
    var marker = connection.CompatibilityMode == OceanBaseCompatibilityMode.Oracle
        ? ":operation_id" : "?";
    command.CommandText =
        "SELECT outcome FROM application_operations WHERE operation_id = " + marker;
    command.Parameters.Add(new OceanBaseParameter
    {
        ParameterName = "operation_id", DbType = DbType.String, Value = operationId,
    });
    var result = await command.ExecuteScalarAsync(token);
    suppression.Complete();
    return result is null or DBNull ? null : (string)result;
}
```

| 核对结果 | 应用处理 |
| --- | --- |
| 找到可信的已提交标记，状态与预期一致 | 按标记对应的业务结果完成该操作；不要重放 |
| 找到标记，但状态或业务结果不符合预期 | 进入业务校验/人工处理，不能当作网络重试 |
| 查不到标记 | 保持“待核对”；可能仍在执行、正在恢复或读到旧数据，单次缺失不证明回滚 |
| 核对查询失败或被取消 | 继续保存待核对状态；核对失败不能改变原操作结果 |

只有获得权威的最终状态，并满足应用的幂等与并发约束后，才能决定补偿或再次执行。普通本地事务没有可供新连接接管的事务 ID；需要 durable prepare、协调器崩溃恢复或跨资源原子性时，应使用另外设计并验收的 XA/协调器方案。本驱动的本地 ambient 登记不提供分布式提升或 durable prepare。

## 当前验证边界

本次验收通过本地明文中继，先收到真实服务器的 COMMIT/ROLLBACK 响应，再向客户端丢弃全部回包或只交付原长度头和一个载荷字节。客户端报告 unknown outcome；独立非池观察连接确认实际提交/回滚结果；同一中继端点、MaxPoolSize=1 的新会话可继续写入并运行本地回滚，旧事务迟到释放不会影响它。

这证明的是配置的 OceanBase4.4.2.2 MySQL/Oracle 直连、Windows/.NET10、TLS disabled 的受控响应丢失，不是服务器崩溃、真实 OBProxy、企业 TLS、外部协调器恢复或所有故障场景。中继只属于测试基础设施，不进入产品，也不会把启用 TLS 的 profile 降级。上述操作表/核对示例是应用模式说明，不是驱动自动恢复功能。
