# 规范校验报告

校验日期：2026-07-20  
校验对象：WorkPilot Hybrid V1.4 AI 开发需求包  
结论：通过，可交给 AI Agent 按原子任务进入开发。

## 1. 内容完整性

- 00–13 共 14 份 Markdown 文档均存在且非空。
- 产品需求包含 27 个稳定需求 ID：EXP 6、SKL 6、CON 6、MCP 9。
- 实施计划包含 T00–T21 共 22 个原子任务。
- P0 安全矩阵包含 SEC-001–SEC-015 共 15 个用例。
- 版本边界完整覆盖 V1.4.0、V1.4.1、V1.4.2。
- 专家、技能、连接器、MCP、权限、安全中心均具有 UI、数据、算法、架构和验收定义。

## 2. 一致性检查

| 项目 | 结果 |
|---|---|
| MCP 完整目标版本 | `2025-11-25`，一致 |
| MCP 有限兼容版本 | `2025-06-18`，一致 |
| MCP 传输 | stdio + Streamable HTTP，一致 |
| 非目标 | sampling、roots、durable tasks，一致 |
| 专家技能上限 | 20，一致 |
| 自动技能上限 | 5，一致 |
| 工具目录上限 | 64 / Schema 256 KiB，一致 |
| MCP 单消息上限 | 4 MiB，一致 |
| 外部结果注入上限 | 单项 20,000 字符，一致 |
| 数据迁移 | 014 / 015 / 016 前向追加，一致 |
| 秘密存储 | DPAPI + credential_ref，一致 |
| 写操作授权 | High、仅此一次，一致 |

## 3. 格式与结构检查

- 冲突标记：0。
- NUL 字节：0。
- Markdown fence 数量：58，为偶数，未发现未闭合代码围栏。
- 文档标题层级、表格和列表可由 GitHub-flavored Markdown 渲染。
- 行尾扫描只发现用于 CommonMark 强制换行的两个空格，未发现非预期混合行尾。
- 原始项目目录是源码快照而非 Git 仓库，因此 `git diff --check` 不适用于本次文档生成；编码阶段仍必须执行该门禁。

## 4. 规范依据检查

- 使用 MCP `2025-11-25` 稳定规范作为目标，不引用草案功能作为必须需求。
- OAuth 明确 OAuth 2.1、PKCE S256、Protected Resource Metadata、authorization server metadata 和 resource indicator。
- MCP tools annotations 被定义为不可信元数据，本地风险引擎不可被降级。
- URL elicitation 明确第三方凭据不得经过客户端/模型。
- Streamable HTTP 明确 POST/GET、JSON/SSE、Origin、SSRF 和逐跳重定向要求。

## 5. 限制说明

本交付物是开发需求与实施规范，不包含 V1.4 产品代码，也不声称已在 Windows 上完成 V1.4 构建。实际编码完成后必须重新执行 `10_QA_ACCEPTANCE_MATRIX.md` 的 Release x64、MSIX、升级、安装与安全门禁。
