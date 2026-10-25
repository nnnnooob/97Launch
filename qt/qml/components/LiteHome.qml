import QtQuick
import QtQuick.Controls.Basic as Controls

Item {
    id: home
    signal settingsRequested(string path)

    readonly property var status: launchStatus.values
    Component.onCompleted: launchStatus.setActive(visible)
    Component.onDestruction: launchStatus.setActive(false)
    onVisibleChanged: launchStatus.setActive(visible)

    Rectangle {
        id: instance
        x: 0
        y: 0
        width: parent.width
        height: 116
        radius: 8
        color: "#ffffff"
        border.color: "#e2e5ea"
        Rectangle {
            x: 12
            y: 12
            width: 40
            height: 40
            radius: 8
            color: "#eceef2"
            Text {
                anchors.centerIn: parent
                visible: !instanceService.current.icon
                text: "MC"
                font.pixelSize: 12
                font.bold: true
                color: "#6c7583"
            }
            Image {
                anchors.centerIn: parent
                width: 30
                height: 30
                source: instanceService.current.icon || ""
                sourceSize: Qt.size(30, 30)
                fillMode: Image.PreserveAspectFit
                smooth: false
            }
        }
        Row {
            x: 64
            y: 12
            width: parent.width - 76
            spacing: 6
            Rectangle {
                anchors.verticalCenter: parent.verticalCenter
                width: 6
                height: 6
                radius: 3
                color: home.status.ready || gameLaunch.starting || gameLaunch.sessions.length > 0 ? "#828a96" : "#a8afb9"
            }
            Text {
                width: parent.width - 12
                text: {
                    const current = gameLaunch.sessions.find(s => s.instancePath.toLowerCase() === (instanceService.current.path || "").toLowerCase());
                    return current ? (current.state === 0 ? "启动中" : "运行中") : home.status.readiness || "未选择实例";
                }
                font.pixelSize: 11
                color: "#7a8390"
                elide: Text.ElideRight
                LiteToolTip {
                    visible: readinessHover.hovered
                    delay: 500
                    text: parent.text + "\n" + (home.status.javaHint || "")
                }
                HoverHandler {
                    id: readinessHover
                }
            }
        }
        Text {
            x: 64
            y: 30
            width: parent.width - 76
            text: instanceService.current.name || "前往实例页选择游戏"
            font.pixelSize: 16
            font.weight: Font.DemiBold
            color: "#303640"
            elide: Text.ElideRight
        }
        Flow {
            id: metadata
            x: 12
            y: 61
            width: parent.width - 24
            spacing: 5
            Repeater {
                model: [
                    {
                        text: home.status.gameVersion || "—",
                        kind: "game"
                    },
                    {
                        text: home.status.loaderDisplay || "—",
                        kind: "loader"
                    },
                    {
                        text: home.status.javaVersion || "暂无对应 Java",
                        kind: "java"
                    },
                    {
                        text: "版本隔离",
                        kind: "isolation"
                    }
                ]
                Rectangle {
                    id: badge
                    required property var modelData
                    readonly property bool java: modelData.kind === "java"
                    readonly property bool isolation: modelData.kind === "isolation"
                    readonly property real maximumWidth: modelData.kind === "game" || modelData.kind === "loader" ? (metadata.width - 5) / 2 : (metadata.width - 5) * (java ? 0.68 : 0.32)
                    width: Math.min(maximumWidth, badgeText.implicitWidth + 14 + (isolation ? 11 : java ? 16 : 0))
                    height: 20
                    radius: 10
                    color: "#eef0f3"
                    Image {
                        x: 7
                        anchors.verticalCenter: parent.verticalCenter
                        width: 11
                        height: 11
                        source: badge.java ? "qrc:/art/icons/coffee.svg" : ""
                        visible: badge.java
                    }
                    Text {
                        id: badgeText
                        x: badge.java ? 23 : 7
                        anchors.verticalCenter: parent.verticalCenter
                        width: parent.width - x - 7 - (badge.isolation ? 11 : 0)
                        text: badge.modelData.text
                        font.pixelSize: 11
                        color: "#717b89"
                        elide: Text.ElideRight
                    }
                    Rectangle {
                        visible: badge.isolation
                        anchors.right: parent.right
                        anchors.rightMargin: 7
                        anchors.verticalCenter: parent.verticalCenter
                        width: 6
                        height: 6
                        radius: 3
                        color: home.status.isolationEnabled ? "#339454" : "#d66b6b"
                    }
                    LiteToolTip {
                        visible: badgeHover.hovered
                        delay: 500
                        text: badge.java ? home.status.javaHint || modelData.text : badge.isolation ? "版本隔离" + (home.status.isolationEnabled ? "已开启" : "已关闭") : modelData.text
                    }
                    HoverHandler {
                        id: badgeHover
                    }
                }
            }
        }
    }

    LiteButton {
        x: 0
        y: 126
        width: parent.width - 98
        height: 44
        text: gameLaunch.starting ? "取消启动" : "启动实例"
        primary: true
        enabled: gameLaunch.starting || gameLaunch.canLaunch
        onClicked: gameLaunch.starting ? gameLaunch.cancel() : gameLaunch.start()
        contentItem: Item {
            Row {
                anchors.centerIn: parent
                spacing: 8
                Image {
                    anchors.verticalCenter: parent.verticalCenter
                    width: 18
                    height: 18
                    sourceSize: Qt.size(18, 18)
                    source: "qrc:/art/icons/play-white.svg"
                }
                Text {
                    text: gameLaunch.starting ? "取消启动" : "启动实例"
                    font.pixelSize: 15
                    font.weight: Font.DemiBold
                    color: "#ffffff"
                }
            }
        }
    }
    LiteButton {
        x: parent.width - 88
        y: 126
        width: 88
        height: 44
        text: "实例设置"
        enabled: !!instanceService.current.path
        onClicked: home.settingsRequested(instanceService.current.path)
    }

    Row {
        x: 0
        y: 180
        width: parent.width
        height: 50
        spacing: 8
        Repeater {
            model: [
                {
                    caption: "已有实例",
                    value: String(home.status.instanceCount || 0)
                },
                {
                    caption: "已有账号",
                    value: String(accountService.count)
                },
                {
                    caption: "上次游戏",
                    value: home.status.lastPlayedDisplay || "从未启动"
                }
            ]
            Rectangle {
                required property var modelData
                width: (home.width - 16) / 3
                height: 50
                radius: 8
                color: "#ffffff"
                border.color: "#e2e5ea"
                Text {
                    x: 12
                    y: 7
                    text: modelData.caption
                    font.pixelSize: 11
                    color: "#89919d"
                }
                Text {
                    x: 12
                    y: 23
                    width: parent.width - 24
                    height: 20
                    text: modelData.value
                    font.pixelSize: modelData.caption === "上次游戏" ? 12 : 18
                    font.weight: modelData.caption === "上次游戏" ? Font.Normal : Font.DemiBold
                    color: "#38404b"
                    verticalAlignment: Text.AlignVCenter
                    elide: Text.ElideRight
                    LiteToolTip {
                        visible: modelData.caption === "上次游戏" && playedHover.hovered
                        delay: 500
                        text: home.status.lastPlayedHint || "此实例尚无成功启动记录。"
                    }
                    HoverHandler {
                        id: playedHover
                    }
                }
            }
        }
    }

    Rectangle {
        x: 0
        y: 240
        width: parent.width
        height: home.height - y
        radius: 8
        color: "#ffffff"
        border.color: "#e2e5ea"
        Text {
            x: 12
            y: 12
            text: "当前游戏"
            font.pixelSize: 12
            font.weight: Font.DemiBold
            color: "#454d59"
        }
        Text {
            anchors.right: parent.right
            anchors.rightMargin: 12
            y: 12
            text: String(gameLaunch.sessions.length)
            font.pixelSize: 11
            color: "#89919d"
        }
        Text {
            anchors.centerIn: parent
            anchors.verticalCenterOffset: 10
            visible: gameLaunch.sessions.length === 0
            text: gameLaunch.error || (gameLaunch.starting ? gameLaunch.detail : "当前无实例运行")
            width: parent.width - 24
            horizontalAlignment: Text.AlignHCenter
            wrapMode: Text.Wrap
            font.pixelSize: 11
            color: gameLaunch.error ? "#c36a66" : "#a0a7b1"
        }
        ListView {
            x: 10; y: 36; width: parent.width - 20; height: parent.height - 44
            clip: true
            spacing: 6
            model: gameLaunch.sessions
            boundsBehavior: Flickable.StopAtBounds
            Controls.ScrollBar.vertical: Controls.ScrollBar { width: 3 }
            delegate: Rectangle {
                id: gameCard
                required property var modelData
                width: ListView.view.width
                height: 52
                radius: 6
                color: gameHover.hovered ? "#f2f4f7" : "#f7f8fa"
                border.color: "#e2e5ea"
                LiteToolTip {
                    visible: gameHover.hovered && !gameAction.hovered
                    delay: 350
                    text: modelData.instanceName
                }
                HoverHandler { id: gameHover }
                Image {
                    x: 10
                    anchors.verticalCenter: parent.verticalCenter
                    width: 30
                    height: 30
                    source: gameCard.modelData.iconPath || instanceService.details(gameCard.modelData.instancePath).icon || "qrc:/art/blocks/vanilla.png"
                    sourceSize: Qt.size(30, 30)
                    fillMode: Image.PreserveAspectFit
                    smooth: false
                }
                Text {
                    x: 50; y: 8; width: parent.width - 88
                    text: gameCard.modelData.instanceName
                    font.pixelSize: 12
                    color: "#454d59"
                    elide: Text.ElideRight
                }
                Text {
                    x: 50; y: 29; width: parent.width - 88
                    text: "PID " + (gameCard.modelData.processId || "—")
                    font.pixelSize: 10
                    color: "#89919d"
                }
                LiteIconButton {
                    id: gameAction
                    anchors.right: parent.right
                    anchors.rightMargin: 7
                    anchors.verticalCenter: parent.verticalCenter
                    width: 24; height: 24; iconSize: 17
                    visible: gameCard.modelData.state === 1
                    text: "停止游戏 · " + gameCard.modelData.instanceName
                    source: "qrc:/art/icons/stop.svg"
                    onClicked: gameLaunch.stop(gameCard.modelData.id)
                }
            }
        }
    }
}
