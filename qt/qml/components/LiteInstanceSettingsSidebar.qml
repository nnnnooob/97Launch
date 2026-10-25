import QtQuick
import QtQuick.Templates as T
import QtQuick.Controls.impl as Impl

Item {
    id: sidebar
    signal sectionRequested(string section)
    property string section: "settings"
    property bool showOpenDirectory: true
    property var sections: [
        {
            title: "游戏本体"
        },
        {
            key: "settings",
            title: "设置",
            icon: "settings"
        },
        {
            key: "modify",
            title: "修改",
            icon: "rename"
        },
        {
            key: "export",
            title: "导出",
            icon: "pack-export"
        },
        {
            title: "游戏资源"
        },
        {
            key: "saves",
            title: "存档",
            icon: "world"
        },
        {
            key: "screenshots",
            title: "截图",
            icon: "image"
        },
        {
            key: "mods",
            title: "Mod",
            icon: "puzzle"
        },
        {
            key: "resourcepacks",
            title: "资源包",
            icon: "instances"
        },
        {
            key: "shaderpacks",
            title: "光影包",
            icon: "sun"
        }
    ]
    Flickable {
        width: parent.width
        height: parent.height - (sidebar.showOpenDirectory ? 42 : 0)
        contentHeight: items.implicitHeight + 12
        boundsBehavior: Flickable.StopAtBounds
        clip: true
        Column {
            id: items
            y: 8
            width: parent.width
            Repeater {
                model: sidebar.sections
                Item {
                    required property var modelData
                    width: items.width
                    height: modelData.key ? 34 : 28
                    Text {
                        visible: !modelData.key
                        x: 16
                        anchors.verticalCenter: parent.verticalCenter
                        text: modelData.title
                        font.pixelSize: 11
                        color: "#939ba7"
                    }
                    T.Button {
                        id: tab
                        visible: !!modelData.key
                        anchors.fill: parent
                        padding: 0
                        hoverEnabled: true
                        property bool selected: sidebar.section === modelData.key
                        Accessible.name: modelData.title
                        background: Rectangle {
                            color: tab.selected ? "#edf0f4" : tab.down ? "#e7ebf0" : tab.hovered ? "#f6f7f9" : "transparent"
                            Rectangle {
                                width: 3
                                height: 18
                                radius: 1.5
                                anchors.verticalCenter: parent.verticalCenter
                                color: "#687384"
                                visible: tab.selected
                            }
                        }
                        contentItem: Item {
                            Impl.IconImage {
                                x: 18
                                anchors.verticalCenter: parent.verticalCenter
                                width: 17
                                height: 17
                                sourceSize: Qt.size(17, 17)
                                source: modelData.icon ? "qrc:/art/icons/" + modelData.icon + ".svg" : ""
                                color: tab.selected ? "#454d59" : "#7f8997"
                            }
                            Text {
                                x: 46
                                anchors.verticalCenter: parent.verticalCenter
                                text: modelData.title
                                color: tab.selected ? "#454d59" : "#6c7685"
                                font.pixelSize: 13
                            }
                        }
                        onClicked: sidebar.sectionRequested(modelData.key)
                    }
                }
            }
        }
    }
    LiteButton {
        visible: sidebar.showOpenDirectory
        x: 14
        y: parent.height - 34
        width: parent.width - 28
        height: 28
        text: "打开实例目录"
        onClicked: instanceSettings.openDirectory()
    }
}
