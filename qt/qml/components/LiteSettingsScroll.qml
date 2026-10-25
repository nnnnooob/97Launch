import QtQuick
import QtQuick.Controls.Basic as Controls

Flickable {
    id: scroll
    default property alias contents: column.data
    contentWidth: width
    contentHeight: column.implicitHeight + 8
    boundsBehavior: Flickable.StopAtBounds
    clip: true
    Controls.ScrollBar.vertical: Controls.ScrollBar {
        width: 4
        policy: Controls.ScrollBar.AsNeeded
    }
    Column {
        id: column
        width: scroll.width - 8
        spacing: 14
    }
}
