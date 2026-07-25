# V1.3 数据模型与迁移规格

## 1. 通用数据规则

- 数据库仍为 `%LOCALAPPDATA%\WorkPilot\workpilot.db`，SQLite、WAL、`foreign_keys=ON`、`busy_timeout=5000`。
- 领域 ID 使用 32 位小写无连字符 GUID；数据库内部 FTS 行使用 `INTEGER PRIMARY KEY`。
- 业务时间保存为 UTC ISO 8601 `O` 格式；文件系统时间保存为 Unix 毫秒整数。
- 布尔值保存为 `INTEGER NOT NULL CHECK(value IN (0,1))`。
- 所有写操作通过 Repository/Service，View 和 ViewModel 禁止 SQL。
- 迁移文件一经发布不可修改，只能新增更高版本。
- 每个连接打开后必须设置 `foreign_keys` 和 `busy_timeout`；不得假设连接级 PRAGMA 自动继承。

## 2. 版本管理

新增表：

```sql
CREATE TABLE IF NOT EXISTS schema_migrations(
  version INTEGER PRIMARY KEY,
  name TEXT NOT NULL,
  applied_at TEXT NOT NULL,
  checksum TEXT NOT NULL
);
```

V1.2 数据库没有迁移表。初始化器必须：

1. 检测 `settings/conversations/messages/projects/automations` 是否存在。
2. 若存在且没有 `schema_migrations`，将其登记为版本 12，名称 `v12_baseline`；登记不改变业务表。
3. 若业务表部分存在或列结构不符合 V1.2，停止启动并提示修复，不猜测结构。
4. 依次执行未应用迁移；校验内置迁移文本 SHA-256 与登记 checksum。

## 3. V1.3 目标表

### 3.1 spaces

```sql
CREATE TABLE spaces(
  id TEXT PRIMARY KEY,
  name TEXT NOT NULL,
  description TEXT NOT NULL DEFAULT '',
  color_token TEXT NOT NULL,
  is_default INTEGER NOT NULL DEFAULT 0 CHECK(is_default IN (0,1)),
  is_archived INTEGER NOT NULL DEFAULT 0 CHECK(is_archived IN (0,1)),
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  row_version INTEGER NOT NULL DEFAULT 1
);
CREATE UNIQUE INDEX ux_spaces_one_default ON spaces(is_default) WHERE is_default=1;
CREATE INDEX ix_spaces_archived_updated ON spaces(is_archived, updated_at DESC);
```

允许的 `color_token`：`green blue cyan violet amber orange rose slate`。数据库不依赖本地化显示名称。

### 3.2 projects

重建现有表并加入空间外键：

```sql
CREATE TABLE projects_v13(
  id TEXT PRIMARY KEY,
  space_id TEXT NOT NULL REFERENCES spaces(id) ON DELETE RESTRICT,
  name TEXT NOT NULL,
  workspace_path TEXT NOT NULL,
  instructions TEXT NOT NULL,
  ignore_rules TEXT NOT NULL DEFAULT '',
  include_hidden INTEGER NOT NULL DEFAULT 0 CHECK(include_hidden IN (0,1)),
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  row_version INTEGER NOT NULL DEFAULT 1
);
CREATE INDEX ix_projects_space_updated ON projects_v13(space_id, updated_at DESC);
```

工作区路径允许不同项目重复绑定，但 UI 显示警告；索引按项目隔离，不共享资产 ID。

### 3.3 conversations

重建并加入空间与项目：

```sql
CREATE TABLE conversations_v13(
  id TEXT PRIMARY KEY,
  space_id TEXT NOT NULL REFERENCES spaces(id) ON DELETE RESTRICT,
  project_id TEXT NULL REFERENCES projects_v13(id) ON DELETE SET NULL,
  title TEXT NOT NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);
CREATE INDEX ix_conversations_space_updated ON conversations_v13(space_id, updated_at DESC);
CREATE INDEX ix_conversations_project ON conversations_v13(project_id, updated_at DESC);
```

旧会话全部进入默认空间，`project_id=NULL`。V1.3 新会话在创建时冻结空间和项目归属；之后全局切换空间不修改它。

### 3.4 tasks

```sql
CREATE TABLE tasks(
  id TEXT PRIMARY KEY,
  space_id TEXT NOT NULL REFERENCES spaces(id) ON DELETE RESTRICT,
  project_id TEXT NULL REFERENCES projects(id) ON DELETE SET NULL,
  main_conversation_id TEXT NULL REFERENCES conversations(id) ON DELETE SET NULL,
  title TEXT NOT NULL,
  description TEXT NOT NULL DEFAULT '',
  status TEXT NOT NULL CHECK(status IN ('backlog','todo','in_progress','blocked','done','cancelled')),
  priority TEXT NOT NULL CHECK(priority IN ('low','normal','high','urgent')),
  due_date TEXT NULL CHECK(due_date IS NULL OR due_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'),
  sort_key INTEGER NOT NULL,
  completed_at TEXT NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  row_version INTEGER NOT NULL DEFAULT 1,
  CHECK((status='done' AND completed_at IS NOT NULL) OR (status<>'done' AND completed_at IS NULL))
);
CREATE UNIQUE INDEX ux_tasks_main_conversation ON tasks(main_conversation_id)
  WHERE main_conversation_id IS NOT NULL;
CREATE INDEX ix_tasks_space_status_sort ON tasks(space_id, status, sort_key);
CREATE INDEX ix_tasks_project_status ON tasks(project_id, status, updated_at DESC);
CREATE INDEX ix_tasks_due ON tasks(space_id, due_date) WHERE due_date IS NOT NULL;
```

`row_version` 每次更新 `+1`。更新 SQL 必须带 `WHERE id=$id AND row_version=$expected`，影响行数为 0 返回 `ConcurrencyConflict`。
数据库 CHECK 只约束截止日期格式；TaskService 必须用 `DateOnly.TryParseExact("yyyy-MM-dd", InvariantCulture)` 验证真实日历日期。

### 3.5 assets

```sql
CREATE TABLE assets(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  public_id TEXT NOT NULL UNIQUE,
  project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
  normalized_path TEXT NOT NULL,
  path_key TEXT NOT NULL,
  display_path TEXT NOT NULL,
  file_name TEXT NOT NULL,
  extension TEXT NOT NULL,
  category TEXT NOT NULL CHECK(category IN ('code','document','data','config','other')),
  size_bytes INTEGER NOT NULL CHECK(size_bytes>=0),
  modified_unix_ms INTEGER NOT NULL,
  quick_fingerprint TEXT NOT NULL,
  sha256 TEXT NULL,
  text_status TEXT NOT NULL CHECK(text_status IN
    ('indexed','metadata_only_type','metadata_only_size_limit','unsupported_encoding','read_error','missing')),
  generation INTEGER NOT NULL,
  last_seen_at TEXT NOT NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  UNIQUE(project_id, path_key)
);
CREATE INDEX ix_assets_project_generation ON assets(project_id, generation);
CREATE INDEX ix_assets_project_modified ON assets(project_id, modified_unix_ms DESC);
CREATE INDEX ix_assets_project_extension ON assets(project_id, extension);
CREATE INDEX ix_assets_file_name ON assets(file_name COLLATE NOCASE);
```

`normalized_path` 使用 `/`、不以 `/` 开头并保留用于显示的 Unicode。SQLite `NOCASE` 对非 ASCII 不足，因此 C++ 核心必须额外返回 `path_key`：使用 Windows `LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_LOWERCASE)` 产生 UTF-8；唯一约束只使用 `(project_id,path_key)`。

### 3.6 asset_chunks 与 FTS

```sql
CREATE TABLE asset_chunks(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  asset_id INTEGER NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
  ordinal INTEGER NOT NULL,
  start_offset INTEGER NOT NULL,
  end_offset INTEGER NOT NULL,
  token_estimate INTEGER NOT NULL,
  content TEXT NOT NULL,
  search_text TEXT NOT NULL,
  file_name_tokens TEXT NOT NULL,
  path_tokens TEXT NOT NULL,
  content_hash TEXT NOT NULL,
  UNIQUE(asset_id, ordinal)
);
CREATE INDEX ix_asset_chunks_asset ON asset_chunks(asset_id, ordinal);

CREATE VIRTUAL TABLE asset_chunks_fts USING fts5(
  search_text,
  file_name_tokens,
  path_tokens,
  content='asset_chunks',
  content_rowid='id',
  tokenize='unicode61 remove_diacritics 2'
);
```

必须创建 `AFTER INSERT/DELETE/UPDATE` 三个触发器保持外部内容 FTS 一致。更新触发器先发 FTS delete 命令再 insert。索引批次提交后运行轻量计数校验；重新构建结束运行：

```sql
INSERT INTO asset_chunks_fts(asset_chunks_fts, rank) VALUES('integrity-check', 1);
```

不得使用 `INSERT OR REPLACE` 更新外部内容 FTS。

### 3.7 asset_index_state

```sql
CREATE TABLE asset_index_state(
  project_id TEXT PRIMARY KEY REFERENCES projects(id) ON DELETE CASCADE,
  status TEXT NOT NULL CHECK(status IN
    ('idle','discovering','scanning','ready','paused','limit_reached','error')),
  generation INTEGER NOT NULL DEFAULT 0,
  discovered_count INTEGER NOT NULL DEFAULT 0,
  processed_count INTEGER NOT NULL DEFAULT 0,
  indexed_text_count INTEGER NOT NULL DEFAULT 0,
  skipped_count INTEGER NOT NULL DEFAULT 0,
  error_count INTEGER NOT NULL DEFAULT 0,
  current_path TEXT NULL,
  last_full_scan_at TEXT NULL,
  last_event_at TEXT NULL,
  last_error_code TEXT NULL,
  last_error_message TEXT NULL,
  updated_at TEXT NOT NULL
);
```

错误信息不得包含绝对路径；`current_path` 只保存相对路径。

## 4. 迁移 013 执行步骤

迁移名称：`013_spaces_tasks_assets`。

### 4.1 执行前

1. 停止 Scheduler、Agent 新任务和所有数据库写入。
2. 关闭数据库连接池，使用单独迁移连接。
3. 使用 SQLite Online Backup API 创建一致性备份 `workpilot.pre-v13.{UTC}.db`，禁止直接复制打开中的 WAL 数据库。
4. 只保留最近 3 个迁移备份；删除目标必须是明确匹配的备份文件，失败只记录不阻止迁移。
5. 运行 `PRAGMA integrity_check`，结果不是 `ok` 则停止。

### 4.2 事务

1. `PRAGMA foreign_keys=OFF`，确认返回状态。
2. `BEGIN IMMEDIATE`。
3. 创建 `spaces`，插入参数化 GUID 的“我的空间”。
4. 创建 `projects_v13` 并从旧 `projects` 复制，`space_id=default_space_id`。
5. 创建 `conversations_v13` 并从旧表复制，`space_id=default_space_id`、`project_id=NULL`。
6. 删除旧 `projects/conversations`，把新表重命名为正式名称，重新创建索引。
7. 创建 tasks、assets、asset_chunks、FTS、触发器和 asset_index_state。
8. 写入设置 `active_space_id=default_space_id`。
9. 写入 `schema_migrations` 版本 13 与内置 checksum。
10. `COMMIT`；任何错误必须 `ROLLBACK`。
11. `PRAGMA foreign_keys=ON`，运行 `foreign_key_check` 和 `integrity_check`。

消息表对 `conversations(id)` 的外键在重建后必须通过 `foreign_key_check`。迁移测试要使用真实 V1.2 schema 副本，不允许只测空数据库。

### 4.3 失败恢复

- 事务内失败：回滚，保留原数据库与备份，应用只读启动并显示迁移错误。
- 事务提交后完整性检查失败：关闭数据库，将损坏文件改名为 `.failed-v13`，通过 SQLite Backup API 还原迁移前备份；不覆盖失败证据。
- 不允许捕获异常后继续以部分 V1.3 schema 启动。

## 5. 删除与关联行为

| 操作 | 数据行为 | 磁盘行为 |
|---|---|---|
| 删除空空间 | 删除 space | 无 |
| 归档空间 | 更新 `is_archived` | 无 |
| 删除项目 | assets/chunks/index_state 级联删除；tasks/conversations 的 project 置空 | 不删除目录或文件 |
| 删除任务 | 删除任务；主对话保留 | 无 |
| 删除对话 | task 主对话置空；消息级联删除 | 无 |
| 文件消失 | 增量事件先标 missing；完整校验后删除 asset 并级联 chunks | 不操作文件 |

## 6. 数据库并发

- 新增 `DatabaseWriteQueue`，进程内所有写事务串行；读连接可并发。
- 写队列容量 200；满时生产者异步等待，不丢任务，不无限扩容。
- 索引每批最多 100 个资产或 5 MiB 文本，先到者提交，避免超大事务。
- UI 写操作优先级高于索引批次；索引每提交一批主动让出队列。
- 读事务不得跨 `await` 长时间持有 DataReader；先映射 DTO 再返回。
- `SQLITE_BUSY` 只允许 100/250/500 ms 三次带取消的重试。

## 7. WAL 与存储维护

- 使用包含 SQLite WAL 并发修复的运行时版本。启动读取 `sqlite_version()`；允许 `>=3.51.3`，或官方修复回移版本 `3.50.7/3.44.6`。其他 3.7.0–3.51.2 版本禁止进入并发索引，构建集成测试必须促使依赖升级而不是在 UI 静默降级。
- 保持默认自动 checkpoint；应用空闲且无扫描/Agent 时执行 `PRAGMA wal_checkpoint(PASSIVE)`。
- 应用正常退出且无活跃读事务时可执行 `TRUNCATE` checkpoint，最多等待 2 秒，失败不阻塞退出。
- 数据库软上限 750 MiB、硬上限 1 GiB。软上限清理搜索缓存并提示；硬上限停止正文索引但继续元数据更新。
- 删除索引后不立即 VACUUM。仅当空闲、外接电源、距上次 VACUUM 超过 30 天且 freelist/page_count > 0.30 时提示用户执行；V1.3 不自动阻塞式 VACUUM。
- 日志 10 MiB×5 文件；迁移备份最多 3；内存预览 LRU 最多 20 项或 10 MiB；搜索缓存最多 100 查询、TTL 5 分钟。
