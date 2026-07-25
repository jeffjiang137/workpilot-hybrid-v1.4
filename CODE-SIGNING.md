# WorkPilot 安装包代码签名（Authenticode）配置指南

当前 `windows-release.yml` 产出的 `WorkPilot-Hybrid-V1.4-win-x64-Setup.exe` **未签名**。
Windows SmartScreen 会在首次运行/安装时弹出“未知发布者”警告。用受信任 CA 颁发的代码签名证书做
Authenticode 签名可消除该警告（EV 证书还能更快建立 SmartScreen 信誉）。

> 本仓库的签名步骤在 `.github/workflows/windows-release.yml` 中是**注释占位**，不配置 secret 也能正常出未签名安装包。

---

## 前置条件

1. 一张代码签名证书（`.pfx`）。
   - 建议由受信任 CA 颁发：DigiCert、Sectigo/Comodo、GlobalSign 等。
   - ⚠️ 自签名证书**无法**消除 SmartScreen 警告，仅用于本地测试。
2. 该证书的密码。

---

## 步骤一：把 .pfx 转成 base64

在 **Windows** 上打开 PowerShell：

```powershell
# 方式 A：certutil（生成带头尾标记的 base64 文本）
certutil -encode C:\certs\workpilot.pfx C:\certs\workpilot.base64.txt
```

打开 `C:\certs\workpilot.base64.txt`，复制 `-----BEGIN CERTIFICATE-----` 与
`-----END CERTIFICATE-----` **之间的全部内容**（不含这两行），即为要填的 secret 值。

```powershell
# 方式 B：纯 PowerShell 一行输出（不含头尾标记，直接可用）
[Convert]::ToBase64String([IO.File]::ReadAllBytes("C:\certs\workpilot.pfx"))
```

---

## 步骤二：在 GitHub 添加 Secrets

1. 打开仓库 `Settings → Secrets and variables → Actions → New repository secret`。
2. 依次添加：
   - **Name**: `SIGN_PFX`  **Value**: 上面的 base64 字符串
   - **Name**: `SIGN_PASSWORD`  **Value**: `.pfx` 的密码
3. 保存。

---

## 步骤三：取消工作流里的签名注释

编辑 `.github/workflows/windows-release.yml`，把 `Sign installer (Authenticode)` 整段的注释去掉
（删除每行开头的 `#` 及紧跟的空格），变成：

```yaml
      - name: Sign installer (Authenticode)
        shell: powershell
        env:
          SIGN_PFX: ${{ secrets.SIGN_PFX }}
          SIGN_PASSWORD: ${{ secrets.SIGN_PASSWORD }}
        run: |
          [IO.File]::WriteAllBytes("cert.pfx", [Convert]::FromBase64String($env:SIGN_PFX))
          $signtool = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin" -Recurse -Filter signtool.exe |
                        Sort-Object FullName -Descending | Select-Object -First 1
          & $signtool.FullName sign /fd SHA256 /f cert.pfx /p "$env:SIGN_PASSWORD" artifacts/installer/*.exe
```

> 注意：签名步骤**没有** `continue-on-error`，证书或密码错误会导致构建失败。请确保 base64 与密码正确。

---

## 步骤四：重新出包

```powershell
cd D:\Xworkc++\workpilot-hybrid-v1.4
git add .github/workflows/windows-release.yml
git commit -m "Enable Authenticode signing in release pipeline"
git push origin main
# 或直接打新 tag 触发自动构建 + Release
git tag v1.5.1
git push origin v1.5.1
```

也可在仓库 **Actions** 标签页手动 `Run workflow`（可勾 `Skip Installer` 只出应用）。

---

## 步骤五：验证签名

下载安装包后在 Windows 上：

```powershell
Get-AuthenticodeSignature "WorkPilot-Hybrid-V1.4-win-x64-Setup.exe"
```

`Status` 应为 `Valid`；或右键文件 → 属性 → 数字签名，能看到你的证书。

---

## 备注

- 未配置签名时，安装包照常产出，仅缺签名（SmartScreen 警告）。
- SBOM（CycloneDX）为 best-effort 占位，生成失败不影响出包。
