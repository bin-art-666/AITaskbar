# AIEnter v1.0.0

首个正式版本，面向 Windows 11 x64。

- 时间日期右侧真实任务栏入口：左键切换显示／收起，右键设置，支持拖入内容。
- 默认提供 DeepSeek、ChatGPT、豆包、小艺、Gemini、Grok、Kimi，可添加更多 HTTPS AI 网页并管理排序。
- 支持悬浮与独立窗口、右下角定位、固定／置顶，保留已打开网页和草稿。
- 上浮淡入／下沉淡出动画；按钮、圆角、颜色、背板、图标大小与光晕可调。
- 任务栏圆形按钮紧凑占位，26 DIP 默认图标仅占 30 × 30 DIP。
- 文件、图片、链接与文字交给当前 AI，不自动发送；无法接收时保留内容重试。

下载 AIEnter-v1.0.0-win-x64.zip，完整解压后运行 AIEnter.exe。压缩包包含 .NET／Windows App SDK 依赖；仍需 WebView2 Runtime。时间右侧任务栏集成需要另外安装 Windhawk 并导入包内 Taskbar/aienter-taskbar-ai.wh.cpp，配置 executable 为程序完整路径。使用步骤见包内 README-使用说明.md。

未包含已取消的自定义图标替换功能，不包含开发工具、本机设置或登录数据，不自动注册开机自启。Windows 版本与 AI 网站变化可能影响任务栏集成和附件接收。
