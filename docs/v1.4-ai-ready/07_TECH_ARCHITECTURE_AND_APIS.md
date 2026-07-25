# 技术架构与内部 API

## 1. 组件边界

```text
WorkPilot.App (C# WinUI 3)
  Views -> ViewModels -> Application Services
                       -> Domain Services
                       -> Adapters
                            |-> NativeInterop -> WorkPilot.Core (C++20)
                            |-> SQLite repositories
                            |-> GitHub/Notion HTTP adapters
                            |-> MCP session/transports
```

### C++ Core 必须拥有

- 路径规范化、受根目录约束的安全解析、文件哈希和原子文件操作。
- ZIP 条目安全判定与流式解压边界（可调用受审计库，但最终策略在 Core）。
- stdio 进程启动、Windows 命令行参数编码、Job Object 生命周期。
- 网络目标 IP 分类与最终 SSRF 地址判定。
- 风险策略的纯函数核心与不可绕过的执行许可 token。

### C# 必须拥有

- WinUI 页面/ViewModel、导航和状态投影。
- 专家/技能应用服务、SQLite repositories 和迁移编排。
- 内置连接器适配、MCP JSON-RPC 会话、OAuth 浏览器流程。
- Tool catalog、prompt compiler 编排、审计与诊断。

不得在 C# 复制一份“近似”的路径/进程/SSRF/最终权限算法。Native Core 返回结构化结果与稳定错误码。

## 2. 建议目录

```text
src/
  WorkPilot.App/
    Features/Experts/{Views,ViewModels,Services}
    Features/Skills/{Views,ViewModels,Services}
    Features/Connections/{Views,ViewModels,Services}
    Features/SecurityCenter/{Views,ViewModels,Services}
    Domain/{Experts,Skills,Capabilities,Security}
    Infrastructure/{Database,Secrets,Http,Connectors,Mcp,Telemetry}
    Interop/
  WorkPilot.Core/
    include/workpilot/{path,archive,process,network,policy}/
    src/{path,archive,process,network,policy}/
tests/
  WorkPilot.App.UnitTests/
  WorkPilot.App.IntegrationTests/
  WorkPilot.Core.Tests/
  WorkPilot.ProtocolTests/
  WorkPilot.UITests/
```

每个 feature 内不再建立第二套通用基础设施。跨 feature 的稳定抽象放 Domain/Infrastructure；只有一个调用方的接口保留在 feature 内。

## 3. 核心类型

```csharp
public sealed record ExpertRevisionId(Guid Value);
public sealed record SkillVersionId(Guid Value);
public sealed record CapabilityId(Guid Value);
public sealed record SchemaHash(string Sha256Hex);

public enum RiskLevel { Low, Medium, High, Critical }
public enum PolicyDecisionKind { Allow, Deny, RequireConfirmation }

public sealed record CapabilityDescriptor(
    CapabilityId Id,
    string StableName,
    CapabilitySource Source,
    JsonElement InputSchema,
    SchemaHash SchemaHash,
    RiskAssessment Risk,
    CapabilityLimits Limits);

public sealed record InvocationRequest(
    Guid RunSnapshotId,
    CapabilityId CapabilityId,
    SchemaHash ExpectedSchemaHash,
    JsonElement Input,
    Guid CorrelationId);
```

领域 ID 不得在业务层使用裸字符串互换。JSON 文档必须在 adapter 边界解析成受限 DTO；禁止 `dynamic`。

## 4. 应用服务接口

```csharp
public interface IExpertService
{
    Task<Page<ExpertSummary>> SearchAsync(ExpertQuery query, CancellationToken ct);
    Task<ExpertEditorModel> GetEditorAsync(ExpertId id, CancellationToken ct);
    Task<SaveExpertResult> SaveAsync(SaveExpertCommand command, CancellationToken ct);
    Task<ArchiveResult> ArchiveAsync(ExpertId id, long rowVersion, CancellationToken ct);
    Task<RunPreview> PreviewAsync(ExpertId id, string message, CancellationToken ct);
}

public interface ISkillPackageService
{
    Task<SkillInspection> InspectAsync(string zipPath, IProgress<InspectionProgress> progress, CancellationToken ct);
    Task<InstallSkillResult> InstallAsync(InspectionToken token, CancellationToken ct);
    Task<UninstallImpact> AnalyzeUninstallAsync(SkillVersionId id, CancellationToken ct);
}

public interface ICapabilityPolicyService
{
    PolicyDecision Evaluate(PolicyContext context, CapabilityDescriptor capability, JsonElement input);
    Task<ExecutionPermit> ConfirmAsync(ConfirmationDecision decision, CancellationToken ct);
}

public interface ICapabilityInvoker
{
    Task<InvocationResult> InvokeAsync(InvocationRequest request, ExecutionPermit permit, CancellationToken ct);
}
```

`ExecutionPermit` 是短生命周期、一次性、不可序列化对象，由 Core 签发并绑定 run/capability/schema/expiry。Adapter 没有 permit 不得发送外部请求或启动进程。

## 5. MCP 接口

```csharp
public interface IMcpTransport : IAsyncDisposable
{
    Task StartAsync(CancellationToken ct);
    ValueTask SendAsync(ReadOnlyMemory<byte> jsonRpcMessage, CancellationToken ct);
    IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync(CancellationToken ct);
    Task StopAsync(StopReason reason, CancellationToken ct);
}

public interface IMcpSession : IAsyncDisposable
{
    McpSessionState State { get; }
    Task<InitializeResult> InitializeAsync(CancellationToken ct);
    Task<CapabilitySnapshot> RefreshCapabilitiesAsync(CancellationToken ct);
    Task<JsonElement> CallToolAsync(string name, JsonElement arguments, CancellationToken ct);
    Task<McpResourceResult> ReadResourceAsync(string uri, CancellationToken ct);
    Task<McpPromptResult> GetPromptAsync(string name, JsonElement args, CancellationToken ct);
}
```

Session 只处理协议，不做产品授权；Invoker 在进入 Session 前完成权限，Session 内仍执行大小、状态与 Schema 防御。Transport 不理解 tools/resources。

## 6. Native ABI

C++/C# 边界使用稳定 C ABI + SafeHandle，所有导出函数：

- 返回 `wp_status`，结果通过拥有明确释放函数的 handle/result buffer 返回。
- UTF-8 字节 + 长度，不依赖 NUL 终止；拒绝无效 UTF-8。
- 不让 C++ 异常越过 ABI；映射到 `category/code/message_key`。
- C# 使用 source-generated P/Invoke；SafeHandle finalizer 只做无阻塞释放。

建议 API：

```text
wp_path_resolve_under_root(root, relative, options, out result)
wp_archive_inspect(zip, limits, out report_handle)
wp_archive_extract_verified(report, staging_root, cancel, out digest)
wp_process_start_stdio(config, limits, out process_handle)
wp_process_stop(process_handle, timeout_ms)
wp_network_classify_endpoint(url, dns_results, mode, out decision)
wp_policy_evaluate(context_json, capability_json, out decision)
```

ABI struct 必须包含 `struct_size` 与 `api_version`；新增字段只追加，不改变既有字段布局。

## 7. 数据库与工作单元

- Repository 不提交事务；Application Service 创建 UnitOfWork。
- 单个 UI 命令最多一个写事务；禁止持有事务等待网络/用户确认。
- 先完成外部测试，再短事务保存投影；需要补偿时使用明确 pending 状态。
- SQLite busy timeout 5 s；写操作通过单写者队列，队列上限 100。
- 查询必须投影所需列；列表不得加载系统提示、manifest 正文或大 JSON。

## 8. 并发与取消

- UI async command 防重入，所有公共 async 方法要求 CancellationToken。
- 不使用 `async void`，事件处理器除外；事件处理器立即委托给可测试命令并捕获错误。
- 后台服务由统一 `IBackgroundTaskRegistry` 管理；应用退出等待最多 5 s 后强制取消。
- Channel/queue 必须有容量和满载行为，禁止 unbounded channel。
- 同一 MCP server 生命周期操作使用异步互斥；不同 server 可并行，全局启动并发 2。
- 对状态更新使用不可变 snapshot + UI dispatcher 批量投影，避免每个 progress 事件触发列表重绘。

## 9. 错误契约

```csharp
public sealed record AppError(
    string Code,
    ErrorCategory Category,
    string MessageKey,
    bool IsRetryable,
    IReadOnlyDictionary<string,string> SafeDetails,
    Exception? DiagnosticException);
```

UI 只使用 Code/MessageKey/SafeDetails。DiagnosticException 进入本地脱敏 logger；不得把第三方正文塞入异常 message。禁止空 `catch`；取消异常转换为 `UserCancelled` 而不是 Error toast。

## 10. 可观测性

使用结构化事件：

- 生命周期：source_id、old_state、new_state、duration、error_category。
- 调用：capability_id、risk、decision、outcome、duration_bucket、size_bucket。
- 包验证：rule_id、result、file_count_bucket、size_bucket。

禁止字段：prompt、content、arguments、response、token、secret、authorization、cookie、full_path、url_query。Logger 写入前再运行键名与值模式脱敏。

## 11. 性能实现要求

- 专家/技能/能力列表使用增量加载和虚拟化。
- JSON Schema 编译按 schema hash 缓存，最多 256 项/32 MiB。
- Skill 入口内容按版本缓存，最多 50 项/16 MiB；文件变化由哈希检测并使技能 invalid。
- MCP 目录刷新生成 diff，禁止全表 delete+insert；单事务 upsert 后发布一次 UI 更新。
- 不在 UI 线程做文件哈希、ZIP、SQLite、DNS、HTTP、JSON 大文档解析。

## 12. 依赖规则

- 新 NuGet/vcpkg 依赖必须在 ADR 记录用途、许可证、维护状态、二进制大小和替代方案。
- 禁止为单个简单算法引入大型框架。
- 协议与安全解析依赖固定版本并启用 Dependabot/等价审计；升级需跑完整协议/安全语料测试。
- 不允许 View 引用 Repository、HTTP client、SecretService 或 Native ABI。
