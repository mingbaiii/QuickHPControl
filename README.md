# QuickHPControl

一款轻量级的 HP 笔记本性能模式切换工具，通过注入 DLL 实现对 HP 系统控制进程的操控，让你自由切换各种性能模式。**可完全替代 HP 官方 App 内的性能调节功能**，无需打开臃肿的 HP 软件，直接从系统托盘一键快速切换。

## 功能特性

- **多模式切换**：支持 7 种性能模式（根据BIOS支持自动刷新）
  - 🚀 Performance（高性能）- 释放全部性能
  - ⚖️ Balanced（平衡）- 标准平衡模式
  - ❄️ Cool（凉爽）- 降低温度
  - 🔇 Quiet（安静）- 减少噪音
  - 🔋 PowerSaver（省电）- 延长续航
  - 🧠 SmartSense（智能）- 自动平衡性能与功耗
  - 🔇 Silent（静音）- 最低噪音

- **系统托盘常驻**：最小化后隐藏到托盘，不占用任务栏
- **开机自启**：支持设置开机自动启动
- **实时监控**：实时显示当前性能模式状态

## 技术实现

本项目采用 DLL 注入方式与 HP 系统控制组件交互：

```
QuickHPControl (WPF 主程序)
    ↓ 注入
BootstrapNative.dll (C++ 桥接层)
    ↓ 加载
PayloadDLL (.NET DLL)
    ↓ 调用
HP.SystemControl.Utility (HP 官方库)
```

### 核心组件

| 组件 | 说明 |
|------|------|
| `QuickHPControl` | WPF 主程序，提供 UI 和托盘功能 |
| `BootstrapNative` | C++ 原生 DLL，负责注入和 CLR 加载 |
| `PayloadDLL` | .NET DLL，实际调用 HP API 切换模式 |

## 系统要求

- Windows 10/11
- HP 笔记本电脑（需要 HP System Control 支持）
- .NET Framework 4.7.2+ 或 .NET 6+

## 使用方法

1. 从 Release 下载最新版本
2. 运行 `QuickHPControl.exe`
3. 在系统托盘找到图标，右键选择性能模式

## 构建

### 前置条件

- Visual Studio 2022
- .NET Desktop Development 工作负载
- C++ Desktop Development 工作负载（用于 BootstrapNative）

### 编译

```bash
# 使用 PowerShell 构建脚本
.\build.ps1
```

或在 Visual Studio 中打开 `QuickHPControl.sln` 编译。。

## 致谢

- UI 框架：[iNKORE.UI.WPF.Modern](https://github.com/iNKORE-NET/UI.WPF.Modern)

## 免责声明

本工具仅供学习交流使用。使用本工具可能会影响电脑的性能和散热，请自行承担风险。作者不对因使用本工具造成的任何损失负责。
