# AI 开发合同

本文件适用于整个仓库。开始修改前先阅读 `README.md`、目标模块和 `docs/ARCHITECTURE.md`；安全相关改动还必须阅读 `docs/SECURITY.md`。

## 不可破坏的边界

- 路径、权限和文件副作用必须保留在 C++ 核心；视图不得绕过 `NativeWorkspaceService` 直接读写项目文件。
- API Key 只能经 `SecretService`/DPAPI 保存；不得写入 SQLite、配置、日志、测试快照或源码。
- 外部输入先验证再执行。模型工具调用顺序固定为：解析 → Schema/本地校验 → 权限决策 → 用户确认 → 执行。
- 取消信号必须贯穿模型和长任务。禁止无限循环、无限重试、静默降权或静默换模型。
- 已发布数据库迁移只追加，禁止改写历史迁移。新增副作用必须有幂等或冲突策略。

## 可维护性

- 单文件不超过 400 行，函数优先不超过 60 行，参数优先不超过 5 个；超限前先拆分职责。
- 依赖方向保持 `Views → Services → Adapters/Core`，禁止视图写 SQL，禁止 C++ Core 依赖 WinUI 或模型 SDK。
- 公共 C ABI 必须记录所有权、编码和失败方式；异常不得跨 DLL 边界。
- 不引入万能工具类、隐式全局、空 catch、字符串拼 Shell、假按钮或未说明的占位实现。
- 魔法数集中为命名常量。错误必须包含用户可采取的下一步，日志不得包含敏感正文。
- 保持现有中文 UI 和文档风格；公共标识符用清晰英文。

## 每个任务的完成定义

1. 描述目标、非目标和允许触达模块。
2. 先做最小闭环，避免把功能改动与大规模重构混在一起。
3. 运行 C++ 测试、受影响模块构建和安装器发布检查。
4. 检查文件行数、输入边界、错误路径、取消、资源释放和日志内容。
5. 行为、构建方式或安全边界变化时同步更新 README/docs。
6. 保留可回滚的小型提交；不得用破坏命令处理用户已有改动。

Windows 发布验证命令：

```powershell
.\scripts\build-installer.ps1
```

非 Windows 环境只能验证可移植 C++ 权限模块、静态结构和 XML/JSON；不得声称已完成 WinUI 二进制运行测试。

## V1.3 开发入口

当任务涉及空间、正式任务、资产索引、资产搜索或 `search_assets` 时，编码前必须完整阅读 `docs/v1.3-ai-ready/` 下全部文档，并严格按 `07_IMPLEMENTATION_PLAN_AND_DOD.md` 的依赖顺序开发。该目录是 V1.3 的权威需求，尚未实现的功能不得在 UI 中占位。

## V1.4 开发入口

当任务涉及专家、技能、连接器、MCP、能力授权或安全中心时，编码前必须先阅读 `docs/v1.4-ai-ready/00_README_AND_SCOPE.md`、`06_SECURITY_AND_PERMISSION_MODEL.md`、`07_TECH_ARCHITECTURE_AND_APIS.md`、`08_AI_DEVELOPMENT_RULES.md`，再阅读当前任务对应专题文档。实现必须按 `09_IMPLEMENTATION_PLAN_AND_DOD.md` 的原子任务顺序执行，并在提交说明中引用 `11_TRACEABILITY_AND_DECISIONS.md` 的需求与测试 ID。发生冲突时以 V1.4 文档中定义的优先级为准；不得自行降低安全限制。
