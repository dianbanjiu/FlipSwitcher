# FlipSwitcher 产品规格说明书

> 本文档是与技术栈无关的功能描述，可用于在不同技术栈下复现 FlipSwitcher 的完整功能。

## 1. 产品概述

FlipSwitcher 是一个 Windows 平台的 **Alt+Tab 窗口切换器替代工具**，采用 Microsoft Fluent 2 Design System 设计风格。它在系统托盘静默运行，通过全局热键唤出一个浮动窗口列表，支持搜索、分组、关闭窗口等操作。

**目标系统**：Windows 10 (1903+) / Windows 11

## 2. 应用生命周期

### 2.1 单实例控制

- 通过命名互斥体（Mutex）保证全局只有一个实例运行
- 启动时检测到已有实例则立即退出

### 2.2 启动流程

1. 单实例检测
2. 读取配置文件（`%AppData%/FlipSwitcher/settings.json`）
3. 权限检查：
   - 配置要求管理员但当前非管理员 → 请求 UAC 提权重启
   - 配置不要求管理员但当前是管理员 → 以普通用户身份重启
   - 提权失败（用户取消 UAC）→ 回退为普通用户运行并更新配置
4. 更新开机自启注册（确保注册方式与管理员状态匹配）
5. 初始化语言服务 → 应用主题 → 应用字体
6. 创建系统托盘图标
7. 可选：延迟 3 秒后静默检查更新
8. 注册全局异常处理 → 使用自定义对话框展示错误
9. 创建主窗口并立即隐藏（窗口常驻内存，通过 Show/Hide 控制可见性）

### 2.3 退出流程

- 释放托盘图标、更新服务、主题服务资源
- 释放互斥体

## 3. 配置系统

### 3.1 配置文件

- 路径：`%AppData%/FlipSwitcher/settings.json`
- 格式：JSON，缩进美观打印
- 写入策略：原子写入（先写临时文件再替换，防止崩溃时数据丢失）
- 配置变更后触发 `SettingsChanged` 事件通知各组件

### 3.2 配置项

| 配置项 | 类型 | 默认值 | 说明 |
|--------|------|--------|------|
| `UseAltTab` | bool | `true` | 启用 Alt+Tab 作为激活热键（替换系统 Alt+Tab） |
| `UseAltSpace` | bool | `false` | 启用 Alt+Space 作为激活热键 |
| `StartWithWindows` | bool | `false` | 开机自启 |
| `HideOnFocusLost` | bool | `true` | 失焦自动隐藏 |
| `Theme` | enum | `Dark` | 主题：Dark / Light / Latte / Mocha |
| `RunAsAdmin` | bool | `false` | 以管理员身份运行（需重启） |
| `Language` | enum | `English` | 语言：English / Chinese / ChineseTraditional |
| `CheckForUpdates` | bool | `false` | 启动时自动检查更新 |
| `FontFamily` | string | `""` | 自定义字体（空字符串表示使用默认字体） |
| `EnablePinyinSearch` | bool | `false` | 启用拼音搜索 |
| `ShowMonitorInfo` | bool | `false` | 显示窗口所在显示器编号 |
| `FollowSystemTheme` | bool | `false` | 跟随系统主题自动切换明暗 |
| `OpenSearchOnActivation` | bool | `false` | 激活时直接进入搜索模式 |
| `ShowOnMouseScreen` | bool | `false` | 在鼠标所在显示器上显示窗口 |

## 4. 主窗口（切换器）

### 4.1 窗口属性

- 尺寸：640×520 像素（固定，不可调整）
- 无边框、透明背景
- 12px 圆角
- 始终置顶
- 不显示在任务栏
- 居中显示（默认在主屏幕工作区居中，可选在鼠标所在屏幕居中）
- 禁止双击最大化

### 4.2 布局结构

从上到下四个区域：

#### 4.2.1 顶栏（标题栏）

- 背景色：次级实心背景色
- 上方圆角 12px
- 内边距：20px 水平，16px 垂直
- 内容：
  - **左侧**：应用图标（32×32，渐变蓝色圆角方块内白色 SVG 图标）+ 应用标题「FlipSwitcher」
  - **右侧**：窗口数量徽章（蓝色圆角 pill 背景，显示 `{数量} windows`）

#### 4.2.2 搜索框

- 背景色：基础实心背景色
- 内边距：16px 水平，12px 垂直
- Fluent 风格搜索文本框，有占位文字「Type to search windows...」
- 输入实时触发过滤

#### 4.2.3 窗口列表

- 背景色：基础实心背景色
- 虚拟化列表（大量窗口时保证性能）
- 内边距：8px 水平，4px 垂直
- 每个列表项包含：
  - **窗口图标**：40×40 容器（8px 圆角，浅色背景），内部 32×32 图标，高质量缩放
  - **窗口信息**（左对齐文本）：
    - 第一行：窗口标题（粗体，超出截断显示省略号）；标题为空时显示进程名
    - 第二行：进程名（浅色辅助文字）
  - **显示器标签**（可选，仅 `ShowMonitorInfo` 启用时显示）：浅色背景 pill，显示「Monitor {N}」
  - **管理员标签**（条件显示）：当窗口进程为管理员权限时显示黄色警告风格 pill「Admin」，同时该列表项整体降低不透明度
  - **最小化标签**（条件显示）：浅色背景 pill，显示「Minimized」
- 列表项可通过鼠标点击激活
- 支持键盘上下选择，选中项自动滚动到可见区域

#### 4.2.4 空状态

- 当搜索无匹配结果时显示（仅搜索词非空时）
- 居中显示搜索图标（64×64 圆形浅色背景 + 放大镜 SVG）
- 主标题「No windows found」
- 副标题「Try a different search term」

#### 4.2.5 底栏（快捷键提示）

- 背景色：次级实心背景色
- 下方圆角 12px
- 内边距：16px 水平，10px 垂直
- **左侧**：快捷键提示（每个由浅色圆角方块包裹的按键名 + 功能文案组成）：
  - `Alt+S` → Search
  - `Alt+W` → Close
  - `Alt+,` → Settings
  - `Alt+D` → Exit（终止进程）
- **右侧**：当前激活热键显示（蓝色圆角背景 pill，如「Alt + Tab」或「Alt + Space」）

### 4.3 交互逻辑

#### 4.3.1 窗口显示

1. 重置分组状态和搜索文本
2. 根据 `OpenSearchOnActivation` 设置决定是否直接进入搜索模式（若是则不启用 Alt+Tab 保持模式）
3. 刷新窗口列表：
   - Alt+Tab 模式：默认选中第 2 个窗口（第 1 个是当前窗口，用户目标是切换到其他窗口）
   - 其他模式：选中第 1 个窗口
4. 定位窗口位置（主屏居中 或 鼠标所在屏幕居中）
5. 显示并激活窗口
6. 聚焦搜索框

#### 4.3.2 窗口隐藏

1. 隐藏窗口（不销毁）
2. 清空搜索文本
3. 重置 Alt+Tab 模式和搜索模式标记

#### 4.3.3 失焦处理

- **Alt+Tab 模式**：若 Alt 键仍按下则不隐藏（用户可能正在按 Tab 导航）
- **HideOnFocusLost 关闭**：搜索模式下尝试重新夺取焦点
- **HideOnFocusLost 开启**：隐藏窗口

#### 4.3.4 窗口切换

1. 若选中窗口为管理员窗口且当前非管理员运行 → 跳过激活，隐藏窗口
2. 临时忽略 Alt 释放事件（防止激活窗口时产生的模拟按键触发重新显示）
3. 激活选中窗口
4. 重置分组状态
5. 隐藏切换器

#### 4.3.5 关闭选中窗口

1. 若选中窗口为管理员窗口且当前非管理员 → 弹出警告对话框
2. 向目标窗口发送 `WM_CLOSE` 消息
3. 从列表移除该窗口
4. 自动选中相邻窗口

#### 4.3.6 终止选中进程

1. 终止目标进程的整个进程树
2. 从列表移除该进程所有窗口
3. 自动选中相邻窗口

#### 4.3.7 窗口分组

- **Alt+→（或右方向键）**：按选中窗口的进程名过滤，只显示同一进程的所有窗口
- **Alt+←（或左方向键）**：取消分组，恢复完整列表，定位到之前分组对应的进程
- 分组状态在窗口切换后自动重置

### 4.4 键盘快捷键（主窗口可见时）

| 按键 | 行为 |
|------|------|
| `Escape` | 隐藏窗口 |
| `Enter` | 激活选中窗口 |
| `↑` / `↓` | 上下移动选中项（循环） |
| `Tab` | 向下移动选中项 |
| `Shift+Tab` | 向上移动选中项 |
| `→` | 按进程分组（搜索框有焦点时需 Alt+→） |
| `←` | 取消分组（搜索框有焦点时需 Alt+←） |
| `Alt+W` | 关闭选中窗口 |
| `Alt+D` | 终止选中进程 |
| `Alt+S` | 进入搜索模式 |
| `Alt+,` | 打开设置窗口 |

## 5. 热键系统

### 5.1 激活热键

支持三种热键方案，在设置中配置：

1. **Alt+Tab**：通过低级键盘钩子（`WH_KEYBOARD_LL`）拦截系统 Alt+Tab
2. **Alt+Space**：通过系统 `RegisterHotKey` API 注册
3. **Ctrl+Space**（回退）：当上述两个都未勾选时自动启用

可同时启用 Alt+Tab 和 Alt+Space。

### 5.2 Alt+Tab 保持模式

当通过 Alt+Tab 激活时：
- 进入「Alt+Tab 保持模式」
- 按住 Alt 的同时按 Tab 可在列表中向下导航，Shift+Tab 向上导航
- 松开 Alt 键 → 激活当前选中窗口
- 搜索框虽有焦点但不全选文本（避免干扰快速切换）

### 5.3 搜索模式

- 通过 Alt+S 手动进入，或通过 `OpenSearchOnActivation` 自动进入
- 进入搜索模式后：
  - 退出 Alt+Tab 保持模式
  - 强制激活窗口并聚焦搜索框
  - 搜索框全选文本
  - 低级钩子不再拦截上下方向键（让搜索框可以正常使用光标）

### 5.4 低级键盘钩子拦截的按键

仅在 Alt+Tab 功能启用时安装钩子，拦截以下按键：

| 条件 | 按键 | 行为 |
|------|------|------|
| 任何时候 | `Escape` | 若切换器可见或设置窗口打开且 Alt 按下 → 隐藏/关闭 |
| 切换器可见 + Alt 释放 | Alt 键 | 触发 AltReleased 事件（确认选择） |
| 切换器不可见 + Alt 按下 | `Tab` | 显示切换器 |
| 切换器可见 + Alt 按下 | `Tab` | 向下导航（Shift+Tab 向上） |
| 切换器可见 + Alt 按下 + 非搜索模式 | `↑` / `↓` | 上下导航 |
| 切换器可见 + Alt 按下 | `←` / `→` | 取消分组 / 按进程分组 |
| 切换器可见 + Alt 按下 | `W` | 关闭选中窗口 |
| 切换器可见 + Alt 按下 | `D` | 终止选中进程 |
| 切换器可见 + Alt 按下 | `S` | 进入搜索模式 |
| 切换器可见 + Alt 按下 | `,` | 打开设置 |

## 6. 窗口枚举与过滤

### 6.1 枚举规则

通过 `EnumWindows` API 遍历所有顶层窗口，满足以下**全部**条件才加入列表：

1. 不是 Shell 窗口（桌面）
2. 可见（`IsWindowVisible`）
3. 未被 DWM 隐藏（`DWMWA_CLOAKED` 为 0）
4. 不是工具窗口（`WS_EX_TOOLWINDOW`），除非有 `WS_EX_APPWINDOW` 标记
5. 不是不可激活窗口（`WS_EX_NOACTIVATE`），除非有 `WS_EX_APPWINDOW` 标记
6. 未最小化时窗口尺寸 ≥ 50×50 像素
7. 不是当前进程的窗口
8. 有 Owner 窗口时：若 Owner 链中有可见窗口则排除（说明这是从属对话框），所有 Owner 都不可见则保留（说明是主窗口）
9. 窗口标题非空
10. 窗口类名不在排除列表中
11. 进程名不在排除列表中

### 6.2 排除的窗口类名

`Progman`, `Button`, `Shell_TrayWnd`, `Shell_SecondaryTrayWnd`, `DV2ControlHost`, `MssgrIMWindow`, `SysShadow`, `Xaml_WindowedPopupClass`, `Windows.UI.Core.CoreWindow`

### 6.3 排除的进程名

`SearchHost`, `ShellExperienceHost`, `StartMenuExperienceHost`, `SearchUI`, `LockApp`, `TextInputHost`

### 6.4 窗口信息

每个窗口记录以下信息：

| 属性 | 说明 |
|------|------|
| Handle | 窗口句柄 |
| Title | 窗口标题 |
| ProcessName | 进程名（不含扩展名） |
| ClassName | 窗口类名 |
| ProcessId | 进程 ID |
| IsMinimized | 是否最小化 |
| IsMaximized | 是否最大化（包括最小化前是最大化的情况） |
| IsElevated | 进程是否以管理员权限运行（惰性求值，按进程 ID 缓存） |
| MonitorNumber | 窗口所在显示器编号（1 起始，惰性求值） |
| Icon | 窗口图标（异步加载，加载完成后通知 UI 更新） |

### 6.5 图标加载策略

按优先级尝试：

1. **UWP 应用**（窗口类名为 `ApplicationFrameWindow`）：
   - 查找子窗口（`Windows.UI.Core.CoreWindow` 或 `Windows.UI.Composition.DesktopWindowContentBridge`）获取真实 UWP 进程 ID
   - 从 `AppxManifest.xml` 读取 `VisualElements` 中的图标路径
   - 按优先级尝试不同尺寸后缀的图标文件（`targetsize-256_altform-unplated` > `targetsize-64` > `scale-200` 等）
   - 回退到 Shell API
2. **窗口消息图标**：通过 `WM_GETICON` 消息获取（优先 `ICON_BIG`，回退 `ICON_SMALL`），超时 50ms
3. **Shell API**：`SHGetFileInfo` 获取大图标
4. **进程可执行文件**：从 EXE 文件提取关联图标

图标加载在后台线程执行，完成后冻结位图并通过 UI 线程通知更新。

### 6.6 搜索/过滤

匹配逻辑（不区分大小写，满足任一即匹配）：
1. 窗口标题包含搜索词
2. 进程名包含搜索词
3. 若启用拼音搜索：
   - 窗口标题拼音首字母包含搜索词
   - 窗口标题全拼包含搜索词
   - 进程名拼音首字母包含搜索词
   - 进程名全拼包含搜索词

拼音数据惰性计算并缓存。

## 7. 窗口激活逻辑

激活目标窗口的策略（按顺序尝试多种方法确保成功）：

1. 允许设置前台窗口（`AllowSetForegroundWindow` + `LockSetForegroundWindow`）
2. 将当前线程附加到前台窗口线程和目标窗口线程（`AttachThreadInput`）
3. 处理最小化窗口：
   - 最小化前是最大化的 → `SW_SHOWMAXIMIZED`
   - 普通最小化 → `SW_RESTORE`
4. `BringWindowToTop` + `SetForegroundWindow`
5. 若仍未成功 → 模拟 Alt 键按下释放后再次 `SetForegroundWindow`
6. 若仍未成功 → `SwitchToThisWindow`
7. 最终回退 → 临时设为置顶再取消置顶
8. 最终清理：分离线程输入附加

## 8. 设置窗口

### 8.1 窗口属性

- 尺寸：500×480 像素（固定）
- 无边框、透明背景、12px 圆角
- 居中显示
- 标题栏可拖动
- 右上角关闭按钮
- 按 Escape 关闭

### 8.2 布局结构

顶栏 + 可滚动的设置内容区域，内容分为以下卡片式分区：

#### 8.2.1 语言（Language）

- 下拉选择框：English / 简体中文 / 繁體中文
- 切换即时生效

#### 8.2.2 热键（Hotkey）

- Alt+Space 开关（复选框）
- Alt+Tab 开关（复选框），旁边有蓝色「Replaces System」徽章
- 底部显示当前激活的热键组合
- 至少需要启用一个热键，否则弹出提示

#### 8.2.3 外观（Appearance）

- 跟随系统主题开关
- 主题下拉框：Dark / Light / Latte / Mocha（跟随系统时此项被忽略）
- 字体下拉框：列出系统已安装字体，首项为「Default (Segoe UI Variable)」

#### 8.2.4 行为（Behavior）

- 以管理员身份运行（开关 + 状态徽章显示 Enabled/Disabled）：切换后需确认重启
- 开机自启
- 失焦自动隐藏
- 拼音搜索
- 显示显示器信息
- 激活时直接进入搜索模式
- 在鼠标所在屏幕显示

#### 8.2.5 更新（Updates）

- 自动检查更新开关
- 手动「Check for Updates」按钮

#### 8.2.6 关于（About）

- 版本号显示（格式：`FlipSwitcher v{版本号}`）
- 一行描述文字
- GitHub 链接

## 9. 对话框

### 9.1 通用 Fluent 对话框

- 400px 宽，高度自适应
- 无边框、透明背景、12px 圆角
- 居中于 Owner 窗口
- 三区域布局：标题栏（图标 + 标题 + 关闭按钮）、消息内容区、按钮区
- 支持按钮类型：OK / YesNo
- 支持图标类型：Information（蓝色）/ Warning（黄色）/ Error（红色）
- 标题栏可拖动
- Escape / Enter 可关闭

## 10. 系统托盘

### 10.1 图标

使用应用 PNG 图标

### 10.2 右键菜单

| 菜单项 | 行为 |
|--------|------|
| Show | 显示主窗口 |
| --- | 分隔线 |
| Settings | 打开设置窗口 |
| Restart | 重启应用 |
| --- | 分隔线 |
| Exit | 退出应用 |

### 10.3 交互

- 双击托盘图标 → 显示主窗口
- 菜单文本跟随语言设置实时更新

## 11. 主题系统

### 11.1 主题列表

| 主题 | 明暗 | 描述 |
|------|------|------|
| Dark | 暗色 | 默认主题，深色背景 + 微软蓝强调色 |
| Light | 亮色 | 浅色背景 |
| Latte | 亮色 | Catppuccin Latte 配色 |
| Mocha | 暗色 | Catppuccin Mocha 配色 |

### 11.2 颜色体系（以 Dark 主题为例）

| 语义令牌 | Dark 色值 | 用途 |
|----------|-----------|------|
| BackgroundAcrylicBase | `#E6202020` | 主容器背景（仿亚克力） |
| BackgroundSolidBase | `#FF1F1F1F` | 实心基础背景 |
| BackgroundSolidSecondary | `#FF2B2B2B` | 顶栏/底栏背景 |
| CardBackground | `#FF2D2D2D` | 设置卡片背景 |
| CardBackgroundSelected | `#FF0078D4` | 列表项选中背景 |
| TextPrimary | `#FFFFFFFF` | 主文字 |
| TextSecondary | `#B3FFFFFF` | 辅助文字 |
| TextTertiary | `#66FFFFFF` | 三级文字 |
| AccentDefault | `#FF0078D4` | 强调色（微软蓝） |
| AccentGradient | `#429CE3 → #0078D4` | 图标渐变背景 |
| SystemFillColorCaution | `#FFFCE100` | 管理员标签前景 |
| SystemFillColorCautionBackground | `#33FCE100` | 管理员标签背景 |

### 11.3 跟随系统主题

- 监听 Windows 注册表 `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme`
- 值为 1 → Light 主题，值为 0 → Dark 主题
- 系统主题变化时自动切换

### 11.4 DWM 窗口效果

根据当前主题明暗设置窗口的 `DWMWA_USE_IMMERSIVE_DARK_MODE` 属性（影响窗口边框颜色等系统级效果）。

## 12. 国际化

### 12.1 支持语言

- English（默认）
- 简体中文
- 繁體中文

### 12.2 本地化范围

- 主窗口所有文本
- 设置窗口所有标签和描述
- 对话框按钮和消息
- 托盘菜单
- 快捷键提示

### 12.3 实现方式

- 每种语言一个字符串资源字典文件
- 通过 `DynamicResource` 绑定实现热切换（无需重启）
- `LanguageService.GetString(key)` 用于代码中获取本地化字符串

## 13. 更新系统

### 13.1 检查逻辑

1. 调用 GitHub API `https://api.github.com/repos/{owner}/{repo}/releases/latest`
2. 解析 `tag_name` 获取最新版本号（去掉 `v` 前缀）
3. 与当前程序集版本比较
4. 有新版本时：
   - 在 `assets` 中查找安装包下载链接（优先版本化文件名，回退通用文件名）
   - 弹出确认对话框
   - 用户确认后在浏览器中打开下载链接

### 13.2 安全措施

- HTTP 客户端 10 秒超时
- 使用信号量防止并发检查
- 下载链接白名单域名验证：仅允许 `github.com` 和 `objects.githubusercontent.com`
- 仅允许 HTTPS 链接

## 14. 开机自启

### 14.1 普通用户模式

通过注册表 `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run` 写入启动项

### 14.2 管理员模式

通过 Windows 计划任务（`schtasks`）创建登录触发、最高权限运行的计划任务

### 14.3 切换逻辑

- 启用自启时根据当前管理员设置选择注册方式，并清理另一种方式的残留
- 禁用自启时同时清理注册表和计划任务

## 15. 管理员权限管理

### 15.1 权限检查

检测当前进程是否以管理员身份运行

### 15.2 提权/降权重启

- **提权**：以 `runas` 动词重新启动自身
- **降权**：通过 Explorer.exe 作为中间进程启动自身（利用 Explorer 以普通用户身份运行的特性）

### 15.3 对窗口操作的影响

- 管理员窗口在列表中降低不透明度并显示「Admin」标签
- 非管理员运行时：
  - 无法激活管理员窗口（跳过并隐藏切换器）
  - 无法关闭管理员窗口（弹出提示对话框）
  - 窗口提权检测通过 `OpenProcessToken` + `GetTokenInformation(TokenElevation)` 实现

## 16. 字体系统

- 默认字体：Segoe UI Variable（Windows 11 系统字体）
- 用户可从系统已安装字体列表中选择
- 字体变更即时应用到全局

## 17. 多显示器定位

### 17.1 窗口居中策略

- **默认**：在系统工作区居中（主显示器）
- **ShowOnMouseScreen 启用**：
  1. 获取鼠标当前坐标
  2. 确定鼠标所在显示器
  3. 获取该显示器的 DPI 缩放比例
  4. 在该显示器工作区居中放置窗口（考虑 DPI 缩放）

### 17.2 窗口显示器编号

- 通过枚举所有显示器并记录句柄列表
- 根据窗口所在显示器句柄在列表中的索引确定编号（1 起始）

## 18. 控件风格规范

### 18.1 文本样式

| 样式名 | 用途 | 参考规格 |
|--------|------|----------|
| TitleTextStyle | 标题栏标题 | 较大字号，主色 |
| SubtitleTextStyle | 设置分区标题 | 中等字号 |
| BodyStrongTextStyle | 列表项标题 / 设置项标题 | 正常字号，粗体 |
| BodyTextStyle | 正文 | 正常字号 |
| CaptionTextStyle | 辅助信息 / 标签 / 提示 | 较小字号，辅色 |

### 18.2 控件样式

- **搜索框**：Fluent 风格，带占位文本和搜索图标
- **列表框**：无边框，选中项使用蓝色高亮背景
- **按钮**：Fluent 风格圆角按钮
- **关闭按钮**：右上角 × 按钮，悬停变色
- **复选框**：Fluent 风格开关式
- **下拉框**：Fluent 风格带圆角
- **滚动条**：细窄 Fluent 风格，悬停加粗

### 18.3 卡片样式

设置页中每个分区使用卡片布局：
- 卡片背景色 + 1px 描边 + 8px 圆角
- 内边距 16px
- 卡片间距 24px

## 19. 应用图标

- 格式：同时提供 `.ico` 和 `.png`
- `.ico` 用于可执行文件图标
- `.png` 用于托盘图标和应用内显示
- 图标设计应符合 Fluent 2 Design 风格
