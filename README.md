# FlipSwitcher

**[English](README.md) | [简体中文](README_zh-cn.md)**

<p align="center">
  <img src="docs/screenshot.png" alt="FlipSwitcher Screenshot" width="600"/>
</p>

A modern, beautiful Alt-Tab replacement for Windows, built with **Fluent 2 Design System**.

## 🚀 Getting Started

### Prerequisites

- Windows 10 (1903+) or Windows 11
- .NET 8.0 Runtime

### Installation

1. Download the latest release from [Releases](https://github.com/dianbanjiu/FlipSwitcher/releases)
2. Extract and run `FlipSwitcher.exe`
3. The app will start in the system tray

### Building from Source

```bash
# Clone the repository
git clone https://github.com/dianbanjiu/FlipSwitcher.git
cd FlipSwitcher

# Build the project
dotnet build -c Release

# Run
dotnet run --project FlipSwitcher
```

## ⌨️ Keyboard Shortcuts

| Shortcut      | Action                                                |
| ------------- | ----------------------------------------------------- |
| `Alt + Space` | Open/Close FlipSwitcher                               |
| `Alt + Tab`   | Open FlipSwitcher (optional, replaces system Alt+Tab) |
| `Alt + S`     | Enter search mode (keep window open)                  |
| `Alt + W`     | Close selected window                                 |
| `Alt + ,`     | Open settings                                         |
| `Alt + ->`    | Summary selected app                                  |
| `Alt + <-`    | Back to windows list                                  |
| `Alt + D`     | Stop the selected process                             |

## ✨ Features

- **Fast Search** - Quickly filter windows by typing
- **Pinyin Search** - Search Chinese window titles using pinyin initials or full pinyin (optional, disabled by default)
- **Window Grouping** - Group windows by application
- **Quick Close** - Close windows with Alt+W without leaving the switcher
- **Process Control** - Terminate processes with Alt+D

## 🛠️ Technology Stack

- **WPF** - Windows Presentation Foundation
- **.NET 8.0** - Latest LTS framework
- **CommunityToolkit.Mvvm** - MVVM helpers
- **Hardcodet.NotifyIcon.Wpf** - System tray support

## 🙏 Acknowledgments

- Inspired by kvakulo [Switcheroo](https://github.com/kvakulo/Switcheroo)
- Microsoft [Fluent 2 Design System](https://fluent2.microsoft.design/)
- [Segoe UI Variable](https://docs.microsoft.com/en-us/windows/apps/design/signature-experiences/typography) font
- [Inno Setup](https://jrsoftware.org/isinfo.php)

---

<p align="center">
  Made with ❤️ for Windows power users
</p>
