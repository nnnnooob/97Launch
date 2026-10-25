import QtQuick
import QtQuick.Controls.Basic as Controls

Controls.RadioButton {
    id: control
    implicitHeight: 30
    spacing: 8
    padding: 0
    indicator: Rectangle {
        y: (control.height - height) / 2
        width: 16
        height: 16
        radius: 8
        border.color: control.checked ? "#687384" : "#cbd1da"
        color: "#ffffff"
        Rectangle {
            anchors.centerIn: parent
            width: 8
            height: 8
            radius: 4
            color: "#687384"
            visible: control.checked
        }
    }
    contentItem: Text {
        leftPadding: 24
        verticalAlignment: Text.AlignVCenter
        text: control.text
        font.pixelSize: 12
        color: "#687384"
    }
}
