import QtQuick
import QtQuick.Controls.Basic as Controls

LiteSettingsScroll {
    id: page
    readonly property var state: globalSettings.updates
    readonly property var release: state.latest || ({})
    LiteSettingsCard {
        Item {
            width: parent.width
            height: 54
            Image {
                width: 44
                height: 44
                anchors.verticalCenter: parent.verticalCenter
                source: "qrc:/art/app-icon.png"
                fillMode: Image.PreserveAspectFit
                mipmap: true
            }
            Column {
                x: 60
                width: parent.width - x
                anchors.verticalCenter: parent.verticalCenter
                spacing: 6
                Text {
                    text: "97Launch"
                    font.pixelSize: 20
                    font.weight: Font.DemiBold
                    color: "#454d59"
                }
                LiteSettingsNote {
                    text: "当前版本 " + (page.state.currentVersion || globalSettings.version) + " · 构建 " + (page.state.currentBuild || 0)
                }
            }
        }
        Text {
            width: parent.width
            text: page.state.status || "尚未检查更新"
            color: "#454d59"
            font.pixelSize: 15
            font.weight: Font.DemiBold
            wrapMode: Text.Wrap
        }
        LiteSettingsNote {
            visible: !!text
            text: page.state.detail || ""
        }
        Controls.ProgressBar {
            id: progress
            visible: !!page.state.downloading
            width: parent.width
            height: 5
            from: 0
            to: 100
            value: page.state.progress || 0
            background: Rectangle {
                radius: 2.5
                color: "#edf0f4"
            }
            contentItem: Item {
                Rectangle {
                    width: progress.visualPosition * parent.width
                    height: parent.height
                    radius: 2.5
                    color: "#788393"
                }
            }
        }
        Row {
            spacing: 10
            LiteButton {
                height: 32
                text: page.state.busy && !page.state.downloading ? "处理中…" : "检查更新"
                enabled: !page.state.busy
                onClicked: globalSettings.updateAction("check")
            }
            LiteButton {
                height: 32
                visible: !!page.release.id
                text: page.state.ready ? "重启并更新" : "下载更新"
                enabled: !page.state.busy
                primary: true
                onClicked: globalSettings.updateAction(page.state.ready ? "apply" : "download")
            }
            LiteButton {
                height: 32
                visible: !!page.state.downloading
                text: "取消下载"
                onClicked: globalSettings.updateAction("cancel")
            }
        }
        Rectangle {
            width: parent.width
            height: errorText.implicitHeight + 20
            visible: !!page.state.error
            radius: 7
            color: "#faf2ea"
            border.color: "#f0e2d4"
            Text {
                id: errorText
                x: 10
                y: 10
                width: parent.width - 44
                text: page.state.error || ""
                color: "#a67c57"
                font.pixelSize: 11
                wrapMode: Text.WrapAnywhere
            }
            LiteIconButton {
                anchors.right: parent.right
                y: 4
                width: 28
                height: 28
                iconSize: 12
                source: "qrc:/art/icons/close.svg"
                text: "关闭提示"
                onClicked: globalSettings.clearUpdateError()
            }
        }
    }
    LiteSettingsCard {
        title: "新版本 " + (page.release.version || "")
        visible: !!page.release.id
        Controls.TextArea {
            width: parent.width
            readOnly: true
            selectByMouse: true
            wrapMode: TextEdit.Wrap
            text: page.release.notes || "暂无更新说明。"
            color: "#687384"
            font.pixelSize: 12
            padding: 0
            background: null
        }
    }
    LiteSettingsCard {
        Item {
            width: parent.width
            height: 48
            Column {
                width: parent.width - 176
                spacing: 7
                Text {
                    text: "更新通道"
                    color: "#454d59"
                    font.pixelSize: 12
                }
                LiteSettingsNote {
                    text: "测试版包含提前体验的更新。"
                }
            }
            LiteComboBox {
                anchors.right: parent.right
                anchors.verticalCenter: parent.verticalCenter
                width: 160
                enabled: !page.state.busy
                model: ["稳定版", "测试版"]
                currentIndex: page.state.channel === "stable" ? 0 : 1
                onActivated: globalSettings.configureUpdates(currentIndex === 0 ? "stable" : "preview", !!page.state.startupCheck)
            }
        }
        LiteCheckBox {
            text: "启动时检查更新"
            checked: !!page.state.startupCheck
            enabled: !page.state.busy
            onClicked: globalSettings.configureUpdates(page.state.channel || "preview", checked)
        }
        LiteSettingsNote {
            text: "下载完成后由你选择重启时间，更新会保留游戏实例和个人设置。"
        }
    }
}
