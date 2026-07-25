# WorkPilot Hybrid V1.3 AI-Ready 需求包

文档版本：1.0.0  
目标产品版本：WorkPilot Hybrid V1.3  
基线：V1.2 C++ Core + C# WinUI 3  
状态：可进入开发  

## 1. 本阶段唯一目标

在不破坏 V1.2 对话、项目、自动化与文件安全能力的前提下，实现三个真实闭环：

1. **空间**：用户可用空间组织项目，并在全局切换当前空间。
2. **正式任务**：任务具有独立实体、状态、优先级、截止日期、项目关系和关联对话，可从任务启动 Agent。
3. **资产索引与搜索**：对项目工作区进行安全、本地、可增量更新的文本资产索引，并在空间内搜索、筛选、预览和定位文件。

任何未写入本需求包的能力都不允许由 AI Agent 自行增加。

## 2. 文档优先级

出现冲突时按以下顺序执行：

1. `00_README_AND_SCOPE.md`：范围与不可变约束。
2. `06_AI_DEVELOPMENT_RULES.md`：开发、安全和维护规则。
3. `03_DATA_MODEL_AND_MIGRATIONS.md`：数据合同。
4. `04_INDEX_AND_SEARCH_ALGORITHMS.md`：算法合同。
5. `05_TECH_ARCHITECTURE_AND_APIS.md`：模块与接口。
6. `02_UI_AND_INTERACTION_SPEC.md`：视觉、页面和交互。
7. `01_PRD_V1.3.md`：产品行为。
8. `07_IMPLEMENTATION_PLAN_AND_DOD.md`：执行顺序。
9. `08_QA_ACCEPTANCE_MATRIX.md`：验收证据。
10. `09_TRACEABILITY_AND_DECISIONS.md`：需求追踪和已冻结决策。
11. `10_AGENT_KICKOFF_PROMPT.md`：可直接复制给开发 AI 的分任务提示词。
12. `11_SPEC_VALIDATION_REPORT.md`：需求文档自身的校验结果和未执行项。

AI 开发前必须完整阅读以上文档、仓库根目录 `AGENTS.md`、`README.md`、`docs/ARCHITECTURE.md` 和 `docs/SECURITY.md`。
`manifest.json` 提供机器可读范围、顺序和硬限制；若与 Markdown 冲突，以本节文档优先级为准并修正 manifest。

## 3. 术语定义

| 术语 | 确切含义 |
|---|---|
| 空间 Space | 顶层组织容器。包含多个项目和空间级任务，本地存储，不等同于磁盘目录。 |
| 项目 Project | 属于一个空间，绑定一个本地工作区目录，保存项目说明。V1.3 不支持多根目录。 |
| 任务 Task | 独立于对话的结构化工作项，可关联项目和一个主对话。 |
| 对话 Conversation | 用户与 Agent 的消息历史，可由任务创建，也可独立存在。 |
| 资产 Asset | 项目工作区中一个文件的本地索引记录；不复制原文件，不代表上传。 |
| 资产块 AssetChunk | 从可索引文本资产中切分出的搜索单元。 |
| 索引 Index | SQLite 中的资产元数据、文本块和 FTS5 倒排索引。 |
| 当前空间 Active Space | 全局选择的空间，决定项目、任务和资产页面的默认范围。 |

## 4. 必须保持兼容的 V1.2 行为

- OpenAI Chat Completions 兼容接口和 SSE 流式响应。
- 会话、消息、项目、自动化、设置和 DPAPI API Key。
- C++ 核心工作区边界、最终路径验证、reparse-point 拒绝、读取/写入上限。
- 默认、只读、完全访问三种权限模式；高风险写入仍需确认。
- Agent 8 个模型步骤、20 次工具调用和无进展熔断。
- 自动化仅在应用运行期间执行且固定只读。
- 安装器、Windows 自包含发布和 Windows CI。

V1.3 不得将项目文件读取绕过 C++ 核心，不得把 API Key、资产正文或提示词写入日志。

## 5. 明确非目标

以下能力不属于 V1.3，禁止创建假入口或静态占位页：

- 专家、技能市场、技能安装、连接器、MCP Client/Server。
- 账号、云同步、团队协作、成员权限、组织策略。
- 向量数据库、Embedding、语义向量检索和在线 RAG 服务。
- 图片 OCR、PDF 文本解析、Office 文档解析、音视频转写、缩略图生成。
- Shell、文件删除、移动、重命名、批量修改和工作区外访问。
- Windows Service、开机自启和应用退出后的后台索引。
- 任务负责人、评论、子任务、依赖关系、工时、甘特图和日历视图。
- 资产上传、云盘、版本控制、内容编辑和资产标签管理。

## 6. 产品级约束

- 仅 Windows 10 1809+、Windows 11、x64。
- 所有新增数据本地保存；索引不得发送给模型，除非用户在对话中明确触发资产搜索并允许将选中结果加入上下文。
- 一个项目只能属于一个空间；一个空间可包含多个项目。
- 每个项目最多索引 100,000 个文件；超过后进入 `limit_reached`，不得无限扫描。
- 单个文件只有 UTF-8 文本且不超过 512 KiB 才索引正文；其他文件只索引元数据。
- 索引数据库软上限 750 MiB、硬上限 1 GiB；达到软上限警告，达到硬上限暂停新增正文索引。
- 搜索结果默认 20 条、最大 50 条；查询最大 200 个 Unicode 文本元素。
- 所有后台任务必须可取消，关闭项目或应用时释放句柄、Watcher 和数据库读取事务。

## 7. 完成判定

V1.3 只有同时满足以下条件才可标记完成：

- 空间、任务、资产页面没有无效按钮或伪数据。
- V1.2 数据可原地升级，失败时数据库保持可恢复状态。
- 首次扫描、增量更新、Watcher 溢出后的重扫、搜索与预览全部通过验收。
- 路径穿越、链接逃逸、提示词注入、索引膨胀和并发覆盖具有测试证据。
- 所有新增代码符合文件行数、依赖方向、取消、错误类型和缓存清理规则。
- Windows 一键构建脚本和安装器继续通过。

## 8. 参考依据

- SQLite FTS5 外部内容表要求应用保持内容表与索引一致，并提供完整性检查命令：<https://www.sqlite.org/fts5.html>
- FileSystemWatcher 可能因缓冲区溢出丢失事件，因此它只能作为增量提示，不能作为唯一事实来源：<https://learn.microsoft.com/dotnet/fundamentals/runtime-libraries/system-io-filesystemwatcher>
- SQLite WAL 需要正确 checkpoint，并应避免长期读取事务导致 WAL 无界增长：<https://sqlite.org/wal.html>
