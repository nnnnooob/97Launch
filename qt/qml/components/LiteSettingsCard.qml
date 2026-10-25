import QtQuick

Rectangle {
    id: card
    property string title
    default property alias content: body.data
    width: parent.width
    implicitHeight: body.y + body.implicitHeight + 14
    radius: 8
    color: "#ffffff"
    border.color: "#e2e5ea"
    Rectangle {
        z: -1
        x: 0
        y: 2
        width: parent.width
        height: parent.height
        radius: 8
        color: "#07303640"
    }
    Text {
        x: 14
        y: 12
        text: card.title
        font.pixelSize: 13
        font.weight: Font.DemiBold
        color: "#454d59"
    }
    Column {
        id: body
        x: 14
        y: card.title ? 38 : 14
        width: parent.width - 28
        spacing: 8
    }
}
