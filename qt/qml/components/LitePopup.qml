import QtQuick
import QtQuick.Controls.Basic as Controls

Controls.Popup {
    property bool closing: false
    parent: Controls.Overlay.overlay
    anchors.centerIn: parent
    modal: true
    focus: true
    enabled: !closing
    padding: 20
    margins: 16
    transformOrigin: Item.Center
    palette.highlight: "#798594"
    palette.highlightedText: "#ffffff"
    onAboutToShow: closing = false
    onAboutToHide: closing = true
    background: Rectangle {
        radius: 12
        color: "#ffffff"
        border.color: "#e2e5ea"
    }
    Controls.Overlay.modal: Rectangle {
        color: "#35303640"
        Behavior on opacity { NumberAnimation { duration: 130; easing.type: Easing.OutCubic } }
    }
    enter: LitePopupTransition {}
    exit: LitePopupTransition { leaving: true }
}
