# QA 与验收矩阵

## 1. 测试层级

| 层级 | 目标 | 外部依赖 |
|---|---|---|
| C++ unit | 路径、ZIP、参数、Job、IP、策略 | 全部 fake/本地语料 |
| C# unit | 领域、状态机、选择、预算、限流、diff | fake clock/random |
| Contract | GitHub/Notion/MCP/OAuth 协议 | 本地 fake HTTP/stdio server |
| Integration | SQLite、DPAPI、文件、Native ABI | 临时 Windows 用户目录 |
| UI | 导航、向导、确认、错误、无障碍 | 测试数据库与 fake services |
| Package | 安装、启动、升级、卸载 | 干净 Windows VM |

测试命名采用 `Method_State_ExpectedResult`；安全语料注明关联规则 ID。

## 2. P0 安全用例

| ID | 场景 | 预期 |
|---|---|---|
| SEC-001 | ZIP 条目 `../../outside.exe` | 导入失败；根外无文件 |
| SEC-002 | ZIP 大小写重复/ADS/设备名/链接 | 导入失败；结构化规则码 |
| SEC-003 | 压缩炸弹超过任一限制 | 流式停止、清理、内存有界 |
| SEC-004 | stdio executable 为 cmd/powershell/UNC/相对路径 | 保存或启动被 Core 拒绝 |
| SEC-005 | 参数含引号、`&|><%`、Unicode | 子进程收到原始单一参数，不发生 shell 解释 |
| SEC-006 | 应用崩溃/退出时 MCP 存在子孙进程 | Job Object 全部终止 |
| SEC-007 | HTTP 指向 127.0.0.1/169.254.169.254/私网 | 公共模式阻止 |
| SEC-008 | 公网 URL 重定向私网或 HTTPS→HTTP | 逐跳阻止，不转发认证头 |
| SEC-009 | OAuth state/issuer/resource 不匹配 | token 交换失败并清理临时状态 |
| SEC-010 | MCP 把 destructive 标成 readOnly | 本地风险保持 High/Critical |
| SEC-011 | 外部内容要求输出 secret/绕过确认 | 策略层拒绝，secret 不进入模型 |
| SEC-012 | capability Schema 变化 | 旧授权 stale，调用被拒绝 |
| SEC-013 | canary token 完整流程后扫描 DB/log/export | 明文命中 0 |
| SEC-014 | 双击确认/重放 permit | 外部副作用最多一次 |
| SEC-015 | 跨 server/connector 引用 credential_ref | 权限拒绝，秘密不展开 |

任一 P0 失败禁止发布。

## 3. 专家与技能功能用例

| ID | 场景 | 预期 |
|---|---|---|
| EXP-A01 | 创建专家并重启 | 字段、技能排序、修订一致 |
| EXP-A02 | 两窗口用旧 row_version 保存 | 第二次返回冲突，不覆盖 |
| EXP-A03 | 保存无变化 | 不新增修订 |
| EXP-A04 | 旧会话对应专家已更新 | 仍用旧 revision，显示历史标记 |
| EXP-A05 | 归档当前专家后继续旧对话 | 要求新建分支选择专家 |
| SKL-A01 | 同版同哈希重复导入 | 幂等提示，无重复记录 |
| SKL-A02 | 同版不同哈希 | 冲突拒绝 |
| SKL-A03 | 升级、回滚、运行中更新 | 快照版本不变，新运行用 active 版本 |
| SKL-A04 | 中英文 alias/tag 命中 | 得分与解释符合公式 |
| SKL-A05 | 平分候选超过 5 | sort_order、ID 确定性选 5 |
| SKL-A06 | pinned 指令超预算 | 阻止运行并指出技能，不静默截断 |
| SKL-A07 | required capability 未授权 | 技能不选，原因可见 |

## 4. 连接器用例

| ID | 场景 | 预期 |
|---|---|---|
| CON-A01 | 凭据有效但空间未启用 | 专家目录不可见能力 |
| CON-A02 | 空间启用但专家未允许 | 能力不可见 |
| CON-A03 | 第 5 个并发请求 | 排队；队满返回 QueueFull |
| CON-A04 | GET 返回 429 + Retry-After | 有界等待后重试 |
| CON-A05 | write 连接中断 | 不自动重放 |
| CON-A06 | 60 秒内 5 次服务失败 | 熔断 30 秒后 half-open 1 次 |
| CON-A07 | 第 11 页/第 501 条/5 MiB | 停止并标 truncated |
| CON-A08 | GitHub 文件为 binary/>1 MiB | 不注入内容，返回安全摘要 |
| CON-A09 | Notion 未知 block | 保留占位，其余内容正常 |
| CON-A10 | 删除连接 | 影响确认、grant 删除、secret 删除 |

## 5. MCP 协议用例

| ID | 场景 | 预期 |
|---|---|---|
| MCP-A01 | 2025-11-25 initialize | ready，能力正常同步 |
| MCP-A02 | 2025-06-18 initialize | 兼容模式且 UI 标记 |
| MCP-A03 | 未支持协议 | 停止并显示支持版本 |
| MCP-A04 | initialize 超时/取消 | 状态可恢复，无 pending 泄漏 |
| MCP-A05 | 乱序 responses | 按 ID 正确完成请求 |
| MCP-A06 | 重复/未知 response ID | 诊断或协议失败，不错误绑定 |
| MCP-A07 | 4 MiB+ 消息/深度 65 | 拒绝且内存有界 |
| MCP-A08 | cursor 循环/超过 50 页 | 停止并 degraded，不无限循环 |
| MCP-A09 | listChanged 风暴 | 500 ms 防抖、2 s 内刷新一次 |
| MCP-A10 | refresh 失败 | 保留 stale 目录，不声称最新 |
| MCP-A11 | stdout 混日志 3 次 | stdio 服务 error 并终止 |
| MCP-A12 | stderr 持续大输出 | 1 MiB 环形覆盖，不阻塞 |
| MCP-A13 | SSE 中断后恢复 | 有界重连，无重复完成调用 |
| MCP-A14 | tools/call tool error | 显示受控工具错误，不当作 transport 崩溃 |
| MCP-A15 | prompt 试图成为 system | 作为外部不可信上下文预览 |
| MCP-A16 | resource binary/超大 | 不注入；显示限制原因 |
| MCP-A17 | elicitation 请求 password/token | 自动拒绝并审计 |

## 6. UI 与无障碍用例

- 100/125/150/200% 缩放：无关键按钮被裁剪，向导可滚动完成。
- 仅键盘：创建专家、导入技能、连接账号、添加 MCP、确认一次调用、撤销授权。
- 屏幕阅读器：按钮名称、字段错误、状态变化、风险级别均可读。
- 高对比度和浅/深主题：状态图标与文字可辨识。
- 网络断开、服务重启、凭据过期期间，页面保持可导航且错误不覆盖用户已输入配置。
- 高风险确认默认焦点“取消”，Esc 等价取消，Enter 不在初次打开时意外允许。

## 7. 性能与稳定性

| ID | 负载 | 门槛 |
|---|---|---|
| PERF-01 | 1,000 能力列表搜索 | P95 ≤ 150 ms |
| PERF-02 | 100 技能选择 | P95 ≤ 50 ms，不含文件 I/O |
| PERF-03 | 5 个 stdio 服务同时启动 | 全部受限；UI 无 500 ms+ 卡顿 |
| PERF-04 | 4 MiB 边界 JSON | 峰值受控，无 LOH 持续增长 |
| PERF-05 | 100,000 审计条目分页 | 首屏 P95 ≤ 300 ms |
| STAB-01 | 8 小时连接/断线循环 | 无无界 handle/thread/memory 增长 |
| STAB-02 | 连续取消 1,000 次调用 | pending/permit/queue 回到 0 |

性能测试在指定 Windows VM 配置记录 CPU、内存、版本和原始结果；不得只写“体感流畅”。

## 8. 数据、升级和安装

- V1.3→1.4.0→1.4.1→1.4.2 顺序升级，原任务/资产/会话数与抽样内容一致。
- V1.3 直接升级 V1.4.2，迁移依次运行且结果一致。
- 每个迁移中点注入失败，数据库回滚并可用旧备份恢复。
- MSIX 首装、覆盖升级、修复、卸载；卸载策略明确用户数据是否保留。
- 无 WebView/VC Runtime/.NET 等隐含开发机依赖；安装包依赖声明正确。
- 断网首次启动仍可使用本地功能，并对外部能力给出准确状态。

## 9. 发布判定

- P0 用例 100% 通过。
- P1 核心功能与迁移用例 100% 通过。
- 无 Critical/High 安全缺陷；无阻断/严重功能缺陷。
- 中低缺陷必须有用户影响、规避方法、负责人和计划版本，不能用文档掩盖必须需求缺失。
- Release x64、MSIX 打包、签名配置验证、干净 VM 冒烟均通过。
