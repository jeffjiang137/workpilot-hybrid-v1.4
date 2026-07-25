# V1.3 QA 与验收矩阵

## 1. 等级

- P0：数据丢失、安全越界、无法启动、安装失败；任一失败禁止发布。
- P1：核心闭环错误、结果不可信、严重性能/资源问题；任一失败禁止发布。
- P2：次要体验、布局和非阻断提示；必须有明确修复结论。

证据至少包含测试输出、环境、构建号和失败截图/日志编号。日志不得包含资产正文、搜索词、任务描述、绝对路径或密钥。

## 2. 数据迁移

| ID | 级别 | 类型 | 场景与预期 |
|---|---|---|---|
| MIG-001 | P0 | 自动 | 真实 V1.2 fixture 升级；项目、会话、消息、设置、自动化数量和内容完全一致。 |
| MIG-002 | P0 | 自动 | 升级后创建唯一默认空间，全部旧项目/会话归入该空间。 |
| MIG-003 | P0 | 自动 | 重复启动不重复执行 013，不重复空间或索引。 |
| MIG-004 | P0 | 自动 | 迁移中途异常回滚，原 V1.2 数据仍可读。 |
| MIG-005 | P0 | 自动 | commit 后完整性失败，自动保留 `.failed-v13` 并恢复备份。 |
| MIG-006 | P0 | 自动 | 迁移前备份使用 SQLite Backup API，包含 WAL 已提交数据。 |
| MIG-007 | P1 | 自动 | 仅部分 V1.2 表存在时拒绝猜测迁移并显示可操作错误。 |
| MIG-008 | P1 | 自动 | checksum 被修改时启动失败，不执行未知迁移。 |
| MIG-009 | P1 | 自动 | `foreign_key_check` 和 `integrity_check` 均通过。 |
| MIG-010 | P2 | 自动 | 只保留最近 3 个迁移备份，其他普通文件不受影响。 |

## 3. 空间

| ID | 级别 | 类型 | 场景与预期 |
|---|---|---|---|
| SPC-001 | P1 | 自动/UI | 创建合法空间后自动切换，重启仍保持。 |
| SPC-002 | P1 | 自动 | 名称 0、1、40、41 文本元素边界正确；组合 Emoji 不被拆错。 |
| SPC-003 | P1 | 自动 | 只接受 8 个颜色 token。 |
| SPC-004 | P1 | UI | 折叠侧栏空间按钮可键盘操作且 Tooltip 正确。 |
| SPC-005 | P1 | 自动/UI | 默认空间不可删除，可重命名。 |
| SPC-006 | P1 | 自动/UI | 含任何项目或任务的空间拒绝删除并显示真实数量。 |
| SPC-007 | P1 | 自动 | 归档当前空间后回退到最早未归档空间。 |
| SPC-008 | P1 | 自动 | 两次并发更新，第二次返回 ConcurrencyConflict，不覆盖。 |
| SPC-009 | P1 | 集成 | 空间切换不改变正在运行 Agent/索引的 project/session。 |
| SPC-010 | P2 | UI | >8 空间出现搜索；已归档默认折叠，可恢复。 |

## 4. 正式任务

| ID | 级别 | 类型 | 场景与预期 |
|---|---|---|---|
| TSK-001 | P1 | 自动/UI | 创建任务并在正确状态列首出现。 |
| TSK-002 | P1 | 自动 | 标题/描述边界、非法空间/跨空间项目被拒绝。 |
| TSK-003 | P1 | 自动 | 状态流转表全部合法路径成功，非法路径返回类型化错误。 |
| TSK-004 | P0 | 自动 | `done` 与 completed_at 不变量始终成立。 |
| TSK-005 | P1 | 自动 | 重新打开 done/cancelled 到 todo 并清空 completed_at。 |
| TSK-006 | P1 | 自动/UI | 跨列拖拽成功更新状态与排序。 |
| TSK-007 | P1 | UI | 模拟写失败，拖拽卡片回原位置，显示错误。 |
| TSK-008 | P1 | 自动 | 同列无排序间隔时只重排目标空间/状态列。 |
| TSK-009 | P1 | 自动/UI | row_version 冲突不覆盖用户/服务端内容。 |
| TSK-010 | P1 | 集成 | 开始任务只创建一个主对话；重复点击打开原对话。 |
| TSK-011 | P1 | 集成 | 删除任务保留对话；删除对话清空任务关联。 |
| TSK-012 | P1 | 集成 | 删除项目保留任务并把 project_id 置空。 |
| TSK-013 | P1 | UI | 任务预填上下文不自动发送。 |
| TSK-014 | P2 | UI | 看板/列表切换保留筛选；重启保留视图偏好。 |
| TSK-015 | P2 | UI | 逾期/今天/7日/无日期筛选按本地时区边界正确。 |
| TSK-016 | P1 | UI | 键盘菜单可完成所有拖拽状态操作。 |

## 5. Native 扫描安全

| ID | 级别 | 类型 | 场景与预期 |
|---|---|---|---|
| NAT-001 | P0 | Native | `../`、绝对路径、盘符相对路径全部拒绝。 |
| NAT-002 | P0 | Native | 根目录本身或任一父组件为 symlink/junction 时拒绝。 |
| NAT-003 | P0 | Native | 工作区内链接到外部或内部都不遍历。 |
| NAT-004 | P0 | Native | 打开句柄后的最终路径不在根内时拒绝。 |
| NAT-005 | P0 | Native | UNC、设备路径、网络共享拒绝。 |
| NAT-006 | P1 | Native | 0/1/200/201/10,000 文件分页无遗漏、无重复。 |
| NAT-007 | P1 | Native | done 后重复 next 返回空完成页，不崩溃。 |
| NAT-008 | P1 | Native | cancel 与 next 并发安全，destroy 不 use-after-free。 |
| NAT-009 | P1 | Native | 深度 32 可扫描，33 被跳过并计数。 |
| NAT-010 | P1 | Native | 100,001 文件进入 limit_reached，内存保持有界。 |
| NAT-011 | P1 | Native | 单页不超过 200 项/2 MiB。 |
| NAT-012 | P1 | Native | 扫描中删除/改名/锁定文件只产生单项错误，不终止进程。 |
| NAT-013 | P1 | Native | Unicode 大小写 path_key 在 Windows 比较下稳定。 |
| NAT-014 | P1 | Native | quick fingerprint 首尾区间不重复，变化判定正确。 |
| NAT-015 | P0 | ABI | 所有返回内存可由 wp_free 释放，异常不跨 ABI。 |

## 6. 忽略规则与发现

| ID | 级别 | 类型 | 场景与预期 |
|---|---|---|---|
| IGN-001 | P1 | 自动 | 默认硬忽略目录不产生资产。 |
| IGN-002 | P1 | 自动 | `* ? ** / trailing slash` 表驱动结果符合规格。 |
| IGN-003 | P1 | 自动 | 规则顺序和最后匹配获胜。 |
| IGN-004 | P1 | 自动 | `!` 不能覆盖硬忽略，但能重新包含用户排除路径。 |
| IGN-005 | P1 | 自动 | 注释、转义 #、空行正确。 |
| IGN-006 | P1 | 自动/UI | >200 条或单条 >500 字符拒绝保存。 |
| IGN-007 | P1 | 集成 | 规则变化增加 generation 并完整重扫。 |

## 7. 文本、分块与 FTS

| ID | 级别 | 类型 | 场景与预期 |
|---|---|---|---|
| IDX-001 | P1 | 自动 | UTF-8 BOM/无 BOM 成功，非法 UTF-8 标 unsupported_encoding。 |
| IDX-002 | P1 | 自动 | 512 KiB 成功，512 KiB+1 仅元数据。 |
| IDX-003 | P1 | 自动 | 非白名单扩展名只索引元数据。 |
| IDX-004 | P1 | 自动 | CRLF/CR 统一为 LF，原文其他字符不变。 |
| IDX-005 | P1 | Golden | 中英混合、Emoji、组合字符产生固定 chunks。 |
| IDX-006 | P1 | 自动 | 每块 ≤1200 token，overlap 80–120，无空块和死循环。 |
| IDX-007 | P1 | 自动 | >2000 块回滚该资产旧块，不留半索引。 |
| IDX-008 | P1 | 自动 | CJK unigram/bigram token 与查询 token 对称。 |
| IDX-009 | P0 | 自动 | 引号、FTS 操作符、控制字符不能注入 MATCH SQL。 |
| IDX-010 | P0 | 集成 | asset insert/update/delete 后 FTS integrity-check 通过。 |
| IDX-011 | P1 | 集成 | 全文 SHA 未变时不重建 chunks。 |
| IDX-012 | P1 | 集成 | 单资产更新事务期间搜索看不到半套 chunks。 |

## 8. 增量、并发与恢复

| ID | 级别 | 类型 | 场景与预期 |
|---|---|---|---|
| INC-001 | P1 | 集成 | create/modify/delete/rename 在 3 秒内反映。 |
| INC-002 | P1 | 集成 | 500 ms 防抖与 2 秒最大等待均成立。 |
| INC-003 | P1 | 集成 | 同路径事件按 delete>rename>modify>create 合并。 |
| INC-004 | P0 | 集成 | Watcher overflow 标脏并完整重扫，不静默丢变化。 |
| INC-005 | P0 | 集成 | 旧 generation 批次返回后被丢弃，不覆盖新索引。 |
| INC-006 | P1 | 集成 | 扫描暂停/继续保持 generation；重建增加 generation。 |
| INC-007 | P1 | 集成 | 临时 `.workpilot.*.tmp` 不进入资产，最终目标进入。 |
| INC-008 | P1 | 集成 | 第二次未变扫描跳过率 ≥95%。 |
| INC-009 | P1 | 集成 | UI 写请求在大扫描中不被长期饿死，Index 也不永久饥饿。 |
| INC-010 | P1 | 集成 | 应用异常退出后启动能恢复 scanning 为 paused/重扫，不永久卡住。 |
| INC-011 | P1 | 集成 | 同时扫描两个项目不串路径、不串 generation。 |
| INC-012 | P1 | 集成 | Agent 读取与索引并发不共享 context。 |

## 9. 搜索与预览

| ID | 级别 | 类型 | 场景与预期 |
|---|---|---|---|
| SRC-001 | P1 | Golden | 固定数据集查询顺序稳定，严格/回退结果符合预期。 |
| SRC-002 | P1 | 自动 | 中文、英文、文件名、路径、正文均可命中。 |
| SRC-003 | P1 | 自动 | 空查询按修改时间返回，不访问 FTS。 |
| SRC-004 | P1 | 自动 | 空间、项目、类型、时间、状态筛选在分页前生效。 |
| SRC-005 | P1 | 自动 | 同一资产多块合并，重叠 >70% 去重。 |
| SRC-006 | P1 | 自动 | 同分排序按文件名精确、时间、项目、路径稳定。 |
| SRC-007 | P1 | UI/自动 | 新查询取消旧查询，旧结果永不覆盖新结果。 |
| SRC-008 | P1 | UI | 查询错误保留旧结果并显示 InfoBar。 |
| SRC-009 | P1 | UI | 预览最多 100 KiB/2000 行，纯文本、不执行 HTML/Markdown。 |
| SRC-010 | P0 | UI | UI 不显示绝对路径。 |
| SRC-011 | P1 | 自动 | 搜索缓存 100/5min/20MiB 任一上限触发逐出。 |
| SRC-012 | P1 | 自动 | 项目 generation 变化后旧缓存不再命中。 |
| SRC-013 | P2 | UI | Ctrl+K 聚焦、加载更多最大 50、筛选清除正确。 |
| SRC-014 | P1 | 性能 | 10k 资产/50k chunks，普通查询 P95 <300 ms。 |

## 10. Agent 与资产引用

| ID | 级别 | 类型 | 场景与预期 |
|---|---|---|---|
| AGT-001 | P0 | 自动 | 无项目时不暴露 search_assets。 |
| AGT-002 | P0 | 自动 | 模型不能提供或伪造 project_id/space_id。 |
| AGT-003 | P1 | 自动 | 返回最多 8 块、每块 4000、总 20000 字符。 |
| AGT-004 | P0 | 安全 | 资产内提示注入不能改变权限、系统提示和工具范围。 |
| AGT-005 | P0 | 抓包 | 只有用户选中并发送的引用正文进入模型请求。 |
| AGT-006 | P1 | UI | 删除引用卡后正文不发送。 |
| AGT-007 | P1 | 自动 | 索引未 ready 返回结构化状态，不直接递归文件。 |
| AGT-008 | P1 | 自动 | search_assets 遵循取消、步骤/工具上限和无进展熔断。 |
| AGT-009 | P0 | 日志 | 日志无资产正文、搜索词、任务描述、API Key。 |
| AGT-010 | P1 | 自动 | MMR 每资产 ≤3 块，结果可重复。 |

## 11. 存储、缓存与生命周期

| ID | 级别 | 类型 | 场景与预期 |
|---|---|---|---|
| RES-001 | P0 | 集成 | 删除项目只删配置/索引，不删除任何磁盘文件。 |
| RES-002 | P1 | 集成 | DB 750 MiB 警告；1 GiB 停正文索引但保留元数据。 |
| RES-003 | P1 | 自动 | Watcher Channel 10k、写队列 200，不无界增长。 |
| RES-004 | P1 | 自动 | 预览 LRU 20/10MiB，项目删除清缓存。 |
| RES-005 | P2 | 自动 | 任务移动撤销 20 步/10 分钟，退出清空。 |
| RES-006 | P1 | 集成 | 日志轮转 10MiB×5。 |
| RES-007 | P1 | 集成 | WAL checkpoint 不被长期 reader 阻塞至无界增长。 |
| RES-008 | P1 | 集成 | 退出 5 秒内取消/释放 Agent、Watcher、Scan、DB、Native handle。 |
| RES-009 | P1 | 长稳 | 4 小时扫描/搜索/修改压力后内存不持续线性增长。 |

## 12. UI、无障碍和发布

| ID | 级别 | 类型 | 场景与预期 |
|---|---|---|---|
| UI-001 | P1 | 手工 | 1024×720 所有核心操作可达，无文字重叠/按钮裁切。 |
| UI-002 | P1 | 手工 | 125%/150%/200% 缩放可用。 |
| UI-003 | P1 | 手工 | Tab/F6 顺序符合视觉；焦点不被后台结果夺走。 |
| UI-004 | P1 | 手工 | 图标按钮有 Tooltip/可访问名称；状态不只靠颜色。 |
| UI-005 | P1 | 手工 | 看板拖拽有键盘菜单等价。 |
| UI-006 | P2 | 手工 | 减少动画和高对比模式正确。 |
| UI-007 | P1 | 手工 | 所有 loading/empty/error/cancelled 状态有真实内容。 |
| UI-008 | P0 | 审查 | 不存在假按钮、随机进度、示例数据和未实现入口。 |
| REL-001 | P0 | Windows | V1.2 覆盖安装到 V1.3，数据完整、AppId 不变。 |
| REL-002 | P0 | Windows | Windows 10 1809 和 Windows 11 x64 干净安装、启动、卸载。 |
| REL-003 | P0 | CI | Native、Managed、Migration、Publish、Inno 全通过。 |
| REL-004 | P1 | 离线 | 无网络仍可使用空间、任务、已有索引搜索；AI 明确提示网络不可用。 |
| REL-005 | P0 | 安全 | Release 包不含真实数据库、日志、路径、密钥和测试数据。 |

## 13. 推荐测试命令

命令以最终项目名为准，但不得用模糊“运行测试”替代：

```powershell
dotnet test .\tests\WorkPilot.Domain.Tests\WorkPilot.Domain.Tests.csproj -c Release
dotnet test .\tests\WorkPilot.Integration.Tests\WorkPilot.Integration.Tests.csproj -c Release
msbuild .\src\WorkPilot.Core.Native\tests\WorkPilot.Core.Tests.vcxproj /p:Configuration=Release /p:Platform=x64
.\artifacts\tests\Release\x64\workpilot_core_tests.exe
.\scripts\build-installer.ps1
```

性能测试必须输出数据集规模、机器 CPU/内存/磁盘、冷/热缓存、P50/P95/P99，不得只记录平均值。

## 14. 发布退出条件

- 所有 P0/P1 通过。
- P2 有结论且无影响核心使用的问题。
- 迁移备份/恢复演练通过。
- Windows 双版本和缩放测试通过。
- 安装器可覆盖 V1.2，卸载不删除用户数据库。
- README、架构、安全、验证记录和版本号一致。
