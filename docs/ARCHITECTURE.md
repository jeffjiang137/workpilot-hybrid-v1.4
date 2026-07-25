# V1.3 架构说明

## 依赖方向

```text
WinUI Views
  ↓
AppServices / SpaceService / TaskService / Asset services / AgentService
  ↓                         ↓
SQLite repositories         OpenAI-compatible client
  ↓                         ↓
NativeWorkspaceFactory → per-run NativeWorkspaceSession
  ↓
workpilot_core.dll：路径、权限、扫描、指纹、读写事务
```

View 不写 SQL、不直接 P/Invoke、不读取项目文件。C++ Core 不依赖 WinUI、数据库或模型协议。`AppServices` 是组合根；领域算法（任务状态、文本规范化、分块）不引用 WinUI。

## Native 生命周期

`wp_abi_version()` 为 `0x00010300`。每次 Agent Run 和每个索引 worker 通过 Factory 建立独立 `wp_context`，避免可变工作区根串用。扫描再建立 `wp_scan`；调用方必须先 `wp_scan_destroy`，再 `wp_destroy`。所有返回 UTF-8 字符串用 `CoTaskMemAlloc` 分配，并用 `wp_free` 释放；异常不跨 C ABI。

分页扫描使用显式目录队列，单页最多 200 项、深度 32、项目 100,000 文件。每个候选文件在输出和读取前都经过 C++ 最终路径验证。快速指纹混合文件大小、修改时间和去重后的首尾各最多 64 KiB。

## 数据与迁移

`DatabaseMigrator` 探测真实 V1.2 基线、登记 migration 012、使用 SQLite Online Backup 后执行 migration 013。事务重建 projects/conversations，建立 spaces/tasks/assets/asset_chunks/FTS/index_state，随后运行外键和完整性检查。失败不会以半套 V1.3 schema 继续启动。

发布依赖显式固定 `SQLitePCLRaw.bundle_e_sqlite3 3.0.4`，索引启动前读取 `sqlite_version()`；低于 3.51.3 且不是 3.50.7/3.44.6 修复回移版本会阻止并发索引并提示重新构建完整安装包。

索引使用 generation 防止旧扫描覆盖新结果。FTS5 是 external-content 表，通过 insert/delete/update 触发器与 `asset_chunks` 同事务更新；单资产的旧块删除和新块插入在一个事务中完成。

## 索引和搜索

索引 Coordinator 使用容量 200 的有界 Channel、两个 worker、项目级取消和单项目互斥。Watcher 缓冲区固定 32 KiB，事件 500 ms 合并后触发 reconciliation；溢出同样重扫。

文本仅接受白名单类型、UTF-8 且不超过 512 KiB。搜索文本做 NFKC/invariant lowercase 和 CJK unigram/bigram 扩展；MATCH 参数始终参数化并逐 token 引号转义。查询序列和 CancellationToken 防止旧结果覆盖新结果。搜索缓存 100 项/5 分钟/20 MiB，预览缓存 20 项/10 MiB。

## Agent

Agent 保留 8 个模型步骤、20 次工具调用、24 条消息和重复调用熔断。`search_assets` 只有在当前项目索引 ready/limit_reached 时暴露，不接受模型提供 project/space ID，最多 8 块、每块 4,000 字符、总 20,000 字符。资产正文用 `untrusted_asset` 标签封装。
