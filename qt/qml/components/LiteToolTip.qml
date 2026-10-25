import QtQuick
import QtQuick.Controls.Basic as Controls

Controls.ToolTip {
    id: tip
    delay: 500
    padding: 8
    margins: 8
    width: Math.min(360, implicitWidth)
    font.pixelSize: 11
    contentItem: Text {
        text: tip.text
        font: tip.font
        color: "#454d59"
        wrapMode: Text.Wrap
    }
    background: Rectangle {
        radius: 6
        color: "#ffffff"
        border.color: "#dce1e7"
    }
    enter: LitePopupTransition { compact: true }
    exit: LitePopupTransition { compact: true; leaving: true }
}
