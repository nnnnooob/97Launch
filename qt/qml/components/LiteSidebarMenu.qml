import QtQuick
import QtQuick.Templates as T

Item {
    id: menu
    property int pageIndex: 1
    property int currentIndex: 0
    signal activated(int index)
    readonly property var titles: ["", "游戏文件夹", "下载分类", "", "设置分类"]
    readonly property var sections: [[], [
            {
                title: "内置文件夹",
                detail: ".minecraft",
                icon: "folder"
            },
            {
                title: "整合包",
                detail: "整合包游戏目录",
                icon: "folder"
            },
            {
                title: "其他文件夹",
                detail: "自定义游戏目录",
                icon: "folder"
            }
        ], [
            {
                title: "Minecraft",
                detail: "",
                icon: "instances"
            },
            {
                title: "整合包",
                detail: "",
                icon: "folder"
            },
            {
                title: "资源包",
                detail: "",
                icon: "download"
            },
            {
                title: "光影",
                detail: "",
                icon: "download"
            }
        ], [], [
            {
                title: "常规",
                detail: "",
                icon: "settings"
            },
            {
                title: "游戏与 Java",
                detail: "",
                icon: "instances"
            },
            {
                title: "下载",
                detail: "",
                icon: "download"
            },
            {
                title: "外观",
                detail: "",
                icon: "settings"
            }
        ]]
    readonly property var entries: sections[pageIndex]
    readonly property string selectedTitle: entries.length > currentIndex ? entries[currentIndex].title : ""

    Text {
        x: 18
        y: 18
        text: menu.titles[menu.pageIndex]
        font.pixelSize: 12
        color: "#89919d"
    }
    Column {
        x: 12
        y: 48
        width: parent.width - 24
        spacing: 6
        Repeater {
            model: menu.entries
            T.Button {
                id: menuButton
                required property int index
                required property var modelData
                readonly property bool selected: menu.currentIndex === index
                width: parent.width
                height: modelData.detail.length > 0 ? 54 : 40
                hoverEnabled: true
                focusPolicy: Qt.StrongFocus
                Accessible.name: modelData.title
                Accessible.description: modelData.detail
                Accessible.role: Accessible.PageTab
                Accessible.selected: selected
                onClicked: menu.activated(index)
                background: Rectangle {
                    radius: 6
                    color: menuButton.selected ? "#e9ecf1" : menuButton.down ? "#e1e5eb" : menuButton.hovered ? "#f0f2f5" : "transparent"
                    border.width: menuButton.visualFocus ? 1 : 0
                    border.color: "#929aa6"
                    Behavior on color {
                        ColorAnimation {
                            duration: 100
                        }
                    }
                    Rectangle {
                        x: 0
                        anchors.verticalCenter: parent.verticalCenter
                        width: 3
                        height: 18
                        radius: 1.5
                        color: "#697483"
                        visible: menuButton.selected
                    }
                }
                contentItem: Item {
                    Image {
                        x: 12
                        anchors.verticalCenter: parent.verticalCenter
                        width: 16
                        height: 16
                        sourceSize: Qt.size(16, 16)
                        source: "qrc:/art/icons/" + menuButton.modelData.icon + ".svg"
                        opacity: menuButton.selected ? 1 : 0.7
                    }
                    Column {
                        x: 36
                        anchors.verticalCenter: parent.verticalCenter
                        width: parent.width - x - 10
                        spacing: 3
                        Text {
                            width: parent.width
                            text: menuButton.modelData.title
                            font.pixelSize: 13
                            font.weight: menuButton.selected ? Font.DemiBold : Font.Normal
                            color: "#454d59"
                            elide: Text.ElideRight
                        }
                        Text {
                            width: parent.width
                            visible: text.length > 0
                            text: menuButton.modelData.detail
                            font.pixelSize: 10
                            color: "#89919d"
                            elide: Text.ElideRight
                        }
                    }
                }
            }
        }
    }
}
