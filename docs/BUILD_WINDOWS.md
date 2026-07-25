# Windows 构建与打包

## 最省事的方式

在 Windows 10 1809+ 或 Windows 11 x64 解压整个源码包，双击仓库根目录 `build-installer.bat`。不要只复制 `src` 目录。

脚本会检查并在用户允许时通过 winget 安装：.NET 8 SDK、Visual Studio 2022 Build Tools（MSVC v143 + Windows SDK）和 Inno Setup 6。首次构建需要访问 NuGet。

完成后的安装包：

```text
artifacts\installer\WorkPilot-Hybrid-V1.4-win-x64-Setup.exe
```

`artifacts\publish` 是可携带版目录，必须整体复制，不能只复制 EXE。

## PowerShell 命令

```powershell
.\scripts\build-installer.ps1 -InstallPrerequisites
```

只构建/测试/发布，不生成安装器：

```powershell
.\scripts\build-installer.ps1 -SkipInstaller
```

构建顺序固定为：Native DLL → Native 安全测试 → 托管服务编译门禁 → V1.4 领域/协议测试 → V1.2→V1.4 迁移测试 → WinUI self-contained publish → Native DLL 存在性检查 → Inno Setup。

## 覆盖升级验证

1. 安装 V1.2，创建项目、会话、消息和自动化。
2. 关闭旧版本，再运行 V1.4 安装器；AppId 不变，会执行覆盖升级。
3. 启动 V1.4，确认“我的空间”包含旧项目和会话。
4. 确认 `%LOCALAPPDATA%\WorkPilot` 下存在迁移前备份，旧消息和自动化可读。
5. 新建空间、任务，给项目建立索引并搜索一个中英文关键词。
6. 创建专家、导入一个测试技能，连接一个测试 MCP 服务并检查安全中心审计。

## 常见问题

- 找不到 MSBuild/v143：在 Visual Studio Installer 安装“使用 C++ 的桌面开发”、MSVC v143 和 Windows SDK。
- NuGet 失败：检查代理、企业证书和 nuget.org；不需要删除用户数据库。
- 找不到 `workpilot_core.dll`：使用安装器或完整复制 `artifacts\publish`。
- 启动显示迁移失败：不要删除数据库；保留 `workpilot.pre-v14.*.db`、`.failed-v13` 和 `workpilot.pre-v13.*.db` 后恢复最近备份。
- SmartScreen：测试包未签名。生产分发前使用 Authenticode 签名 Native DLL、应用 EXE 和最终安装器。
