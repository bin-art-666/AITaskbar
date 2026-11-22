# Edge 最近关闭

这是需要分别启用的可选组件。适用于 64 位 Windows，需要支持「显示 Edge 最近关闭」的 AITaskbar。

## 安装

1. 将压缩包解压到要长期保留的目录。运行 `Install.cmd`，为当前 Windows 用户注册主机。
2. 在要连接的 Edge 配置中打开 `edge://extensions`，开启开发人员模式，通过「加载解压缩的扩展」选择压缩包内的 `Browser` 文件夹。
3. 在 AITaskbar 的「任务栏设置 → 图标设置」确认「显示 Edge 最近关闭」已开启。新配置默认开启。

## 卸载

运行 `Uninstall.cmd` 断开主机。下次打开 Edge 的任务栏右键菜单时，「最近关闭」会隐藏。再在 `edge://extensions` 删除扩展，然后可移除解压目录。

单个 Windows 用户下，主机一次服务一个已连接的 Edge 配置。扩展仅在收到菜单请求时读取会话，只发送网页标题和会话 ID。AITaskbar 不另外保存浏览历史。
