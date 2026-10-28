# 证书/TLS测试与默认CI隔离（2026-10-28）

按用户要求，生成/导入证书、申请系统TLS凭据、TLS/mTLS握手及TLS消费者测试移至
`tests/OceanBaseConnector.SecurityTests`。该项目不在主解决方案，不被普通或tag CI调用。
真实数据库仍只由已有的显式opt-in IntegrationTests/验收控制台访问，未新增实库CI步骤。

默认UnitTests/ConsumerTests保留纯配置、SSLRequest编码、握手前能力拒绝和TCP协议测试。
混合压缩、MySQL存储过程、连接重试、端点健康和发现黑名单矩阵，分别编译TCP/TLS参数：
默认入口只有TCP行，安全入口定义`SECURITY_ACCEPTANCE`后只有TLS行。共享partial helper
通过源码链接复用，不复制断言，不以Skip或平台判断将安全验收标成通过。

尤其端点健康/发现黑名单中，服务端读取SSLRequest后立即断开，客户端仍可能申请系统TLS
凭据；这4项同步/异步用例也已隔离。清除CLIENT_SSL后在TLS启动前拒绝的测试继续保留。

验证证据（ignored `TestResults/test-split/`）：

- 修改涉及的45个原测试源码文件中，335个测试方法的方法体token逐一一致且各保留一次。
- 最终UnitTests、ConsumerTests、SecurityTests项目Release构建均零警告/错误。
- 发现清单：默认Unit 7,994项、Consumer 2,860项，安全验收671项（原Unit 147、Consumer 524）；
  默认与安全清单无相同用例，默认混合矩阵无TLS参数。运行时展开的theory数量可能不同于发现数量。
- 默认完整单测8,003通过、零失败/跳过；最终重编译后的混合矩阵TCP专项139/139；DI14/14。
- 默认ConsumerTests完整2,860/2,860通过、零失败/跳过，本机耗时30分1秒；不代表远程CI耗时保证。
- 显式安全入口专项11/11：证书准备、完整握手、静默握手中断及两组客户端TLS失败行。
- 本地受限沙箱连最小File.Replace也返回Access denied，原TNS并发文件替换测试因此失败；
  完整单测在普通用户上下文重跑通过，未排除此测试、未改动其断言。
- 独立走查已复核迁移、partial helper、混合数据、collection隔离和项目/CI依赖；补查的两组
  客户端TLS行已修复并复核。驱动执行逻辑未修改，仅增加安全测试程序集的内部可见性。

安全套件完整671项尚未整体执行；11项专项只证明当前Windows/.NET10本机的合成TLS入口可用。
不证明当前真实服务端支持TLS、企业CA/撤销、OBProxy或KDC；这些环境验收门禁仍开放。
