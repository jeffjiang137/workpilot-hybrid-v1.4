# WorkPilot Hybrid 1.3.0

## 新增

- 空间 CRUD、归档/恢复、全局切换和 V1.2 默认空间迁移。
- 正式任务实体、看板/列表、筛选、拖拽、撤销、并发版本和主对话。
- C++ 分页安全扫描、快速指纹和独立 Native Session。
- 本地文本资产索引、FTS5、中日韩关键词扩展、筛选、预览和 Watcher reconciliation。
- 对话资产草稿引用和受限 `search_assets` Agent 工具。

## 升级与兼容

- 安装器 AppId 保持 `{E99D04D6-6F40-4C26-BB79-3D94C66D846C}`，支持覆盖 V1.2。
- 首次启动在 Online Backup 后执行 migration 013；最多保留 3 个迁移前备份。
- V1.2 的项目、会话、消息、设置和自动化保留；项目和会话进入“我的空间”。
- C ABI 版本为 `0x00010300`，V1.2 导出保持不变。

## 构建

Windows x64 双击 `build-installer.bat`。成功产物为：

```text
artifacts\installer\WorkPilot-Hybrid-V1.3-win-x64-Setup.exe
```

正式分发前请完成代码签名和 `docs/VALIDATION.md` 所列 Windows 手工门禁。
