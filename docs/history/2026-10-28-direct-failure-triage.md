# 18项实库失败归因（2026-10-28）

本记录针对 `direct-profile-ownership-timeout` 的18项失败做了独立对照，不把测试失败码直接当作驱动缺陷或“服务器不支持”。凭据仍只在本机user-secrets；对照使用官方 `com.oceanbase:oceanbase-client:2.4.14`，源码固定提交 `6adc311ba5db8b2f19f98f3e6e4734667fdfdda1`。

## 结论矩阵

| 原失败 | 数量 | 结论 | 依据与处理 |
| --- | ---: | --- | --- |
| Oracle complex national：utf8mb3 × NCHAR/NVARCHAR2 × sync/async | 4 | 驱动校验缺陷，已修复 | 服务端把 utf8/utf8mb3 会话名和排序规则归一为 utf8mb4；Java 2.4.14 同样连接并精确往返。仅允许“配置mb3→服务端mb4归一名”，仍拒绝补充字符，池身份和其他字符集严格匹配；6项相关实库用例现已通过。 |
| MySQL JSON：utf8mb3 × sync/async | 2 | 驱动校验缺陷，已修复 | 同一归一行为；JSON用例现已通过。JSON存储/NULL断言实际执行。 |
| Oracle complex national：SJIS/Big5 × NCHAR/NVARCHAR2 × sync/async | 8 | 当前服务端/模式能力限制 | .NET与官方Java均在 `SET character_set_results='utf8mb4'` 后失败900；不改成静默UTF-8或跳过。 |
| Oracle collection nested-table 表列 | 1 | 服务端明确不支持 | CREATE TYPE成功，CREATE TABLE返回600/0A000 `nested table type not supported`；Java同SQL同样失败。过程参数的collection能力仍单独保留。 |
| Oracle `SELECT CURSOR(...)` prepared结果列 | 1 | 结果形状与当前服务端/协议不兼容，非普通REF CURSOR整体缺陷 | 文本SQL可返回CURSOR列元数据，但实际列值不是现有四字节游标标识；.NET解析失败，Java取对象也失败；prepared路径两者均返回902/HY000。OUT REF CURSOR路径已有独立通过证据。 |
| Oracle CREATE VIEW | 1 | 当前账户权限限制 | .NET与官方Java均返回1031/HY000；拆分后组合键/index/FK和其他目录断言可独立通过。测试保留为显式权限验收，不遮住核心目录功能。 |
| Oracle CREATE SYNONYM | 1 | 当前账户权限限制 | .NET与官方Java均返回1031/HY000；已从Types/Sequences综合用例拆出，其他断言独立运行。 |

## 对照细节

MySQL/Oracle默认、utf8和MySQL SJIS/Big5由 Java 2.4.14 成功连接；MySQL SJIS/Big5状态分别为 `sjis_japanese_ci`/`big5_chinese_ci`，说明这两项不是通用字符集实现缺失。Oracle SJIS/Big5失败发生在服务端不接受后续 `character_set_results` 语句，而不是 national 数组编码或绑定逻辑。

Java 对 `SELECT CURSOR(SELECT 7 FROM DUAL)` 的文本结果报告 `CURSOR` 元数据，但 `getObject` 不能得到可用游标对象；这不能证明可用的文本游标功能。没有新增 workaround，也没有把这个形状改成通过。

## 代码和测试变化

- `ClientCharacterSet` 只放宽了配置为 `utf8mb3` 时对服务端 `utf8mb4` 名称/排序规则的接受；编码器仍拒绝补充字符，pool key仍使用canonical `utf8mb3`。
- Schema综合测试拆成核心目录、独立View、独立Synonym三组，权限失败不再阻止其他断言。
- 没有删除失败项、改成skip或增加未有协议依据的游标/nested-table workaround。

证据：ignored `TestResults/java-gap-probe-final.log`、`TestResults/text-cursor-dotnet-probe.log`、`TestResults/mb3-normalization-live.log`、`TestResults/schema-permission-split-live.log`。
