import QtQuick
import QtQuick.Layouts
import QtQuick.Controls.Basic

ToolBar {
    id: statusBar

    property string pageTitle: "启动"
    property string accountId: "Steve"
    property string frameworkText: "Qt / QML"

    implicitHeight: 28
    leftPadding: 16
    rightPadding: 16
    topPadding: 3
    bottomPadding: 3
    readonly property color secondaryText: Qt.lighter(palette.windowText, 1.9)

    background: Rectangle {
        color: Qt.lighter(statusBar.palette.button, 1.08)
        Rectangle {
            width: parent.width
            height: 1
            color: Qt.lighter(statusBar.palette.mid, 1.15)
        }
    }

    contentItem: RowLayout {
        spacing: 18
        Label {
            text: statusBar.pageTitle
            Layout.fillWidth: true
            font.pixelSize: 12
            color: statusBar.secondaryText
            elide: Text.ElideRight
        }
        Label {
            text: statusBar.frameworkText
            font.pixelSize: 12
            color: statusBar.secondaryText
        }
        Label {
            text: statusBar.accountId
            Layout.maximumWidth: 160
            font.pixelSize: 12
            color: statusBar.secondaryText
            elide: Text.ElideRight
        }
        Label {
            text: "v" + Qt.application.version
            font.pixelSize: 12
            color: statusBar.secondaryText
        }
    }
}
