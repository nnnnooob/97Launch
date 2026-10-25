import QtQuick

Item {
    property var account: ({})
    readonly property bool hasSkin: account.skin !== undefined && account.skin.length > 0
    readonly property int skinScale: account.skinScale || 1
    implicitWidth: 64
    implicitHeight: 64
    Image {
        anchors.centerIn: parent
        width: parent.width * 1.25
        height: parent.height * 1.25
        source: "qrc:/art/icons/avatar-shadow.svg"
        sourceSize: Qt.size(80, 80)
    }
    Image {
        anchors.centerIn: parent
        width: parent.width * 0.75
        height: parent.height * 0.75
        source: parent.hasSkin ? parent.account.skin : (parent.account.avatar || "qrc:/art/steve_head.png")
        sourceClipRect: parent.hasSkin ? Qt.rect(8 * parent.skinScale, 8 * parent.skinScale, 8 * parent.skinScale, 8 * parent.skinScale) : Qt.rect(0, 0, 0, 0)
        smooth: false
    }
    Image {
        anchors.centerIn: parent
        width: parent.width * 0.875
        height: parent.height * 0.875
        visible: parent.hasSkin
        source: parent.hasSkin ? parent.account.skin : ""
        sourceClipRect: Qt.rect(40 * parent.skinScale, 8 * parent.skinScale, 8 * parent.skinScale, 8 * parent.skinScale)
        smooth: false
    }
}
