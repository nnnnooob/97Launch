import QtQuick
import QtQuick.Controls.Basic as Controls

LiteSettingsScroll {
    id: page
    readonly property var preset: globalSettings.values.GameDefaults || ({})
    property string captureKey: ""
    readonly property bool conflict: preset.SprintKey === preset.SneakKey || preset.SprintKey === preset.PerspectiveKey || preset.SneakKey === preset.PerspectiveKey
    Column {
        width: parent.width
        spacing: 6
        Text {
            text: "默认游戏设置"
            color: "#454d59"
            font.pixelSize: 20
            font.weight: Font.DemiBold
        }
        LiteSettingsNote {
            text: "为新实例准备熟悉的画面与操作习惯。"
        }
    }
    LiteSettingsCard {
        Column {
            width: parent.width
            spacing: 8
            Text {
                width: parent.width
                text: "新实例首次启动时应用默认游戏设置"
                color: "#454d59"
                font.pixelSize: 12
                wrapMode: Text.Wrap
            }
            Controls.Switch {
                id: enabledSwitch
                padding: 0
                implicitHeight: 26
                checked: !!page.preset.Enabled
                onClicked: globalSettings.setPreset("Enabled", checked)
                indicator: Rectangle {
                    width: 36
                    height: 20
                    radius: 10
                    color: enabledSwitch.checked ? "#788393" : "#e2e5ea"
                    Rectangle {
                        x: enabledSwitch.checked ? 18 : 2
                        y: 2
                        width: 16
                        height: 16
                        radius: 8
                        color: "#ffffff"
                        Behavior on x {
                            NumberAnimation {
                                duration: 100
                                easing.type: Easing.OutCubic
                            }
                        }
                    }
                }
                contentItem: Text {
                    leftPadding: 44
                    height: 26
                    verticalAlignment: Text.AlignVCenter
                    text: enabledSwitch.checked ? "开" : "关"
                    color: "#778292"
                    font.pixelSize: 11
                }
            }
        }
        LiteSettingsNote {
            text: "仅在首次启动前应用一次，之后在游戏中修改不会被覆盖。已有实例保持原设置。"
        }
    }
    LiteSettingsCard {
        title: "画面与鼠标"
        enabled: !!page.preset.Enabled
        opacity: enabled ? 1 : 0.5
        Repeater {
            model: [
                {
                    label: "视野（°）",
                    key: "FieldOfView",
                    min: 30,
                    max: 110
                },
                {
                    label: "亮度（%）",
                    key: "Brightness",
                    min: 0,
                    max: 100
                },
                {
                    label: "渲染距离（区块）",
                    key: "RenderDistance",
                    min: 2,
                    max: 32
                },
                {
                    label: "鼠标灵敏度（%）",
                    key: "MouseSensitivity",
                    min: 0,
                    max: 200
                }
            ]
            LiteSettingRow {
                required property var modelData
                label: modelData.label
                labelWidth: 140
                LiteSlider {
                    width: parent.width - 106
                    from: modelData.min
                    to: modelData.max
                    value: Number(page.preset[modelData.key])
                    onMoved: globalSettings.setPreset(modelData.key, value)
                }
                LiteSpinBox {
                    anchors.right: parent.right
                    width: 96
                    from: modelData.min
                    to: modelData.max
                    value: Number(page.preset[modelData.key])
                    onValueModified: globalSettings.setPreset(modelData.key, value)
                }
            }
        }
        LiteSettingsNote {
            text: "渲染距离越高，游戏对内存和图形性能的要求越高。"
        }
    }
    LiteSettingsCard {
        title: "按键"
        enabled: !!page.preset.Enabled
        opacity: enabled ? 1 : 0.5
        Repeater {
            model: [
                {
                    label: "疾跑",
                    key: "SprintKey"
                },
                {
                    label: "潜行",
                    key: "SneakKey"
                },
                {
                    label: "切换视角",
                    key: "PerspectiveKey"
                }
            ]
            LiteSettingRow {
                required property var modelData
                label: modelData.label
                LiteButton {
                    height: 32
                    anchors.right: parent.right
                    width: 138
                    text: globalSettings.keyLabel(page.preset[modelData.key] || "")
                    onClicked: {
                        page.captureKey = modelData.key;
                        capture.open();
                    }
                }
            }
        }
        LiteSettingsNote {
            text: "点击键帽后按下单个按键，支持左右修饰键和鼠标侧键；Esc 取消。"
        }
        LiteSettingsNote {
            visible: page.conflict
            color: "#b27d53"
            text: "有多个操作使用了相同按键，请检查按键设置。"
        }
    }
    Row {
        spacing: 10
        LiteButton {
            height: 32
            text: "从实例读取"
            onClicked: readDialog.open()
        }
        LiteButton {
            height: 32
            text: "恢复默认值"
            onClicked: globalSettings.resetPreset()
        }
    }
    LiteSettingsNote {
        text: "设置会自动保存，仅用于之后新安装的实例。"
    }
    LiteSettingsNote {
        text: "支持 Minecraft 1.7.2 及以上版本；不支持的设置会自动跳过。"
    }
    LitePopup {
        id: capture
        width: 360
        height: 132
        padding: 20
        closePolicy: Controls.Popup.CloseOnEscape
        contentItem: Item {
            focus: true
            Column {
                width: parent.width
                spacing: 12
                Text {
                    text: "请按下要绑定的按键"
                    color: "#454d59"
                    font.pixelSize: 15
                }
                LiteSettingsNote {
                    text: "支持键盘及鼠标按键，按 Esc 取消。"
                }
            }
            MouseArea {
                anchors.fill: parent
                acceptedButtons: Qt.LeftButton | Qt.RightButton | Qt.MiddleButton | Qt.BackButton | Qt.ForwardButton
                onClicked: function (mouse) {
                    const keys = {};
                    keys[Qt.LeftButton] = "left";
                    keys[Qt.RightButton] = "right";
                    keys[Qt.MiddleButton] = "middle";
                    keys[Qt.BackButton] = "4";
                    keys[Qt.ForwardButton] = "5";
                    globalSettings.setPreset(page.captureKey, "key.mouse." + keys[mouse.button]);
                    capture.close();
                }
            }
        }
        onOpened: {
            contentItem.forceActiveFocus();
            globalSettings.captureKey(true);
        }
        onClosed: globalSettings.captureKey(false)
    }
    Connections {
        target: globalSettings
        function onKeyCaptured(key) {
            if (!capture.opened)
                return;
            if (key)
                globalSettings.setPreset(page.captureKey, key);
            capture.close();
        }
    }
    Component.onDestruction: globalSettings.captureKey(false)
    LitePopup {
        id: readDialog
        width: 400
        padding: 20
        contentItem: Column {
            spacing: 14
            Text {
                text: "从实例读取"
                color: "#454d59"
                font.pixelSize: 16
                font.weight: Font.DemiBold
            }
            LiteSettingsNote {
                text: "选择一个已有游戏设置的实例，读取画面、鼠标和按键设置。"
            }
            LiteComboBox {
                id: sourceInstance
                width: parent.width
                model: instanceService.available
                textRole: "name"
            }
            LiteSettingsNote {
                visible: !instanceService.available.length
                text: "暂无可读取的实例。"
            }
            Row {
                anchors.right: parent.right
                spacing: 10
                LiteButton {
                    height: 32
                    text: "取消"
                    onClicked: readDialog.close()
                }
                LiteButton {
                    height: 32
                    text: "读取"
                    primary: true
                    enabled: sourceInstance.currentIndex >= 0 && instanceService.available.length > 0
                    onClicked: {
                        globalSettings.readPreset(instanceService.available[sourceInstance.currentIndex].path);
                        readDialog.close();
                    }
                }
            }
        }
    }
}
