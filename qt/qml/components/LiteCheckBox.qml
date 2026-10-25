import QtQuick
import QtQuick.Controls.Basic as Controls

Controls.CheckBox {
    id: control
    implicitHeight: 30
    padding: 0
    spacing: 9
    hoverEnabled: true
    opacity: enabled ? 1 : 0.45
    indicator: Rectangle {
        x: control.leftPadding
        anchors.verticalCenter: parent.verticalCenter
        width: 16
        height: 16
        radius: 4
        color: control.checked ? "#454d59" : control.hovered ? "#eef1f5" : "#ffffff"
        border.color: control.checked ? "#454d59" : control.activeFocus ? "#929aa6" : "#cdd2da"
        Image {
            anchors.centerIn: parent
            width: 12
            height: 12
            sourceSize: Qt.size(12, 12)
            source: "qrc:/art/icons/check.svg"
            visible: control.checked
        }
    }
    contentItem: Text {
        leftPadding: control.indicator.width + control.spacing
        text: control.text
        color: "#626c7a"
        font.pixelSize: 12
        verticalAlignment: Text.AlignVCenter
        elide: Text.ElideRight
    }
}
