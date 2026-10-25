import QtQuick
import QtQuick.Templates as T

Item {
    id: navigation
    property int currentIndex: 0
    readonly property var pages: [
        {
            title: "启动",
            icon: "play"
        },
        {
            title: "实例",
            icon: "instances"
        },
        {
            title: "下载",
            icon: "download"
        },
        {
            title: "传输",
            icon: "transfer"
        },
        {
            title: "设置",
            icon: "settings"
        }
    ]
    implicitWidth: 416
    implicitHeight: 40

    Row {
        anchors.fill: parent
        Repeater {
            id: tabs
            model: navigation.pages
            Item {
                id: entry
                required property int index
                required property var modelData
                property alias button: tab
                width: index === 2 ? 96 : 80
                height: navigation.height

                T.Button {
                    id: tab
                    readonly property bool selected: navigation.currentIndex === entry.index
                    onSelectedChanged: {
                        if (selected)
                            selectionBounce.restart();
                        else {
                            selectionBounce.stop();
                            scale = 1;
                        }
                    }
                    SequentialAnimation {
                        id: selectionBounce
                        NumberAnimation {
                            target: tab
                            property: "scale"
                            to: 1.07
                            duration: 100
                            easing.type: Easing.OutQuad
                        }
                        NumberAnimation {
                            target: tab
                            property: "scale"
                            to: 1
                            duration: 190
                            easing.type: Easing.OutBack
                        }
                    }
                    width: 80
                    height: parent.height
                    text: entry.modelData.title
                    hoverEnabled: true
                    focusPolicy: Qt.StrongFocus
                    Accessible.name: text
                    Accessible.role: Accessible.PageTab
                    Accessible.selected: selected
                    onClicked: navigation.currentIndex = entry.index
                    Keys.onLeftPressed: {
                        const previous = (entry.index + navigation.pages.length - 1) % navigation.pages.length;
                        navigation.currentIndex = previous;
                        tabs.itemAt(previous).button.forceActiveFocus(Qt.TabFocusReason);
                    }
                    Keys.onRightPressed: {
                        const next = (entry.index + 1) % navigation.pages.length;
                        navigation.currentIndex = next;
                        tabs.itemAt(next).button.forceActiveFocus(Qt.TabFocusReason);
                    }
                    Keys.onReturnPressed: navigation.currentIndex = entry.index
                    Keys.onEnterPressed: navigation.currentIndex = entry.index
                    background: Item {
                        Rectangle {
                            anchors.centerIn: parent
                            width: parent.width
                            height: 32
                            radius: height / 2
                            color: "#cbd1d8"
                            opacity: tab.selected ? 1 : tab.down ? 0.8 : tab.hovered || tab.visualFocus ? 0.45 : 0
                            Behavior on opacity {
                                NumberAnimation {
                                    duration: tab.selected ? 120 : tab.hovered ? 90 : 150
                                    easing.type: Easing.OutCubic
                                }
                            }
                        }
                        Rectangle {
                            anchors.centerIn: parent
                            width: parent.width
                            height: 32
                            radius: height / 2
                            color: "transparent"
                            border.width: 1
                            border.color: "#8a94a1"
                            visible: tab.visualFocus
                        }
                    }
                    contentItem: Item {
                        Row {
                            anchors.centerIn: parent
                            spacing: 6
                            Image {
                                anchors.verticalCenter: parent.verticalCenter
                                width: 16
                                height: 16
                                sourceSize: Qt.size(16, 16)
                                source: "qrc:/art/icons/" + entry.modelData.icon + ".svg"
                                opacity: tab.selected ? 1 : tab.hovered ? 0.9 : 0.7
                                Behavior on opacity {
                                    NumberAnimation {
                                        duration: 120
                                    }
                                }
                            }
                            Text {
                                text: tab.text
                                font.pixelSize: 13
                                font.weight: tab.selected ? Font.DemiBold : Font.Normal
                                color: tab.selected ? "#303640" : tab.hovered ? "#454d59" : "#737d8a"
                                Behavior on color {
                                    ColorAnimation {
                                        duration: 120
                                    }
                                }
                            }
                        }
                    }
                }
                Rectangle {
                    visible: entry.index === 2
                    x: 87
                    anchors.verticalCenter: parent.verticalCenter
                    width: 1
                    height: 20
                    color: "#c9ced5"
                }
            }
        }
    }
}
