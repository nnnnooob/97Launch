import QtQuick
import QtQuick.Controls.Basic as Controls

LitePopup {
    id: dialog
    property string heading
    property string message
    property string confirmText: "确定"
    property bool answered: false
    signal confirmed
    signal rejected
    width: Math.min(420, parent.width - 48)
    padding: 20
    closePolicy: Controls.Popup.CloseOnEscape
    onAboutToShow: answered = false
    onAboutToHide: {
        if (!answered) {
            answered = true;
            rejected();
        }
    }
    contentItem: Column {
        spacing: 16
        Text {
            width: parent.width
            text: dialog.heading
            color: "#454d59"
            font.pixelSize: 16
            font.weight: Font.DemiBold
            wrapMode: Text.Wrap
        }
        Flickable {
            width: parent.width
            height: Math.min(230, description.implicitHeight)
            contentHeight: description.implicitHeight
            clip: true
            boundsBehavior: Flickable.StopAtBounds
            Text {
                id: description
                width: parent.width
                text: dialog.message
                color: "#778292"
                font.pixelSize: 12
                wrapMode: Text.Wrap
            }
        }
        Row {
            anchors.right: parent.right
            spacing: 10
            LiteButton {
                width: 84
                height: 32
                text: "取消"
                onClicked: dialog.close()
            }
            LiteButton {
                width: 104
                height: 32
                primary: true
                text: dialog.confirmText
                onClicked: {
                    if (dialog.answered)
                        return;
                    dialog.answered = true;
                    dialog.confirmed();
                    dialog.close();
                }
            }
        }
    }
}
