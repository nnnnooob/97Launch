import QtQuick
import QtQuick.Controls.Basic as Controls

Controls.Menu {
    padding: 6
    transformOrigin: Item.Top
    palette.highlight: "#e7ebf0"
    palette.highlightedText: "#343940"
    background: Rectangle {
        radius: 8
        color: "#ffffff"
        border.color: "#e2e5ea"
    }
    enter: LitePopupTransition { compact: true }
    exit: LitePopupTransition { compact: true; leaving: true }
}
