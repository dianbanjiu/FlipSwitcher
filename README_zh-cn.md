# FlipSwitcher

**[English](README.md) | [简体中文](README_zh-cn.md)**

<p align="center">
  <img src="docs/screenshot.png" alt="FlipSwitcher 截图" width="600"/>
</p>

一个基于 **Fluent 2 Design** 构建的现代化、美观的 Windows Alt-Tab 替代工具。

## ✨ 功能特性

- 🎨 **Fluent 2 Design** - 现代化的深色主题，支持 Mica/Acrylic 效果
- 🌓 **主题支持** - 在深色和浅色主题之间切换（需要重启）
- ⚡ **快速窗口切换** - 即时在打开的窗口之间切换
- 🔍 **实时搜索** - 输入时按标题或进程名过滤窗口
- ⌨️ **键盘优先** - 专为喜欢键盘快捷键的高级用户设计
- 🖼️ **窗口图标** - 通过应用程序图标进行视觉识别
- 💾 **轻量级** - 资源占用极少，在系统托盘中运行

## 🚀 快速开始

### 系统要求

- Windows 10 (1903+) 或 Windows 11
- .NET 8.0 运行时

### 安装

1. 从 [Releases](https://github.com/dianbanjiu/FlipSwitcher/releases) 下载最新版本
2. 解压并运行 `FlipSwitcher.exe`
3. 应用程序将在系统托盘中启动

### 从源码构建

```bash
# 克隆仓库
git clone https://github.com/dianbanjiu/FlipSwitcher.git
cd FlipSwitcher

# 构建项目
dotnet build -c Release

# 运行
dotnet run --project FlipSwitcher
```

## ⌨️ 键盘快捷键

| 快捷键        | 操作                                    |
| ------------- | --------------------------------------- |
| `Alt + Space` | 打开/关闭 FlipSwitcher                  |
| `Alt + Tab`   | 打开 FlipSwitcher（可选，替换系统 Alt+Tab） |
| `Alt + S`     | 进入搜索模式（保持窗口打开）            |
| `Alt + W`     | 关闭选中的窗口                          |
| `Alt + ,`     | 打开设置                                |
| `Alt + ->`    | 按选中应用分组                          |
| `Alt + <-`    | 返回窗口列表                            |

## 🎨 设计

FlipSwitcher 遵循 Microsoft 的 [Fluent 2 设计系统](https://fluent2.microsoft.design/) 构建：

- **Mica 材质** - 半透明背景，适配桌面壁纸
- **圆角设计** - 一致的 8px/12px 圆角半径
- **Segoe UI Variable** - 现代化的可变字体，提供清晰的排版
- **主题支持** - 在设置中选择深色或浅色主题（需要重启）
- **流畅动画** - 平滑的过渡和悬停效果

## 🏗️ 架构

```
FlipSwitcher/
├── Assets/         # 应用程序图标和图片
├── Converters/     # WPF 值转换器
├── Core/           # Windows API 互操作 (NativeMethods)
├── Models/         # 数据模型 (AppWindow)
├── Properties/     # 发布配置文件
├── Services/       # 业务逻辑
├── Themes/         # Fluent 2 样式和颜色（深色/浅色主题）
├── ViewModels/     # MVVM 视图模型
└── Views/          # WPF 窗口 (MainWindow, SettingsWindow)
```

## 🛠️ 技术栈

- **WPF** - Windows Presentation Foundation
- **.NET 8.0** - 最新 LTS 框架
- **CommunityToolkit.Mvvm** - MVVM 辅助工具
- **Hardcodet.NotifyIcon.Wpf** - 系统托盘支持

## 🙏 致谢

- Inspired by kvakulo [Switcheroo](https://github.com/kvakulo/Switcheroo) 
- Microsoft [Fluent 2 Design](https://fluent2.microsoft.design/)
- [Segoe UI Variable](https://docs.microsoft.com/en-us/windows/apps/design/signature-experiences/typography) 字体

---

<p align="center">
  为 Windows 高级用户精心打造 ❤️
</p>

