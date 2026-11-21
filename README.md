# AITaskbar

把 Halo、AIEnter 和 LumaBar 的源码整合为一个 Windows 11 x64 应用。三个模块编入同一个 `AITaskbar.exe`，共用设置窗口。LumaBar 的外观检测在同一进程的后台线程运行。

| 位置 | 模块 | 行为 |
| --- | --- | --- |
| 左端 | Halo | 从圆环拖入文件、图片、文字、链接，打开中转站后可预览或拖出 |
| 中间 | 应用区 | 固定应用、运行窗口和缩略图 |
| 最右端 | AIEnter | 打开或收起 AI 对话，拖入内容后交给当前 AI 的附件或草稿 |
| AITaskbar 预览栏 | LumaBar | 根据窗口或壁纸选择明暗，应用玻璃材质、对比度保护和鼠标光效 |

## 构建和运行

需要 Windows 11 x64 和 .NET 10 SDK。AI 对话需要系统的 WebView2 Runtime。可将便携 SDK 放在 `.tools\dotnet`，也可通过 `-DotnetPath` 指定 SDK。已有离线依赖时可放入 `.tools\feed` 并使用 `-Offline`。

```powershell
.\build.ps1
.\dist\AITaskbar.exe
```

设置窗口提供「任务栏外观」「Halo」「AIEnter」三页。任务栏外观包含 LumaBar 的玻璃、光效、明暗与应用规则，以及双栏对照和应用区设置。设置窗口底部的「退出 AITaskbar」会关闭集成模块并释放预览栏占用的空间。

集成版默认在 `%LOCALAPPDATA%\AITaskbar` 下为每个模块保存资料。本机切换时保留了原有 AIEnter 和 Halo 资料目录，集成版通过 `%LOCALAPPDATA%\AITaskbar\use-existing-profiles` 直接使用这些资料。LumaBar 设置已复制到集成版自己的子目录。

## 任务栏预览

开发期间，本机登录后自动打开双栏对比。预览栏通过 Windows AppBar 停靠在主屏原生任务栏上方，和原生栏同时使用。点击预览栏右侧的关闭按钮可释放占用空间。可从设置窗口点「开启预览栏」恢复对比。

构建后可运行 `wscript.exe .\tools\StartBackground.vbs` 重现登录时的双栏启动。若已关闭预览栏，只需重新运行该命令即可在现有进程中开启对比。

中间的应用区按应用显示图标。固定项在前，正在运行的非固定应用在后，空间不足时从「更多应用」打开。小点表示正在运行，短线表示当前前台应用。有一个窗口时单击切换或还原，有多个窗口时从图标上方的标题列表选择。右键可固定支持的桌面应用，也可通过「固定应用」选择 `.exe`。固定列表保存在 `%LOCALAPPDATA%\AITaskbar\preview-pins.json`。Halo 和 AIEnter 按钮可打开各自的面板，也可接收拖入内容，AIEnter 的内容进入附件或草稿流程。

预览栏左端的 Halo 圆环和最右端的 AIEnter 彩球沿用各自现有的外观设置。右键图标可直达 AITaskbar 设置中的对应页。Windows 原生任务栏保持系统外观。

「任务栏外观」可直接调节背景透明度和模糊强度，动态效果会即时显示在预览栏。选择「模糊玻璃」后可调模糊强度。点「取消外观更改」或关闭设置窗口可恢复已保存值，点「保存任务栏外观」可在下次启动时恢复。展开「场景设置」可单独编辑桌面、普通窗口和最大化场景，也可把当前材质、透明度和模糊值应用到所有场景。

预览栏中间的应用图标使用铺满格子的圆角底板。自带底板的图标用原底色补齐透明边缘，透明标志使用适配明暗的底板。设置窗口的「任务栏外观 → 应用图标大小」统一调节图标和格子，默认 32 DIP，可在 24–36 DIP 之间即时调节并保存。右键应用图标可切换「使用原始图标」或「使用圆角图标」，单个应用的选择会保存。Windows 高对比度模式下使用原始图标。

在「任务栏外观 → 运行标记颜色」可为运行圆点和前台短条选色。选色时即时预览，点「应用颜色」保存，点「跟随系统」恢复 Windows 强调色。Windows 高对比度模式下使用系统前景色。

将鼠标停在正在运行的应用图标上约 400 毫秒，可在图标上方查看该应用的实时窗口缩略图，点击缩略图可激活对应窗口。窗口较多时可翻页。直接点击多窗口应用图标仍可打开原有的窗口标题列表。

可在预览栏内拖动固定应用，松手后将图标放到标记所示的前后位置。固定顺序即时保存，下次启动时恢复。

## 登录启动

当前用户登录后，计划任务「AITaskbar」延迟 20 秒运行 `tools\StartBackground.vbs`，只启动 AITaskbar 并打开双栏对比。启动脚本将执行结果记到 `%LOCALAPPDATA%\AITaskbar\startup.log`。开始菜单中的 AITaskbar 快捷方式可打开设置窗口。

旧版曾通过便携 Windhawk 把 Halo 和 AIEnter 接入 Explorer。升级已有部署时，可对旧运行时执行 `tools\RegisterTaskbar.ps1 -RuntimeRoot .tools\windhawk -Disable`，重启后移除已加载的扩展。

## 源码

- `modules/AIEnter` 保存 AI 窗口、WebView2、截图、拖放和右侧按钮。
- `modules/Halo` 保存中转站、文件库、预览和左侧圆环。
- `modules/LumaBar` 保存外观检测、材质、光效和原生组件。整合版由根目录统一构建与更新。

LumaBar 的 GPL-3.0 许可证、原始 MIT 声明、第三方版权及旧版 ExplorerTAP 对应源码版本见 `modules/LumaBar` 中的许可文件和 `native/README.md`。许可文件随构建产物复制。
