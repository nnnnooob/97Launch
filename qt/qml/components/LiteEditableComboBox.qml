import QtQuick

LiteComboBox {
    id: control
    property string fieldLabel: ""
    editable: true
    contentItem: LiteTextField {
        text: control.editText
        font: control.font
        height: control.height
        padding: 0
        leftPadding: control.fieldLabel ? caption.implicitWidth + 4 : 0
        verticalAlignment: TextInput.AlignVCenter
        background: null
        selectByMouse: true
        Text {
            id: caption
            anchors.verticalCenter: parent.verticalCenter
            visible: !!control.fieldLabel
            text: control.fieldLabel + "："
            font: control.font
            color: "#778292"
        }
    }
}
