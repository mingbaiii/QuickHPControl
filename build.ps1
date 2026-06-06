# ================================================================
#  HP System Control — 一键编译脚本
#  编译顺序: BootstrapNative → PayloadDLL → QuickHPControl → 打包安装程序
#  运行方式: powershell -ExecutionPolicy Bypass -File build.ps1
#           或右键 → "使用 PowerShell 运行"
# ================================================================

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $ScriptDir

# --- 路径配置 ---
$MSBuild      = "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe"

$BootstrapDir = Join-Path $ScriptDir "BootstrapNative"
$BootstrapProj = Join-Path $BootstrapDir "BootstrapNative.vcxproj"
$BootstrapOut  = Join-Path $BootstrapDir "x64\Release\BootstrapNative.dll"

$PayloadDir   = Join-Path $ScriptDir "PayloadDLL"
$PayloadProj  = Join-Path $PayloadDir "PayloadDLL.csproj"
$PayloadOut   = Join-Path $PayloadDir "bin\Release\net481\PayloadDLL.dll"

$QuickHPControlProj      = Join-Path $ScriptDir "QuickHPControl.csproj"
$OutDir       = Join-Path $ScriptDir "bin\Release\net481"
$QuickHPControlOut       = Join-Path $OutDir "QuickHPControl.exe"

$ISCC         = "C:\Program Files\Inno Setup 7\ISCC.exe"
$InstallerIss = Join-Path $ScriptDir "installer.iss"

# 需要复制到输出目录的 HP 库 DLL
$LibsDir      = Join-Path $ScriptDir "QuickHPControl\libs"
$HpLibs       = @(
    "HP.SystemControl.BiosWmi.dll",
    "HP.SystemControl.Utility.dll",
    "HP.SystemControl.Utility.Framework.dll",
    "HP.SystemControl.AppData.dll"
)

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

    if (-not (Test-Path $MSBuild)) {
        Write-Host "  错误: 找不到 MSBuild: $MSBuild" -ForegroundColor Red
        Write-Host "  请确认 Visual Studio 18 Community 已安装" -ForegroundColor Red
        $valid = $false
    }

    if (-not (Test-Path $BootstrapProj)) {
        Write-Host "  错误: 找不到 BootstrapNative 项目: $BootstrapProj" -ForegroundColor Red
        $valid = $false
    }

    if (-not (Test-Path $PayloadProj)) {
        Write-Host "  错误: 找不到 PayloadDLL 项目: $PayloadProj" -ForegroundColor Red
        $valid = $false
    }

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
#  1. BootstrapNative (C++ x64 Release, MSBuild)
# ================================================================
Write-Section "1/4 编译 BootstrapNative (C++ x64 Release)"

$Timer = [System.Diagnostics.Stopwatch]::StartNew()

# 清理旧输出
$BootstrapOutDir = Join-Path $BootstrapDir "x64\Release"
if (Test-Path $BootstrapOutDir) {
    Remove-Item (Join-Path $BootstrapOutDir "*") -Force -ErrorAction SilentlyContinue
}

$BootstrapOk = $false
try {
    & $MSBuild $BootstrapProj /p:Configuration=Release /p:Platform=x64 /v:minimal /nologo 2>&1 | ForEach-Object {
        if ($_ -match "error ") {
            Write-Host "  $_" -ForegroundColor Red
        }
    }
    $BootstrapOk = $LASTEXITCODE -eq 0 -and (Test-Path $BootstrapOut)
} catch {
    Write-Host "  异常: $_" -ForegroundColor Red
}
$Timer.Stop()
Write-Result "BootstrapNative" $BootstrapOk $Timer.Elapsed
if (-not $BootstrapOk) { $AllOk = $false }


# ================================================================
#  2. PayloadDLL (.NET 4.8.1 Release, dotnet build)
# ================================================================
Write-Section "2/4 编译 PayloadDLL (.NET 4.8.1 Release)"

$Timer = [System.Diagnostics.Stopwatch]::StartNew()

# 清理旧输出
$PayloadOutDir = Join-Path $PayloadDir "bin\Release"
if (Test-Path $PayloadOutDir) {
    Remove-Item $PayloadOutDir -Recurse -Force -ErrorAction SilentlyContinue
}

$PayloadOk = $false
try {
    & dotnet build $PayloadProj -c Release 2>&1 | ForEach-Object {
        if ($_ -match "error ") {
            Write-Host "  $_" -ForegroundColor Red
        }
    }
    $PayloadOk = $LASTEXITCODE -eq 0 -and (Test-Path $PayloadOut)
} catch {
    Write-Host "  异常: $_" -ForegroundColor Red
}
$Timer.Stop()
Write-Result "PayloadDLL" $PayloadOk $Timer.Elapsed
if (-not $PayloadOk) { $AllOk = $false }


# ================================================================
#  3. QuickHPControl (WPF .NET 4.8.1 Release, dotnet build)
#     → 构建完成后复制 BootstrapNative.dll + PayloadDLL.dll + HP libs
# ================================================================
Write-Section "3/4 编译 QuickHPControl (WPF .NET 4.8.1 Release)"

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

if ($QuickHPControlBuildOk) {
    Write-Host "  复制依赖文件到输出目录..." -ForegroundColor Gray

    # 复制 BootstrapNative.dll
    if (Test-Path $BootstrapOut) {
        Copy-Item $BootstrapOut $OutDir -Force
        Write-Host "    [OK] BootstrapNative.dll" -ForegroundColor Gray
    } else {
        Write-Host "    [SKIP] BootstrapNative.dll (源文件不存在)" -ForegroundColor Yellow
    }

    # 复制 PayloadDLL.dll
    if (Test-Path $PayloadOut) {
        Copy-Item $PayloadOut $OutDir -Force
        Write-Host "    [OK] PayloadDLL.dll" -ForegroundColor Gray
    } else {
        Write-Host "    [SKIP] PayloadDLL.dll (源文件不存在)" -ForegroundColor Yellow
    }

    # 复制 HP 库 DLL
    foreach ($lib in $HpLibs) {
        $src = Join-Path $LibsDir $lib
        if (Test-Path $src) {
            Copy-Item $src $OutDir -Force
            Write-Host "    [OK] $lib" -ForegroundColor Gray
        } else {
            Write-Host "    [SKIP] $lib (源文件不存在)" -ForegroundColor Yellow
        }
    }
}

$Timer.Stop()
Write-Result "QuickHPControl" $QuickHPControlBuildOk $Timer.Elapsed
if (-not $QuickHPControlBuildOk) { $AllOk = $false }


# ================================================================
#  4. 打包安装程序 (Inno Setup 7, 仅当编译全部成功时)
# ================================================================
Write-Section "4/4 打包安装程序 (Inno Setup 7)"

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
