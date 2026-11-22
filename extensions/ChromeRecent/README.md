# Chrome 最近关闭

这是适用于 Windows x64 的可选组件。运行 `extensions/EdgeRecent/BuildPackage.ps1 -Browser Chrome` 可生成独立组件压缩包。

解压后，运行 `Install.cmd`。在要连接的 Chrome 配置中打开 `chrome://extensions`，开启开发者模式并加载包内的 `Browser` 文件夹。在 AITaskbar 的「任务栏设置 → 图标设置」确认「显示 Chrome 最近关闭」已开启。此开关默认开启，与「显示最近文件」各自独立。

卸载时运行 `Uninstall.cmd`，再从 Chrome 的扩展管理页移除扩展。AITaskbar 只在打开菜单时查询会话，最多显示五条，无痕标签和网页历史不会另外保存。
