# AI Agent 启动提示词

以下模板用于把原子任务交给编码 AI。每次只替换一个任务 ID，不要一次要求“完成整个 V1.4”。

## 1. 单任务实现模板

```text
你正在 WorkPilot Hybrid 仓库实现 V1.4 的【Txx 任务名】。

开始前必须阅读：
1. 仓库根 AGENTS.md
2. docs/v1.4-ai-ready/00_README_AND_SCOPE.md
3. docs/v1.4-ai-ready/06_SECURITY_AND_PERMISSION_MODEL.md
4. docs/v1.4-ai-ready/07_TECH_ARCHITECTURE_AND_APIS.md
5. docs/v1.4-ai-ready/08_AI_DEVELOPMENT_RULES.md
6. docs/v1.4-ai-ready/09_IMPLEMENTATION_PLAN_AND_DOD.md 的 Txx
7. 与任务相关的 01–05、10、11 文档

先检查 git status 和现有实现，列出：需求 ID、拟修改文件、非目标、安全边界、测试命令。只实现 Txx，不扩展范围。

实现要求：
- 真实功能闭环，不允许 placeholder、mock 产品行为、空 catch、TODO 或 NotImplemented。
- 遵守 C++ Core/C# 分层，安全判定不得只在 UI。
- 所有输入、队列、缓存、分页、重试、消息和超时有硬上限。
- 同时完成成功、边界、取消/超时、恶意输入和持久化测试。
- 不覆盖用户已有改动，不格式化无关文件，不新增未审查依赖。

完成后按 08 文档的“任务交付模板”汇报，附实际构建/测试命令与结果。若文档冲突，停止冲突部分并准确报告，不自行猜测。
```

## 2. 安全审查模板

```text
只审查，不修改代码。审查当前变更对以下边界的影响：秘密、路径/ZIP、进程、网络/SSRF、OAuth、权限/consent、Schema 变化、提示注入、日志/审计、取消与资源释放。

以 docs/v1.4-ai-ready/06_SECURITY_AND_PERMISSION_MODEL.md 和 10_QA_ACCEPTANCE_MATRIX.md 为准。输出按严重性排序的发现，每项包含：文件与位置、可复现场景、违反的规则/测试 ID、影响、最小修复建议。若无发现，列出实际检查过的攻击面和仍未验证的风险。不要把缺少证据写成“安全”。
```

## 3. 可维护性审查模板

```text
只审查，不修改代码。检查当前任务是否违反：依赖方向、重复领域逻辑、模糊命名、文件/函数过大、不可注入时间/随机/IO、无界集合、异常吞噬、DTO/实体混用、事务跨网络、测试耦合实现细节。

依据 docs/v1.4-ai-ready/07_TECH_ARCHITECTURE_AND_APIS.md 和 08_AI_DEVELOPMENT_RULES.md。输出具体文件位置、长期维护影响和可局部完成的重构方案；不要建议与当前任务无关的大改。
```

## 4. 版本验收模板

```text
验证 WorkPilot V1.4.x 是否达到 ReleaseReady。不要先修改代码。

1. 根据 01_PRD_V1.4.md 列出该增量全部“必须”需求。
2. 根据 09_IMPLEMENTATION_PLAN_AND_DOD.md 核对任务证据。
3. 执行 10_QA_ACCEPTANCE_MATRIX.md 中适用的 P0/P1、构建、迁移、安装门禁。
4. 搜索 TODO/NotImplemented/placeholder/fake data/empty catch/硬编码秘密与无界重试。
5. 输出逐项 Pass/Fail/Blocked，证据为测试名、命令结果或文件位置。

只有全部必须项通过才能写 ReleaseReady。Blocked 不等于 Pass；页面存在不等于功能实现。
```

## 5. 失败处理模板

```text
当前任务被阻塞。请不要扩大权限或降低安全规则。输出：
- 阻塞的任务/需求 ID
- 已确认事实与实际错误
- 已尝试的安全方案
- 需要人工决定的最小问题（最多 3 个选项，注明安全/兼容/维护取舍）
- 可继续完成且不依赖该决定的范围

未经明确决定，不实现会改变冻结范围、数据兼容或安全模型的替代方案。
```

## 6. 推荐提交粒度

每个任务可拆为但不要跨任务：

1. 领域类型/接口与失败测试。
2. 核心实现与单元测试。
3. adapter/持久化与集成测试。
4. UI/资源/无障碍与 UI 测试。
5. 文档、发布检查和清理。

每个提交必须能编译；若临时失败不可避免，保留在本地工作树，不把红色提交作为交付。
