# AIEnter · AI 入口

Windows 11 任务栏上的 AI 对话入口。在时间日期右侧嵌入真实任务栏按钮并预留位置，左键切换对话显示／收起，右键打开设置。

## 功能

- 默认提供 DeepSeek、ChatGPT、豆包、小艺、Gemini、Grok、Kimi，支持通过“添加 AI”加入多个 HTTPS AI 网页，数量不限。
- 内置和新增服务统一排序；新增 AI 支持编辑、移除、设为默认及名称／网址搜索，窄窗口中放不下的服务进入可滚动的“更多”菜单。
- 悬浮面板每次打开定位到工作区右下角；支持固定、调整大小及切换独立窗口。切换模式保留同一网页和草稿。
- 对话打开时上浮淡入（180 毫秒／16 DIP），收起时下沉淡出（130 毫秒）；跟随 Windows 动画设置，保留当前网页与草稿。
- 紧凑单行工具栏，顶部空白可拖动独立窗口，不常驻显示标题和底部网址。
- 可分别设置顶栏背板透明度与模糊度，以及背板圆角、网页圆角、按钮大小、圆角和颜色；颜色分别支持普通按钮和选中 AI。
- 任务栏圆形图标支持大小调节，对话打开时显示可调光晕。图标位于时间与“显示桌面”区域之间。
- 文件、图片、链接和文字可拖入窗口或任务栏图标；图标悬停唤起窗口，松开交给当前 AI，加入附件／草稿，不自动发送。网站未就绪时保留内容并提供重试。
- 外观实时预览，保存后记住，关闭设置撤销未保存的预览。
- 设置窗口按 AI 服务、窗口与操作、任务栏、外观、备份与恢复分组；支持数值微调、预览和未保存更改确认。

默认设置来自当前 AIEnter 配置：悬浮、不置顶，面板 388.5×816.5 DIP；网页圆角 28 DIP、背板圆角 20 DIP、背板透明度 20%、模糊度 0%；任务栏图标 25 DIP，光晕范围 6 DIP、强度 100%；普通按钮为半透明青色，选中按钮为亮青色。默认快捷键 Alt+Space。顶栏顺序为 DeepSeek、ChatGPT、豆包、小艺、Gemini、Grok、Kimi。UI 风格为默认预设。完整清单见 [UI 默认值](config/ui-defaults.json)。这些默认只用于新安装或恢复全部默认，升级不覆盖既有设置；本机窗口坐标、登录资料和网址查询参数不会作为默认值发布。

本项目嵌入 AI 官方网页，首次使用请自行登录；不需要在应用中配置 API 密钥。账号、网页登录资料和设置保存在本机 `%LOCALAPPDATA%\AIEnter`，不随源码上传。

## 构建与运行

当前发行版为 [v3.0.0](https://github.com/bin-art-666/AIEnter/releases/tag/v3.0.0)。便携版下载 `AIEnter-v3.0.0-win-x64.zip`，完整解压后运行 `AIEnter.exe`，不要只复制单个 EXE。程序包包含 .NET 与 Windows App SDK 运行依赖，仍需要 WebView2 Runtime。

时间右侧的真实任务栏按钮需要另行安装 Windhawk，导入发行包内的 `Taskbar/aienter-taskbar-ai.wh.cpp` 并将其 `executable` 设置为解压后的 `AIEnter.exe` 完整路径。具体步骤见 [发行版使用说明](docs/release-usage.md)。发行包不包含用户设置、登录缓存和 Windhawk 运行时，也不自动配置开机自启。

环境要求：Windows 11 x64、.NET 10 SDK、WebView2 Runtime。原生任务栏扩展需要 Windhawk 1.7.3 便携版及其 C++/WinRT 开发工具。

1. 克隆仓库：

   ```powershell
   git clone https://github.com/bin-art-666/AIEnter.git
   cd AIEnter
   ```

2. 安装 .NET 10 SDK（或放入 `.tools\dotnet`），将 Windhawk 便携运行时及编译器准备到 `.tools\windhawk`；扩展构建脚本要求存在 `.tools\windhawk\Compiler\bin\clang++.exe`。
3. 构建并启动：

   ```powershell
   .\build.ps1
   .\tools\taskbar-ai\Build.ps1
   .\tools\taskbar-ai\Start.ps1
   ```

主体构建将从 NuGet 获取依赖；已准备 `.tools\feed` 离线源时可使用 `.\build.ps1 -Offline`。开发依赖与缓存位于 `.tools`，输出位于 `dist`，验证记录位于 `.build`，均不提交 Git。

更新正在运行的版本前，先运行 `.\tools\taskbar-ai\Stop.ps1`。此脚本停止应用并卸载本项目的任务栏扩展，不取消已有的登录自启配置。仓库构建与启动脚本默认不注册开机自启。

常用入口：

```powershell
.\dist\AIEnter.exe             # 打开 AI
.\dist\AIEnter.exe --toggle-ai # 切换 AI 显示／收起
.\dist\AIEnter.exe --settings  # 打开设置
.\dist\AIEnter.exe --quit      # 退出应用
.\tools\taskbar-ai\Stop.ps1    # 停止应用及任务栏入口
```

## 验证

配置、导航与几何检查：

```powershell
dotnet run --project tests\AiChecks --configuration Release
```

构建后可使用真实 WinUI / WebView2 本地测试页验证窗口模式、顶栏布局、排序、外观预览、原生拖动和草稿保留；不会登录或连接 AI 官网：

```powershell
.\dist\AIEnter.exe --verify-ai-modes --data-dir .build\mode-check
```

结果写入指定目录的 `mode-result.json`。本地页还检查位图、普通图片文件和截图进入网页附件的路径，以及附件回执、草稿保留和重复投递保护。

## 兼容性与来源

任务栏扩展依赖 Explorer 内部 XAML 布局和符号，不是微软保证兼容的公共扩展接口。当前面向 Windows 11 x64 主任务栏；不支持的布局或符号获取失败时不插入，系统升级后需要重新验证。多屏热插拔、自动隐藏及所有系统版本的兼容性尚未全面验证。

- [任务栏与窗口实现说明](docs/taskbar-ai.md)
- [来源与独立边界](docs/source-origin.md)
- [UI 默认值](config/ui-defaults.json)
- [任务栏辅助代码的 MIT 许可及来源](ThirdParty/TaskbarIntegration-MIT.txt)

Windhawk、.NET、Windows App SDK 等依赖保留各自许可证，依赖运行时不包含在本仓库中。

旧版单个自定义 AI 会自动迁移为服务列表的一项，保留名称、网址、顺序和原网页登录目录。设置保存后使用版本 2，版本 1 原配置由备份保留；请勿用旧版程序覆盖新版设置。移除 AI 只移除入口，不清理网页登录数据。默认 AI 在应用下次启动时使用，运行中的当前对话不因修改默认项而切换。

任务栏支持实时流动色球：圆形轮廓固定，粉、黄、紫、青色场在内部变化。设置 → 任务栏可调整流速 0–1800%，默认 200%，0 暂停；支持预览、保存和撤销。v3.0.0 升级须同时更新程序与 Windhawk 模组。

安装向导构建和联网依赖见 [安装版说明](docs/installer.md)。

普通用户可下载 [v3.0.0 一键安装版 EXE](https://github.com/bin-art-666/AIEnter/releases/download/v3.0.0/AIEnter-v3.0.0-Setup-win-x64.exe)。安装时联网自动部署任务栏运行组件，详细条件见 [安装版说明](docs/installer.md)。

双击截图：遵循 Windows 双击速度，框选后保存 PNG 并显示可选择 AI 的限时预览；设置 → 任务栏支持截图开关、驻留时间和保存目录。
