import QtQuick
import QtQuick.Templates as T
import QtQuick.Controls.Basic as Controls
import QtQuick.Controls.impl as Impl

T.Button {
    id: control
    property var flickable
    implicitWidth: 34
    implicitHeight: 34
    hoverEnabled: true
    focusPolicy: Qt.StrongFocus
    text: "返回顶部"
    Accessible.name: text
    opacity: flickable && flickable.contentY - flickable.originY > 80 ? 1 : 0
    visible: opacity > 0
    enabled: opacity > 0.05
    Behavior on opacity {
        NumberAnimation {
            duration: 120
        }
    }
    background: Rectangle {
        radius: width / 2
        color: control.down ? "#edf0f4" : control.hovered ? "#f7f8fa" : "#ffffff"
        border.color: "#e2e5ea"
        Rectangle {
            z: -1
            y: 2
            width: parent.width
            height: parent.height
            radius: width / 2
            color: "#12303640"
        }
    }
    contentItem: Item {
        Impl.IconImage {
            anchors.centerIn: parent
            width: 17
            height: 17
            sourceSize: Qt.size(17, 17)
            source: "qrc:/art/icons/back-top.svg"
            color: control.down ? "#303640" : control.hovered || control.visualFocus ? "#555d69" : "#89919d"
        }
    }
    onClicked: {
        flickable.cancelFlick();
        toTop.restart();
    }
    onFlickableChanged: toTop.stop()
    NumberAnimation {
        id: toTop
        target: control.flickable
        property: "contentY"
        to: control.flickable ? control.flickable.originY : 0
        duration: 180
        easing.type: Easing.OutCubic
    }
    Connections {
        target: control.flickable
        function onMovementStarted() {
            toTop.stop();
        }
    }
    LiteToolTip {
        visible: parent.hovered
        delay: 500
        text: parent.text
    }
}
