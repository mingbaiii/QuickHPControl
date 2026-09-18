# QuickHPControl

一款轻量级的 HP 笔记本性能模式切换工具。**直连 BIOS 读写热控模式**，只需管理员权限即可自由切换性能模式。**可替代 HP 官方 App 内的性能调节功能**，无需打开 HP 软件，直接从系统托盘一键快速切换。

当前版本：**2.0**

## 功能特性

- **多模式切换**：支持 7 种性能模式（根据 BIOS 支持情况自动刷新）
  - 🚀 Performance（高性能）- 释放全部性能
  - ⚖️ Balanced（平衡）- 标准平衡模式
  - ❄️ Cool（凉爽）- 降低温度
  - 🔇 Quiet（安静）- 减少噪音
  - 🔋 PowerSaver（省电）- 延长续航
  - 🧠 SmartSense（智能）- 自动平衡性能与功耗
  - 🔇 Silent（静音）- 最低噪音

- **Windows 节能模式联动**：切换到省电模式时自动打开 Windows 11 的「节能模式」开关，
  切换到性能模式自动关闭。
- **Windows 节能模式独立开关**：主界面「选择性能模式」下方与托盘右键菜单都可单独控制，
  不依赖 BIOS 连接；使用 WNF 广播，直接对应 Windows 设置里的开关。
- **防 HP 服务抢模式**：切换平衡系列前先写入 HP ApplicationData 的用户选择与
  `triggerSource=UserAction`，避免最冷/平衡/安静/静音被 HP 后台服务改回智能模式。
- **风扇控制**（BIOS 支持时显示）：最大转速开关 + 转速级别调节（未测试）
- **系统托盘常驻**：最小化后隐藏到托盘，不占用任务栏
- **开机自启**：支持设置开机自动启动
- **实时监控**：每 2 秒轮询 BIOS 真实模式，跟随外部改动

## 技术实现

### 通道

直接通过 WMI 与 BIOS 对话：

```
QuickHPControl (WPF 主程序)
    ↓ System.Management
root\WMI → hpqBIntM 实例（ACPI PNP0C14）
    ↓ 调用 hpqBIOSInt<N> 方法
BIOS 热控模式寄存器
```

`hpqBIntM` 提供一组 `hpqBIOSInt<N>` 方法，`N` 是返回缓冲区大小，合法值只有
`{0, 4, 128, 1024, 4096, 8192, 16384, 32768}`。

入参是 `hpqBDataIn` 模板类：`Command`（1=读 / 2=写）、`CommandType`、`hpqBData`、`Sign`、`Size`。
其中 `Sign` 必须恰为 4 字节 `53 45 43 55`（ASCII `SECU`），否则 `rwReturnCode=3`。
`hpqBDataIn` 带 `@key`/`@read` 属性，只能用 `ManagementClass.CreateInstance()` 构造。

### 关键参数

| 用途 | 方法 | Command | CommandType | Size | 数据 | 结果 |
|------|------|---------|-------------|------|------|------|
| 读模式 | `hpqBIOSInt4` | 1 | 76 | 0 | — | `Data[0]` = 模式值 |
| 写模式 | `hpqBIOSInt0` | 2 | 76 | 4 | `{mode,0,0,0}` | `rwReturnCode=0` 即成功 |
| 能力块 | `hpqBIOSInt128` | 1 | 13 | 0 | — | `Data[6]`=支持掩码，`Data[8]&1`=版本位 |

模式值 ↔ 能力位掩码的映射（HP 官方 `modeOrderMap`）：

```
bit 1→5(SmartSense)  bit 2→1(Balanced)  bit 3→0(Performance)
bit 4→2(Cool)        bit 5→3(Quiet)     bit 6→4(PowerSaver)  bit 7→6(Silent)
```

### 同步更新 Windows 电源模式滑块

HP 的后台服务会双向同步「Windows 电源模式滑块」和「BIOS 热控模式」。如果只改 BIOS
不动滑块，HP 服务检测到两者不一致时可能把模式改回去。所以 `SetMode()` 会按以下顺序
执行：

1. 写入 HP 自己的 ApplicationData（平衡系列的 `SelectedBalancedSerices`，以及
   `EffectivePowerModeAppData.triggerSource=UserAction`）；
2. 写 BIOS 热控模式；
3. 把滑块切到对应档位（`powrprof.dll!PowerSetUserConfigured{AC,DC}PowerMode`）；
4. 仅在模式真正变化时同步 Windows 节能模式。

第 1 步是防止「切到最冷/平衡/安静/静音后跳回智能模式」的关键：HP 的
`HP.SystemControl.Background.exe` 在观察到电源模式变为 Balanced 且 `triggerSource=Other(-1)`
时，会把 `SelectedBalancedSerices` 覆写为默认的 SmartSense，再写回 BIOS。新版通过
`HpSystemControlBridge` 写入同一份 UWP `ApplicationData.LocalSettings` hive，并先于 BIOS
写入 `UserAction`，不给 HP 服务读取中间状态的机会。

HP hive 的实际位置为：

```text
%LOCALAPPDATA%\Packages\AD2F1837.myHP_v10z8vjag6ke6\Settings\settings.dat
```

对应键为 `LocalState\\SystemControlAppDataHelper\\StorageInfo` 与
`LocalState\\EffectivePowerModeAppDataHelper\\StorageInfo`，值是 UWP 专用类型
`0x05F5E10C` 的 UTF-16LE JSON + FILETIME。相关实现见 `QuickHPControl/HpSystemControlBridge.cs`。

对应滑块档位为：

- Performance(0) → 最佳性能
- PowerSaver(4) → 最佳能效
- 其余 → 平衡

注意这是**设置 → 系统 → 电源和电池 → 电源模式**的三档滑块，不是 `powercfg` 的电源计划。

### 风扇控制

风扇指令走同一个 `hpqBIntM` 通道，用 `Command = 0x20008`（`Cmd.Default`）配不同的 `CommandType`：

| 用途 | CommandType | 方向 | 数据 / 结果 |
|------|-------------|------|-------------|
| 风扇数量 | `0x10` | 读 | `Data[0]` = 1~8 |
| 风扇类型 | `0x2C` | 读 | 每半字节一个：1=CPU 2=GPU 3=排气 4=水泵 5=进气 |
| 最大转速 | `0x26` / `0x27` | 读 / 写 | `{on,0,0,0}` |
| 转速级别 | `0x2D` / `0x2E` | 读 / 写 | `{fan1,fan2,0,0}` |
| 风扇模式 | `0x1A` | 写 | `{0xFF, mode, 0, 0}` |

程序在连接时会**运行时探测**本机是否支持风扇指令（两种 `Command` 编码各试一次，
以「能读出 1~8 之间的风扇数量」为判定依据），探不到就整块隐藏 UI。

两点必须说清楚：

- BIOS 只暴露**转速级别档位值**（观测范围 `0x00` ~ `0x37`），**不是 RPM**。真实的转速/百分比
  走 EC（嵌入式控制器），需要内核驱动，本程序不碰。
- 部分机型需要先经 EC 打开「手动风扇」（`PlatformData.FanManual`）转速级别才真正生效。
  这种情况下 BIOS 会返回 `rwReturnCode = 0` 但风扇可能没有变化 —— UI 上已标注这一点。
  相比之下「最大转速」开关是可靠的。

### Windows 节能模式联动

Windows 11 的「节能模式」（设置 → 系统 → 电源和电池 → 节能模式）本体是注册表值：

```
HKLM\SYSTEM\CurrentControlSet\Control\Power\EnergySaverState   REG_DWORD
    1 = 开      2 = 关
```

写入需要管理员权限（程序本就以管理员运行）。它与电源模式滑块、`powercfg` 电源计划**都不是一回事**：

- `PowerGetEffectiveOverlayScheme` 返回的 overlay scheme 是**电源模式滑块**，不是这个开关；
- `SUB_ENERGYSAVER` 子组下的 `ES POLICY` / `ESBRIGHTNESS` / `ESBATTTHRESHOLD` 是开关**生效后**的
  子设置，改它们不会改变开关本身。

`SetMode()` 在写 BIOS **之前**先读一次旧模式，只有 `previous != mode` 时才调用一次
`SyncEnergySaver()`。所以：

- 切到省电模式(4) → 打开节能模式
- 切到其他模式 → 关闭节能模式
- 模式没变（比如点了当前已选中的按钮）→ 完全不动
- 用户切换完之后自己在设置里改回来 → 程序不会把它改回去（轮询只读不写）

### 核心组件

| 文件 | 说明 |
|------|------|
| `QuickHPControl/BiosWmiClient.cs` | BIOS WMI 通道封装：连接、读/写模式、能力枚举、风扇指令、节能模式联动 |
| `QuickHPControl/PowerPlanManager.cs` | Windows 电源模式滑块读写与电池状态检测 |
| `QuickHPControl/EnergySaverManager.cs` | Windows 11 节能模式开关读写（WNF + `EnergySaverState`） |
| `QuickHPControl/HpSystemControlBridge.cs` | HP ApplicationData 兼容层：保存用户模式与 `triggerSource=UserAction` |
| `QuickHPControl/MainWindow.cs` | WPF 主界面、模式按钮、独立节能开关、风扇区块、轮询、托盘联动 |
| `QuickHPControl/App.cs` | 托盘图标、单实例、开机自启 |

## 系统要求

- Windows 10/11
- HP 笔记本电脑（BIOS 需暴露 `hpqBIntM` 接口）
- .NET Framework 4.8.1
- **必须管理员权限**（枚举 `hpqBIntM` 实例需要 High 完整性级别，`app.manifest` 已声明）

## 使用方法

1. 从 Release 下载最新版本，运行安装程序
2. 启动 `QuickHPControl.exe`（会请求管理员权限）
3. 在主界面或系统托盘图标右键选择性能模式

## 构建

### 前置条件

- .NET SDK（用于 `dotnet build`）
- Visual Studio（可选，也可直接双击 `.csproj` 在 VS 中打开）
- Inno Setup 7（仅打包安装程序需要，路径 `C:\Program Files\Inno Setup 7\ISCC.exe`）

### 编译

```powershell
# 编译 + 打包安装程序
.\build.ps1
```

也可以直接在 Visual Studio 中打开 `QuickHPControl.csproj` 编译。

## 致谢

- UI 框架：[iNKORE.UI.WPF.Modern](https://github.com/iNKORE-NET/UI.WPF.Modern)
- 风扇指令码与热控参数逆向参考：[OmenMon](https://github.com/OmenMon/OmenMon)（GPL-3.0）的
  `Hardware/BiosCtl.cs`、`BiosData.cs`、`PlatformData.cs`

## 免责声明

本工具仅供学习交流使用。使用本工具可能会影响电脑的性能和散热，请自行承担风险。作者不对因使用本工具造成的任何损失负责。
