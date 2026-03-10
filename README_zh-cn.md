# FlipSwitcher

**[English](README.md) | [简体中文](README_zh-cn.md)**

<p align="center">
  <img src="docs/screenshot.png" alt="FlipSwitcher 截图" width="600"/>
</p>

一个基于 **Fluent 2 Design** 构建的现代化、美观的 Windows Alt-Tab 替代工具。

## 🚀 快速开始

### 系统要求

- Windows 10 (1903+) 或 Windows 11

### 安装

1. 从 [Releases](https://github.com/dianbanjiu/FlipSwitcher/releases/latest) 下载 `FlipSwitcher-windows-x64-Setup.exe`
2. 运行安装程序并按提示完成安装
3. 安装完成后应用程序将在系统托盘中启动

> 💡 **提示**：如果需要关闭或者退出管理员窗口或者进程，推荐在设置中启用"以管理员身份运行"

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

| 快捷键        | 操作                                        |
| ------------- | ------------------------------------------- |
| `Alt + Space` | 打开/关闭 FlipSwitcher                      |
| `Alt + Tab`   | 打开 FlipSwitcher（可选，替换系统 Alt+Tab） |
| `Alt + S`     | 进入搜索模式（保持窗口打开）                |
| `Alt + W`     | 关闭选中的窗口                              |
| `Alt + ,`     | 打开设置                                    |
| `Alt + →`     | 按选中应用分组显示窗口                      |
| `Alt + ←`     | 返回完整窗口列表                            |
| `Alt + D`     | 终止选定进程                                |

## ✨ 特性

- **快速搜索** - 输入关键字快速筛选窗口
- **拼音搜索** - 支持使用拼音首字母或全拼搜索中文窗口标题（可选，默认关闭）
- **多显示器支持** - 多显示器时在窗口列表显示每个窗口所在的显示器（可选，默认关闭）
- **窗口分组** - 按 `Alt + →` 聚焦查看同一应用的所有窗口
- **快速关闭** - 使用 `Alt + W` 快速关闭窗口，无需离开切换器
- **进程控制** - 使用 `Alt + D` 终止进程
- **主题支持** - 提供深色、浅色、Catppuccin Latte 和 Catppuccin Mocha 四种主题，支持跟随系统主题自动切换
- **多语言** - 支持英语、简体中文和繁体中文
- **自定义字体** - 可在设置中配置偏好的字体
- **自动更新** - 可选择在启动时自动检查更新
- **开机自启** - 可选择随 Windows 启动自动运行
- **系统托盘** - 静默运行于后台，通过托盘图标随时访问

## 🛠️ 技术栈

- **WPF** - Windows Presentation Foundation
- **.NET 8.0** - 最新 LTS 框架
- **CommunityToolkit.Mvvm** - MVVM 辅助工具
- **Hardcodet.NotifyIcon.Wpf** - 系统托盘支持
- **TinyPinyin.Net** - 拼音搜索支持

## 🙏 致谢

- Inspired by kvakulo [Switcheroo](https://github.com/kvakulo/Switcheroo)
- Microsoft [Fluent 2 Design](https://fluent2.microsoft.design/)
- [Segoe UI Variable](https://docs.microsoft.com/en-us/windows/apps/design/signature-experiences/typography) 字体
- [Inno Setup](https://jrsoftware.org/isinfo.php)
- [Catppuccin](https://catppuccin.com/palette/) 配色

---

<p align="center">
  为 Windows 高级用户精心打造 ❤️
</p>
