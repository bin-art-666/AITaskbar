# AIEnter v1.3.0

本版将当前已保存的 AIEnter 偏好整理为新安装默认设置。

- 默认 AI 顺序为 DeepSeek、ChatGPT、豆包、小艺、Gemini、Grok、Kimi；默认服务 DeepSeek。
- 默认使用悬浮模式，不置顶；面板默认 388.5 × 829 DIP，右下角定位。
- 默认任务栏图标 25 DIP，光晕范围 6 DIP、强度 100%；保留已保存的按钮尺寸、圆角、背板、颜色与网页圆角。
- 默认快捷键为 Alt+Space；UI 风格使用默认预设。
- Gemini 默认网址去掉了查询参数；本机窗口坐标、聊天、账号和网页登录资料不进入发行包。

下载 `AIEnter-v1.3.0-win-x64.zip`，完整解压后运行 `AIEnter.exe`。程序包包含 .NET／Windows App SDK 运行依赖；仍需 WebView2 Runtime。时间右侧任务栏集成需要另行安装 Windhawk，并导入 `aienter-taskbar-ai.wh.cpp`，配置 executable 为程序完整路径。使用步骤见包内 `README-使用说明.md`。

新默认仅用于没有既有 AI 配置的安装以及“恢复全部默认”。升级时不会覆盖已有设置。发行包不注册开机自启，也不包含本机设置或登录资料。
