import QtQuick
import QtQuick.Controls.Basic as Controls

Controls.TextField {
    id: control
    implicitWidth: 212
    implicitHeight: 38
    leftPadding: 12
    rightPadding: 12
    topPadding: 8
    bottomPadding: 8
    font.pixelSize: 13
    color: "#454d59"
    placeholderTextColor: "#9aa1ac"
    selectionColor: "#d8dee7"
    selectedTextColor: "#343940"
    selectByMouse: true
    verticalAlignment: TextInput.AlignVCenter
    opacity: enabled ? 1 : 0.55
    background: Rectangle {
        radius: 8
        color: control.activeFocus ? "#ffffff" : "#f7f8fa"
        border.width: 1
        border.color: control.activeFocus ? "#929aa6" : "#e2e5ea"
        Behavior on border.color {
            ColorAnimation {
                duration: 120
            }
        }
        Behavior on color {
            ColorAnimation {
                duration: 120
            }
        }
    }
}
