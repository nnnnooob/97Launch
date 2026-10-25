import QtQuick
import QtQuick.Templates as T

T.Button {
    id: control
    property bool closeAction: false
    implicitWidth: 32
    implicitHeight: 32
    padding: 9
    hoverEnabled: true
    focusPolicy: Qt.StrongFocus
    Accessible.name: text

    background: Rectangle {
        radius: 4
        color: "transparent"
        border.width: control.visualFocus ? 1 : 0
        border.color: "#9198a4"
    }
    contentItem: Rectangle {
        radius: width / 2
        color: control.closeAction ? "#e46060" : "#a3a8b0"
        opacity: control.down ? 0.6 : control.hovered ? 0.85 : 1
        Behavior on opacity {
            NumberAnimation {
                duration: 90
            }
        }
    }
}
