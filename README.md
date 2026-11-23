# 摸鱼阅读器

一个像文字贴图一样使用的 Windows 阅读器。跨应用置顶，鼠标移入时不显示提示文字、黑底信息条或工具栏。

## 运行

下载 [Windows 便携版 v0.1.1](https://github.com/Piz0922/moyu-reader/releases/download/v0.1.1/MoyuReader-v0.1.1-win-x64.zip)，或前往 [Releases](https://github.com/Piz0922/moyu-reader/releases) 查看发布版本。适用于 Windows 10 / 11 x64。

解压便携版，双击 `MoyuReader.exe`。请保留同目录的运行文件，不要只移动 EXE。无需安装 .NET 或开发环境。

首次打开显示书架。拖入 TXT / EPUB，或点击“导入书籍”；单个文件导入后直接阅读，多文件导入后留在书架。阅读区右键可打开书架、导入、目录和设置。

| 操作 | 结果 |
| --- | --- |
| 按住正文拖动 | 移动阅读区域，不选择文字 |
| 拖四角 | 区域和字号等比缩放 |
| 拖四边中间 | 单独改变宽度或高度，文字重新换行 |
| 滚轮 | 连续滚动正文 |
| Ctrl＋滚轮 | 只改变字号 |
| 双击正文 | 隐藏 |
| Ctrl＋Alt＋空格 | 隐藏 / 恢复，可在设置中更改 |
| 单击托盘图标 | 恢复隐藏前的页面 |
| 右键托盘图标 | 打开“书架、设置、退出”菜单 |

拖动边缘时先预览新的阅读区域，拖动四角时文字跟随等比缩放；松开后正文才重新换行，并尽量保持原来的阅读位置。

只有托盘菜单的“退出”才结束程序。关闭书架只会隐藏。Windows 可能将托盘图标收在时钟旁的上箭头中。

## 书籍和数据

程序旁的 `resources` 目录包含：

```text
resources/
  books/       导入电子书的独立副本
  config/      书架和全局设置
  progress/    每本书的阅读进度
```

导入后移动或删除原文件不影响阅读；从书架移除只清理副本及记录，不删除原文件。备份或迁移时，请先退出阅读器，再复制整个程序文件夹。资源目录不可写时会明确报错，不会静默换位置保存。配置采用临时文件替换并保留上一份备份，损坏时会提示恢复结果。

普通 EPUB 保留章节和插图，采用统一字体和配色。第一版不支持 DRM、漫画和固定版式 EPUB；对异常大文件设有导入上限：文件 128 MB、EPUB 解压总量 256 MB、单个资源 32 MB。TXT 自动识别 UTF-8、带 BOM 的 UTF-16/32，非 UTF-8 文本回退至 GB18030。

长篇书籍首次打开或跳到较远位置时需要排版；本机约 1.6 MB 测试正文的打开与中段定位合计约 7 秒。普通滚动使用连续正文，不逐页翻页。

## 源码构建

开发需要 Windows 和 .NET 10 SDK，终端定位到源码根目录：

```powershell
dotnet run --project src/Moyu.Desktop/Moyu.Desktop.csproj
dotnet run --project tests/Moyu.Tests/Moyu.Tests.csproj -c Release
powershell -ExecutionPolicy Bypass -File scripts/build.ps1
```

构建脚本输出 `dist/MoyuReader-win-x64`、便携版 ZIP 和源码 ZIP。已有 `resources` 不会被脚本删除或打包进干净发布包。

```text
src/Moyu.Core/       导入解析、本地书库、缩放几何
src/Moyu.Desktop/    WPF 窗口、托盘、快捷键和设置
tests/Moyu.Tests/    文件导入、数据保护和几何集成检查
scripts/            构建与验证入口
docs/               规格、设计记录、取舍和验证报告
```

可运行 `scripts/verify.ps1` 验证便携包。脚本将程序复制到新的隔离目录，执行真实 WPF 窗口测试并输出界面渲染图，不修改正式版资源目录。不要在个人使用目录直接运行内部 `--smoke-test` 参数，它会导入合成测试书籍。

窗口测试涵盖程序内的 WPF 滚轮事件、文本锚点、页面恢复、EPUB 插图和本地保存；不等同于真实鼠标设备操作。此次桌面自动操作服务不可用，实际拖角、双击手感及跨显示器 DPI 行为仍需要实机试用确认。

详见 [第一版规格](docs/spec.md) 和 [验证记录](docs/validation.md)。
