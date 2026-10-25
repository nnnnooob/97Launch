import QtQuick
import QtQuick.Templates as T

T.Button {
    id: button
    property string title
    property int count: 0
    property bool expanded: false
    property bool collapsible: true
    property real expansion: expanded ? 1 : 0
    implicitHeight: 36
    padding: 12
    hoverEnabled: collapsible
    enabled: collapsible
    Accessible.name: title + "，" + count + " 项"
    background: Rectangle {
        radius: 8
        color: button.hovered ? "#fafbfc" : "#ffffff"
        border.color: "#e2e5ea"
        Rectangle {
            visible: button.expanded && button.count > 0
            x: 1
            width: parent.width - 2
            height: 12
            anchors.bottom: parent.bottom
            color: parent.color
        }
    }
    contentItem: Item {
        Text {
            anchors.verticalCenter: parent.verticalCenter
            width: parent.width - 24
            text: button.title + " (" + button.count + ")"
            font.pixelSize: 12
            font.weight: Font.DemiBold
            color: "#454d59"
            elide: Text.ElideRight
        }
        Image {
            anchors.right: parent.right
            anchors.verticalCenter: parent.verticalCenter
            width: 14
            height: 14
            sourceSize: Qt.size(14, 14)
            source: "qrc:/art/icons/chevron-down.svg"
            visible: button.collapsible
            rotation: 180 * button.expansion
        }
    }
}
