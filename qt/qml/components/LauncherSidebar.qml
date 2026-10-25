import QtQuick
import QtQuick.Layouts
import QtQuick.Controls.Basic

Pane {
    id: sidebar

    property int currentPage: 0
    property string accountId: "Steve"
    property string loginMethod: "离线账户"
    property url avatarSource: "qrc:/art/steve.png"
    property rect avatarClipRect: Qt.rect(8, 8, 8, 8)
    signal navigate(int pageIndex)
    signal accountRequested()

    readonly property var mainItems: [
        {label: "启动", page: 0, symbol: "M8 5 L20 12 L8 19 Z"},
        {label: "实例", page: 1, symbol: "M12 3 L21 8 L21 17 L12 22 L3 17 L3 8 Z M3 8 L12 13 L21 8 M12 13 L12 22 M7.5 5.5 L16.5 10.5"},
        {label: "下载", page: 2, symbol: "M12 3 L12 16 M7 11 L12 16 L17 11 M4 16 L4 21 L20 21 L20 16"}
    ]
    readonly property var footerItems: [
        {label: "传输", page: 3, symbol: "M4 7 L20 7 M15 2 L20 7 L15 12 M20 17 L4 17 M9 12 L4 17 L9 22"},
        {label: "设置", page: 4, symbol: "M4 6 L8 6 M12 6 L20 6 M4 12 L13 12 M17 12 L20 12 M4 18 L6 18 M10 18 L20 18 M8 3 L8 9 M17 9 L17 15 M6 15 L6 21"}
    ]

    implicitWidth: 224
    padding: 12
    background: Rectangle {
        color: "#F8FAFD"
        Rectangle { anchors.right: parent.right; height: parent.height; width: 1; color: "#E3E8F0" }
    }

    ColumnLayout {
        anchors.fill: parent
        spacing: 8
        Label {
            text: "97Launch"
            font.pixelSize: 23
            font.bold: true
            color: "#172338"
            Layout.leftMargin: 8
            Layout.topMargin: 10
            Layout.bottomMargin: 17
        }
        AccountButton {
            accountId: sidebar.accountId
            loginMethod: sidebar.loginMethod
            avatarSource: sidebar.avatarSource
            avatarClipRect: sidebar.avatarClipRect
            Layout.fillWidth: true
            Layout.bottomMargin: 18
            onClicked: sidebar.accountRequested()
        }
        Repeater {
            model: sidebar.mainItems
            NavigationButton {
                required property var modelData
                text: modelData.label
                symbolPath: modelData.symbol
                selected: sidebar.currentPage === modelData.page
                Layout.fillWidth: true
                onClicked: sidebar.navigate(modelData.page)
            }
        }
        Item { Layout.fillHeight: true }
        Repeater {
            model: sidebar.footerItems
            NavigationButton {
                required property var modelData
                text: modelData.label
                symbolPath: modelData.symbol
                selected: sidebar.currentPage === modelData.page
                Layout.fillWidth: true
                onClicked: sidebar.navigate(modelData.page)
            }
        }
    }
}
