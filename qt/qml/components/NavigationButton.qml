import QtQuick
import QtQuick.Layouts
import QtQuick.Shapes
import QtQuick.Controls.Basic

Button {
    id: control

    property bool selected: false
    property string symbolPath: ""
    readonly property color foreground: selected ? "#1457D4" : hovered ? "#263B59" : "#536174"

    implicitHeight: 48
    implicitWidth: 200
    leftPadding: 20
    rightPadding: 14
    hoverEnabled: true
    Accessible.name: text
    Accessible.description: selected ? "当前页面" : ""
    scale: down ? 0.985 : 1
    Behavior on scale { NumberAnimation { duration: 100; easing.type: Easing.OutCubic } }

    background: Rectangle {
        radius: 10
        color: control.down ? (control.selected ? "#D4E3FF" : "#E0E7F1")
                            : control.selected ? "#E6EFFF" : control.hovered ? "#EBEFF5" : "#F8FAFD"
        border.width: control.visualFocus ? 2 : 0
        border.color: "#1765F5"
        Behavior on color { ColorAnimation { duration: 150 } }

        Rectangle {
            x: 4
            anchors.verticalCenter: parent.verticalCenter
            width: 4
            height: control.selected ? 26 : 8
            radius: 2
            opacity: control.selected ? 1 : 0
            color: "#1765F5"
            Behavior on height { NumberAnimation { duration: 180; easing.type: Easing.OutCubic } }
            Behavior on opacity { NumberAnimation { duration: 140 } }
        }
    }

    contentItem: RowLayout {
        spacing: 13
        Shape {
            Layout.preferredWidth: 24
            Layout.preferredHeight: 24
            Accessible.ignored: true
            ShapePath {
                strokeWidth: 1.7
                strokeColor: control.foreground
                fillColor: "transparent"
                capStyle: ShapePath.RoundCap
                joinStyle: ShapePath.RoundJoin
                PathSvg { path: control.symbolPath }
                Behavior on strokeColor { ColorAnimation { duration: 140 } }
            }
        }
        Label {
            text: control.text
            Layout.fillWidth: true
            font.pixelSize: 14
            font.weight: control.selected ? Font.DemiBold : Font.Normal
            color: control.foreground
            elide: Text.ElideRight
            Behavior on color { ColorAnimation { duration: 140 } }
        }
    }
}
