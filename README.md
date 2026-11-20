# AITaskbar

把 Halo、AIEnter 和 LumaBar 的源码整合为一个 Windows 11 x64 应用。三个模块编入同一个 `AITaskbar.exe`，由同一个宿主管理启动与退出。Halo 和 AIEnter 共享 WinUI 界面线程，LumaBar 沿用自己的 WinForms 界面并在同一进程的另一条界面线程运行。

| 位置 | 模块 | 行为 |
| --- | --- | --- |
| 左端 | Halo | 从圆环拖入文件、图片、文字、链接，打开中转站后可预览或拖出 |
| 中间 | Windows Explorer | 原生开始菜单、固定应用和运行中窗口 |
| 时钟右边 | AIEnter | 打开或收起 AI 对话，拖入内容后交给当前 AI 的附件或草稿 |
| 整条任务栏 | LumaBar | 根据窗口或壁纸选择明暗，应用玻璃材质、对比度保护和鼠标光效 |

## 构建和运行

需要 Windows 11 x64 和 .NET 10 SDK。AI 对话需要系统的 WebView2 Runtime。可将便携 SDK 放在 `.tools\dotnet`，也可通过 `-DotnetPath` 指定 SDK。已有离线依赖时可放入 `.tools\feed` 并使用 `-Offline`。

```powershell
.\build.ps1
.\dist\AITaskbar.exe
```

设置窗口的「预览栏」管理双栏对比和应用区外观，「集成模块」打开 Halo、AIEnter 或 LumaBar 的面板与对应设置。若原版 LumaBar 正在运行，先从原版托盘选择「退出并恢复」。设置窗口底部的「退出 AITaskbar」会尝试恢复已应用的系统外观并退出程序。

集成版默认在 `%LOCALAPPDATA%\AITaskbar` 下为每个模块保存资料。本机切换时保留了原有 AIEnter 和 Halo 资料目录，集成版通过 `%LOCALAPPDATA%\AITaskbar\use-existing-profiles` 直接使用这些资料。LumaBar 设置已复制到集成版自己的子目录。

## 任务栏预览

开发期间，本机登录后自动打开双栏对比。预览栏通过 Windows AppBar 停靠在主屏原生任务栏上方，和原生栏同时使用。点击预览栏右侧的关闭按钮可释放占用空间，Halo、AIEnter 和 LumaBar 继续服务于原生栏。可从设置窗口点「开启预览栏」恢复对比。

构建后可运行 `wscript.exe .\tools\StartBackground.vbs` 重现登录时的双栏启动。若已关闭预览栏，只需重新运行该命令即可在现有进程中开启对比。

中间的应用区按应用显示图标。固定项在前，正在运行的非固定应用在后，空间不足时从「更多应用」打开。小点表示正在运行，短线表示当前前台应用。有一个窗口时单击切换或还原，有多个窗口时从图标上方的标题列表选择。右键可固定支持的桌面应用，也可通过「固定应用」选择 `.exe`。固定列表保存在 `%LOCALAPPDATA%\AITaskbar\preview-pins.json`。Halo 和 AIEnter 按钮可打开各自的面板，也可接收拖入内容，AIEnter 的内容进入附件或草稿流程。

预览栏左端的 Halo 圆环和最右端的 AIEnter 彩球沿用各自现有的外观设置。右键图标可以打开对应设置。LumaBar 已启用时，两条栏共用当前场景、明暗、材质、染色、模糊、对比度保护和鼠标光效参数。原生栏由 Explorer 绘制，预览栏的背景由自己的 WinUI 窗口绘制。

预览栏中间的应用图标使用铺满格子的圆角底板。自带底板的图标用原底色补齐透明边缘，透明标志使用适配明暗的底板。设置窗口的「预览栏 → 应用图标大小」统一调节图标和格子，默认 32 DIP，可在 24–36 DIP 之间即时调节并保存。右键应用图标可切换「使用原始图标」或「使用圆角图标」，单个应用的选择会保存。Windows 高对比度模式下使用原始图标。

在「预览栏 → 运行标记颜色」可为运行圆点和前台短条选色。选色时即时预览，点「应用颜色」保存，点「跟随系统」恢复 Windows 强调色。Windows 高对比度模式下使用系统前景色。

## 接入真实任务栏

Halo 和 AIEnter 的任务栏按钮仍使用 Explorer 内的 Windhawk 扩展，源码随项目一起维护。已运行的 AITaskbar 通过操作信号接收任务栏点击和拖放。任务栏操作也可在后台启动 AITaskbar。

```powershell
.\tools\BuildTaskbar.ps1 -RuntimeRoot 'D:\Tools\Windhawk'
.\tools\RegisterTaskbar.ps1 -RuntimeRoot 'D:\Tools\Windhawk'
```

`RuntimeRoot` 是带开发工具的 Windhawk 便携版目录。应用启用前先在 Windhawk 中停用原版 Halo 和 AIEnter 的任务栏模组，再启动或重新加载便携 Windhawk。停用集成版入口时对同一个 `RuntimeRoot` 运行 `RegisterTaskbar.ps1 -Disable`，再重新加载。

本机便携运行时位于 `.tools\windhawk`。当前用户登录后，计划任务「AITaskbar」延迟 20 秒运行 `tools\StartBackground.vbs`，以当前用户权限启动 Windhawk 和集成应用。启动脚本会把执行结果记到 `%LOCALAPPDATA%\AITaskbar\startup.log`，可在任务计划程序里查看上次运行结果。Halo 和 AIEnter 挂在原生栏两端，LumaBar 从后台加载保存的外观。开始菜单中的 AITaskbar 快捷方式打开设置窗口。

## 源码

- `modules/AIEnter` 保存 AI 窗口、WebView2、截图、拖放和右侧按钮。
- `modules/Halo` 保存中转站、文件库、预览和左侧圆环。
- `modules/LumaBar` 保存外观检测、材质、光效和原生组件。整合版由根目录统一构建与更新。

LumaBar 的 GPL-3.0 许可证、原始 MIT 声明、第三方版权及 ExplorerTAP 对应源码版本见 `modules/LumaBar` 中的许可文件和 `native/README.md`。根目录构建会校验原生 DLL，并将许可文件随构建产物复制。
