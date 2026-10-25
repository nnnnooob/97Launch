import QtQuick

BorderImage {
    property bool navigation: false
    readonly property int extent: navigation ? 22 : 12
    readonly property int corner: navigation ? 10 : 8
    x: -extent
    y: -extent + 2
    width: parent.width + extent * 2
    height: parent.height + extent * 2
    z: -1
    source: navigation ? "image://shadows/navigation" : "image://shadows/card"
    border.left: extent + corner
    border.top: extent + corner
    border.right: extent + corner
    border.bottom: extent + corner
    opacity: navigation ? 0.08 : 0.06
    cache: true
}
