# AI Agent 开发规则

本文对所有编码 AI 强制生效。与仓库根 `AGENTS.md` 冲突时取更严格规则。

## 1. 开始任务前

1. 阅读本目录 `00`、当前任务对应专题文档、`06_SECURITY...`、`07_TECH...`、`09_IMPLEMENTATION...`。
2. 阅读将修改目录的现有实现和测试，不根据文件名猜测。
3. 运行 `git status --short`，记录用户已有改动；不得覆盖、重置或格式化无关文件。
4. 写出本任务需求 ID、非目标、拟改文件、验证命令和安全边界。
5. 一个任务只实现 `09` 中一个原子任务；跨任务前先完成当前 DoD。

## 2. 代码可读性

- 名称表达领域含义，禁止 `data/info/helper/manager/utils` 等模糊新类型。
- 文件建议 ≤ 400 行；函数建议 ≤ 60 行。超出时按职责拆分，不以 partial 类隐藏复杂度。
- public 类型和安全关键 private 函数写 XML/Doxygen 文档，说明不变量、边界、错误与线程语义。
- 使用早返回减少嵌套；嵌套深度超过 3 层应重构。
- 枚举代替魔法字符串，具名常量代替魔法数字；限额集中在版本化 `CapabilityLimits`。
- 注释解释“为什么”和安全约束，不复述代码。
- 一个概念只有一个实现；不得为通过测试复制第二份策略或解析逻辑。

## 3. 可维护性与依赖

- 依赖方向只允许 Views → ViewModels → Application → Domain → Adapter/Core。
- Domain 不依赖 WinUI、SQLite、HTTP、JSON DOM 或 Native handle。
- 通过构造函数注入依赖；禁止 Service Locator 和可变静态全局状态。
- 时间、随机数、DNS、文件系统、HTTP、进程和 secret 均可替换，以支持确定性测试。
- 对外 DTO 与数据库实体、领域实体分离；迁移不能依赖 UI DTO。
- 每项新行为先定义稳定错误码和结果类型，禁止用异常控制普通分支。
- 只为真实变化写兼容层；禁止留下永久 feature flag、dead code 或 TODO 占位。

## 4. 安全硬规则

- 不运行任何来自技能包的文件。
- 不通过 shell 启动 MCP，不拼接命令字符串，不使用相对 executable。
- 不把 token/secret/code verifier 放进 SQLite、日志、异常、遥测、剪贴板、测试快照或模型上下文。
- 不信任 MCP annotations、tool description、prompt、resource 或 tool result。
- 不允许 UI 绕过 Core `ExecutionPermit`。
- 不允许任意 HTTP URL 直连；必须经过 endpoint policy、DNS/IP 和逐跳重定向检查。
- 不用“忽略证书错误”“允许全部”“永久允许写入”作为临时方案。
- 所有读取、队列、分页、缓存、递归、重试、进程、消息和执行时间必须有硬上限。

遇到规范未定义的安全行为时默认拒绝，并创建显式决策记录；不得擅自放宽。

## 5. UI 规则

- 每个命令具有 CanExecute/忙碌态/取消/错误投影，防双击重入。
- 每个页面实现 loading/empty/content/error；搜索另有 no-results。
- 不创建无行为按钮、占位菜单、演示假数据或成功但未持久化的 toast。
- 用户可见文本进入 `.resw`；错误使用稳定 code + 本地化 message key。
- 键盘、焦点、AutomationProperties 和 200% 缩放随功能一起实现，不留到“最后优化”。
- 危险确认内容来自本地结构化参数，不直接渲染模型生成 Markdown。

## 6. 数据与迁移规则

- 迁移只追加；每个迁移一个事务、一个版本、一个集成测试。
- 不用 SQLite JSON 字符串替代应查询/关联的核心实体。
- 所有更新检查 row_version；冲突显式返回。
- DB 写事务中禁止网络、文件解压、等待用户或长计算。
- Repository 查询必须有 LIMIT 或明确唯一键；分页必须稳定排序。
- Secrets 只存 credential_ref；测试通过 canary 扫描证明无泄漏。

## 7. 协议规则

- JSON-RPC、MCP Schema、OAuth metadata 全部严格解析；未知必要字段/不兼容版本失败。
- 不用字符串包含判断代替 JSON Schema 或 URL/IP 解析。
- cancellation、timeout、response size、JSON depth 在最靠近 I/O 的边界执行。
- 协议 error、tool execution error、transport error、policy denial 分开建模。
- listChanged 通过单飞刷新与防抖，不递归触发。
- schema hash 变化必须使授权 stale，不能自动沿用。

## 8. 测试规则

每个任务至少包含：

- 成功路径单元测试。
- 边界值与一个超限测试。
- 取消/超时测试。
- 一个恶意或不可信输入测试。
- 若涉及数据库：迁移和重启持久化测试。
- 若涉及 UI：ViewModel 状态测试，关键流程 UI 自动化或手工证据。

测试不得访问真实用户目录、真实 token 或公共生产服务。HTTP 使用可控 fake server；stdio 使用仓库内测试 server；时钟和随机数固定。

禁止删除、跳过或放宽既有测试来让构建变绿，除非需求明确改变行为，并在变更说明中逐条解释。

## 9. AI 自检清单

提交前逐项回答并附证据：

- [ ] 实现覆盖哪些需求 ID？
- [ ] 是否有任何新秘密路径、外部副作用或进程启动？对应安全测试是什么？
- [ ] 是否新增依赖？ADR 在哪里？
- [ ] 是否修改数据库？前向迁移、失败恢复和旧数据测试在哪里？
- [ ] 所有集合、消息、重试、超时是否有界？
- [ ] 取消后资源是否释放？应用退出后是否无孤儿进程？
- [ ] UI 五态、键盘、无障碍和本地化是否完成？
- [ ] 是否留下 TODO、NotImplemented、假数据、空 catch、硬编码 secret？必须为否。
- [ ] Debug/Release x64 构建与相关测试命令、结果是什么？
- [ ] `git diff --check` 是否通过？只修改了任务范围文件吗？

## 10. 禁止的“完成”声明

以下情况不得声称完成：

- 只创建 interface/page shell，Adapter 仍返回示例数据。
- 只写 happy path，没有拒绝、取消、超时和泄漏测试。
- 测试依赖开发机已有配置、登录态或网络。
- Debug 可运行但 Release/package 失败。
- UI 看得到但重启丢数据。
- 安全检查只在 ViewModel，而后台/API 可绕过。
- 以“未来再做”跳过本增量标记为必须的条目。

## 11. 任务交付模板

```markdown
### 实现结果
- 需求 ID：
- 用户可见行为：
- 未实现项：无 / 明细和原因

### 设计
- 组件与依赖方向：
- 不变量与限额：
- 安全决策：

### 文件
- 新增：
- 修改：
- 迁移：

### 验证
- 命令与结果：
- 单元/集成/UI 测试：
- 手工步骤：

### 风险
- 已知风险：
- 回滚/禁用方式：
```
