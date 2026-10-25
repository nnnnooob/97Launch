import QtQuick
import QtQuick.Controls.Basic as Controls

Controls.SpinBox {
    id: control
    implicitHeight: 32
    implicitWidth: 88
    editable: true
    padding: 6
    leftPadding: 26
    rightPadding: 26
    contentItem: TextInput {
        text: control.textFromValue(control.value, control.locale)
        color: "#454d59"
        font.pixelSize: 12
        horizontalAlignment: Text.AlignHCenter
        verticalAlignment: Text.AlignVCenter
        validator: control.validator
        inputMethodHints: Qt.ImhDigitsOnly
        selectByMouse: true
        readOnly: !control.editable
    }
    background: Rectangle {
        radius: 7
        color: control.activeFocus ? "#ffffff" : "#f7f8fa"
        border.color: control.activeFocus ? "#98a2b1" : "#e2e5ea"
    }
    down.indicator: Text {
        width: 24
        height: control.height
        text: "−"
        horizontalAlignment: Text.AlignHCenter
        verticalAlignment: Text.AlignVCenter
        color: control.down.pressed ? "#343a43" : "#939ba7"
    }
    up.indicator: Text {
        x: control.width - width
        width: 24
        height: control.height
        text: "+"
        horizontalAlignment: Text.AlignHCenter
        verticalAlignment: Text.AlignVCenter
        color: control.up.pressed ? "#343a43" : "#939ba7"
    }
}
