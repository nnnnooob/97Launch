import QtQuick
import QtQuick.Controls.Basic
import QtQuick.Controls.impl

TabButton {
    id: control

    property int trailingGap: 0

    implicitWidth: 96
    implicitHeight: 36
    padding: 8
    rightPadding: 8 + trailingGap
    rightInset: trailingGap
    hoverEnabled: true
    font.pixelSize: 14
    font.weight: checked ? Font.DemiBold : Font.Normal
    icon.width: 16
    icon.height: 16
    icon.color: palette.windowText
    display: AbstractButton.TextBesideIcon

    background: Rectangle {
        radius: 3
        color: control.down ? control.palette.mid : control.checked ? control.palette.button
                                          : control.hovered ? Qt.lighter(control.palette.button, 1.08) : control.palette.window
        border.width: control.visualFocus ? 2 : 0
        border.color: control.palette.windowText
        Behavior on color { ColorAnimation { duration: 130 } }
    }

    contentItem: IconLabel {
        spacing: control.spacing
        mirrored: control.mirrored
        display: control.display
        icon: control.icon
        text: control.text
        font: control.font
        color: control.palette.windowText
    }

}
