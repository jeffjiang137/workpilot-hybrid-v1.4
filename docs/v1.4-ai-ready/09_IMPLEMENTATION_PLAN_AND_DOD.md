# 实施计划与完成定义

## 1. 执行原则

- 严格按依赖顺序开发；每个任务一个可审查提交。
- 任务未通过自己的 DoD，不开始依赖它的任务。
- 先领域/安全/测试，后 UI 接线；但每个版本结束必须形成完整纵切。
- 所有外部能力先只读，再引入写操作与确认。

## 2. V1.4.0：专家与技能

### T00 基线与测试骨架

范围：冻结 V1.3 构建结果，新增测试项目、fake clock/random/filesystem 和统一限制常量。  
DoD：V1.3 全部测试通过；Debug/Release x64 构建通过；无产品行为变化。

### T01 Migration 014 与领域类型

范围：专家、修订、技能、版本、绑定、运行快照表与 repository。  
DoD：13→14、空库→14、失败回滚、重复启动幂等测试通过；默认专家存在。

### T02 专家修订服务

范围：CRUD、复制、归档、row_version、canonical snapshot、NoChange。  
DoD：并发冲突、旧修订不可变、内置专家保护、重启持久化测试通过。

### T03 专家 UI

范围：列表、五步编辑、详情/修订、专家选择器、旧会话分支提示。  
DoD：UI 五态、验证、未保存关闭、键盘/无障碍；不存在假按钮。

### T04 技能包检查器

范围：manifest schema、ZIP 安全检查、staging、哈希、结构化错误。  
DoD：第 04/06 文档的恶意语料全部拒绝；取消不留最终文件；fuzz 运行至少 10 分钟无崩溃。

### T05 技能安装与版本管理

范围：审查 token、原子安装、升级/回滚/卸载影响、最多 3 版本。  
DoD：同版同/异哈希、崩溃恢复、引用版本保留和重启一致性测试通过。

### T06 技能选择与 Prompt Compiler

范围：规范化、确定性评分、证据、预算、外部不可信边界。  
DoD：中英文 golden tests、平局、依赖缺失、超预算、注入语料通过；相同输入字节级一致。

### T07 技能 UI 与运行快照

范围：列表、导入、详情、绑定、激活解释；创建 RunSnapshot 并显示能力摘要。  
DoD：V1.4.0 PRD 发布验收全部通过；MSIX 升级/卸载冒烟通过。

## 3. V1.4.1：连接器

### T08 Migration 015、SecretService 与连接器契约

范围：表、DPAPI secret lease、定义清单、状态机、统一错误。  
DoD：canary token DB/log 全盘扫描 0 命中；删除/解密失败可恢复；契约测试通过。

### T09 调用基础设施

范围：有界并发/队列、token bucket、retry、circuit breaker、分页、缓存。  
DoD：使用 fake clock 的阈值测试；写操作不重放；取消释放全部槽位。

### T10 GitHub 只读适配

范围：V1.4.1 列出的 7 个操作、Schema、错误映射、结果上限。  
DoD：fake HTTP 契约测试覆盖分页/429/401/二进制/超大文件；无真实 token 测试。

### T11 Notion 只读适配

范围：4 个操作、block 转换、受控 filter/sort DTO。  
DoD：未知 block、深层/超大响应、分页、限流和无效 ID 测试通过。

### T12 连接 UI 与授权

范围：添加/测试/重连/禁用/删除、空间与专家授权、能力卡片、诊断。  
DoD：未双重允许时能力目录不可见；删除影响确认与秘密清理；V1.4.1 发布验收通过。

## 4. V1.4.2：MCP 与治理

### T13 Migration 016 与能力策略核心

范围：MCP/能力/授权/审计表、C++ 风险策略、ExecutionPermit。  
DoD：所有权限求值组合表测试；permit 过期/重放/错 run/schema 拒绝。

### T14 MCP JSON-RPC 与目录

范围：会话状态机、request map、limits、initialize、list/diff/listChanged。  
DoD：协议 fake server 覆盖乱序、重复 ID、超限、循环 cursor、Schema 变化和取消。

### T15 C++ stdio Process Broker

范围：路径/宿主检查、安全参数编码、Job Object、管道和 stderr 环形缓冲。  
DoD：引号/空格/Unicode 参数准确；shell 拒绝；崩溃/关闭无孤儿进程；stdout 污染安全失败。

### T16 Streamable HTTP 与 SSRF

范围：POST/GET/SSE/session、endpoint policy、DNS/IP、redirect、TLS。  
DoD：SSRF 语料、跨主机 header、降级、超大 SSE、断线重连和 session 删除测试通过。

### T17 OAuth 2.1 + PKCE

范围：metadata、client metadata/DCR、loopback 回调、resource indicator、token 刷新。  
DoD：state/issuer/PKCE/resource/redirect 错误均拒绝；token canary 无泄漏；listener 必定关闭。

### T18 MCP tools/resources/prompts/elicitation

范围：能力适配、风险复核、资源/提示预览、受限 elicitation。  
DoD：不可信 annotations 不降级；资源/提示不成为系统规则；敏感 elicitation 拒绝。

### T19 写操作与确认

范围：GitHub create/comment、Notion append、高风险确认、once receipt。  
DoD：参数预览准确、取消 0 外部请求、重复点击只发送一次、失败不自动重放。

### T20 MCP/安全中心 UI

范围：添加向导、详情、能力 diff、授权、审计、诊断与对话调用卡。  
DoD：全部状态/焦点/键盘/200% 缩放完成；撤销与紧急禁用可验证。

### T21 稳定性、升级与发布

范围：故障注入、性能、秘密扫描、打包、安装/升级/卸载、文档。  
DoD：第 10 文档全部 P0/P1 门禁通过，V1.4.2 发布验收通过，无阻断缺陷。

## 5. 每个任务的统一 DoD

- 需求 ID 和代码/测试可追踪。
- 实现、错误、取消、边界和安全测试齐全。
- 受影响文档、资源文本和迁移说明更新。
- Release x64 构建和相关测试通过，`git diff --check` 通过。
- 无新增 compiler/analyzer warning，无 TODO/NotImplemented/假数据。
- 手工验收证据包含步骤、预期、实际和截图/日志位置（不含秘密）。

## 6. 发布门禁与回退

- V1.4.0/1/2 各自使用独立 feature version，不以运行时隐藏未完成页面。
- 新模块发生故障可禁用来源，但数据库不可降级；回退安装前提示较新数据库只能由兼容版本读取。
- 连接器/MCP 异常不阻止使用本地任务、资产和默认对话。
- 任何秘密泄漏、权限绕过、任意进程启动、SSRF 或孤儿进程为发布阻断 P0。
