import QtQuick
import "LiteMotion.js" as Motion

Item {
    id: page
    property string section: "launch"
    property string displayedSection: "launch"
    onSectionChanged: {
        forceActiveFocus();
        enter.stop();
        exit.restart();
    }
    Item {
        id: pane
        anchors.fill: parent
        anchors.bottomMargin: status.visible ? status.height + 8 : 0
        Repeater {
            model: ["launch", "java", "manage", "gameDefaults", "about", "update"]
            LiteRetainedLoader {
                required property string modelData
                anchors.fill: parent
                shown: page.displayedSection === modelData
                source: ({
                        launch: "LiteGlobalLaunch.qml",
                        java: "LiteGlobalJava.qml",
                        manage: "LiteGlobalManage.qml",
                        gameDefaults: "LiteGlobalDefaults.qml",
                        about: "LiteGlobalAbout.qml",
                        update: "LiteGlobalUpdate.qml"
                    })[modelData]
            }
        }
    }
    Rectangle {
        id: status
        anchors.bottom: parent.bottom
        width: parent.width
        height: Math.min(70, message.implicitHeight + 16)
        visible: !!globalSettings.error
        radius: 7
        color: "#faf2ea"
        border.color: "#f0e2d4"
        Text {
            id: message
            x: 10
            y: 8
            width: parent.width - 44
            text: globalSettings.error
            color: "#a67c57"
            font.pixelSize: 11
            wrapMode: Text.WrapAnywhere
            maximumLineCount: 3
            elide: Text.ElideRight
        }
        LiteIconButton {
            anchors.right: parent.right
            width: 30
            height: 30
            iconSize: 12
            source: "qrc:/art/icons/close.svg"
            text: "关闭提示"
            onClicked: globalSettings.clearError()
        }
    }
    SequentialAnimation {
        id: exit
        ParallelAnimation {
            NumberAnimation {
                target: pane
                property: "opacity"
                to: 0
                duration: 70
            }
            NumberAnimation {
                target: pane
                property: "scale"
                to: 0.97
                duration: 70
                easing.type: Easing.InQuad
            }
        }
        ScriptAction {
            script: {
                page.displayedSection = page.section;
                enter.restart();
            }
        }
    }
    ParallelAnimation {
        id: enter
        NumberAnimation {
            target: pane
            property: "opacity"
            to: 1
            duration: 100
        }
        NumberAnimation {
            target: pane
            property: "scale"
            to: 1
            duration: 350
            easing.type: Easing.BezierSpline
            easing.bezierCurve: Motion.outBackWeak
        }
    }
}
