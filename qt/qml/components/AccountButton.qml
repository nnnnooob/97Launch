import QtQuick
import QtQuick.Layouts
import QtQuick.Controls.Basic

Button {
    id: control

    property string accountId: "Steve"
    property string loginMethod: "离线账户"
    property url avatarSource: "qrc:/art/steve.png"
    property rect avatarClipRect: Qt.rect(8, 8, 8, 8)
    property bool compact: false

    implicitHeight: compact ? 44 : 78
    implicitWidth: 200
    leftPadding: 12
    rightPadding: 12
    topPadding: compact ? 4 : 12
    bottomPadding: compact ? 4 : 12
    hoverEnabled: true
    Accessible.name: accountId + "，" + loginMethod
    Accessible.description: "管理账户"

    scale: down ? 0.985 : 1
    Behavior on scale { NumberAnimation { duration: 100; easing.type: Easing.OutCubic } }

    background: Rectangle {
        radius: control.compact ? 4 : 12
        color: control.down ? Qt.darker(control.palette.button, 1.05)
                            : control.hovered ? Qt.lighter(control.palette.button, 1.08)
                            : control.compact ? control.palette.window : Qt.lighter(control.palette.button, 1.1)
        border.width: control.visualFocus ? 2 : 1
        border.color: control.visualFocus ? control.palette.windowText : control.compact ? "transparent" : control.palette.mid
        Behavior on color { ColorAnimation { duration: 140 } }
        Behavior on border.color { ColorAnimation { duration: 140 } }
    }

    contentItem: RowLayout {
        spacing: 11
        Rectangle {
            Layout.preferredWidth: control.compact ? 32 : 44
            Layout.preferredHeight: control.compact ? 32 : 44
            radius: control.compact ? 3 : 9
            color: control.palette.button
            Image {
                anchors.centerIn: parent
                width: control.compact ? 28 : 38
                height: control.compact ? 28 : 38
                source: control.avatarSource
                sourceClipRect: control.avatarClipRect
                fillMode: Image.PreserveAspectFit
                smooth: false
                Accessible.ignored: true
            }
        }
        ColumnLayout {
            Layout.fillWidth: true
            spacing: control.compact ? 3 : 5
            Label {
                text: control.accountId
                Layout.fillWidth: true
                font.pixelSize: control.compact ? 14 : 15
                font.weight: Font.DemiBold
                color: control.palette.windowText
                elide: Text.ElideRight
            }
            Label {
                text: control.loginMethod
                Layout.fillWidth: true
                font.pixelSize: control.compact ? 11 : 12
                color: Qt.lighter(control.palette.windowText, 1.7)
                elide: Text.ElideRight
            }
        }
        Label {
            text: "›"
            font.pixelSize: 24
            color: Qt.lighter(control.palette.windowText, control.hovered ? 1 : 1.8)
            Accessible.ignored: true
            transform: Translate {
                x: control.hovered ? 2 : 0
                Behavior on x { NumberAnimation { duration: 140; easing.type: Easing.OutCubic } }
            }
        }
    }
}
