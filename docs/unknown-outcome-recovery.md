# Unknown outcome 与应用核对

`OceanBaseException.IsExecutionOutcomeUnknown=true` 表示请求可能已经执行，客户端没有得到足够的最终确认。它不表示执行失败或已回滚。网络断开、超时、取消，以及 COMMIT/ROLLBACK 回包丢失都可能进入该状态；取消调用者的令牌也不能撤销已经完成的服务器操作。

驱动不会自动重放这类 SQL。失败传输销毁、事务失效；连接池释放容量并允许新租约，不提供事务迁移。对旧事务再次 Commit/Rollback 会拒绝；Dispose 也不是补偿操作。显式本地事务与 ambient/XA 的恢复协议各自独立，下面只讨论普通本地事务。

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
