import QtQuick
import QtQuick.Controls.Basic as Controls

Controls.ComboBox {
    id: control
    property string hint: ""
    implicitHeight: 32
    leftPadding: 10
    rightPadding: 28
    font.pixelSize: 12
    hoverEnabled: true
    opacity: enabled ? 1 : 0.45
    LiteToolTip {
        visible: control.hovered && !!control.hint && !control.popup.visible
        text: control.hint
        delay: 400
    }
    contentItem: Text {
        text: control.displayText
        color: "#454d59"
        font: control.font
        verticalAlignment: Text.AlignVCenter
        elide: Text.ElideRight
    }
    indicator: Image {
        x: control.width - width - 10
        y: (control.height - height) / 2
        width: 12
        height: 12
        sourceSize: Qt.size(12, 12)
        source: "qrc:/art/icons/chevron-down.svg"
        rotation: control.popup.visible ? 180 : 0
        Behavior on rotation {
            NumberAnimation {
                duration: 100
                easing.type: Easing.OutCubic
            }
        }
    }
    background: Rectangle {
        radius: 7
        color: control.down ? "#eef0f3" : "#f7f8fa"
        border.color: control.activeFocus ? "#929aa6" : control.hovered ? "#c6cbd3" : "#e2e5ea"
    }
    delegate: Controls.ItemDelegate {
        required property var modelData
        required property int index
        width: control.width - 8
        height: 32
        highlighted: control.highlightedIndex === index
        contentItem: Text {
            text: control.textRole ? modelData[control.textRole] : modelData
            font.pixelSize: 12
            color: "#454d59"
            verticalAlignment: Text.AlignVCenter
            elide: Text.ElideRight
        }
        background: Rectangle {
            radius: 5
            color: parent.highlighted ? "#eef1f5" : "transparent"
        }
    }
    popup: LiteFlyout {
        y: control.height + 4
        width: control.width
        implicitHeight: Math.min(210, contentItem.implicitHeight + 8)
        padding: 4
        contentItem: ListView {
            clip: true
            implicitHeight: contentHeight
            model: control.popup.visible ? control.delegateModel : null
            currentIndex: control.highlightedIndex
            Controls.ScrollBar.vertical: Controls.ScrollBar {}
        }
    }
}
