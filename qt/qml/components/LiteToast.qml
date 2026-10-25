import QtQuick
import QtQuick.Controls.Basic as Controls

Controls.Popup {
    id: toast
    property string message: ""
    property string nextMessage: ""
    property bool failed: false
    property bool nextFailed: false
    property bool closing: false
    property real offset: 0
    parent: Controls.Overlay.overlay
    x: (parent.width - width) / 2
    y: 70 + offset
    width: Math.min(parent.width - 40, label.implicitWidth + 52)
    padding: 12
    margins: 16
    z: 100
    modal: false
    dim: false
    focus: false
    closePolicy: Controls.Popup.NoAutoClose

    function show(text, error) {
        if (closing && visible) {
            nextMessage = text;
            nextFailed = !!error;
            return;
        }
        message = text;
        failed = !!error;
        hold.interval = failed ? 8000 : message.indexOf("\n") >= 0 ? 4500 : 2600;
        open();
        hold.restart();
    }
    onAboutToShow: closing = false
    onAboutToHide: closing = true
    onClosed: {
        if (nextMessage) {
            const text = nextMessage;
            const error = nextFailed;
            nextMessage = "";
            Qt.callLater(function () { toast.show(text, error); });
        }
    }
    background: Rectangle {
        radius: 8
        color: "#ffffff"
        border.color: toast.failed ? "#e8d8c8" : "#dce1e7"
    }
    contentItem: Item {
        implicitHeight: Math.max(16, label.implicitHeight)
        Image {
            width: 16
            height: 16
            anchors.verticalCenter: parent.verticalCenter
            source: toast.failed ? "qrc:/art/icons/info.svg" : "qrc:/art/icons/download.svg"
            sourceSize: Qt.size(16, 16)
        }
        Text {
            id: label
            x: 26
            width: parent.width - x
            text: toast.message
            color: toast.failed ? "#a67c57" : "#454d59"
            font.pixelSize: 12
            wrapMode: Text.Wrap
            maximumLineCount: 10
            elide: Text.ElideRight
        }
        TapHandler { onTapped: toast.close() }
        HoverHandler { onHoveredChanged: if (hovered) hold.stop(); else if (toast.visible && !toast.closing) hold.restart(); }
    }
    Timer { id: hold; interval: 2600; onTriggered: toast.close() }
    enter: Transition {
        ParallelAnimation {
            NumberAnimation { property: "opacity"; from: 0; to: 1; duration: 140; easing.type: Easing.OutCubic }
            NumberAnimation { target: toast; property: "offset"; from: -8; to: 0; duration: 200; easing.type: Easing.OutCubic }
        }
    }
    exit: Transition {
        ParallelAnimation {
            NumberAnimation { property: "opacity"; to: 0; duration: 130; easing.type: Easing.OutCubic }
            NumberAnimation { target: toast; property: "offset"; to: -5; duration: 130; easing.type: Easing.InCubic }
        }
    }
}
