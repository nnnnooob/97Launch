import QtQuick
import QtQuick.Controls.Basic

ItemDelegate {
    id: control
    leftPadding: 14
    rightPadding: 12
    topPadding: 12
    bottomPadding: 12
    hoverEnabled: true

    background: Rectangle {
        radius: 8
        color: control.down ? control.palette.button
                           : control.hovered ? Qt.lighter(control.palette.button, 1.10) : control.palette.window
        border.width: control.visualFocus ? 2 : 1
        border.color: control.visualFocus ? control.palette.dark : Qt.lighter(control.palette.mid, 1.23)
        Behavior on color { ColorAnimation { duration: 130 } }
        LightShadow {}
    }
}
