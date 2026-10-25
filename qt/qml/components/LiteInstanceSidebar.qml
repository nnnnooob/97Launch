import QtQuick
import QtQuick.Templates as T
import QtQuick.Controls.Basic as Controls

Item {
    id: rail
    signal actionRequested(string action, var record)
    property var contextFolder: ({})
    Column {
        width: parent.width
        y: 12
        spacing: 8
        Text {
            x: 18
            text: "文件夹列表"
            color: "#89919d"
            font.pixelSize: 11
        }
        ListView {
            id: folderList
            width: parent.width
            height: Math.min(contentHeight, rail.height - 160)
            model: instanceService.folders
            clip: true
            boundsBehavior: Flickable.StopAtBounds
            Controls.ScrollBar.vertical: Controls.ScrollBar {}
            delegate: T.Button {
                id: folderButton
                required property var modelData
                width: folderList.width
                height: 56
                padding: 0
                hoverEnabled: true
                Accessible.name: modelData.name + "，" + modelData.path
                background: Rectangle {
                    color: folderButton.down ? "#e5e9ef" : folderButton.modelData.selected ? "#edf0f4" : folderButton.hovered ? "#f6f7f9" : "transparent"
                    Rectangle {
                        x: 2
                        y: 12
                        width: 3
                        height: parent.height - 24
                        radius: 1.5
                        color: "#626d7c"
                        visible: folderButton.modelData.selected
                    }
                }
                contentItem: Item {
                    Column {
                        x: 18
                        anchors.verticalCenter: parent.verticalCenter
                        width: parent.width - x - 36
                        spacing: 3
                        Text {
                            width: parent.width
                            text: folderButton.modelData.name
                            font.pixelSize: 13
                            color: folderButton.modelData.exists ? "#454d59" : "#9aa1ac"
                            elide: Text.ElideRight
                        }
                        Text {
                            width: parent.width
                            text: folderButton.modelData.path
                            font.pixelSize: 10
                            color: "#939ba7"
                            elide: Text.ElideMiddle
                        }
                    }
                    LiteIconButton {
                        anchors.right: parent.right
                        anchors.rightMargin: 6
                        anchors.verticalCenter: parent.verticalCenter
                        width: 28
                        height: 28
                        iconSize: 17
                        source: "qrc:/art/icons/more.svg"
                        text: "文件夹操作"
                        revealed: folderHover.hovered
                        onClicked: rail.openMenu(folderButton.modelData, folderButton)
                    }
                }
                HoverHandler {
                    id: folderHover
                }
                MouseArea {
                    anchors.fill: parent
                    acceptedButtons: Qt.RightButton
                    onClicked: rail.openMenu(folderButton.modelData, folderButton)
                }
                onClicked: rail.actionRequested("switch", modelData)
            }
        }
        Text {
            x: 18
            topPadding: 10
            bottomPadding: 2
            text: "添加或导入"
            color: "#89919d"
            font.pixelSize: 11
        }
        Repeater {
            model: [
                {
                    label: "添加已有文件夹",
                    icon: "folder-add",
                    action: "add"
                },
                {
                    label: "导入整合包",
                    icon: "pack-import",
                    action: "import"
                }
            ]
            T.Button {
                id: actionButton
                required property var modelData
                x: 10
                width: rail.width - 20
                height: 36
                padding: 8
                hoverEnabled: true
                Accessible.name: modelData.label
                enabled: modelData.action !== "import" || !instanceService.readingPack
                background: Rectangle {
                    radius: 6
                    color: actionButton.down ? "#e5e9ef" : actionButton.hovered ? "#f1f3f6" : "transparent"
                }
                contentItem: Item {
                    Image {
                        width: 18
                        height: 18
                        anchors.verticalCenter: parent.verticalCenter
                        sourceSize: Qt.size(18, 18)
                        source: "qrc:/art/icons/" + actionButton.modelData.icon + ".svg"
                        fillMode: Image.PreserveAspectFit
                    }
                    Text {
                        x: 28
                        anchors.verticalCenter: parent.verticalCenter
                        text: actionButton.modelData.label
                        color: "#454d59"
                        font.pixelSize: 13
                    }
                }
                onClicked: rail.actionRequested(modelData.action, {})
            }
        }
    }
    function openMenu(record, button) {
        contextFolder = record;
        const point = button.mapToItem(rail, rail.width - folderMenu.width - 8, button.height);
        folderMenu.x = point.x;
        folderMenu.y = Math.max(4, Math.min(point.y, rail.height - folderMenu.implicitHeight - 4));
        folderMenu.open();
    }
    LiteMenu {
        id: folderMenu
        width: 208
        padding: 6
        palette.highlight: "#e7ebf0"
        palette.highlightedText: "#343940"
        LiteMenuItem {
            text: "重命名"
            icon.source: "qrc:/art/icons/rename.svg"
            enabled: rail.contextFolder.custom === true
            onTriggered: rail.actionRequested("rename", rail.contextFolder)
        }
        LiteMenuItem {
            text: "刷新"
            icon.source: "qrc:/art/icons/refresh.svg"
            onTriggered: instanceService.refresh()
        }
        Controls.MenuSeparator {
            padding: 4
            contentItem: Rectangle {
                implicitHeight: 1
                color: "#e9ecf0"
            }
        }
        LiteMenuItem {
            text: "在资源管理器中打开"
            icon.source: "qrc:/art/icons/folder.svg"
            onTriggered: instanceService.openFolder(rail.contextFolder.path)
        }
        LiteMenuItem {
            text: "从列表中移除"
            icon.source: "qrc:/art/icons/trash.svg"
            enabled: rail.contextFolder.custom === true
            onTriggered: rail.actionRequested("remove", rail.contextFolder)
        }
    }
}
