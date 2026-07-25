# WorkPilot Hybrid V1.4 发布说明

## 新增

- 专家配置、不可变修订、对话选择与运行快照。
- 安全声明式技能包导入、版本管理、绑定与确定性选择。
- GitHub/Notion 原生连接器及真实读写操作。
- MCP stdio、Streamable HTTP、OAuth 2.1 + PKCE、tools/resources/prompts。
- 能力 Schema 变化复核、风险确认、consent receipt、审计和安全中心。
- Windows Job Object 管理 MCP 进程树；HTTP Endpoint/SSRF/重定向防护。

## 数据库

- 新增 Migration 014、015、016。
- 从 V1.2 或 V1.3 首次启动可顺序升级到 V1.4。
- 升级前创建 `workpilot.pre-v14.*.db`，最多保留三份。

## 兼容与限制

- 目标 Windows 10 1809+ / Windows 11 x64。
- MCP 完整目标版本 `2025-11-25`，有限兼容 `2025-06-18`。
- 本地 MCP 不是 AppContainer 沙箱，以当前 Windows 用户权限执行。
- OAuth 当前支持动态客户端注册的授权服务器；不支持的服务可使用其发放的 Bearer token。
- 安装器未附带商业签名证书。
