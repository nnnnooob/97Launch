import QtQuick
import QtQuick.Layouts
import QtQuick.Controls.Basic

ToolBar {
    id: navigation

    property int currentPage: 0
    property string accountId: "Steve"
    property string loginMethod: "离线账户"
    property url avatarSource: "qrc:/art/steve.png"
    property rect avatarClipRect: Qt.rect(8, 8, 8, 8)
    signal navigate(int pageIndex)
    signal accountRequested()

    implicitHeight: 56
    leftPadding: 20
    rightPadding: 12
    topPadding: 6
    bottomPadding: 6

    background: Rectangle {
        radius: 10
        color: navigation.palette.window
        border.width: 1
        border.color: Qt.lighter(navigation.palette.mid, 1.23)
        LightShadow { navigation: true }
    }

    contentItem: RowLayout {
        spacing: 16
        Label {
            text: "97Launch"
            font.pixelSize: 23
            font.bold: true
            color: navigation.palette.windowText
            Layout.preferredWidth: 168
        }
        Item { Layout.fillWidth: true }
        Item {
            id: tabArea
            readonly property int groupGap: 20
            readonly property real tabWidth: (width - groupGap - tabs.spacing * Math.max(0, tabs.count - 1)) / Math.max(1, tabs.count)
            Layout.preferredWidth: 450
            Layout.preferredHeight: 40
            TabBar {
                id: tabs
                width: parent.width
                height: 36
                spacing: 4
                currentIndex: navigation.currentPage
                onCurrentIndexChanged: {
                    if (currentIndex >= 0 && currentIndex !== navigation.currentPage)
                        navigation.navigate(currentIndex)
                }
                Repeater {
                    model: ["启动", "实例", "下载", "传输", "设置"]
                    TopNavigationTab {
                        required property int index
                        required property string modelData
                        text: modelData
                        icon.source: "qrc:/art/icons/" + ["play", "instances", "download", "transfer", "settings"][index] + ".svg"
                        trailingGap: index === 2 ? tabArea.groupGap : 0
                        width: tabArea.tabWidth + trailingGap
                    }
                }
            }
            Rectangle {
                x: 3 * tabArea.tabWidth + 2 * tabs.spacing + (tabArea.groupGap + tabs.spacing - width) / 2
                y: 8
                width: 1
                height: 20
                color: navigation.palette.mid
            }
            Rectangle {
                x: tabs.currentIndex * (tabArea.tabWidth + tabs.spacing) + (tabs.currentIndex > 2 ? tabArea.groupGap : 0) + (tabArea.tabWidth - width) / 2
                y: parent.height - height
                width: 28
                height: 2
                radius: 1
                color: navigation.palette.dark
                Behavior on x { NumberAnimation { duration: 210; easing.type: Easing.OutCubic } }
            }
        }
        Item { Layout.fillWidth: true }
        AccountButton {
            compact: true
            accountId: navigation.accountId
            loginMethod: navigation.loginMethod
            avatarSource: navigation.avatarSource
            avatarClipRect: navigation.avatarClipRect
            Layout.preferredWidth: 176
            Layout.preferredHeight: 44
            onClicked: navigation.accountRequested()
        }
    }
}
