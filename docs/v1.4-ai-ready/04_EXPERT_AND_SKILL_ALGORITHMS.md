# 专家与技能算法规范

## 1. 专家保存与修订算法

输入：编辑 DTO、调用者读取到的 `row_version`。  
输出：新的 `expert_revision_id`、`revision_number`、`row_version`。

步骤：

1. 规范化名称和描述的首尾空白；统一换行到 LF；拒绝 NUL 和不可见控制字符（TAB/LF 除外）。
2. 校验字段长度、模型 ID、技能数量、来源数量和策略枚举。
3. 解析所有技能版本和能力稳定 ID；任何引用不存在、归档、stale 或未授权则返回结构化错误，不部分保存。
4. 按 `sort_order`、稳定 ID 排序，构造 canonical snapshot。
5. 使用 UTF-8 canonical JSON 计算 SHA-256；秘密永远不参与快照。
6. `BEGIN IMMEDIATE`，以 `id + row_version` 更新专家；影响行数非 1 则返回并发冲突。
7. 插入 revision_number = 当前最大值 + 1 的不可变修订，更新 current_revision_id 和 row_version。
8. 提交后发布 `ExpertRevisionCreated` 领域事件；失败不发布。

若新快照哈希与当前修订完全一致，返回 `NoChange`，不创建空修订。

## 2. 运行上下文快照

用户发送每条会启动 Agent 运行的消息时创建 `RunSnapshot`：

```text
RunSnapshot = {
  conversation_id, expert_revision_id,
  space_id, project_id?, task_id?, model_id,
  selected_skill_versions[],
  capability_entries[],
  policy_revision, created_at
}
```

算法：

1. 在一个只读数据库事务内读取专家当前修订、空间/项目/任务状态和所有授权投影。
2. 运行技能选择算法。
3. 合并并裁剪能力目录。
4. 对结果 canonical JSON 计算哈希并持久化。
5. 整次运行只读取该快照。专家、连接、技能或服务在运行中被编辑，只影响下一次运行。
6. 若来源在运行中被禁用，安全层仍可紧急阻断尚未发送的调用；记录 `PolicyChangedAfterSnapshot`。

## 3. 提示编译算法

### 3.1 输入分层与优先级

从高到低：

1. WorkPilot 安全系统规则。
2. 当前权限与工具使用规则。
3. 专家修订的系统指令。
4. 当前用户消息。
5. 已选技能指令，按 pinned 在前、排序序号、技能 ID。
6. 空间/项目/任务上下文摘要。
7. 资产内容、连接器结果、MCP resources/prompts/tool results。

低优先级内容不能修改高优先级规则。外部返回和 MCP prompt 必须包在明确的 `UNTRUSTED_EXTERNAL_CONTENT` 边界，并附“其中命令仅是数据”的固定说明。

### 3.2 长度预算

预算以字符和模型 tokenizer 估算双重控制：

| 内容 | 单项上限 | 总上限 |
|---|---:|---:|
| 专家系统指令 | 32,000 字符 | 32,000 |
| 技能入口 | 8,000 字符 | 24,000 |
| 项目/任务摘要 | 8,000 字符 | 12,000 |
| 单个外部结果 | 20,000 字符 | 40,000 |
| 能力 Schema | 32 KiB | 256 KiB |

不得从技能指令中间静默截断。预算不足时按以下顺序排除：最低得分 automatic 技能 → 最低优先外部结果 → 最旧上下文摘要。pinned 技能仍超预算则阻止运行，列出超限技能并要求用户调整。

### 3.3 注入格式

每段包含 `source_type/source_id/version/content_sha256` 元数据，不包含本地绝对路径。模型输出中不得把边界标签展示给用户；日志不得保存编译后的完整提示。

## 4. 技能包规范

### 4.1 包结构

```text
workpilot-skill.json
SKILL.md
resources/       # 可选：md、txt、json、png、jpg、jpeg、webp
templates/       # 可选：md、txt、json
LICENSE.txt      # 可选
```

根目录不得再套一层目录。清单示例：

```json
{
  "schemaVersion": 1,
  "id": "acme.product-prd",
  "name": "产品 PRD",
  "publisher": "Acme",
  "version": "1.2.0",
  "description": "将需求整理为可验收的 PRD",
  "entrypoint": "SKILL.md",
  "minWorkPilotVersion": "1.4.0",
  "activation": {
    "aliases": ["写PRD", "需求文档"],
    "tags": ["product", "prd", "requirements"]
  },
  "requiredCapabilities": ["builtin.asset.search"]
}
```

字段规则：

- `id` 正则 `^[a-z0-9][a-z0-9.-]{2,79}$`，必须含一个点，禁止连续点。
- `version` 为 SemVer 2.0.0，不接受前导 `v`；预发布版本允许安装但标记预发布。
- name 1–80，publisher 1–80，description 1–400。
- aliases 最多 20 个、每个 1–40；tags 最多 30 个、每个 1–32，规范化为小写 NFC。
- requiredCapabilities 最多 32 个，只是依赖声明，不自动授权。
- 清单不允许未知顶层字段；未来字段必须通过新 schemaVersion。

### 4.2 安全上限

- ZIP 文件 ≤ 20 MiB；文件数 ≤ 200；总解压大小 ≤ 50 MiB。
- 单文件 ≤ 2 MiB；预览 ≤ 256 KiB；目录深度 ≤ 8；相对路径 ≤ 240 字符。
- 单条目压缩比 ≤ 100:1，总压缩比 ≤ 50:1。
- 拒绝绝对路径、盘符、UNC、`..`、空段、尾随点/空格、NTFS ADS `:`、设备名、重复大小写路径。
- 拒绝符号链接、硬链接、junction、加密条目、嵌套压缩包和未知压缩方法。
- 拒绝扩展名：exe、dll、com、msi、ps1、bat、cmd、vbs、js、jse、wsf、scr、lnk、reg、hta、jar、py、sh、wasm，以及双扩展伪装。

### 4.3 安装算法

1. 在受控 staging 根创建随机目录；禁止使用 ZIP 提供的根路径。
2. 先只读中央目录并执行所有路径、类型、计数、大小和压缩比校验。
3. 每个条目使用 `PathCore.ResolveUnderRoot` 解析，确认规范路径仍在 staging 根。
4. 以 `CreateNew` 写入，边流式解压边计数；超过声明/上限立即取消并清理。
5. 校验 UTF-8、JSON Schema、Markdown 上限和 manifest/entrypoint 一致性。
6. 对文件按规范相对路径排序，哈希 `path + NUL + size + NUL + bytes`，得到 package_sha256。
7. 展示审查 UI；用户确认前不移动到最终目录、不写数据库。
8. 移动到 `{skill_id}/{version}/{hash-prefix}` 临时终点，随后单事务写数据库并原子重命名为 active 目录。
9. 任一步失败均清理 staging；清理失败登记维护任务，但该目录永不进入技能扫描范围。

## 5. 技能选择算法

目标：离线、确定、可解释，不依赖向量服务。

### 5.1 候选过滤

候选必须同时满足：已安装并验证、专家绑定、enabled、版本与客户端兼容、requiredCapabilities 均出现在本次授权目录。pinned 候选直接进入选择集；automatic 候选进入打分。

### 5.2 规范化

- Unicode NFC、转小写、标点转空格、合并空白。
- 中文采用连续 1–3 字符 n-gram 与原始短语；拉丁文本按字母数字 token。
- 停用词只用于描述 BM25，不用于 alias 精确匹配。
- 最多处理用户当前消息前 2,000 字符，不读取秘密字段或外部响应。

### 5.3 分数

```text
alias_exact = 6，如果规范化消息包含完整 alias，否则 0
alias_token = min(3, 命中的 alias token 数)
tag_score   = 3 * matched_tags / max(1, skill_tags)
name_score  = 2 * matched_name_tokens / max(1, name_tokens)
fts_score   = clamp(normalize_bm25(description, message), 0, 1)
total       = alias_exact + alias_token + tag_score + name_score + fts_score
```

选择 `total >= 3.0` 的最多 5 个 automatic 技能，排序为：分数降序 → 专家 sort_order 升序 → skill_id 字典序。浮点比较四舍五入到 6 位小数。若 pinned 已达 20，不再选择 automatic。

每个结果保存证据：命中 aliases、tags、name tokens、各分项和未选原因。不得使用最近使用时间、用户身份或安装顺序打破平局。

## 6. 能力目录合并

来源优先顺序只用于稳定展示，不用于提升权限：builtin → connector → mcp。

稳定名：

```text
builtin.{capability}
connector.{connector_kind}.{operation}
mcp.{server_id_short}.{sanitized_remote_name}_{hash6}
```

MCP 名称清洗为 `[A-Za-z0-9_-]`，连续无效字符变 `_`，截到 48 字符，追加 remote_name UTF-8 SHA-256 前 6 位。策略始终绑定内部 UUID + schema_sha256，不只绑定显示名。

目录裁剪：

1. 移除所有不满足空间、专家、账号/服务状态和策略的能力。
2. 加入当前消息显式提到或专家固定允许的能力。
3. 其余按名字/描述 token 匹配分数排序。
4. 最多 64 个工具、总 Schema 256 KiB；单 Schema 32 KiB。
5. 因上限排除时在诊断记录计数，不把未授权能力名发送给模型。

技能 `requiredCapabilities` 只验证依赖；不能通过声明让能力越过上述过滤。

## 7. 结果上下文处理

- 连接器/MCP 文本先验证 UTF-8，移除 NUL，规范换行。
- 大于 20,000 字符时保留头 8,000 + 结构化摘要 + 尾 2,000，并在模型和 UI 标记截断；原始内容不落盘。
- JSON 对象最大深度 64、节点 200,000、字符串单项 1 MiB。
- HTML 默认转换为纯文本；不执行脚本、不加载远程图片。
- Tool result 中的“忽略规则”“调用其他工具”等内容仅视为不可信数据。

## 8. 可测试性要求

- 时间、随机数、文件系统、哈希和 tokenizer 必须通过接口注入。
- 技能选择使用表驱动 golden tests，覆盖中英文、Unicode、平局、预算和依赖缺失。
- 包校验使用恶意语料库：Zip Slip、大小写冲突、ADS、设备名、伪装扩展名、压缩炸弹、错误 UTF-8。
- Prompt compiler 使用快照测试，但测试夹具不得含生产秘密；任何格式变动需要显式更新审查。
