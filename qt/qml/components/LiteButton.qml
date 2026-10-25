import QtQuick
import QtQuick.Templates as T

T.Button {
    id: control
    property bool primary: false
    implicitWidth: Math.max(72, contentItem.implicitWidth + leftPadding + rightPadding)
    implicitHeight: 44
    padding: 12
    hoverEnabled: true
    opacity: enabled ? 1 : 0.45
    focusPolicy: Qt.StrongFocus
    Accessible.name: text
    scale: down ? 0.96 : hovered ? 1.025 : 1
    Behavior on scale {
        NumberAnimation {
            duration: 180
            easing.type: Easing.OutBack
        }
    }

    background: Rectangle {
        radius: 8
        color: control.primary ? (control.down ? "#262a30" : control.hovered ? "#444a53" : "#343940") : (control.down ? "#e9ecf0" : control.hovered ? "#f0f2f5" : "#ffffff")
        border.width: control.visualFocus ? 2 : 1
        border.color: control.visualFocus ? "#929aa6" : control.primary ? "transparent" : "#e2e5ea"
        Behavior on color {
            ColorAnimation {
                duration: 90
            }
        }
    }
    contentItem: Text {
        text: control.text
        font.pixelSize: control.primary ? 15 : 13
        font.weight: control.primary ? Font.DemiBold : Font.Normal
        color: control.primary ? "#ffffff" : "#454c57"
        horizontalAlignment: Text.AlignHCenter
        verticalAlignment: Text.AlignVCenter
        elide: Text.ElideRight
    }
}
