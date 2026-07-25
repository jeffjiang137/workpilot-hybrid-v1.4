# WorkPilot Hybrid V1.3 验证记录

验证日期：2026-07-20。

## 当前 Linux 交付环境已执行

- 用真实 SQLite 建立 V1.2 fixture（项目、会话、消息、设置），执行内置 migration 013：建立 44 个 schema 对象，旧数据保留。
- 迁移后 `PRAGMA foreign_key_check` 无结果，`PRAGMA integrity_check` 返回 `ok`。
- 全部 XAML、csproj、vcxproj 和 manifest 通过解析。
- 可移植权限策略使用 C++20、`-Wall -Wextra -Werror` 编译并运行。
- 静态检查没有 TODO/FIXME/NotImplementedException、空 catch 或超过 400 行的 C#/C++ 文件。
- 校验安装器 AppId 未改变，应用/文件/程序集版本和产物名均为 1.3.0/V1.3。

## Windows 一键构建会执行

1. MSVC v143 `/W4 /WX` 构建 C++ DLL 和 Native 测试。
2. Native 测试覆盖权限、原子写入、路径穿越、reparse/symlink、分页、取消和快速指纹。
3. .NET 领域测试覆盖 SSE、CJK token、FTS 查询转义、分块上限/重叠和任务状态机。
4. 集成测试从真实 V1.2 schema 迁移两次，验证幂等与旧数据。
5. 发布 self-contained x64 WinUI 3，复制 `workpilot_core.dll`，再生成 Inno Setup 安装器。

精确命令：

```powershell
.\scripts\build-installer.ps1
```

## 本环境不能声明通过的项目

当前容器没有 .NET SDK、Visual Studio/Windows SDK、WinUI Runtime 和 Inno Setup，因此未在这里生成或运行 Windows EXE/MSIX/Inno 安装包，也未执行 Windows 10/11 GUI、拖拽、DPI、屏幕阅读器、Watcher overflow、junction 和 10k/100k 性能测试。

这些不是被伪报为“已通过”的项目。交付包已提供 Windows 本地一键脚本和 `windows-2022` CI；首次正式分发前必须在 Windows 机器运行该脚本，并按 `docs/v1.3-ai-ready/08_QA_ACCEPTANCE_MATRIX.md` 完成 P0/P1 手工发布门禁。
