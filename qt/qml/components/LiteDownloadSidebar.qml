import QtQuick
import QtQuick.Controls.Basic as Controls

Item {
    id: sidebar
    property string section: "minecraft"
    signal sectionRequested(string section)
    readonly property var sections: [
        {
            key: "minecraft",
            title: "Minecraft",
            icon: "minecraft"
        },
        {
            key: "mod",
            title: "Mod",
            icon: "mod"
        },
        {
            key: "modpack",
            title: "整合包",
            icon: "package"
        },
        {
            key: "shader",
            title: "光影包",
            icon: "shader"
        },
        {
            key: "resourcepack",
            title: "资源包",
            icon: "resourcepack"
        },
        {
            key: "datapack",
            title: "数据包",
            icon: "datapack"
        }
    ]
    Column {
        width: parent.width
        y: 10
        Text {
            x: 16
            height: 28
            text: "下载"
            color: "#939ba7"
            font.pixelSize: 11
        }
        Repeater {
            model: sidebar.sections
            Controls.AbstractButton {
                id: button
                required property var modelData
                width: sidebar.width
                height: 38
                hoverEnabled: true
                property bool selected: sidebar.section === modelData.key
                Accessible.name: modelData.title
                background: Rectangle {
                    color: button.selected ? "#edf0f4" : button.down ? "#e7ebf0" : button.hovered ? "#f6f7f9" : "transparent"
                    Rectangle {
                        width: 3
                        height: 18
                        radius: 1.5
                        anchors.verticalCenter: parent.verticalCenter
                        color: "#687384"
                        visible: button.selected
                    }
                }
                contentItem: Item {
                    Image {
                        x: 16
                        anchors.verticalCenter: parent.verticalCenter
                        width: 20
                        height: 20
                        source: "qrc:/art/icons/download-" + modelData.icon + ".svg"
                        fillMode: Image.PreserveAspectFit
                        sourceSize: Qt.size(20, 20)
                        smooth: true
                    }
                    Text {
                        x: 48
                        anchors.verticalCenter: parent.verticalCenter
                        text: modelData.title
                        font.pixelSize: 13
                        color: button.selected ? "#454d59" : "#7f8998"
                    }
                }
                onClicked: sidebar.sectionRequested(modelData.key)
            }
        }
    }
}
