# WorkPilot Hybrid V1.4 AI 开发需求包

文档状态：已冻结，可进入开发  
基线：WorkPilot Hybrid V1.3  
目标平台：Windows 10 22H2 / Windows 11，x64  
技术栈：C++20 核心库 + C#/.NET 8 + WinUI 3 + SQLite + C++/WinRT  
MCP 目标协议：稳定版 `2025-11-25`

## 1. 交付目标

在 V1.3 已有“空间、项目、正式任务、资产索引与搜索、桌面 AI 对话”的基础上，增加四组可实际使用、可治理、可审计的能力：

1. 专家：可配置的 AI 工作角色，绑定系统指令、技能、连接器、MCP 服务和权限。
2. 技能：纯声明式、可导入、可启停、可解释激活原因的本地知识/工作流包。
3. 连接器：由 WorkPilot 内置和维护的外部服务适配器；V1.4 首批实现 GitHub 与 Notion。
4. MCP：支持本地 stdio 与远程 Streamable HTTP 服务，支持 tools、resources、prompts，并统一纳入安全中心。

本需求包不是概念稿。所有“必须”条目均应实现；没有真实行为的按钮、假数据、吞异常、无限重试和绕过授权的快捷实现均不通过验收。

## 2. 版本拆分

| 版本 | 必须交付 | 不在该增量内 |
|---|---|---|
| V1.4.0 | 专家 CRUD、不可变修订、技能导入/校验/启停/选择、对话选择专家、能力快照 | 外部连接器写操作、MCP |
| V1.4.1 | 连接器框架、凭据保险库、GitHub/Notion 只读能力、空间与专家授权、限流/熔断/审计 | 外部写操作、MCP |
| V1.4.2 | MCP stdio/Streamable HTTP、OAuth 2.1 + PKCE、tools/resources/prompts、写操作确认、安全中心 | MCP sampling、roots、durable tasks、第三方二进制插件 |

每个增量都必须可构建、可安装、可从上一增量原位升级、可卸载，且数据库迁移只能前向追加。

## 3. 冻结边界

### 3.1 必须保持的 V1.3 边界

- C++ 核心拥有路径规范化、文件系统副作用、进程启动与最终权限判定。
- C# WinUI 3 只负责视图、编排、状态投影和平台集成，不复制核心安全算法。
- 密钥只进入 Windows DPAPI 保护的 SecretService；SQLite 不得保存明文 token、密码或 OAuth refresh token。
- 所有工具调用必须经过：解析 → Schema 校验 → 本地风险分类 → 权限策略 → 必要时用户确认 → 执行 → 审计。
- 任意后台任务必须支持取消、超时、有界队列与有界缓存。
- 现有空间、项目、任务、资产、会话和搜索行为不得回归。

### 3.2 术语边界

- 专家（Expert）：AI 行为配置，不是模型，也不是后台常驻 Agent。
- 技能（Skill）：Markdown 指令、模板和静态资源的声明式包，不允许携带或执行代码。
- 连接器（Connector）：WorkPilot 内置的服务适配器，代码随客户端发布并接受同一安全审查。
- MCP 服务（MCP Server）：独立进程或远程服务，属于外部不可信能力源。
- 能力（Capability）：可被调用的工具、可读取的资源或可套用的提示模板。
- 授权（Grant）：在明确空间、专家、能力和有效期范围内允许使用的记录。

## 4. 文档优先级

发生冲突时按以下顺序处理：

1. `06_SECURITY_AND_PERMISSION_MODEL.md`
2. `03_DOMAIN_AND_DATA_MIGRATIONS.md`
3. `05_CONNECTORS_AND_MCP_PROTOCOL.md`
4. `04_EXPERT_AND_SKILL_ALGORITHMS.md`
5. `02_UI_AND_INTERACTION_SPEC.md`
6. `01_PRD_V1.4.md`
7. 其余实施与说明文档

不得由 AI 自行猜测冲突意图。发现冲突时停止相关任务，在实现记录中给出文件、标题、冲突项与建议决策。

## 5. 文档目录

| 文件 | 用途 |
|---|---|
| `01_PRD_V1.4.md` | 用户、场景、范围、版本验收 |
| `02_UI_AND_INTERACTION_SPEC.md` | 页面、控件、状态、键盘与无障碍 |
| `03_DOMAIN_AND_DATA_MIGRATIONS.md` | 实体、不变量、SQLite 迁移 |
| `04_EXPERT_AND_SKILL_ALGORITHMS.md` | 修订、提示编译、技能包与选择算法 |
| `05_CONNECTORS_AND_MCP_PROTOCOL.md` | 连接器、JSON-RPC、传输、OAuth、生命周期 |
| `06_SECURITY_AND_PERMISSION_MODEL.md` | 威胁模型、风险分类、授权与审计 |
| `07_TECH_ARCHITECTURE_AND_APIS.md` | 分层、模块、接口、错误与并发 |
| `08_AI_DEVELOPMENT_RULES.md` | AI Agent 强制开发规则 |
| `09_IMPLEMENTATION_PLAN_AND_DOD.md` | 原子任务、依赖、完成定义 |
| `10_QA_ACCEPTANCE_MATRIX.md` | 测试矩阵与发布门禁 |
| `11_TRACEABILITY_AND_DECISIONS.md` | 需求追踪、已冻结决策与非目标 |
| `12_AGENT_KICKOFF_PROMPTS.md` | 可复制给编码 AI 的执行提示词 |
| `13_SPEC_VALIDATION_REPORT.md` | 文档完整性与一致性校验结果 |
| `manifest.json` | 交付包版本、文件清单与内容哈希 |

## 6. 完成口径

功能“已实现”必须同时满足：

- UI 可到达，空态、加载、成功、失败、取消和权限拒绝状态完整。
- 真实数据持久化，重启后保持一致。
- 核心算法具有单元测试，外部边界具有契约/集成测试。
- 高风险操作有确认与审计，敏感字段在日志和数据库中不可见。
- `dotnet build -c Release -p:Platform=x64` 无错误；发布配置不得新增警告。
- 安装包在干净 Windows 用户环境完成安装、首次启动、升级和卸载冒烟测试。

## 7. 规范依据

- MCP 稳定规范：<https://modelcontextprotocol.io/specification/2025-11-25>
- MCP Authorization：<https://modelcontextprotocol.io/specification/2025-11-25/basic/authorization>
- MCP Tools：<https://modelcontextprotocol.io/specification/2025-11-25/server/tools>
- MCP Elicitation：<https://modelcontextprotocol.io/specification/2025-11-25/client/elicitation>
- Streamable HTTP 传输：<https://modelcontextprotocol.io/specification/2025-03-26/basic/transports>

实现只以已冻结的稳定规范为准，不引入草案功能。官方规范与本文不一致时，安全限制取更严格者；协议格式取官方稳定规范，并记录差异。
