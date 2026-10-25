import QtQuick
import QtQuick.Controls.Basic as Controls
import QtQuick.Controls.impl
import QtQuick.Templates as T

T.Button {
    id: control
    property url source
    property int iconSize: 20
    property bool revealed: true
    implicitWidth: 36
    implicitHeight: 36
    padding: 0
    hoverEnabled: true
    focusPolicy: Qt.StrongFocus
    Accessible.name: text
    opacity: revealed || (enabled && (hovered || visualFocus || down)) ? (enabled ? 1 : 0.35) : 0
    Behavior on opacity {
        enabled: control.enabled && control.visible
        NumberAnimation {
            duration: 140
        }
    }
    HoverHandler {
        cursorShape: control.enabled ? Qt.PointingHandCursor : Qt.ArrowCursor
    }
    LiteToolTip {
        visible: parent.hovered && parent.enabled && parent.opacity > 0.5
        delay: 500
        text: parent.text
    }
    background: Item {}
    contentItem: Item {
        IconImage {
            anchors.centerIn: parent
            width: control.iconSize
            height: control.iconSize
            source: control.source
            sourceSize: Qt.size(control.iconSize, control.iconSize)
            fillMode: Image.PreserveAspectFit
            color: control.down ? "#303640" : control.hovered || control.visualFocus ? "#555d69" : "#89919d"
            Behavior on color {
                ColorAnimation {
                    duration: control.down ? 40 : 100
                }
            }
        }
    }
}
