# 连接器与 MCP 协议规范

## 1. 连接器统一契约

每个内置连接器实现：

```text
IConnectorDefinition
  Id, Version, CapabilityManifest
  ValidateConfiguration()
  TestConnectionAsync()
  CreateSession(account, secretLease)

IConnectorSession
  GetHealthAsync()
  InvokeAsync(operationId, validatedInput, cancellationToken)
```

`InvokeAsync` 只能接收通过 JSON Schema 和本地策略的输入。返回 `ConnectorResult{structuredContent,textSummary,itemCount,isTruncated,diagnostics}`；diagnostics 不含正文或秘密。

不允许运行时反射加载第三方 DLL。GitHub/Notion definition 和操作清单作为版本化只读资源随应用发布，启动时验证资源 SHA-256。

## 2. 外部调用算法

### 2.1 执行管线

1. 解析稳定能力 ID，读取 RunSnapshot。
2. 校验来源仍 enabled，Schema 哈希仍匹配。
3. 校验输入 Schema；移除未声明属性，`additionalProperties=false` 时返回错误。
4. 运行本地风险引擎和授权决策。
5. 获取短生命周期 SecretLease；秘密只在构造授权头时展开。
6. 获取全局并发槽和账号并发槽，进入 token bucket。
7. 发送请求，应用超时、响应大小和重定向限制。
8. 验证响应类型/Schema，生成有界结果。
9. 更新健康状态、限流器和审计；释放秘密与并发槽。

### 2.2 限流、重试、熔断

- 每账号并发 4，全局并发 12，等待队列各 100；满时立即返回 `QueueFull`。
- token bucket 参数由连接器定义，默认 capacity 10、refill 2/s；服务器 rate-limit 响应可降低但不能无限提高。
- 单请求连接超时 10 s、总超时 30 s；长轮询/MCP 另行定义。
- 仅 GET/HEAD 或显式 idempotent 操作可对 429/502/503/504 与连接前失败重试。
- 重试延时为 250 ms、1,000 ms、3,000 ms，加 0–20% 确定性可注入 jitter，最多 3 次；尊重合理的 `Retry-After`，上限 30 s。
- 写操作只有服务支持并实际发送稳定 idempotency key 时才可自动重试；V1.4 GitHub/Notion 写操作默认不自动重试。
- 60 s 内连续 5 次可归因服务/网络失败则熔断 open 30 s；随后 half-open 只放行 1 次。401/403 不进入熔断计数，而更新凭据/权限状态。

### 2.3 分页与缓存

- 每操作最多 10 页、500 条、原始响应累计 5 MiB，以最先达到者停止并标记 truncated。
- 游标只在单次调用内存中存在，不写入日志。
- 只读 GET 可缓存 10 分钟；每账号最多 200 项/50 MiB，LRU 淘汰。
- 缓存键使用 connector/account/operation/canonical-input 哈希；审计不保存该哈希。
- 写成功后按连接器定义失效相关缓存；无法确定时清空该账号缓存。

## 3. GitHub/Notion 适配要求

### 3.1 GitHub

- 对 owner、repo、path、issue number 做独立类型校验，不拼接未编码路径。
- 文件读取只接受文本；二进制或 >1 MiB 返回元数据和拒绝原因。
- 分支/ref 必须作为单独编码参数。
- 创建 Issue 确认摘要包含 owner/repo、标题、正文字符数；评论包含 issue number 和正文字符数。
- 连接测试只读取当前身份和授权可见信息，不枚举全部仓库。

### 3.2 Notion

- page/database ID 解析为规范 UUID 表示后使用。
- block 转换器按类型白名单处理，未知类型输出 `[Unsupported block: type]`。
- rich text 只保留纯文本、链接目标和基础注释，不渲染 HTML。
- 数据库查询只允许受控 filter/sort DTO 转换，拒绝任意 JSON 透传。
- append 仅允许 paragraph、heading、bulleted/numbered item、to_do、code；最多 100 blocks/20,000 字符。

## 4. MCP 兼容范围

目标协议 `2025-11-25`。初始化接受服务选择：

- `2025-11-25`：完整 V1.4.2 支持。
- `2025-06-18`：兼容模式，仅 tools/resources/prompts 和基础 OAuth；UI 标记兼容模式。
- 其他版本：停止初始化，显示客户端支持列表，不尝试猜测。

支持服务能力：tools、resources、resource templates、prompts、logging、progress、cancellation、listChanged。支持客户端能力：form/URL elicitation 仅按第 11 节；不支持 roots、sampling、durable tasks。

## 5. JSON-RPC 会话层

### 5.1 约束

- JSON-RPC 版本必须为 `2.0`。
- request ID 为每会话单调递增十进制字符串；pending map 最大 64。
- 入站消息最大 4 MiB，JSON 深度 64、节点 200,000、字符串 1 MiB。
- 未知 response ID 记录一次脱敏诊断并忽略；重复 response 判定协议错误。
- notification 队列最大 256；进度更新按 token 合并，UI 每 200 ms 最多刷新一次。
- 单调用默认 60 s；list 30 s；initialize 10 s；用户取消立即标记本地取消并发送协议取消通知（服务支持时）。

### 5.2 初始化

1. transport ready。
2. 发送 `initialize`，包含 target protocol、clientInfo、明确 client capabilities。
3. 10 s 内等待响应，校验 protocolVersion、serverInfo、capabilities。
4. 协议可接受则发送 `notifications/initialized`。
5. 拉取 tools/resources/prompts 列表；各列表失败不必让整个服务退出，但对应能力进入 degraded。
6. 生成 capability hash；与上次不同则运行差异和 stale 授权算法。

初始化期间不允许模型调用。服务在 initialized 之前发送普通请求时返回协议错误；合法 notification 可有界缓存或忽略并诊断。

### 5.3 列表同步

每类列表最多 50 页/5,000 项，cursor 不得循环；维护已见 cursor 集，重复立即停止。按服务端 name 去重，重复同名不同 Schema 判协议冲突。

`list_changed` 使用 500 ms 防抖、最长 2 s 强制刷新。同类刷新只允许一个在途，后续合并。刷新失败保留上次目录并标 `stale`；stale 能力默认不可新授权，已运行快照在安全层再次检查。

## 6. stdio transport

### 6.1 启动规则

- executable 必须是用户选择的现有本地绝对文件，扩展名 `.exe`，经过 C++ PathCore 规范化。
- 不允许 `cmd.exe`、`powershell.exe`、`pwsh.exe`、`wscript.exe`、`cscript.exe`、`mshta.exe` 作为宿主。
- 参数以字符串数组直接传给 `CreateProcessW` 的安全参数编码器；禁止 shell、管道、重定向、环境变量命令替换。
- cwd 必须存在且是本地目录；默认 executable 所在目录，不允许 UNC。
- 环境变量总数 ≤ 64、单值 ≤ 8 KiB、总计 ≤ 32 KiB。默认只继承最小系统 allowlist；秘密通过 SecretLease 注入子进程环境。

创建 Windows Job Object，设置 `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`；主进程退出、服务禁用或启动取消时终止整个作业。一个服务只允许一个活动进程，全局最多 5 个 MCP 子进程。

### 6.2 流处理

- stdin/stdout 传 MCP JSON-RPC，一条消息一行 UTF-8，消息内不得含未转义换行。
- stdout 只允许协议消息；连续 3 条非法行或单条超限则终止服务。
- stderr 独立读取，1 MiB 环形缓冲，按行脱敏；不得阻塞子进程。
- 写队列最大 64/4 MiB；满时不再接受调用。
- graceful stop：停止新请求 → 取消在途 → 关闭 stdin → 等待 2 s → 终止 Job Object。

## 7. Streamable HTTP transport

### 7.1 Endpoint 与 SSRF

- 默认只允许 HTTPS，URL 不得含 userinfo、fragment 或疑似秘密 query 参数。
- 显式“本地模式”只允许 `http://127.0.0.1`、`http://[::1]` 或解析后仅 loopback 的 localhost。
- 每次连接与每次重定向前解析 DNS；公共模式拒绝 loopback、RFC1918、link-local、CGNAT、multicast、unspecified、保留地址和云元数据目标。
- 最多 3 次重定向；每跳重新校验，禁止 HTTPS 降级 HTTP，禁止跨主机转发 Authorization。
- 校验证书链与主机名，不提供“忽略证书错误”。
- 客户端请求设置 `Origin`；本地服务的安全责任仍在服务端，客户端不降低自身校验。

### 7.2 HTTP 语义

- MCP endpoint 使用 POST 发送消息，`Accept` 同时包含 `application/json` 与 `text/event-stream`。
- 正确处理 JSON 响应与 SSE；SSE 单事件最大 4 MiB，空闲 90 s 超时，心跳不进入模型。
- 若服务返回 session ID，后续请求原样回传；仅存在内存，退出即丢弃。
- 服务支持 DELETE session 时，正常禁用/删除发送一次；失败不阻塞本地删除。
- GET SSE 监听为可选能力；断线重连指数退避 1/2/5/10/30 s，最多 5 次，用户动作可立即重试。

## 8. OAuth 2.1

只实现 Authorization Code + PKCE S256。

流程：

1. 从 401 `WWW-Authenticate` 或规范 well-known 发现 Protected Resource Metadata。
2. 获取 authorization server metadata；支持 RFC 8414 或 OIDC discovery，严格校验 issuer。
3. 生成 32 随机字节 state、32 随机字节 verifier，challenge = BASE64URL(SHA256(verifier))。
4. 使用 Client ID Metadata Document（服务支持时）；否则允许规范动态注册。静态 client ID 由产品配置提供，不让用户粘贴 client secret 到普通设置。
5. 打开系统浏览器；回调使用随机端口 loopback，接收单次请求后关闭。
6. 校验 state、回调路径、错误字段和 code；token 请求使用相同 redirect URI、verifier，并携带 canonical MCP resource indicator。
7. 校验 token 响应类型、scope 不超请求；如可验证 audience/resource，必须匹配 MCP 服务。
8. access/refresh token 立即写入 DPAPI，内存临时缓冲清零。

规则：认证和 token endpoint 必须 HTTPS；PKCE 元数据不支持 S256 时拒绝；不进行 token passthrough；不得把第三方服务 token 交给 MCP 服务。刷新只允许一次并发，`invalid_grant` 进入 expired，要求用户重新连接。

## 9. tools、resources、prompts

### 9.1 Tools

- `tools/list` 输入输出均 Schema 校验。
- 服务 annotations 作为不可信元数据保存；本地风险只能保持或提高，不能因 annotation 降低。
- `tools/call` 前校验本地保存的 schema_sha256；变化则暂停调用。
- Tool error 与协议 error 分开：前者可作为受控结果展示，后者更新健康状态。

### 9.2 Resources

- 支持 list、templates、read；用户或模型只能读取已授权 URI。
- URI scheme 默认允许 `file`（仅服务返回、仍不直接访问本地）、`https` 和服务自定义不透明 scheme；客户端不自行解引用。
- 单 read 1 MiB，文本 UTF-8；binary 只返回媒体类型、大小和“未注入”状态。
- resource 内容按不可信外部数据封装，不自动保存为资产；用户可显式“保存为资产”，沿用 V1.3 导入权限。

### 9.3 Prompts

- 支持 list/get；参数必须符合服务声明。
- 返回消息预览，用户确认后才加入对话上下文。
- 服务 prompt 不能成为系统消息；统一降为带来源的外部上下文。
- prompt 引用的 embedded resource 继续应用资源大小和类型限制。

## 10. 能力变化算法

以 `kind + remote_name + canonical_schema + security_relevant_annotations` 计算 schema_sha256。

- 新增：discovered，默认未授权。
- 描述/title 改变：更新展示，不撤销授权。
- input Schema、readOnly/destructive/openWorld/idempotent 等关键 annotation 改变：stale，撤销会话授权并要求复核。
- 删除：removed，立即从新目录移除。
- 同名恢复且哈希相同：恢复原批准状态；哈希不同视为新增待批准版本。

## 11. Elicitation

V1.4.2 支持受限 form 与 URL elicitation：

- form 最多 20 字段，只允许 string/number/integer/boolean/enum；禁止 password、secret、token、信用卡、身份证等敏感字段。
- 用户可接受、拒绝或取消；服务不得假定填写。
- URL 模式先显示完整 origin、目的说明和随机 elicitation ID，再由用户打开系统浏览器。
- 第三方凭据只能在浏览器与目标服务/MCP 服务间交换，不经过 WorkPilot 表单或模型。
- 回到应用后只向 MCP 服务报告协议允许的完成状态，不采集浏览器内容。

任何不满足 Schema、来源未授权、URL 非 HTTPS/非显式 loopback 或描述疑似诱导秘密的 elicitation 直接拒绝并审计。

## 12. 错误分类

统一类别：`Configuration`、`Authentication`、`Authorization`、`RateLimited`、`Network`、`Tls`、`Protocol`、`Schema`、`PolicyDenied`、`UserCancelled`、`Timeout`、`ServerFailure`、`ResourceLimit`、`Internal`。

外部原始错误映射到稳定用户错误码；原始正文不落盘。只有 `Network/RateLimited/Timeout/ServerFailure` 且操作幂等时 UI 提供重试。
