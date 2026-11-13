import QtQuick
import QtQuick.Shapes
import QtQuick.Controls.Basic as Controls

Item {
    id: page
    property real formOpacity: 1
    property real formScale: 1
    property bool activeView: true
    readonly property real formWidth: width * 0.51 - 68

    component ProgressTrack: Controls.ProgressBar {
        height: 4
        palette.highlight: "#798493"
        palette.midlight: "#edf0f4"
    }
    onVisibleChanged: if (!visible) {
        scene.offsetX = 0;
        scene.offsetY = 0;
    }

    Item {
        id: scene
        x: parent.width * 0.51
        width: parent.width - x
        height: parent.height
        clip: true
        property real offsetX: 0
        property real offsetY: 0
        Image {
            x: -8 + scene.offsetX
            y: -8 + scene.offsetY
            width: parent.width + 16
            height: parent.height + 16
            source: "qrc:/art/welcome-valley.png"
            fillMode: Image.PreserveAspectCrop
            mipmap: true
            Accessible.name: "清晨的 Minecraft 山谷与蓝色湖泊"
            Behavior on x {
                NumberAnimation {
                    duration: 180
                    easing.type: Easing.OutCubic
                }
            }
            Behavior on y {
                NumberAnimation {
                    duration: 180
                    easing.type: Easing.OutCubic
                }
            }
        }
        Item {
            id: wave
            width: 62
            height: parent.height
            Shape {
                width: 84
                height: 636
                preferredRendererType: Shape.CurveRenderer
                transform: Scale {
                    xScale: wave.width / 84
                    yScale: wave.height / 636
                }
                ShapePath {
                    strokeWidth: -1
                    fillColor: "#ffffff"
                    PathSvg {
                        path: "M-2,0 L84,0 C25,26 28,74 51,127 C100,228 8,282 42,387 C69,482 -4,547 34,636 L-2,636 Z"
                    }
                }
            }
        }
    }
    HoverHandler {
        enabled: page.activeView
        onPointChanged: {
            scene.offsetX = (point.position.x / Math.max(1, page.width) - 0.5) * 8;
            scene.offsetY = (point.position.y / Math.max(1, page.height) - 0.5) * 8;
        }
        onHoveredChanged: if (!hovered && page.activeView) {
            scene.offsetX = 0;
            scene.offsetY = 0;
        }
    }
    Item {
        width: page.width * 0.51
        height: parent.height
        opacity: page.formOpacity
        scale: page.formScale
        Row {
            x: 38
            y: 24
            spacing: 10
            Image {
                width: 26
                height: 26
                source: "qrc:/art/app-icon.png"
                mipmap: true
            }
            Text {
                height: 26
                text: "97Launch"
                font.pixelSize: 18
                font.weight: Font.DemiBold
                color: "#454d59"
                verticalAlignment: Text.AlignVCenter
            }
        }

        Item {
            x: 38
            y: 78
            width: page.formWidth
            height: parent.height - y - 46
            Flickable {
                width: parent.width
                height: parent.height
                contentHeight: preparationForm.implicitHeight
                boundsBehavior: Flickable.StopAtBounds
                clip: true
                Controls.ScrollBar.vertical: Controls.ScrollBar { width: 3 }
                Column {
                    id: preparationForm
                    width: parent.width
                    spacing: 12
                    Text {
                        text: "首次准备"
                        font.pixelSize: 30
                        font.weight: Font.DemiBold
                        color: "#454d59"
                    }
                    Text {
                        width: parent.width
                        text: "正在准备本地环境，完成后自动进入。"
                        font.pixelSize: 12
                        color: "#939ba7"
                        wrapMode: Text.Wrap
                    }
                    Column {
                        width: parent.width
                        spacing: 14
                        Repeater {
                            model: launcherFlow.preparationSteps
                            delegate: Row {
                                required property var modelData
                                spacing: 10
                                Text {
                                    width: 14
                                    text: modelData.state === "done" ? "✓" : modelData.state === "active" ? "●" : modelData.state === "error" ? "!" : "○"
                                    color: modelData.state === "error" ? "#c36969" : modelData.state === "waiting" ? "#a4acb7" : "#687384"
                                    font.pixelSize: 13
                                }
                                Text {
                                    text: modelData.label
                                    color: modelData.state === "waiting" ? "#939ba7" : "#454d59"
                                    font.pixelSize: 12
                                }
                            }
                        }
                    }
                    ProgressTrack {
                        width: parent.width
                        from: 0
                        to: 1
                        value: launcherFlow.preparationProgress
                        indeterminate: launcherFlow.preparationBusy
                    }
                    Text {
                        width: parent.width
                        text: launcherFlow.preparationDetail
                        color: "#939ba7"
                        font.pixelSize: 11
                        wrapMode: Text.Wrap
                        Accessible.role: Accessible.StaticText
                    }
                    Text {
                        width: parent.width
                        visible: text.length > 0
                        text: launcherFlow.preparationError
                        color: "#c36969"
                        font.pixelSize: 11
                        wrapMode: Text.WrapAnywhere
                    }
                    Row {
                        spacing: 8
                        visible: !launcherFlow.preparationBusy && !!launcherFlow.preparationError
                        LiteButton { text: "重新准备"; primary: true; onClicked: launcherFlow.retryPreparation() }
                        LiteButton { text: "稍后安装"; visible: launcherFlow.canSkipPreview; onClicked: launcherFlow.skipPreview() }
                    }
                    Text {
                        width: parent.width
                        text: "首次完成后，下次将直接进入。"
                        color: "#a4acb7"
                        font.pixelSize: 10
                        wrapMode: Text.Wrap
                    }
                }
            }
        }
        Item {
            x: 38
            width: page.formWidth
            height: 28
            y: parent.height - 34
            Text {
                anchors.verticalCenter: parent.verticalCenter
                text: "v" + globalSettings.version
                color: "#939ba7"
                font.pixelSize: 10
            }
        }
    }
}
