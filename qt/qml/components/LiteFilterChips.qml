import QtQuick
import QtQuick.Controls.Basic as Controls

Item {
    id: chips
    property string label
    property var choices: []
    property string selected: "全部"
    signal selectedValue(string value)
    implicitHeight: Math.max(30, flow.implicitHeight)
    width: parent.width
    Text {
        width: 64
        height: 30
        verticalAlignment: Text.AlignVCenter
        text: chips.label
        font.pixelSize: 11
        color: "#939ba7"
    }
    Flow {
        id: flow
        x: 70
        width: parent.width - x
        spacing: 5
        Repeater {
            model: chips.choices
            Controls.AbstractButton {
                id: button
                required property string modelData
                Accessible.name: modelData
                width: Math.max(54, textLabel.implicitWidth + 20)
                height: 28
                padding: 0
                hoverEnabled: true
                checked: chips.selected === modelData
                background: Rectangle {
                    radius: 14
                    color: button.checked ? "#687384" : button.down ? "#e1e6ed" : button.hovered ? "#edf0f4" : "transparent"
                }
                contentItem: Text {
                    id: textLabel
                    text: button.modelData
                    font.pixelSize: 12
                    horizontalAlignment: Text.AlignHCenter
                    verticalAlignment: Text.AlignVCenter
                    color: button.checked ? "#ffffff" : "#687384"
                }
                onClicked: chips.selectedValue(modelData)
            }
        }
    }
}
