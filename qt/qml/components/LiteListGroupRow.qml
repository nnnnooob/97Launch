import QtQuick
import QtQuick.Templates as T

T.Button {
    id: button
    property bool last: false
    property bool grouped: true
    property bool selected: false
    readonly property bool rounded: last || !grouped
    readonly property color rowColor: down ? "#e7ebf0" : selected ? "#edf0f4" : hovered ? "#f2f4f7" : "#ffffff"
    padding: 0
    hoverEnabled: true
    background: Rectangle {
        radius: button.rounded ? 8 : 0
        color: button.rowColor
        border.width: button.rounded ? 1 : 0
        border.color: "#e2e5ea"
        Rectangle {
            visible: button.grouped
            width: parent.width - 2
            x: 1
            height: 12
            color: button.rowColor
        }
        Rectangle {
            visible: button.grouped
            width: 1
            height: button.last ? 12 : parent.height
            color: "#e2e5ea"
        }
        Rectangle {
            visible: button.grouped
            anchors.right: parent.right
            width: 1
            height: button.last ? 12 : parent.height
            color: "#e2e5ea"
        }
    }
}
