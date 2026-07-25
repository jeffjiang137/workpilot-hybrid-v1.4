# WorkPilot Hybrid V1.4

WorkPilot 是 Windows 原生桌面 AI 工作台，采用 C++20 安全核心 + C#/.NET 8 + WinUI 3。V1.4 在 V1.3 的空间、项目、任务和本地资产基础上，新增专家、声明式技能、GitHub/Notion 连接器、MCP 客户端和统一安全中心。

## 已实现功能

- 专家：创建、复制、编辑、归档、乐观并发、不可变修订；对话选择专家并冻结运行快照。
- 技能：安全检查 `.zip`、路径穿越/链接/压缩比/文件类型/大小限制、原子安装、版本冲突检测、启停和专家绑定。
- 技能选择：离线确定性别名/标签/名称评分、能力依赖过滤、固定技能、指令预算和激活证据。
- 连接器：GitHub 与 Notion 真实连接测试、只读查询和受确认写入；DPAPI 凭据、空间/专家双重授权、限流、重试、熔断和有界结果。
- MCP：稳定协议 `2025-11-25`，兼容 `2025-06-18`；stdio 与 Streamable HTTP、tools/resources/prompts、能力 Schema 变化复核、SSE、session 和取消/超时。
- MCP 安全：本地 EXE/宿主限制、无 shell 参数传递、Windows Job Object 进程树回收；远程 HTTPS/loopback 模式、DNS 私网阻断和逐跳重定向检查。
- OAuth：Protected Resource Metadata、authorization server metadata、动态客户端注册、Authorization Code、PKCE S256、state、resource indicator 和 loopback 回调。
- 治理：统一能力目录、Schema 参数校验、Critical 阻止、High 每次确认、一次性 consent receipt、脱敏审计和紧急禁用。
- 安全中心：来源概览、连接器/MCP 禁用、风险与失败统计、脱敏 JSONL 导出。
- 保留 V1.3：空间、正式任务、项目、资产索引/搜索、自动化与本地文件 Agent 工具继续可用。

## 一键生成安装包

在 Windows 10/11 x64 解压完整源码包后双击：

```text
build-installer.bat
```

脚本会检查并可安装 .NET 8 SDK、Visual Studio 2022 C++ Build Tools 和 Inno Setup 6，然后运行 Native 测试、托管算法测试、V1.2→V1.4 迁移测试、WinUI 自包含发布和安装器编译。

产物：

```text
artifacts\installer\WorkPilot-Hybrid-V1.4-win-x64-Setup.exe
```

源码不包含签名证书。正式外部分发前需要对 Native DLL、应用 EXE 和安装器进行 Authenticode 签名。详细说明见 [docs/BUILD_WINDOWS.md](docs/BUILD_WINDOWS.md)。

## 首次使用

1. 在“设置”保存 OpenAI 兼容端点、模型与 API Key。
2. 在“技能”导入符合 `docs/v1.4-ai-ready/04_EXPERT_AND_SKILL_ALGORITHMS.md` 的声明式技能包。
3. 在“连接”添加 GitHub/Notion 账号或 MCP 服务。连接器新增后到“专家”页面授权；新增 MCP 会绑定当前专家。
4. 在“专家”创建工作角色，配置系统指令并勾选技能、连接器和 MCP。
5. 在“新任务”选择专家与项目后开始对话。高风险写操作弹出仅本次确认。
6. 在“安全中心”查看来源、禁用异常连接并导出脱敏审计。

## MCP 使用说明

- stdio：选择已有的本地 `.exe`，参数必须逐行填写。禁止 cmd、PowerShell、脚本宿主、相对路径和网络共享。
- HTTP：远程服务必须 HTTPS；本地服务勾选“本地模式”后只允许 loopback HTTP/HTTPS。
- Bearer token 由 DPAPI 保存；支持的服务也可选择 OAuth 2.1 + PKCE。
- 本地 MCP 程序以当前 Windows 用户权限运行，不是安全沙箱，请只添加可信程序。
- 能力新增或 Schema 变化后需要重新审查；服务端 annotations 不能降低本地风险。

## 数据与升级

- 数据库：`%LOCALAPPDATA%\WorkPilot\workpilot.db`。
- V1.4 前向迁移：014 专家/技能、015 连接器、016 MCP/治理。
- 迁移前备份：`workpilot.pre-v14.{UTC}.db`，最多 3 份；V1.2→V1.3 备份继续保留。
- 外部凭据：`%LOCALAPPDATA%\WorkPilot\secrets\{credential_ref}.bin`，SQLite 只保存引用。
- 技能：`%LOCALAPPDATA%\WorkPilot\skills`；临时检查目录位于 `skill-staging`。
- 安装器 AppId 保持不变，可覆盖升级；卸载不会删除用户数据库和工作区文件。

## 边界

V1.4 不提供技能市场、第三方 DLL/脚本技能、MCP sampling/roots/durable tasks、任意通用 HTTP 工具、无人值守高风险写入或跨平台客户端。

## 验证状态

当前交付环境为 Linux，已使用 .NET 8.0.418 实际编译全部托管服务，并运行逻辑测试与 V1.2→V1.4 迁移测试；同时完成 XML/JSON 和 SQLite 独立完整性校验。Linux 不能运行 MSVC、WinUI XAML Compiler、DPAPI、Windows Job Object 或 Inno Setup，因此 Windows 上仍必须运行 `build-installer.bat` 才能取得最终安装器验证结果。详见 [docs/V1.4_IMPLEMENTATION_REPORT.md](docs/V1.4_IMPLEMENTATION_REPORT.md)。
