import QtQuick
import QtQuick.Controls.Basic as Controls

Controls.Slider {
    id: control
    implicitHeight: 32
    from: 0
    to: 100
    stepSize: 1
    leftPadding: 7
    rightPadding: 7
    background: Rectangle {
        x: control.leftPadding
        y: (control.height - height) / 2
        width: control.availableWidth
        height: 4
        radius: 2
        color: "#e2e5ea"
        Rectangle {
            width: control.visualPosition * parent.width
            height: parent.height
            radius: 2
            color: control.enabled ? "#788393" : "#bec4ce"
        }
    }
    handle: Rectangle {
        x: control.leftPadding + control.visualPosition * (control.availableWidth - width)
        y: (control.height - height) / 2
        width: 14
        height: 14
        radius: 7
        color: control.pressed ? "#454d59" : control.hovered ? "#687384" : "#8792a2"
        border.width: 2
        border.color: "#ffffff"
        opacity: control.enabled ? 1 : 0.45
    }
}
