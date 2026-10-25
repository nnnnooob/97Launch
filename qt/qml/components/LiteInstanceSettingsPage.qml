import QtQuick
import QtQuick.Controls.Basic as Controls
import QtQuick.Dialogs
import "LiteMotion.js" as Motion

Item {
    id: page
    signal downloadRequested(string category, string instancePath)
    property string section: "settings"
    property string displayedSection: "settings"
    property real contentScale: 1
    readonly property var options: instanceSettings.values
    onSectionChanged: {
        forceActiveFocus();
        enterMotion.stop();
        exitMotion.restart();
    }
    onDisplayedSectionChanged: Qt.callLater(function () {
        instanceSettings.setMemoryPreviewActive(displayedSection === "settings");
        if (secondary.item && ["settings", "modify", "export"].indexOf(displayedSection) < 0)
            secondary.item.kind = displayedSection;
    })
    function changeSection(value) {
        section = value;
    }
    component Field: Item {
        id: field
        property string label
        property string option
        property string hint
        property var choices: []
        property var choiceValues: choices
        width: parent.width
        height: 34
        Text {
            width: 100
            anchors.verticalCenter: parent.verticalCenter
            text: field.label
            font.pixelSize: 12
            color: "#687384"
        }
        LiteComboBox {
            x: 104
            width: parent.width - x
            height: 32
            visible: field.choices.length > 0
            model: field.choices
            currentIndex: Math.max(0, field.choiceValues.indexOf(String(page.options[field.option] || "")))
            onActivated: instanceSettings.setOption(field.option, field.choiceValues[currentIndex])
        }
        LiteTextField {
            x: 104
            width: parent.width - x
            height: 32
            visible: field.choices.length === 0
            text: String(page.options[field.option] || "")
            placeholderText: field.hint
            font.pixelSize: 12
            onEditingFinished: {
                if (enabled && text !== String(page.options[field.option] || ""))
                    instanceSettings.setOption(field.option, text);
            }
        }
    }
    Flickable {
        id: scroll
        anchors.fill: parent
        anchors.bottomMargin: status.height ? status.height + 8 : 0
        contentWidth: width
        contentHeight: settingsColumn.implicitHeight + 4
        boundsBehavior: Flickable.StopAtBounds
        clip: true
        visible: page.displayedSection === "settings"
        scale: page.contentScale
        Controls.ScrollBar.vertical: Controls.ScrollBar {
            width: 4
            policy: Controls.ScrollBar.AsNeeded
        }
        Column {
            id: settingsColumn
            enabled: !instanceJobs.mutating
            width: scroll.width - 8
            spacing: 12
            Rectangle {
                width: parent.width
                height: 66
                radius: 8
                color: "#ffffff"
                border.color: "#e2e5ea"
                Image {
                    x: 14
                    anchors.verticalCenter: parent.verticalCenter
                    width: 34
                    height: 34
                    sourceSize: Qt.size(34, 34)
                    source: instanceSettings.instance.icon || ""
                    fillMode: Image.PreserveAspectFit
                    smooth: false
                }
                Column {
                    x: 60
                    anchors.verticalCenter: parent.verticalCenter
                    width: parent.width - x - 54
                    spacing: 5
                    Text {
                        width: parent.width
                        text: instanceSettings.instance.name || ""
                        font.pixelSize: 15
                        font.weight: Font.DemiBold
                        color: "#454d59"
                        elide: Text.ElideRight
                    }
                    Text {
                        width: parent.width
                        text: [instanceSettings.instance.version || "", instanceSettings.instance.loader || "原版", "启动 " + (instanceSettings.instance.launchCount || 0) + " 次"].join(" · ")
                        font.pixelSize: 11
                        color: "#939ba7"
                        elide: Text.ElideRight
                    }
                }
                LiteIconButton {
                    anchors.right: parent.right
                    anchors.rightMargin: 10
                    anchors.verticalCenter: parent.verticalCenter
                    width: 28
                    height: 28
                    iconSize: 16
                    source: "qrc:/art/icons/rename.svg"
                    text: "修改实例名称"
                    onClicked: page.section = "modify"
                }
            }
            LiteSettingsCard {
                title: "启动选项"
                LiteCheckBox {
                    width: parent.width
                    text: "跟随全局启动选项"
                    checked: !!page.options.UseGlobalLaunchOptions
                    onClicked: instanceSettings.setOption("UseGlobalLaunchOptions", checked)
                }
                Field {
                    label: "版本隔离"
                    option: "VersionIsolation"
                    enabled: !page.options.UseGlobalLaunchOptions
                    choices: ["开启", "关闭"]
                    choiceValues: ["隔离所有版本", "关闭"]
                }
                Field {
                    label: "游戏窗口标题"
                    option: "WindowTitle"
                    enabled: !page.options.UseGlobalLaunchOptions
                    hint: "留空则使用默认标题"
                }
                Field {
                    label: "进程优先级"
                    option: "ProcessPriority"
                    enabled: !page.options.UseGlobalLaunchOptions
                    choices: ["高（优先保证游戏运行）", "中（平衡）", "低（优先保证其他程序运行）"]
                }
                Field {
                    label: "窗口大小"
                    option: "WindowSize"
                    enabled: !page.options.UseGlobalLaunchOptions
                    choices: ["默认", "854 × 480", "1280 × 720", "1920 × 1080"]
                }
                Field {
                    label: "网络偏好"
                    option: "IpPreference"
                    enabled: !page.options.UseGlobalLaunchOptions
                    choices: ["IPv4 优先", "IPv6 优先", "系统默认"]
                }
            }
            LiteSettingsCard {
                title: "Java 运行时"
                Field {
                    label: "选择方式"
                    option: "JavaMode"
                    choices: ["自动选择并按需下载", "使用实例文件夹中的 Java", "强制指定 Java"]
                }
                Item {
                    width: parent.width
                    height: 34
                    Text {
                        anchors.verticalCenter: parent.verticalCenter
                        text: "指定 Java"
                        font.pixelSize: 12
                        color: "#687384"
                    }
                    LiteComboBox {
                        id: javaChoice
                        x: 104
                        width: parent.width - x - 64
                        height: 32
                        enabled: page.options.JavaMode === "强制指定 Java"
                        model: instanceSettings.javaRuntimes
                        textRole: "name"
                        currentIndex: {
                            const rows = instanceSettings.javaRuntimes;
                            for (let i = 0; i < rows.length; ++i)
                                if (rows[i].path === page.options.JavaPath)
                                    return i;
                            return -1;
                        }
                        displayText: currentIndex < 0 ? "选择已安装的 Java" : currentText
                        onActivated: instanceSettings.setOption("JavaPath", instanceSettings.javaRuntimes[currentIndex].path)
                    }
                    LiteIconButton {
                        x: parent.width - 58
                        width: 28
                        height: 32
                        iconSize: 16
                        text: "添加 Java"
                        source: "qrc:/art/icons/folder-add.svg"
                        onClicked: javaPicker.open()
                    }
                    LiteIconButton {
                        x: parent.width - 28
                        width: 28
                        height: 32
                        iconSize: 16
                        text: "重新查找 Java"
                        source: "qrc:/art/icons/refresh.svg"
                        onClicked: instanceSettings.discoverJava()
                    }
                }
                Text {
                    width: parent.width
                    text: page.options.JavaMode === "使用实例文件夹中的 Java" ? "在实例文件夹中查找兼容的 Java。" : page.options.JavaMode === "强制指定 Java" ? "使用指定的 Java，请确保它与 Minecraft 和加载器兼容。" : "启动时自动选择兼容的 Java，缺少时按需下载。"
                    font.pixelSize: 11
                    color: "#939ba7"
                    wrapMode: Text.Wrap
                }
            }
            LiteSettingsCard {
                title: "游戏内存"
                LiteCheckBox {
                    width: parent.width
                    text: "跟随全局内存设置"
                    checked: !!page.options.UseGlobalMemory
                    onClicked: instanceSettings.setOption("UseGlobalMemory", checked)
                }
                Row {
                    spacing: 18
                    LiteCheckBox {
                        text: "自动分配"
                        checked: !!page.options.IsAutoMemory
                        onClicked: instanceSettings.setOption("IsAutoMemory", true)
                    }
                    LiteCheckBox {
                        text: "自定义"
                        checked: page.options.IsAutoMemory === false
                        onClicked: instanceSettings.setOption("IsAutoMemory", false)
                    }
                    Text {
                        anchors.verticalCenter: parent.verticalCenter
                        text: Number(instanceSettings.memory.allocated || 0).toFixed(1) + " GB"
                        font.pixelSize: 13
                        color: "#454d59"
                    }
                }
                Controls.Slider {
                    id: memorySlider
                    width: parent.width
                    height: 24
                    enabled: page.options.IsAutoMemory === false
                    from: 0.5
                    to: Number(instanceSettings.memory.max || 16)
                    stepSize: 0.5
                    value: Number(page.options.CustomMemoryGb || 3)
                    onMoved: instanceSettings.setOption("CustomMemoryGb", value)
                    background: Rectangle {
                        x: memorySlider.leftPadding
                        y: (memorySlider.height - height) / 2
                        width: memorySlider.availableWidth
                        height: 4
                        radius: 2
                        color: "#e8ebef"
                        Rectangle {
                            width: memorySlider.visualPosition * parent.width
                            height: parent.height
                            radius: 2
                            color: memorySlider.enabled ? "#798493" : "#cdd2da"
                        }
                    }
                    handle: Rectangle {
                        x: memorySlider.leftPadding + memorySlider.visualPosition * (memorySlider.availableWidth - width)
                        y: (memorySlider.height - height) / 2
                        width: 12
                        height: 12
                        radius: 6
                        color: "white"
                        border.color: memorySlider.enabled ? "#798493" : "#cdd2da"
                        border.width: 2
                    }
                }
                Rectangle {
                    width: parent.width
                    height: 7
                    radius: 3.5
                    color: "#eef0f3"
                    clip: true
                    Rectangle {
                        width: parent.width * Math.min(1, Number(instanceSettings.memory.allocated || 0) / Math.max(1, Number(instanceSettings.memory.total || 1)))
                        height: parent.height
                        radius: 3.5
                        color: "#8994a4"
                    }
                }
                Text {
                    width: parent.width
                    text: "系统可用 " + Number(instanceSettings.memory.available || 0).toFixed(1) + " / " + Number(instanceSettings.memory.total || 0).toFixed(1) + " GB" + (instanceSettings.memory.mods ? " · " + instanceSettings.memory.mods + " 个 Mod" : "") + (instanceSettings.memory.low ? " · 内存紧张" : "") + (page.options.IsAutoMemory ? "\n自动模式在启动时重新估算。" : "")
                    font.pixelSize: 11
                    color: instanceSettings.memory.low ? "#a58257" : "#939ba7"
                    wrapMode: Text.Wrap
                }
            }
            LiteSettingsCard {
                title: "高级启动选项"
                LiteCheckBox {
                    width: parent.width
                    text: "跟随全局高级启动选项"
                    checked: !!page.options.UseGlobalAdvancedOptions
                    onClicked: instanceSettings.setOption("UseGlobalAdvancedOptions", checked)
                }
                Field {
                    label: "JVM 参数头部"
                    option: "JvmArgsHead"
                    hint: "额外的 JVM 参数"
                }
                Field {
                    label: "游戏参数尾部"
                    option: "GameArgsTail"
                    hint: "例如 --demo"
                }
                Field {
                    label: "启动前执行命令"
                    option: "PreLaunchCommand"
                    hint: "留空则不执行"
                }
                LiteCheckBox {
                    width: parent.width
                    text: "要求 Java 使用高性能显卡"
                    checked: !!page.options.RequireHighPerformanceGpu
                    onClicked: instanceSettings.setOption("RequireHighPerformanceGpu", checked)
                }
                LiteCheckBox {
                    width: parent.width
                    text: "使用 java.exe 而不是 javaw.exe"
                    checked: !!page.options.UseJavaExe
                    onClicked: instanceSettings.setOption("UseJavaExe", checked)
                }
            }
        }
    }
    Loader {
        id: secondary
        anchors.fill: scroll
        active: page.displayedSection !== "settings"
        source: page.displayedSection === "modify" ? "LiteInstanceModify.qml" : page.displayedSection === "export" ? "LiteInstanceExport.qml" : "LiteInstanceContent.qml"
        scale: page.contentScale
        onLoaded: {
            if (page.displayedSection !== "modify" && page.displayedSection !== "export")
                item.kind = page.displayedSection;
        }
    }
    Connections {
        target: secondary.item
        ignoreUnknownSignals: true
        function onDownloadRequested(category, instancePath) {
            page.downloadRequested(category, instancePath);
        }
    }
    Text {
        id: status
        anchors.bottom: parent.bottom
        width: parent.width - 8
        height: text ? implicitHeight : 0
        text: instanceSettings.error || instanceJobs.error
        font.pixelSize: 11
        color: "#bc6666"
        wrapMode: Text.Wrap
    }
    FileDialog {
        id: javaPicker
        title: "选择 Java 可执行文件"
        nameFilters: ["Java (java.exe javaw.exe)"]
        onAccepted: instanceSettings.addJava(selectedFile)
    }
    SequentialAnimation {
        id: exitMotion
        ParallelAnimation {
            NumberAnimation {
                targets: [scroll, secondary]
                property: "opacity"
                to: 0
                duration: 75
            }
            NumberAnimation {
                target: page
                property: "contentScale"
                to: 0.97
                duration: 75
                easing.type: Easing.InQuad
            }
        }
        ScriptAction {
            script: {
                page.displayedSection = page.section;
                scroll.contentY = 0;
                instanceSettings.clearError();
                instanceJobs.clearError();
                enterMotion.restart();
            }
        }
    }
    ParallelAnimation {
        id: enterMotion
        NumberAnimation {
            targets: [scroll, secondary]
            property: "opacity"
            to: 1
            duration: 100
        }
        NumberAnimation {
            target: page
            property: "contentScale"
            to: 1
            duration: 350
            easing.type: Easing.BezierSpline
            easing.bezierCurve: Motion.outBackWeak
        }
    }
}
