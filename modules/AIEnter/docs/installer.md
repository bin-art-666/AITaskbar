# AIEnter 安装版

运行 `AIEnter-v3.0.0-Setup-win-x64.exe`，选择安装位置后安装。默认安装到当前用户的 LocalAppData/Programs/AIEnter。

安装包包含主程序及预编译任务栏模组，联网下载官方 Windhawk 1.7.3 离线组件包（约 142 MiB），校验固定 SHA256 后以 portable 模式部署到独立 TaskbarRuntime 目录，并生成当前安装位置的模组配置。不会复制开发机的运行配置、缓存或开机任务。

需要 Windows 11 x64 和 Microsoft Edge WebView2 Runtime。可选桌面快捷方式及登录自启；默认不启用自启。快捷方式同时启动任务栏组件与 AIEnter。用户自行登录 AI 网站。

通过 Windows 已安装应用卸载；卸载停止本安装的任务栏组件并删除程序与运行组件，保留 `%LOCALAPPDATA%/AIEnter` 用户数据。Windhawk 的进程为全局单实例，如已有独立 Windhawk 安装，需先关闭后再使用本安装版，当前版本不迁移其他模组。

## 构建

先构建主程序和任务栏模组，再运行 `tools/installer/Build.ps1`。编译器为 Inno Setup 6.7.4（放在 `.tools/inno`），简体中文语言文件来自 Inno Setup 官方仓库。输出位于 `.build/release-v3.0.0`。

安装版未做商业代码签名，Windows 可能显示未知发布者提示。安装时需要能访问 GitHub 下载组件。

验证：在项目隔离目录完成安装，任务栏 ButtonAttached=1、ColorFlowActive=1；卸载返回 0、运行组件目录已移除、隔离用户资料保留。尚未在干净的第二台电脑验证。

卸载时 Explorer 可能暂时锁定 Windhawk 引擎 DLL；安装器会为本安装目录登记一次性清理，在下次注销再登录后删除残留运行目录。清理脚本校验固定目录名、绝对路径、非重解析点及卸载标记，不删除用户截图和资料。
