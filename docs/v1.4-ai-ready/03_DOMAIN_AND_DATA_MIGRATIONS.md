# 领域模型与数据迁移

## 1. 通用约束

- 所有 ID 为小写 UUID v7 字符串，数据库列 `TEXT NOT NULL`。
- 所有时间为 UTC ISO-8601，毫秒精度，以 `Z` 结尾。
- 布尔值使用 SQLite `INTEGER NOT NULL CHECK(value IN (0,1))`。
- JSON 写入前必须通过对应 Schema，采用 UTF-8、稳定键序列并限制大小。
- 软删除实体使用 `archived_at_utc`；秘密和安装文件删除不做软删除。
- 每个可编辑聚合含 `row_version INTEGER NOT NULL DEFAULT 1`，更新条件必须包含旧版本。
- 外键开启 `PRAGMA foreign_keys=ON`；发布构建启动时执行 `quick_check`。

## 2. 聚合与不变量

### 2.1 Expert

`experts` 保存当前投影；`expert_revisions` 保存不可变执行定义。

关键字段：

```text
experts(id, name, description, color_key, current_revision_id,
        is_builtin, status, created_at_utc, updated_at_utc,
        archived_at_utc, row_version)

expert_revisions(id, expert_id, revision_number, model_preference_json,
        system_instruction, capability_policy_json, snapshot_json,
        snapshot_sha256, created_at_utc)
```

不变量：

- 同一专家 `revision_number` 从 1 连续递增，唯一索引 `(expert_id, revision_number)`。
- 修订写入后不可更新；修订与 `experts.current_revision_id` 在同一事务提交。
- `snapshot_json` 包含技能绑定、连接/MCP 稳定 ID 和策略，不含秘密。
- 内置默认专家不可归档；其修订可被“恢复默认”追加新版本。
- 一个专家最多 20 个已启用技能、20 个连接来源、64 个暴露工具。

### 2.2 Skill

```text
skills(id, publisher, display_name, active_version_id, status,
       source_kind, installed_at_utc, updated_at_utc, row_version)

skill_versions(id, skill_id, semantic_version, manifest_json,
       package_sha256, content_root, instruction_sha256,
       validation_status, installed_at_utc)

expert_skills(expert_id, skill_version_id, sort_order, activation_mode,
       enabled, created_at_utc)
```

约束：

- `(skill_id, semantic_version)` 唯一；同版本不同哈希拒绝进入数据库。
- `content_root` 是应用技能根目录下的相对路径，不保存任意绝对路径。
- `activation_mode` 仅 `pinned|automatic`。
- 每专家 `sort_order` 唯一，0 开始且服务层保存时重新压紧。
- 每技能保留最多 3 个版本；删除前确认没有运行快照引用。被引用版本只标记待清理。

### 2.3 Connector

```text
connector_definitions(id, kind, display_name, version,
       capability_manifest_json, is_builtin)

connector_accounts(id, connector_definition_id, display_name,
       identity_summary, credential_ref, granted_scopes_json,
       state, last_success_at_utc, last_error_code,
       created_at_utc, updated_at_utc, row_version)

space_connectors(space_id, connector_account_id, enabled, policy_json,
       created_at_utc, updated_at_utc)

expert_connector_grants(expert_id, connector_account_id,
       allowed_capabilities_json, enabled, created_at_utc, updated_at_utc)
```

`credential_ref` 是随机 UUID，不包含 token。`identity_summary` 只允许服务返回的公开账号名或脱敏邮箱，最多 120 字符。

有效能力必须同时满足：账号 connected/degraded、空间 enabled、专家 grant enabled、能力在连接器定义中、能力 ID 在 allowlist、风险策略允许。

### 2.4 MCP

```text
mcp_servers(id, display_name, transport_kind, config_json,
       credential_ref, enabled, state, negotiated_protocol,
       server_info_json, capability_hash, last_connected_at_utc,
       last_error_code, created_at_utc, updated_at_utc, row_version)

mcp_capabilities(id, mcp_server_id, kind, remote_name, stable_name,
       title, description, input_schema_json, annotations_json,
       local_risk, schema_sha256, status, discovered_at_utc)

space_mcp_servers(space_id, mcp_server_id, enabled, policy_json,
       created_at_utc, updated_at_utc)

expert_mcp_grants(expert_id, mcp_server_id,
       allowed_capability_ids_json, enabled, created_at_utc, updated_at_utc)
```

约束：

- `transport_kind` 为 `stdio|streamable_http`。
- stdio `config_json` 只能含 executable 相对保护表示/绝对路径、args 数组、cwd、环境变量名与 secret refs；禁止保存展开后的秘密。
- HTTP `config_json` 不保存 URL userinfo，保存前去除 fragment；query 中疑似秘密参数拒绝。
- `stable_name` 在应用范围唯一，由本地生成，不因服务 title 变化而改变。
- capability 的 Schema/关键 annotations 改变会生成新 `schema_sha256`，旧授权状态置为 `stale`。

### 2.5 运行快照、授权与审计

```text
agent_run_snapshots(id, conversation_id, expert_revision_id,
       space_id, project_id, task_id, model_id,
       selected_skills_json, capability_catalog_json,
       snapshot_sha256, created_at_utc)

consent_receipts(id, run_snapshot_id, source_kind, source_id,
       capability_stable_id, schema_sha256, risk_level,
       scope, expires_at_utc, decision, created_at_utc)

capability_audit(id, run_snapshot_id, expert_id, space_id,
       source_kind, source_id, capability_stable_id,
       risk_level, decision, outcome, error_category,
       duration_ms, result_size, created_at_utc)
```

禁止把参数、响应正文、提示文本、token、完整 URL 查询串写入 consent/audit。若需关联相同调用，可保存随机 `correlation_id`，不得保存参数哈希作为侧信道。

## 3. 状态机

### 3.1 Connector account

```text
disconnected -> authenticating -> connected
connected -> degraded | expired | disabled | error
degraded -> connected | expired | disabled | error
expired -> authenticating | disabled
error -> authenticating | disabled
disabled -> disconnected
```

只有显式用户动作可从 disabled 离开。`401/invalid_token` 进入 expired；短暂网络错误进入 degraded；配置无效进入 error。

### 3.2 MCP server

```text
disabled -> disconnected -> starting -> initializing -> ready
ready -> degraded -> reconnecting -> initializing
starting|initializing|ready|degraded|reconnecting -> stopping -> disconnected
任意非 disabled -> error
error -> disconnected | disabled
```

状态写入数据库只保存稳定投影；高频瞬态保存在内存。应用异常退出后，所有非 disabled 服务重置为 disconnected，不声称仍 ready。

### 3.3 Capability

`discovered -> approved -> stale -> approved`，或任意状态到 `blocked/removed`。Schema 变化从 approved 进入 stale；服务列表不再返回时进入 removed，保留审计引用但不进入目录。

## 4. 数据库迁移

### 4.1 Migration 014：专家与技能

1. 创建 experts、expert_revisions、skills、skill_versions、expert_skills、agent_run_snapshots。
2. 创建默认专家及修订 1。
3. V1.3 已有会话不回填快照；其 UI 标记“旧版默认配置”。首次继续时创建新分支与快照。
4. 设置 `PRAGMA user_version=14`。

### 4.2 Migration 015：连接器

1. 创建 connector_definitions、connector_accounts、space_connectors、expert_connector_grants。
2. 种子写入 GitHub 和 Notion definition；能力清单来自代码内版本化资源并校验哈希。
3. 不创建示例账号或假 token。
4. 设置 `PRAGMA user_version=15`。

### 4.3 Migration 016：MCP 与治理

1. 创建 mcp_servers、mcp_capabilities、space_mcp_servers、expert_mcp_grants、consent_receipts、capability_audit。
2. 为筛选创建索引：audit 时间、空间、专家、来源、风险、结果。
3. 设置 `PRAGMA user_version=16`。

### 4.4 迁移事务与恢复

- 启动迁移前关闭所有后台写入并对数据库做时间戳备份，最多保留 3 个。
- 每个版本一个 `BEGIN IMMEDIATE` 事务；DDL、种子和版本号同事务。
- 迁移完成执行 `foreign_key_check` 和 `quick_check`；失败则回滚并保持备份。
- 禁止在迁移中删除列、重命名已有表或重新解释 V1.3 字段。
- 迁移代码具备从 13→14→15→16 和各中间版本升级的测试。

## 5. SecretService 文件布局

```text
%LOCALAPPDATA%/WorkPilot/secrets/{credential_ref}.bin
```

明文结构在内存中为 `{version, kind, created_at, fields}`，序列化后使用 DPAPI CurrentUser 保护，并以应用固定 entropy + credential UUID 派生附加 entropy。文件写入临时文件、flush 后原子替换。

规则：

- 最大秘密载荷 64 KiB。
- 不允许备份到技能包、诊断包或普通配置导出。
- 删除账号先把数据库引用标记 pending_delete，删除秘密文件成功后事务删除账号；文件不存在视为幂等成功。
- 进程内秘密使用后清零可变缓冲；不得转换为长期驻留的普通 `string`。

## 6. 保留与清理

- 审计默认保留 180 天且最多 100,000 条，以先达到者为准。
- 空闲维护每批删除 1,000 条，单次不超过 100 ms，支持取消。
- 旧专家修订只要被会话/任务引用就保留；无引用修订每专家保留最近 20 个。
- 连接/MCP 能力 removed 记录保留 180 天，以支持审计展示。
- 技能暂存目录启动时清理超过 24 小时的未完成目录，但必须限制在已验证的应用 staging 根目录内。

## 7. 索引与查询上限

- 专家名称/描述和技能名称/描述进入 SQLite FTS5；不索引系统指令、外部响应和秘密。
- 能力列表每页 100，最多读取 50 页/5,000 项。
- 审计 UI 每页 100，导出最多 100,000 条并流式写入。
- 所有 `IN` 查询分批最多 500 个 ID。
- JSON 列读取后必须先检查长度；单列上限 1 MiB，运行快照 catalog 上限 256 KiB。
