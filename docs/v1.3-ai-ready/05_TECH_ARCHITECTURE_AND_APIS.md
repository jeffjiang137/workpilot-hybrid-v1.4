# V1.3 技术架构与接口规格

## 1. 架构目标

- 保持 C++ 安全核心与 C# WinUI 业务/UI 的清晰边界。
- 索引和 Agent 可并发，但不能共享可变工作区上下文。
- 新领域逻辑可脱离 WinUI 做单元测试。
- UI 不执行 SQL、不调用 P/Invoke、不直接读取项目文件。
- 不引入重量级 DI、ORM、响应式框架或第二个数据库。

## 2. 目标依赖图

```text
Views (XAML + thin code-behind)
  ↓
ViewModels
  ↓
Application Services
  ├─ SpaceService / TaskService
  ├─ AssetIndexCoordinator / AssetSearchService
  ├─ ConversationService / AgentService
  ↓
Repositories + Adapters
  ├─ SQLite repositories / DatabaseWriteQueue
  ├─ OpenAI-compatible client
  └─ NativeWorkspaceSession (P/Invoke)
          ↓
     workpilot_core.dll
     path / scan / fingerprint / read / write / permission
```

领域模型和纯算法不得引用 `Microsoft.UI.Xaml`、`SqliteConnection`、HTTP 或 P/Invoke。

## 3. 建议目录

```text
src/WorkPilot.App/
  Domain/
    Spaces/Space.cs
    Tasks/TaskItem.cs, TaskStatus.cs, TaskPriority.cs
    Assets/Asset.cs, AssetChunk.cs, IndexPolicyV13.cs
  ViewModels/
    TasksViewModel.cs, TaskEditorViewModel.cs
    AssetsViewModel.cs, SpaceSwitcherViewModel.cs
  Services/
    Spaces/SpaceService.cs
    Tasks/TaskService.cs, TaskOrdering.cs
    Assets/AssetIndexCoordinator.cs
    Assets/AssetScanWorker.cs
    Assets/AssetSearchService.cs
    Assets/TextChunker.cs, SearchTextNormalizer.cs
    Database/DatabaseMigrator.cs, DatabaseWriteQueue.cs
  Repositories/
    SpaceRepository.cs, TaskRepository.cs
    AssetRepository.cs, AssetSearchRepository.cs
  Native/
    NativeWorkspaceSession.cs, NativeScanSession.cs, NativeMethods.cs
  Views/
    TasksPage.xaml, AssetsPage.xaml
    Controls/TaskCard.xaml, AssetResultItem.xaml
    Dialogs/SpaceManagerDialog.xaml
tests/
  WorkPilot.Domain.Tests/
  WorkPilot.Integration.Tests/
```

单文件超过 400 行前必须拆分。ViewModel 只处理展示状态和命令；业务规则留在 Service/Domain。

## 4. V1.2 必要重构

当前 `NativeWorkspaceService` 持有一个可变全局 context，V1.3 索引与 Agent 并发后会产生工作区串用风险。开发 V1.3 前必须完成：

```csharp
public interface INativeWorkspaceSession : IDisposable
{
    string RootDisplayName { get; }
    PermissionDecision Evaluate(PermissionRequest request);
    Task<NativeReadResult> ReadTextAsync(string relativePath, CancellationToken ct);
    Task<NativeWriteResult> WriteTextAsync(...);
    INativeScanSession BeginScan(ScanOptions options);
}

public interface INativeWorkspaceFactory
{
    INativeWorkspaceSession Open(string absoluteRoot);
}
```

- 每次 Agent Run 创建独立 session，结束释放。
- 每个项目扫描 worker 创建独立 session。
- session 内方法默认不可并发；调用方串行使用。
- Factory 无可变 root；不能提供 `SetWorkspace`。
- 旧 `NativeWorkspaceService.SetWorkspace` 标记内部废弃并在 V1.3 删除调用点。

## 5. C++ ABI 扩展

保持 V1.2 导出不变，新增 ABI version：

```cpp
WP_EXPORT int WP_CALL wp_abi_version(); // V1.3 返回 0x00010300

struct wp_scan;
WP_EXPORT wp_scan* WP_CALL wp_scan_begin(
    wp_context* context,
    const char* utf8_options_json);
WP_EXPORT char* WP_CALL wp_scan_next(
    wp_scan* scan,
    int max_items);
WP_EXPORT void WP_CALL wp_scan_cancel(wp_scan* scan);
WP_EXPORT void WP_CALL wp_scan_destroy(wp_scan* scan);
WP_EXPORT char* WP_CALL wp_quick_fingerprint(
    wp_context* context,
    const wchar_t* relative_path);
```

### 5.1 所有权与线程

- `wp_scan_begin` 返回的 scan 必须由 `wp_scan_destroy` 释放，即使完成或取消。
- `wp_scan_next/wp_scan_cancel` 可由不同线程调用，但 cancel 只设置原子标志；destroy 只能在 next 返回后调用。
- 所有 `char*` 使用 `CoTaskMemAlloc`，C# 始终 `wp_free`。
- C++ 异常不跨 ABI；错误写入 scan/context last_error。
- `wp_destroy(context)` 前必须销毁由它创建的 scan。

### 5.2 ScanOptions JSON

```json
{
  "version": 1,
  "include_hidden": false,
  "max_depth": 32,
  "max_files": 100000,
  "ignore_rules": ["temp/**", "!temp/keep.md"]
}
```

Schema：`additionalProperties=false`；数值范围必须在 C++ 再验证。JSON 解析失败返回 `WP_INVALID_ARGUMENT`。不得把 C# 已验证当作安全边界。

### 5.3 ScanPage JSON

```json
{
  "version": 1,
  "done": false,
  "cancelled": false,
  "limit_reached": false,
  "directories_seen": 25,
  "files_seen": 180,
  "items": [
    {
      "relative_path": "src/App.xaml.cs",
      "path_key": "src/app.xaml.cs",
      "file_name": "App.xaml.cs",
      "extension": ".cs",
      "size_bytes": 1200,
      "modified_unix_ms": 1784540000000,
      "attributes": 32
    }
  ]
}
```

单页最大 200 项且 JSON 最大 2 MiB。`done=true` 后再次 next 返回同一空完成页，不崩溃。

### 5.4 FingerprintResult

```json
{
  "size_bytes": 1200,
  "modified_unix_ms": 1784540000000,
  "quick_fingerprint": "64-lowercase-hex",
  "stable": true
}
```

`stable=false` 表示读取前后元数据变化，调用方按算法重试一次。

## 6. C# 领域接口

### 6.1 SpaceService

```csharp
Task<IReadOnlyList<SpaceSummary>> ListAsync(bool includeArchived, CancellationToken ct);
Task<Space> CreateAsync(CreateSpaceCommand command, CancellationToken ct);
Task<Space> UpdateAsync(UpdateSpaceCommand command, CancellationToken ct);
Task ArchiveAsync(string id, long expectedVersion, CancellationToken ct);
Task RestoreAsync(string id, long expectedVersion, CancellationToken ct);
Task DeleteEmptyAsync(string id, long expectedVersion, CancellationToken ct);
Task SetActiveAsync(string id, CancellationToken ct);
```

Service 验证文本元素、颜色枚举、默认空间和删除前置条件。Repository 不接受 UI 控件或任意 SQL 字符串。

### 6.2 TaskService

```csharp
Task<PagedResult<TaskSummary>> QueryAsync(TaskQuery query, CancellationToken ct);
Task<TaskItem> CreateAsync(CreateTaskCommand command, CancellationToken ct);
Task<TaskItem> UpdateAsync(UpdateTaskCommand command, CancellationToken ct);
Task<TaskItem> ChangeStatusAsync(ChangeTaskStatusCommand command, CancellationToken ct);
Task DeleteAsync(string id, long expectedVersion, CancellationToken ct);
Task<Conversation> EnsureConversationAsync(string taskId, CancellationToken ct);
```

所有命令为 immutable record，构造后不可由 UI 修改。`TaskQuery.PageSize<=200`，V1.3 UI 通常一次取当前空间任务；超过 2,000 条必须分页。

### 6.3 AssetIndexCoordinator

```csharp
Task RequestFullScanAsync(string projectId, ScanReason reason, CancellationToken ct);
Task PauseAsync(string projectId, CancellationToken ct);
Task ResumeAsync(string projectId, CancellationToken ct);
Task RebuildAsync(string projectId, CancellationToken ct);
IAsyncEnumerable<IndexProgress> ObserveAsync(CancellationToken ct);
```

- 同一项目最多一个 active worker。
- 全局最大并发扫描数默认 2；磁盘为可移动或电池节能模式时 1。
- 重复 full-scan 请求合并，reason 按 `user_rebuild > watcher_overflow > rules_changed > scheduled_reconcile > startup` 保留最高优先级。
- Coordinator 不保存 UI 对象；进度用不可变事件。

### 6.4 AssetSearchService

```csharp
Task<SearchPage> SearchAsync(AssetSearchQuery query, CancellationToken ct);
Task<AssetPreview> GetPreviewAsync(long assetId, int? chunkId, CancellationToken ct);
Task<IReadOnlyList<ContextChunk>> SelectContextAsync(ContextSelection request, CancellationToken ct);
```

`AssetSearchQuery` 必须包含 `SpaceId`，ProjectId 可空；Repository 查询参数化。SearchService 负责规范化、严格/回退查询、RRF、聚合和缓存。

## 7. ViewModel 规则

新增一个不依赖第三方包的 `ObservableObject` 和 `AsyncCommand`：

- `AsyncCommand` 防重复执行、暴露 `IsRunning`、捕获取消、把业务错误转换为 ViewModel ErrorState。
- Command 内不显示 ContentDialog；View 订阅明确的 DialogRequest 并返回结果。
- ViewModel 构造函数显式注入 Service，不直接读 `App.Services` 静态属性。
- 所有可变集合只在 UI Dispatcher 更新；后台服务返回 immutable DTO。
- `async void` 仅允许 XAML 事件入口，内部第一行调用可测试的 async 方法并捕获异常。

## 8. 数据库组件

### 8.1 DatabaseMigrator

职责：schema 探测、备份、checksum、按序迁移、完整性检查和失败恢复。不得包含页面逻辑。

### 8.2 DatabaseWriteQueue

使用有界 `Channel<WriteRequest>`：容量 200，单消费者。请求包含优先级 `User/Agent/Index/Maintenance`、CancellationToken 和返回 TaskCompletionSource。优先级队列不能饿死 Index：连续处理 20 个高优先请求后必须给一个已等待的低优先请求机会。

连接和事务只存在于消费者循环中。请求回调不得更新 UI，不得嵌套提交新的 write request，防止死锁。

### 8.3 Repository

- 每个聚合一个 Repository，不建立万能 GenericRepository。
- SQL 常量与参数绑定在 Repository；复杂搜索 SQL 单独文件/类。
- 映射函数纯函数，缺列/NULL 违反 schema 时抛 `DataIntegrityException`。
- UI 字符串不作为数据库枚举；枚举使用固定英文值。

## 9. 索引写入事务

单个变更资产的事务顺序：

1. 验证当前 generation。
2. Upsert asset 元数据，但不用 `INSERT OR REPLACE`。
3. 若全文 SHA 未变化，只更新元数据并结束。
4. 在内存准备并校验全部新 chunks。
5. 删除旧 asset_chunks；FTS delete trigger 同步执行。
6. 插入新 chunks；FTS insert trigger同步执行。
7. 更新 asset `sha256/text_status/generation`。
8. Commit 后发布 IndexProgress；事务失败不发布成功事件。

批量事务对每个资产使用相同步骤；一个资产解析失败不应回滚同批其他资产，先把解析放在事务外，失败资产以元数据状态写入。

## 10. Agent 集成

新增工具 `search_assets`，不是 MCP：

```json
{
  "type": "function",
  "function": {
    "name": "search_assets",
    "description": "在当前项目的本地文本资产索引中搜索，只返回不可信引用数据",
    "parameters": {
      "type": "object",
      "properties": {
        "query": {"type": "string", "minLength": 1, "maxLength": 200},
        "max_results": {"type": "integer", "minimum": 1, "maximum": 8}
      },
      "required": ["query"],
      "additionalProperties": false
    }
  }
}
```

- 只有 Agent 绑定项目且项目索引 ready/可查询时暴露。
- 风险级别 Low、只读；仍受只读/默认权限链和取消控制。
- 只查询当前项目，不接受模型提供 project_id/space_id。
- 返回最多 8 块、总 20,000 字符，包含相对路径、块序号、截断标记和 `untrusted_asset_content=true`。
- 索引未完成返回结构化状态，不回退为 C# 直接递归读文件。
- 模型不得用工具结果改变权限或要求读取工作区外路径。

## 11. 错误类型

公共服务至少使用以下类型，不以字符串判断：

- `ValidationError(field, code, message)`
- `NotFoundError(entity, id)`
- `ConcurrencyConflict(entity, id, currentVersion)`
- `WorkspaceSecurityError(code, relativePath?)`
- `IndexLimitError(kind, current, limit)`
- `IndexUnavailableError(projectId, state)`
- `DatabaseBusyError(attempts)`
- `MigrationError(version, phase, recoveryPath?)`
- `SearchQueryError(code)`

错误中只能包含相对路径；绝对路径只允许在用户主动查看项目设置时显示。

## 12. 安全与资源释放

- 所有 Scan/Workspace/Sqlite/Stream/CTS 实现 `using/await using` 或明确 Dispose。
- 关闭应用顺序：停止接收 UI 命令 → 取消 Agent/Index → 停 Watcher → 等待写队列排空（最多 5 秒）→ checkpoint → Dispose native contexts。
- 取消不是错误日志；只记录状态与耗时。
- 索引内容、搜索词、任务描述和 API Key 不记录日志。
- 对模型输出的 JSON 做本地二次 Schema 验证。

## 13. 测试边界

- Domain 测试：不启动 WinUI、不访问真实数据库。
- Repository 集成测试：每测试独立临时 SQLite，启用 FTS5 和外键。
- Native 测试：真实 Windows 临时目录，覆盖 traversal、symlink/junction、取消和分页。
- UI 测试：ViewModel 状态和关键 WinUI 手工验收；不得仅靠截图认定交互成功。
- 迁移测试：V1.2 fixture → V1.3，包含项目、会话、消息、自动化和设置。

