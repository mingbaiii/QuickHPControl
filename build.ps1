# ================================================================
#  QuickHPControl — 一键编译脚本
#  编译顺序: QuickHPControl → 打包安装程序
#
#  技术方案：直连 root\WMI\hpqBIntM 读写 BIOS 热控模式。
#  不再需要 C++ 注入器、PayloadDLL，也不再需要 HP 的任何 DLL。
#
#  运行方式: powershell -ExecutionPolicy Bypass -File build.ps1
#           或右键 → "使用 PowerShell 运行"
# ================================================================

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $ScriptDir

# --- 路径配置 ---
$QuickHPControlProj = Join-Path $ScriptDir "QuickHPControl.csproj"
$OutDir             = Join-Path $ScriptDir "bin\Release\net481"
$QuickHPControlOut  = Join-Path $OutDir "QuickHPControl.exe"

$ISCC         = "C:\Program Files\Inno Setup 7\ISCC.exe"
$InstallerIss = Join-Path $ScriptDir "installer.iss"

# --- 工具函数 ---
function Write-Section($title) {
    Write-Host ""
    Write-Host "============================================================" -ForegroundColor Cyan
    Write-Host "  $title" -ForegroundColor Cyan
    Write-Host "============================================================" -ForegroundColor Cyan
}

function Write-Result($name, $ok, $elapsed) {
    $icon = if ($ok) { "[OK]" } else { "[FAIL]" }
    $color = if ($ok) { "Green" } else { "Red" }
    Write-Host "  $icon $name  [$([math]::Round($elapsed.TotalSeconds,1))s]" -ForegroundColor $color
}

function Test-Prerequisites {
    $valid = $true

    if (-not (Test-Path $QuickHPControlProj)) {
        Write-Host "  错误: 找不到 QuickHPControl 项目: $QuickHPControlProj" -ForegroundColor Red
        $valid = $false
    }

    return $valid
}

# ================================================================
#  前置检查
# ================================================================
Write-Section "环境检查"

if (-not (Test-Prerequisites)) {
    Write-Host ""
    Write-Host "前置条件不满足，无法继续编译" -ForegroundColor Red
    exit 1
}
Write-Host "  环境检查通过" -ForegroundColor Green

$TotalTimer = [System.Diagnostics.Stopwatch]::StartNew()
$AllOk = $true

# ================================================================
#  1. QuickHPControl (WPF .NET 4.8.1 Release, dotnet build)
# ================================================================
Write-Section "1/2 编译 QuickHPControl (WPF .NET 4.8.1 Release)"

$Timer = [System.Diagnostics.Stopwatch]::StartNew()

# 清理旧输出
if (Test-Path $OutDir) {
    Remove-Item $OutDir -Recurse -Force -ErrorAction SilentlyContinue
}

$QuickHPControlBuildOk = $false
try {
    & dotnet build $QuickHPControlProj -c Release 2>&1 | ForEach-Object {
        if ($_ -match "error ") {
            Write-Host "  $_" -ForegroundColor Red
        }
    }
    $QuickHPControlBuildOk = $LASTEXITCODE -eq 0 -and (Test-Path $QuickHPControlOut)
} catch {
    Write-Host "  异常: $_" -ForegroundColor Red
}

$Timer.Stop()
Write-Result "QuickHPControl" $QuickHPControlBuildOk $Timer.Elapsed
if (-not $QuickHPControlBuildOk) { $AllOk = $false }


# ================================================================
#  2. 打包安装程序 (Inno Setup 7, 仅当编译全部成功时)
# ================================================================
Write-Section "2/2 打包安装程序 (Inno Setup 7)"

$InstallerOk = $false

if (-not $AllOk) {
    Write-Host "  编译未全部成功，跳过安装程序打包" -ForegroundColor Yellow
} elseif (-not (Test-Path $ISCC)) {
    Write-Host "  未找到 Inno Setup 7: $ISCC，跳过安装程序打包" -ForegroundColor Yellow
} elseif (-not (Test-Path $InstallerIss)) {
    Write-Host "  未找到安装脚本: $InstallerIss，跳过打包" -ForegroundColor Yellow
} else {
    $Timer = [System.Diagnostics.Stopwatch]::StartNew()

    # 确保 Installer 输出目录存在
    $InstallerOutDir = Join-Path $ScriptDir "Installer"
    if (-not (Test-Path $InstallerOutDir)) {
        New-Item -ItemType Directory -Path $InstallerOutDir -Force | Out-Null
    }

    try {
        & $ISCC $InstallerIss 2>&1 | ForEach-Object {
            Write-Host "  $_" -ForegroundColor Gray
        }
        $InstallerOk = $LASTEXITCODE -eq 0
    } catch {
        Write-Host "  异常: $_" -ForegroundColor Red
    }

    $Timer.Stop()
    Write-Result "打包安装程序" $InstallerOk $Timer.Elapsed
    if (-not $InstallerOk) { $AllOk = $false }
}


# ================================================================
#  汇总
# ================================================================
$TotalTimer.Stop()

Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
if ($AllOk) {
    Write-Host "  编译完成 - 全部成功!" -ForegroundColor Green
} else {
    Write-Host "  编译完成 - 有项目失败，请查看上方日志" -ForegroundColor Yellow
}
Write-Host "  总耗时: $([math]::Round($TotalTimer.Elapsed.TotalSeconds,1))s" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan

# 列出输出目录文件
Write-Host ""
Write-Host "输出目录: $OutDir" -ForegroundColor White
if (Test-Path $OutDir) {
    $files = Get-ChildItem $OutDir -File -Name | Sort-Object
    foreach ($f in $files) {
        $path = Join-Path $OutDir $f
        $size = (Get-Item $path).Length
        if ($size -gt 1MB) {
            $sizeStr = "$([math]::Round($size/1MB, 1)) MB"
        } elseif ($size -gt 1KB) {
            $sizeStr = "$([math]::Round($size/1KB, 1)) KB"
        } else {
            $sizeStr = "$size B"
        }
        Write-Host "    $f  ($sizeStr)" -ForegroundColor Gray
    }
}

# 返回码
if (-not $AllOk) { exit 1 }
