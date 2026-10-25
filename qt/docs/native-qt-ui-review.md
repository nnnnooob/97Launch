# 原生 Qt UI 候选

检索日期：2026-10-24。以下为原项目截图，当前未运行这些组件库，也未作性能对比。

## ElaWidgetTools（优先审核）

- GitHub：https://github.com/Liniyous/ElaWidgetTools
- C++ / Qt Widgets，支持 Qt 6，MIT。
- GitHub API 核对最新提交：2026-10-09（UTC），aae58f5。
- 导航、卡片、搜索、弹窗、消息提示和主题管理较完整，适合启动器。

![ElaWidgetTools 原项目浅色预览](https://raw.githubusercontent.com/Liniyous/ElaWidgetTools/main/doc/preview/ElaWidgetTools_Light.png)

## Qlementine

- GitHub：https://github.com/oclero/qlementine
- C++ / Qt Widgets 的 QStyle 与补充控件，Qt 6.8+，MIT。
- GitHub API 核对最新提交：2026-10-05（UTC），7825161。
- 界面较简洁，适合偏克制的桌面应用；导航与启动器卡片需自行组织。

![Qlementine 原项目 Windows 浅色预览](https://raw.githubusercontent.com/oclero/qlementine/dev/branding/screenshots/windows-light.png)

## 若继续采用 QML

Qt 官方 FluentWinUI3 样式从 Qt 6.8 起提供：
https://doc.qt.io/qt-6/qtquickcontrols-fluentwinui3.html

该样式由 Qt Quick 绘制，部分控件仍回退为 Fusion 样式。它提供基础控件外观，完整导航、卡片和启动器布局需要另行实现。

## 选择影响

ElaWidgetTools 和 Qlementine 均为原生 Qt Widgets 路线，不能直接当作现有 QML 控件导入。待用户审核视觉后，再决定采用 Widgets 或保持 Qt Quick / QML。
