import QtQuick
import QtQuick.Controls.Basic as Controls

Controls.Popup {
    padding: 4
    transformOrigin: Item.Top
    background: Rectangle {
        radius: 8
        color: "#ffffff"
        border.color: "#e2e5ea"
    }
    enter: LitePopupTransition { compact: true }
    exit: LitePopupTransition { compact: true; leaving: true }
}
