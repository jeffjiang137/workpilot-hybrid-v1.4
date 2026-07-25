# V1.3 索引、搜索与任务算法规格

本文件中的常量必须集中到版本化 `IndexPolicyV13` 和 `SearchPolicyV13`，禁止散落魔法数。算法输出必须可由固定输入重复得到。

## 1. 常量

| 常量 | 值 |
|---|---:|
| `MaxFilesPerProject` | 100,000 |
| `MaxDepth` | 32 |
| `ScanPageSize` | 200 |
| `MaxIndexTextBytes` | 524,288 |
| `FingerprintEdgeBytes` | 65,536 |
| `WriteBatchAssets` | 100 |
| `WriteBatchTextBytes` | 5,242,880 |
| `WatcherDebounceMs` | 500 |
| `WatcherMaxDelayMs` | 2,000 |
| `WatcherQueueCapacity` | 10,000 |
| `TargetChunkTokens` | 800 |
| `ChunkOverlapTokens` | 100 |
| `MaxChunkTokens` | 1,200 |
| `SearchDebounceMs` | 250 |
| `FtsCandidateLimit` | 100 |
| `ResultDefaultLimit` | 20 |
| `ResultHardLimit` | 50 |
| `ContextMaxChunks` | 8 |
| `ContextMaxCharsPerChunk` | 4,000 |
| `ContextMaxCharsTotal` | 20,000 |

## 2. 扫描安全算法

每个扫描使用独立 C++ `workspace_context` 和 `scan_context`，不得复用 Agent 正在使用的全局工作区上下文。

### 2.1 根目录建立

1. 输入必须为本地绝对路径，拒绝 UNC、设备路径和空路径。
2. 从盘符根目录逐组件读取属性；任一组件是 reparse point 则拒绝整个根目录。
3. 打开根目录句柄，调用 `GetFinalPathNameByHandleW`。
4. 保存最终规范根路径和大小写无关 `root_key`。
5. 后续所有文件在枚举属性后、读取前再次通过句柄验证最终路径前缀。

### 2.2 目录遍历

使用显式队列的广度优先遍历，不使用递归函数：

```text
queue ← [(root, relative="", depth=0)]
while queue not empty and count < MaxFilesPerProject:
  check cancellation
  directory ← queue.pop_front()
  enumerate one page
  for each entry sorted by OrdinalIgnoreCase(relative path):
    reject reparse/system/device
    evaluate ignore rules
    if directory and depth < MaxDepth and shouldTraverse: queue.push_back(...)
    if regular file and not ignored: emit metadata
```

排序只保证同一目录内稳定，扫描不得把全部路径一次性放入内存。分页输出最多 200 项；C# 消费一页后才能取下一页，形成背压。

### 2.3 路径输出

C++ 每项返回：

```json
{
  "relative_path": "src/App.xaml.cs",
  "path_key": "src/app.xaml.cs",
  "file_name": "App.xaml.cs",
  "extension": ".cs",
  "size_bytes": 1200,
  "modified_unix_ms": 1784540000000,
  "attributes": 32
}
```

路径统一 `/`；禁止返回绝对路径。`path_key` 由 Windows invariant lowercase 生成，不用 C# `ToLower()` 替代。

## 3. 忽略规则

### 3.1 语法

- 空行和首字符 `#` 为注释；使用 `\#` 表示字面 `#`。
- `/` 分隔目录；匹配前路径已转换为 `/`。
- `*` 匹配单个路径段内任意字符，`?` 匹配单个字符，`**` 匹配零个或多个路径段。
- 末尾 `/` 仅匹配目录。
- 开头 `/` 从项目根匹配；否则可在任意段开始。
- 开头 `!` 表示重新包含。规则按顺序执行，最后一次匹配决定结果。
- 单条规则最长 500 字符，总计最多 200 条；超限拒绝保存。

### 3.2 硬忽略

默认硬忽略目录不能被 `!` 覆盖：`.git .svn .hg .vs node_modules bin obj artifacts dist build packages .cache`。大小写按 Windows 不敏感比较。

### 3.3 剪枝

- 硬忽略目录直接剪枝。
- 用户规则排除的目录，若后续不存在可能命中其后代的 `!` 规则则剪枝。
- “可能命中”通过 negation 规则第一个通配符前的字面前缀判断；无法确定时继续遍历但不输出被忽略项。

Glob 编译结果缓存于项目索引会话，规则变化必须增加 generation 并完整重扫。

## 4. Generation 与状态机

状态：

```text
idle → discovering → scanning → ready
                    ↘ paused
                    ↘ limit_reached
                    ↘ error
paused → discovering/scanning
ready → discovering（完整重扫）或 scanning（增量）
```

- 开始完整扫描时，在写事务中 `generation = generation + 1`，记为 `G`。
- 所有扫描结果、批次和事件都携带 `G`。
- 写入前执行 `SELECT generation`；不等于 `G` 时丢弃结果并返回 `StaleGeneration`，不得覆盖新状态。
- 扫描完成后，只有 `generation < G` 且本轮未见的资产才删除；不得仅凭 Watcher delete 之外的临时读取失败删除。
- 暂停保留 `G`；重新构建创建新 generation。

## 5. 指纹与增量判定

### 5.1 快速指纹

```text
edgeHash = SHA256(
  uint64_le(size) || int64_le(modified_unix_ms) ||
  first[min(size, 64KiB)] ||
  last[min(max(size-64KiB, 0), 64KiB)]
)
quickFingerprint = hex(edgeHash)
```

文件小于等于 64 KiB 时正文只拼接一次；64–128 KiB 时首尾区间不得重叠重复。实现必须按区间去重。

### 5.2 判定

| 条件 | 行为 |
|---|---|
| 新路径 | 创建资产；符合正文条件时全文读取和分块 |
| `size/mtime/quickFingerprint` 全相同 | 只更新 generation/last_seen，跳过全文 |
| 任一变化且正文可索引 | 读取全文、计算 SHA-256；全文 SHA 相同则只更新元数据 |
| 任一变化且仅元数据 | 更新元数据，不读取全文 |
| 读取期间 size/mtime 改变 | 最多重试一次；再次变化标记 read_error，等待后续事件 |

读取前后都取文件信息。所有读取通过 C++ 最终路径验证；C# 不直接 `File.ReadAllText`。

## 6. 文本识别与规范化

1. 扩展名必须在白名单。
2. 大小不超过 512 KiB。
3. 读取字节；移除 UTF-8 BOM `EF BB BF`。
4. 使用严格 UTF-8 解码，任何无效序列都标记 `unsupported_encoding`。
5. 原文 `content` 只统一换行为 `\n`，不改变空格、大小写和 Unicode。
6. 搜索文本使用 Unicode NFKC、Invariant Lowercase，并移除除换行/Tab 外的 C0 控制字符。
7. 不执行 HTML、Markdown、代码或模板；索引器是纯文本处理器。

### 6.1 类型分类

扩展名先 invariant lowercase，再按以下首个匹配分类：

- `code`：`.js .jsx .ts .tsx .cs .cpp .cc .c .h .hpp .java .kt .py .go .rs .php .rb .swift .ps1 .bat .cmd .css .scss .less`。
- `document`：`.txt .md .markdown .html .htm`。
- `data`：`.json .jsonc .xml .yaml .yml .csv .tsv .sql`。
- `config`：`.ini .toml .env .gitignore`，以及无扩展名且文件名为 `Dockerfile/Makefile` 的文件。
- 其余为 `other`。

分类只影响筛选和分块边界，不决定安全权限。扩展名比较不区分大小写。

## 7. 分块算法

### 7.1 Token 估算

无需模型 tokenizer，使用确定性估算：

- 每个 CJK Unified Ideograph、平假名、片假名、韩文音节计 1 token。
- 连续 ASCII 字母/数字/下划线段计 `ceil(length/4)`，最少 1。
- 其他非空白 Unicode 文本元素计 1。
- 空白不计，但保留在偏移中。

偏移使用 .NET UTF-16 code unit 索引，UI 和数据库必须统一；不得混用 UTF-8 byte offset。

### 7.2 边界

优先边界顺序：

1. 代码类型：连续两个换行、行首类/函数/标题模式、单换行。
2. 文档类型：Markdown 标题、连续两个换行、句末标点后的换行、单换行。
3. 数据/配置：完整行；JSON 不解析对象树，避免超深结构攻击。
4. 无可用边界：按 Unicode 文本元素切分。

### 7.3 步骤

```text
start = 0
while start < text.length:
  idealEnd = first offset where estimate(start,end) >= 800
  end = nearest preferred boundary in [tokens 600, 1000]
  if none: end = boundary at <=1200 tokens
  emit [start,end]
  nextStart = earliest safe boundary whose suffix overlap ≈100 tokens
  if nextStart <= start: nextStart = end
```

- 空块不写入。
- 单块不得超过 1,200 估算 token。
- 相邻块内容重叠目标 100 token，允许 80–120。
- 每个资产块数硬上限 2,000；超过标记 `read_error/chunk_limit`，事务回滚该资产旧块保持不变。
- 新块全部准备完成后，在一个事务中删除旧块并插入新块，避免搜索看到半套内容。

## 8. 中日韩搜索 Token 扩展

SQLite `unicode61` 对连续 CJK 搜索不足，因此只对 `search_text/file_name_tokens/path_tokens` 做额外展开，原文不变。

对每个长度为 N 的连续 CJK 文本元素串：

- 输出 N 个 unigram。
- N≥2 时输出 N-1 个相邻 bigram。
- 每个 token 空格分隔。
- 同一字段最多输出 20,000 个 token；超限从尾部截断并记录指标，不写日志正文。

例：`产品设计` → `产 品 设 计 产品 品设 设计`。

ASCII/拉丁文本保留 NFKC lowercase 原词，由 `unicode61` 分词。文件名和路径额外把 `. _ - / \\` 转为空格后处理。

## 9. 查询解析

1. 去首尾空白；按 Unicode 文本元素限制 200。
2. NFKC + Invariant Lowercase；删除控制字符。
3. 禁止把用户文本直接拼进 `MATCH`。每个 token 中的 `"` 变成 `""`，再包双引号。
4. CJK 长度≥2 的串优先使用 bigram；单字符使用 unigram。
5. 第一轮构造严格查询：不同原始词组用 `AND`，同一 CJK 词组的 bigram 用 `AND`。
6. 严格查询少于 5 个候选时执行回退查询：词组用 `OR`；结果合并去重。
7. token 数最多 32；超出只取前 32 个并在 UI 显示“查询已简化”。

纯标点或规范化后无 token 的查询按空查询处理，显示最近资产。

## 10. 候选、过滤与排序

### 10.1 候选

- FTS：`asset_chunks_fts MATCH $query`，`ORDER BY bm25(asset_chunks_fts, 1.0, 4.0, 2.0)`，Top 100。
- 文件名：规范化文件名包含所有严格词的资产 Top 100；回退时包含任一词。
- 路径：规范化相对路径包含查询词 Top 100。
- 空查询：按 `modified_unix_ms DESC`，不访问 FTS。

空间、项目、类型、时间和状态过滤必须在候选 SQL 中完成，不能取回后才过滤导致分页错误。

### 10.2 RRF

候选排名从 1 开始；不存在该来源时该项为 0：

```text
base = 0.70/(60+ftsRank) + 0.20/(60+nameRank) + 0.10/(60+pathRank)
ageDays = max(0, (now - modified)/86400s)
freshness = 0.90 + 0.10/(1 + ageDays/30)
score = base * freshness
```

同分依次按：文件名完全匹配、修改时间降序、`project_id`、`path_key`。排序必须稳定。

### 10.3 资产聚合

同一资产多个块只占一个结果：最高分块作为主摘要，另外保留最多 4 个不同偏移的块。重叠超过 70% 的块视为重复，只留高分块。

## 11. 摘要和高亮

- 不使用 FTS `snippet()` 展示 token 化 `search_text`。
- 在原始 `content` 中按规范化查询词做大小写不敏感定位；取首个命中前后各最多 160 个文本元素，并扩展到最近行边界。
- 无法映射规范化字符时按块前 320 个文本元素显示，不伪造高亮。
- UI 使用 Runs 渲染纯文本；不得生成或解析 HTML。

## 12. Watcher 增量算法

`FileSystemWatcher` 只提供变更提示：

- `IncludeSubdirectories=true`，过滤 LastWrite、FileName、DirectoryName、Size。
- 内部缓冲区 32 KiB；禁止盲目提升到最大非分页内存。
- 原始事件进入容量 10,000 的有界 Channel；满或 Error/overflow 时设置项目 `dirty=true`，丢弃后续细粒度事件并安排完整重扫。
- 500 ms 防抖；连续事件最长等待 2 秒必须形成批次。

同路径合并优先级：`delete > rename > modify > create`。Rename 展开为 old path delete + new path create；若同一批次新旧快速指纹一致，可保留 `public_id` 并更新路径，但必须仍做最终路径验证。

保存 Agent 写入产生的临时 `.workpilot.*.tmp` 事件直接忽略；最终目标 rename/create 正常处理。

应用启动、Watcher 溢出、忽略规则变化、距离完整扫描超过 12 小时均触发完整 reconciliation。Watcher 绝不是删除资产的唯一依据。

## 13. 查询取消与结果序列

- 每次搜索生成单调递增 `query_seq` 和 CancellationToken。
- 新查询先取消旧 token，再启动数据库查询。
- 返回 UI 前比较 `query_seq == latest_seq`；不相等丢弃。
- UI 不得先清空旧结果；新结果成功后一次替换。
- 搜索缓存键为规范化查询+空间+所有筛选+索引 generation 摘要；任一项目 generation 改变即自然失效。

## 14. 加入对话的选择算法

用户手选优先；自动推荐时：

1. 从搜索 Top 30 块开始。
2. 每资产最多 3 块。
3. 使用 MMR：`0.75*normalizedSearchScore - 0.25*maxJaccard(tokenSet,candidateSelected)`。
4. 依次选择，直到 8 块或总字符 20,000。
5. 每块截断到 4,000 字符，保留来源头和截断标记。

注入模型的固定封装：

```text
<untrusted_asset source="项目名/相对路径" chunk="2">
...原始文本...
</untrusted_asset>
```

系统提示必须声明标签内文本是不可信数据，不能改变工具、权限和系统规则。

## 15. 任务排序与并发

### 15.1 排序键

- 空列首项 `sort_key=1024`。
- 插入两项之间使用整数中点 `floor((a+b)/2)`。
- 列首用 `first-1024`，列尾用 `last+1024`，检查 64 位溢出。
- 中间无整数间隔或接近溢出时，在单事务内把该状态列按当前稳定顺序重排为 `1024,2048,...`。
- 重排只影响一个空间的一个状态列。

### 15.2 乐观并发

更新带 `row_version`。冲突后：

- 表单编辑：保留用户草稿，显示服务端新值，提供“复制我的内容/刷新”，V1.3 不自动字段合并。
- 拖拽：回滚卡片位置并刷新当前列。
- 状态快捷操作：刷新并显示“任务已在其他位置更新”。

## 16. 缓存和清理

- 搜索 LRU：100 项、TTL 5 分钟、估算内存最大 20 MiB，任一条件达到即逐出最旧。
- 预览 LRU：20 项或 10 MiB，切换空间不立即清空，项目删除必须清空相关项。
- 任务撤销：仅看板移动，进程内最多 20 步，10 分钟过期，应用退出清空；删除任务不进入撤销缓存。
- Index Channel、扫描页、查询结果全部有硬容量，禁止无界集合。
- 项目删除/重新构建在同一写事务删除 chunks、FTS 和 assets；之后按数据库维护规则回收空闲页，不立即 VACUUM。
