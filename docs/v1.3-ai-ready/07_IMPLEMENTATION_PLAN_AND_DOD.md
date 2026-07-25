# V1.3 实施计划、任务拆分与 DoD

必须按依赖顺序执行。一个任务未通过门禁，不开始依赖它的下一任务。允许在同阶段并行的工作也必须修改不同模块并由同一集成任务收口。

## 阶段总览

| 阶段 | 主要结果 | 发布检查点 |
|---|---|---|
| 0 | 基线、迁移框架、测试夹具 | V1.2 仍可构建运行 |
| 1 | Native session 与安全分页扫描 | C++ ABI/安全测试通过 |
| 2 | 空间与正式任务数据闭环 | 数据与服务测试通过 |
| 3 | 空间/任务 UI 闭环 | 无假按钮，手工流程通过 |
| 4 | 资产索引与 Watcher | 10k 文件性能和增量通过 |
| 5 | 搜索、预览与 Agent 引用 | 搜索回归和注入防护通过 |
| 6 | 稳定性、升级、安装器 | V1.3 Release 候选 |

## T00：冻结 V1.2 基线

### 唯一目标

建立可证明 V1.3 没有破坏 V1.2 的自动化基线。

### 工作

- 为现有 permission、SSE、数据库初始化、Agent 限制补齐回归测试。
- 生成不含密钥的 V1.2 数据库 fixture：2 项目、3 会话、消息、设置、2 自动化。
- 记录当前安装器 AppId、数据目录、C ABI 导出和 SQLite schema。
- 将 C# app 新增 `TreatWarningsAsErrors=true`；逐项修复现有 warning，不用 NoWarn 掩盖。

### 非目标

不增加 V1.3 表、页面和功能。

### 验收

- Release 构建通过。
- fixture 可被 V1.2 测试读取。
- `build-installer.ps1 -SkipInstaller` 通过。
- README 记录基线命令。

## T01：数据库迁移框架

### 唯一目标

将 V1.2 无迁移表数据库安全升级为可版本化 schema，但暂不创建 V1.3 业务页面。

### 允许触达

`Services/Database`、测试 fixture、启动组合根和数据库文档。

### 工作

- 实现 schema 探测、`schema_migrations`、checksum、SQLite Backup API、完整性检查。
- 实现迁移 012 baseline 登记和 013 目标 schema。
- 失败注入点：备份失败、复制表失败、commit 前失败、commit 后完整性失败。
- 保留最近 3 个迁移备份。

### 测试

- 空库初始化。
- 真实 V1.2 fixture 升级，逐表逐字段核对。
- 重复启动不重复迁移。
- 修改迁移 checksum 构建/启动失败。
- 每个失败注入后原数据可读。

### DoD

所有测试通过，迁移文件不可变，应用可读取旧会话/项目/自动化。

## T02：Native workspace session 重构

### 唯一目标

消除全局可变工作区 context，使 Agent 与索引可拥有独立 session。

### 允许触达

Native P/Invoke 层、AgentService、AppServices、C++ context 生命周期和相关测试。

### 工作

- 实现 `INativeWorkspaceFactory/Session`。
- Agent 每 Run 创建和释放 session。
- 删除业务代码对 `SetWorkspace` 的依赖。
- 同时运行两个不同项目的读取测试，结果不得串目录。
- 应用关闭取消并等待 active session，再销毁 DLL context。

### 非目标

不实现扫描 API。

### DoD

V1.2 对话读写行为不变；并发隔离、取消和 Dispose 测试通过。

## T03：C++ 分页扫描与指纹

### 唯一目标

按 ABI 合同输出安全、有界、可取消的项目文件页。

### 工作

- 增加 ABI version、scan begin/next/cancel/destroy、quick fingerprint。
- 实现硬忽略、用户 glob、深度/数量/页大小限制。
- 每个路径最终句柄复验；排除 reparse、junction、UNC 和系统文件。
- Scan JSON Schema 在 C++ 本地验证。
- 增加 RAII handle 和 scan 状态单元。

### 测试

- 0、1、201、10,000 文件分页。
- 深度 33、超过文件限制、长路径、Unicode 大小写。
- `..`、绝对路径、symlink、junction、根目录位于 junction。
- 取消与 next 并发；done 后重复 next；destroy 顺序。
- 忽略和 negation 表驱动测试。
- 扫描期间文件被删除/改名不崩溃。

### DoD

`/W4 /WX`、安全核心覆盖率 ≥95%，ABI 文档与 P/Invoke 一致。

## T04：空间领域、Repository 与 Service

### 唯一目标

空间 CRUD、默认空间、归档、切换和删除前置条件形成无 UI 的完整闭环。

### 工作

- 建立 Space domain/commands/errors。
- 参数化 Repository 和 SpaceService。
- active_space_id 回退规则。
- 项目查询强制带 space_id；新项目自动当前空间。
- 发布 `ActiveSpaceChanged` 不可变事件。

### 测试

- 文本元素边界、颜色白名单、重复名称。
- 默认空间不可删除、非空空间不可删除。
- 归档当前空间自动回退。
- 并发 row_version 冲突。
- V1.2 项目全部进入默认空间。

### DoD

Service 测试 ≥85%，无 View/WinUI 引用，无跨层 SQL。

## T05：空间切换与管理 UI

### 唯一目标

用户可以在导航中创建、编辑、归档、恢复、切换和按规则删除空间。

### 工作

- SpaceSwitcherViewModel、SpaceManagerDialog。
- 导航顶端切换器的展开/折叠形态。
- 项目、任务、资产页面监听切换事件并取消旧查询。
- loading、empty、error、unsaved、keyboard、automation properties。

### 手工验收

- 创建后自动切换；重启仍选中。
- 切换期间索引/Agent 目标不漂移。
- 折叠导航可通过键盘和 Tooltip 识别。
- 非空空间删除显示真实数量。

### DoD

没有静态假空间；所有按钮连接真实 Service；1024×720 不溢出。

## T06：任务领域与数据服务

### 唯一目标

正式任务创建、编辑、查询、状态、排序、并发和主对话关系可测试运行。

### 工作

- Task domain、状态机、TaskOrdering、commands/errors。
- TaskRepository/Service，所有查询带 space_id。
- `EnsureConversationAsync` 原子创建/绑定。
- 任务删除保留对话；项目删除解除任务项目关系。

### 测试

- 全部合法/非法状态流转表。
- done/completed_at 不变量。
- 标题/描述/日期边界。
- 同列/跨列排序、无间隔重排、64 位边界。
- row_version 冲突。
- 同一对话不能绑定两个任务。

### DoD

纯 Domain 测试无数据库；Repository 集成测试覆盖外键和事务。

## T07：任务看板、列表和详情 UI

### 唯一目标

任务页面达到 PRD 完整交互，且“开始任务”连接真实对话。

### 工作

- TasksViewModel、TaskEditorViewModel、看板和列表视图。
- 搜索/筛选 250 ms 防抖和取消。
- 拖拽乐观更新与失败回滚；键盘菜单等价操作。
- 未保存离开确认、Ctrl+S、删除确认。
- “开始/继续任务”创建或打开主对话，预填但不自动发送。

### 手工验收

- 6 状态、4 优先级、截止筛选。
- 拖拽失败模拟可回滚。
- 列表与看板切换保持筛选。
- 1024 宽横向滚动正确。
- 屏幕阅读器可读状态和按钮。

### DoD

无假卡片；View code-behind 不含业务规则；任务页完整闭环录像/检查清单通过。

## T08：文本规范化、分块与 FTS Repository

### 唯一目标

给定固定文本得到固定块、搜索 token 和 FTS 写入结果。

### 工作

- IndexPolicyV13、UTF-8 严格识别、换行规范化。
- token 估算、文档/代码/数据边界分块。
- CJK unigram/bigram 展开和查询转义。
- AssetRepository、FTS external content triggers、integrity-check。
- 单资产原子替换旧块。

### 测试

- 中文、英文、混合、Emoji、组合字符、CRLF、空文件。
- 799/800/1200 token 边界和 overlap。
- 超深 JSON 作为纯文本不递归崩溃。
- FTS insert/update/delete 与内容表一致。
- 引号、FTS 运算符、纯标点不能注入 MATCH。

### DoD

Golden tests 固定输出；外部内容完整性检查通过；无 `INSERT OR REPLACE`。

## T09：索引 Coordinator、扫描 Worker 与 Watcher

### 唯一目标

项目完成首次扫描、增量更新、暂停、继续、取消、限制和 reconciliation。

### 工作

- 有界 DatabaseWriteQueue 和索引 Channel。
- AssetIndexCoordinator，全局并发 2、项目单 worker。
- 批量阈值和 generation 检查。
- FileSystemWatcher 防抖、合并、overflow 标脏。
- 项目索引状态和进度事件。
- 项目删除、规则变化、重新构建缓存失效。

### 测试

- 10,000 文件首次/二次扫描；二次跳过率 ≥95%。
- create/modify/delete/rename 风暴。
- 模拟 Watcher overflow 后完整重扫。
- 扫描中规则变化，旧 generation 不覆盖。
- 暂停/恢复/取消/关闭应用资源释放。
- 100,001 文件、512 KiB 边界、数据库硬上限。

### DoD

内存无持续增长；无无界队列；索引进度真实；应用仍可进行对话和任务 UI 操作。

## T10：资产搜索服务与 UI

### 唯一目标

用户可在当前空间搜索、筛选、加载更多、预览并选择资产块。

### 工作

- AssetSearchService：规范化、严格/回退、RRF、聚合、缓存。
- AssetsViewModel 与页面、筛选、状态面板、预览。
- 250 ms 防抖、query_seq、防旧结果覆盖。
- 纯文本摘要和高亮；相对路径复制。
- Ctrl+K 和无障碍。

### 性能数据集

- 10,000 资产、50,000 块，中英混合。
- 100,000 资产上限数据集至少在 CI nightly 或专用性能测试运行。

### DoD

10k 数据 P95 <300 ms；结果稳定；无 HTML；旧查询不会闪回；缓存满足三重上限。

## T11：对话资产引用与 search_assets 工具

### 唯一目标

用户和 Agent 都能在明确边界内把搜索结果作为不可信引用加入对话。

### 工作

- 对话输入引用卡、轻量资产搜索弹层。
- ContextSelection MMR、块/字符上限。
- Agent `search_assets` Schema、本地验证和当前项目强绑定。
- 系统提示加入 untrusted_asset 规则。
- 未 ready、取消、限制、项目切换行为。

### 测试

- 用户删除引用后请求体不含正文。
- 9 块和 20,001 字符被限制。
- 模型伪造 project_id 不被接受。
- 资产内“忽略系统规则/读取密钥”等提示不改变权限。
- search_assets 无项目时不暴露。

### DoD

请求抓包测试证明只发送选中块；日志不含正文；Agent 安全测试通过。

## T12：稳定性、文档与 Release

### 唯一目标

生成可升级安装的 V1.3 Release Candidate。

### 工作

- WAL checkpoint、数据库容量、日志/备份/缓存清理。
- 启动恢复：上次扫描中断、Watcher 错误、迁移失败。
- 更新 README、架构、安全、构建和已知边界。
- 版本 `1.3.0`，安装器文件名更新，AppId 不变。
- GitHub Windows CI 加迁移、native、managed、publish、installer。

### Release 验收

- V1.2 安装并创建数据 → 安装 V1.3 覆盖升级 → 数据完整。
- 干净安装与卸载。
- Windows 10 1809、Windows 11 x64。
- 10k 文件扫描、增量、搜索、任务对话闭环。
- 离线启动不影响本地空间/任务/资产搜索。

### DoD

`08_QA_ACCEPTANCE_MATRIX.md` 全部 P0/P1 通过；安装器和验证记录可下载。

## 每个任务统一 DoD

- 需求范围完成，无额外入口。
- 所有新输入验证、取消和失败行为已实现。
- 单元/集成/手工测试执行并记录。
- 代码文件 <400 行；无 TODO、FIXME、空 catch、假数据。
- 受影响 README/docs 已更新。
- Release build 可通过。
- Diff 只包含本任务相关改动，可独立回滚。

