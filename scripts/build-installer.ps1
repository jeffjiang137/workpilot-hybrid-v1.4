[CmdletBinding()]
param(
    [switch]$InstallPrerequisites,
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root "artifacts"

function Find-MSBuild {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path $vswhere)) { return $null }
    $path = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
    return $path
}

function Install-Package([string]$id, [string[]]$arguments = @()) {
    if (-not (Get-Command winget.exe -ErrorAction SilentlyContinue)) {
        throw "Missing prerequisite '$id' and winget is unavailable. Install it manually; see docs\BUILD_WINDOWS.md."
    }
    Write-Host "Installing prerequisite: $id" -ForegroundColor Yellow
    & winget.exe install --id $id --exact --accept-package-agreements --accept-source-agreements @arguments
    if ($LASTEXITCODE -ne 0) { throw "Failed to install prerequisite: $id" }
}

if (-not (Get-Command dotnet.exe -ErrorAction SilentlyContinue)) {
    if (-not $InstallPrerequisites) { throw ".NET 8 SDK is required." }
    Install-Package "Microsoft.DotNet.SDK.8"
}

$msbuild = Find-MSBuild
if (-not $msbuild) {
    if (-not $InstallPrerequisites) { throw "Visual Studio 2022 C++ Build Tools are required." }
    Install-Package -id "Microsoft.VisualStudio.2022.BuildTools" -arguments @(
        "--override", "--wait --passive --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"
    )
    $msbuild = Find-MSBuild
    if (-not $msbuild) { throw "Build Tools installed but MSBuild was not found. Restart Windows, then rerun this script." }
}

$isccCandidates = @(
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $SkipInstaller -and -not $iscc) {
    if (-not $InstallPrerequisites) { throw "Inno Setup 6 is required." }
    Install-Package "JRSoftware.InnoSetup"
    $iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { throw "Inno Setup installed but ISCC.exe was not found." }
}

New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
Write-Host "Building C++ core and tests..." -ForegroundColor Cyan
& $msbuild (Join-Path $root "src\WorkPilot.Core.Native\WorkPilot.Core.Native.vcxproj") /p:Configuration=Release /p:Platform=x64 /m /v:minimal
if ($LASTEXITCODE -ne 0) { throw "Native build failed." }
& $msbuild (Join-Path $root "src\WorkPilot.Core.Native\tests\WorkPilot.Core.Tests.vcxproj") /p:Configuration=Release /p:Platform=x64 /m /v:minimal
if ($LASTEXITCODE -ne 0) { throw "Native test build failed." }

$testExe = Join-Path $artifacts "tests\Release\x64\workpilot_core_tests.exe"
& $testExe
if ($LASTEXITCODE -ne 0) { throw "Native tests failed." }

Write-Host "Running managed protocol tests..." -ForegroundColor Cyan
& dotnet.exe build (Join-Path $root "tests\WorkPilot.ServiceCompile.Tests\WorkPilot.ServiceCompile.Tests.csproj") -c Release
if ($LASTEXITCODE -ne 0) { throw "Managed service compile gate failed." }
& dotnet.exe run --project (Join-Path $root "tests\WorkPilot.Logic.Tests\WorkPilot.Logic.Tests.csproj") -c Release
if ($LASTEXITCODE -ne 0) { throw "Managed protocol tests failed." }
& dotnet.exe run --project (Join-Path $root "tests\WorkPilot.Integration.Tests\WorkPilot.Integration.Tests.csproj") -c Release
if ($LASTEXITCODE -ne 0) { throw "Migration integration tests failed." }

Write-Host "Restoring and publishing WinUI 3 application..." -ForegroundColor Cyan
& dotnet.exe restore (Join-Path $root "src\WorkPilot.App\WorkPilot.App.csproj") -r win-x64 -p:Platform=x64
if ($LASTEXITCODE -ne 0) { throw "NuGet restore failed." }
$publish = Join-Path $artifacts "publish"
& dotnet.exe publish (Join-Path $root "src\WorkPilot.App\WorkPilot.App.csproj") -c Release -r win-x64 --self-contained true -p:Platform=x64 -o $publish --no-restore
if ($LASTEXITCODE -ne 0) { throw "WinUI publish failed." }
if (-not (Test-Path (Join-Path $publish "workpilot_core.dll"))) { throw "Published application is missing workpilot_core.dll." }

if (-not $SkipInstaller) {
    Write-Host "Creating installer..." -ForegroundColor Cyan
    & $iscc (Join-Path $root "installer\WorkPilot.iss")
    if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed." }
    $installer = Join-Path $artifacts "installer\WorkPilot-Hybrid-V1.4-win-x64-Setup.exe"
    if (-not (Test-Path $installer)) { throw "Installer compiler completed but the expected setup executable is missing." }
}

if ($SkipInstaller) {
    Write-Host "Build completed successfully. Published application: artifacts\publish" -ForegroundColor Green
} else {
    Write-Host "Build completed successfully. Installer: artifacts\installer\WorkPilot-Hybrid-V1.4-win-x64-Setup.exe" -ForegroundColor Green
}
