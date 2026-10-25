# 拟态 UI 参考与审核

检索日期：2026-10-24。以下图片是项目作者提供的原图，不是 97Launch 新版截图。

## 推荐视觉参考：Themesberg Neumorphism UI

- GitHub：https://github.com/themesberg/neumorphism-ui-bootstrap
- 在线演示：https://demo.themesberg.com/neumorphism-ui/
- 组件演示：https://demo.themesberg.com/neumorphism-ui/html/components/all.html
- 许可证：仓库标注 MIT。若后续复制代码或素材，保留对应声明，并单独核对素材许可证。
- 技术：Bootstrap / HTML / Sass；作为视觉参考，不能直接导入 QML。

![作者提供的组件预览](https://themesberg.s3.us-east-2.amazonaws.com/public/products/neumorphism-ui/github/all-components.jpg)

适合借鉴浅灰同色表面、轻微凸起的按钮、内凹输入框、滑块及开关。建议用于 97Launch 时保留蓝色主操作和清晰的文字、焦点与状态颜色。

## Qt 原生参考：0smr/neumorphism

- GitHub：https://github.com/0smr/neumorphism
- 许可证：MIT。
- Qt Quick / QML 的拟态组件集，覆盖按钮、输入框、滑块、进度条、下拉框等。
- README 明确注明其着色器目前不兼容 Qt 6，不能据此认定可直接作为新版依赖。
- 定位：外观和交互参考，待选定方向后再评估具体实现。

![作者提供的 Qt 拟态预览](https://github.com/0smr/neumorphism/assets/51415059/40294c6d-3a9c-4388-9967-c02b7bc4aa4a)

## Qt 6 实现依据

Qt 官方已提供 RectangularShadow 的 NeumorphicPanel 示例：
https://doc.qt.io/qt-6/qtquick-multieffect-neumorphicpanel-example.html

选定外观后优先用 Qt 6 支持的原生控件和阴影 API 实现。性能需在实际主页、卡片列表和皮肤模型同时存在时测量；没有对上述样式作性能承诺。

## 拟用于启动器的处理（待审核）

- 浅灰蓝色底，文字保持深色，沿用 97Launch 蓝色主按钮。
- 左侧导航的选中项轻微内凹；启动按钮在按下时由凸起转为内凹。
- 服务器卡片只用一层柔和外阴影，卡片内信息区保持简洁。
- 皮肤预览的背景以 MC 场景为主，避免再叠加强烈的拟态装饰。
- 先确定视觉方向，再制作 Qt 主页原型。当前未构建、未执行功能测试，也未实际运行上述第三方组件库。
