import QtQuick

Loader {
    id: loader
    property bool shown: false
    property bool visited: false
    active: shown || visited
    visible: shown
    onShownChanged: if (shown) visited = true
    Component.onCompleted: if (shown) visited = true
}
