import QtQuick

LiteSettingRow {
    id: field
    property string option
    property string hint
    property var choices: []
    readonly property var value: globalSettings.values[option]
    LiteComboBox {
        width: parent.width
        visible: field.choices.length > 0
        model: field.choices
        currentIndex: Math.max(0, field.choices.indexOf(String(field.value || "")))
        onActivated: globalSettings.setOption(field.option, currentText)
    }
    LiteTextField {
        width: parent.width
        height: 32
        visible: field.choices.length === 0
        text: String(field.value || "")
        placeholderText: field.hint
        font.pixelSize: 12
        onEditingFinished: {
            if (text !== String(field.value || ""))
                globalSettings.setOption(field.option, text);
        }
    }
}
