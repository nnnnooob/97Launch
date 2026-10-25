import QtQuick
import QtQuick.Controls.Basic as Controls
import QtQuick.Controls.impl

Controls.MenuItem {
    id: control
    implicitWidth: 200
    implicitHeight: 32
    padding: 8
    opacity: enabled ? 1 : 0.4
    background: Rectangle {
        radius: 5
        color: control.down ? "#e1e6ed" : control.highlighted ? "#eef1f5" : "transparent"
    }
    contentItem: Item {
        implicitHeight: 18
        IconImage {
            anchors.verticalCenter: parent.verticalCenter
            width: 17
            height: 17
            sourceSize: Qt.size(17, 17)
            source: control.icon.source
            color: control.down ? "#303640" : control.highlighted ? "#555d69" : "#788290"
            fillMode: Image.PreserveAspectFit
        }
        Text {
            x: 27
            width: parent.width - x
            anchors.verticalCenter: parent.verticalCenter
            text: control.text
            font.pixelSize: 12
            color: "#454d59"
            elide: Text.ElideRight
        }
    }
}
