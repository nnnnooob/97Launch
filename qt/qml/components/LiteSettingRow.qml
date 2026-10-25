import QtQuick

Item {
    id: row
    property string label
    property real labelWidth: 150
    default property alias contents: field.data
    width: parent.width
    implicitHeight: 34
    height: implicitHeight
    Text {
        width: row.labelWidth
        height: parent.height
        verticalAlignment: Text.AlignVCenter
        text: row.label
        color: "#687384"
        font.pixelSize: 12
        wrapMode: Text.Wrap
    }
    Item {
        id: field
        x: row.labelWidth + 16
        width: parent.width - x
        height: parent.height
    }
}
