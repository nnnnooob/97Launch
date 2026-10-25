import QtQuick
import QtQuick.Controls.Basic as Controls

LiteSettingsScroll {
    id: page
    readonly property var options: globalSettings.values
    readonly property var memory: globalSettings.memory
    Component.onCompleted: globalSettings.setActive(visible)
    onVisibleChanged: globalSettings.setActive(visible)
    Component.onDestruction: globalSettings.setActive(false)
    LiteSettingsCard {
        title: "启动选项"
        LiteSettingsField {
            label: "版本隔离"
            option: "VersionIsolation"
            choices: ["关闭", "隔离可安装 Mod 的版本", "隔离非正式版", "隔离可安装 Mod 的版本与非正式版", "隔离所有版本"]
        }
        LiteSettingsField {
            label: "游戏窗口标题"
            option: "WindowTitle"
            hint: "留空则使用默认标题"
        }
        LiteSettingsField {
            label: "进程优先级"
            option: "ProcessPriority"
            choices: ["高（优先保证游戏运行）", "中（平衡）", "低（优先保证其他程序运行）"]
        }
        LiteSettingRow {
            label: "窗口大小"
            Row {
                width: parent.width
                spacing: 8
                LiteComboBox {
                    width: page.options.WindowSize === "自定义" ? parent.width - 172 : parent.width
                    model: ["默认", "854 × 480", "1280 × 720", "1920 × 1080", "自定义"]
                    currentIndex: Math.max(0, model.indexOf(page.options.WindowSize))
                    onActivated: globalSettings.setOption("WindowSize", currentText)
                }
                LiteTextField {
                    visible: page.options.WindowSize === "自定义"
                    width: 68
                    height: 32
                    text: page.options.WindowWidth
                    placeholderText: "宽"
                    validator: IntValidator {
                        bottom: 1
                        top: 16384
                    }
                    onEditingFinished: globalSettings.setOption("WindowWidth", text)
                }
                Text {
                    visible: page.options.WindowSize === "自定义"
                    height: 32
                    verticalAlignment: Text.AlignVCenter
                    text: "×"
                    color: "#939ba7"
                }
                LiteTextField {
                    visible: page.options.WindowSize === "自定义"
                    width: 68
                    height: 32
                    text: page.options.WindowHeight
                    placeholderText: "高"
                    validator: IntValidator {
                        bottom: 1
                        top: 16384
                    }
                    onEditingFinished: globalSettings.setOption("WindowHeight", text)
                }
            }
        }
        LiteSettingsField {
            label: "IP 协议偏好"
            option: "IpPreference"
            choices: ["IPv4 优先", "IPv6 优先", "系统默认"]
        }
    }
    LiteSettingsCard {
        title: "游戏内存"
        Controls.ButtonGroup {
            id: memoryMode
        }
        LiteRadioButton {
            text: "自动配置（预计 " + Number(page.memory.automatic || 0).toFixed(1) + " GB）"
            checked: page.options.IsAutoMemory
            Controls.ButtonGroup.group: memoryMode
            onClicked: globalSettings.setOption("IsAutoMemory", true)
        }
        LiteSettingsNote {
            text: "系统可用 " + Number(page.memory.available || 0).toFixed(1) + " / " + Number(page.memory.total || 0).toFixed(1) + " GB" + (page.memory.mods ? " · " + page.memory.mods + " 个 Mod" : "") + (page.memory.low ? " · 内存紧张" : "") + "。自动模式在启动时重新估算。"
        }
        LiteRadioButton {
            text: "自定义"
            checked: !page.options.IsAutoMemory
            Controls.ButtonGroup.group: memoryMode
            onClicked: globalSettings.setOption("IsAutoMemory", false)
        }
        LiteSlider {
            width: parent.width
            enabled: !page.options.IsAutoMemory
            from: 0.5
            to: Math.max(0.5, Number(page.options.MaxMemoryGb))
            stepSize: 0.5
            value: page.options.CustomMemoryGb
            onMoved: globalSettings.setOption("CustomMemoryGb", value)
        }
        LiteSettingsNote {
            text: Number(page.options.CustomMemoryGb).toFixed(1) + " GB"
        }
    }
    LiteSettingsCard {
        title: "高级启动选项"
        Repeater {
            model: [
                {
                    label: "JVM 参数头部",
                    key: "JvmArgsHead",
                    hint: ""
                },
                {
                    label: "游戏参数尾部",
                    key: "GameArgsTail",
                    hint: "例如 --demo"
                },
                {
                    label: "启动前执行命令",
                    key: "PreLaunchCommand",
                    hint: "留空则不执行"
                }
            ]
            LiteSettingRow {
                required property var modelData
                label: modelData.label
                height: 76
                Controls.TextArea {
                    id: input
                    width: parent.width
                    height: parent.height
                    padding: 9
                    wrapMode: TextEdit.Wrap
                    selectByMouse: true
                    text: page.options[modelData.key] || ""
                    placeholderText: modelData.hint
                    placeholderTextColor: "#9ba4b1"
                    color: "#454d59"
                    font.pixelSize: 12
                    background: Rectangle {
                        radius: 7
                        color: input.activeFocus ? "#ffffff" : "#f7f8fa"
                        border.color: input.activeFocus ? "#98a2b1" : "#e2e5ea"
                    }
                    onActiveFocusChanged: {
                        if (!activeFocus && text !== page.options[modelData.key])
                            globalSettings.setOption(modelData.key, text);
                    }
                }
            }
        }
        LiteCheckBox {
            text: "要求 Java 使用高性能显卡"
            checked: page.options.RequireHighPerformanceGpu
            onClicked: globalSettings.setOption("RequireHighPerformanceGpu", checked)
        }
        LiteCheckBox {
            text: "使用 java.exe 而不是 javaw.exe"
            checked: page.options.UseJavaExe
            onClicked: globalSettings.setOption("UseJavaExe", checked)
        }
    }
}
