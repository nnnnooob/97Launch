import QtQuick
import QtQuick.Controls.Basic as Controls

Item {
    id: dialogs
    signal finished
    signal modeSelected(string view)
    property string view: "choices"
    property int profileIndex: 0
    function open(initialView) {
        view = initialView;
        if (view === "choices")
            accountService.clearError();
        popup.open();
    }
    LitePopup {
        id: popup
        width: 372
        height: Math.min(parent.height - 24, dialogs.view === "choices" ? 246 : 330)
        padding: 18
        palette.highlight: "#798594"
        palette.highlightedText: "#ffffff"
        closePolicy: Controls.Popup.CloseOnEscape
        onClosed: {
            if (dialogs.view !== "choices" && accountService.busy)
                accountService.cancelLogin();
            dialogs.finished();
        }
        contentItem: Item {
            Item {
                width: parent.width
                height: 32
                Text {
                    anchors.verticalCenter: parent.verticalCenter
                    text: dialogs.view === "choices" ? "添加账号" : "选择使用的角色"
                    font.pixelSize: 18
                    font.weight: Font.DemiBold
                    color: "#303640"
                }
                LiteButton {
                    anchors.right: parent.right
                    width: 32
                    height: 32
                    padding: 8
                    text: "关闭"
                    contentItem: Image {
                        source: "qrc:/art/icons/close.svg"
                        sourceSize: Qt.size(16, 16)
                    }
                    onClicked: popup.close()
                }
            }
            Flickable {
                id: scroll
                y: 46
                width: parent.width
                height: parent.height - y
                contentHeight: formLoader.implicitHeight
                clip: true
                boundsBehavior: Flickable.StopAtBounds
                Controls.ScrollBar.vertical: Controls.ScrollBar {
                    policy: Controls.ScrollBar.AsNeeded
                }
                Loader {
                    id: formLoader
                    width: scroll.width
                    sourceComponent: dialogs.view === "profiles" ? profiles : choices
                }
            }
        }
    }
    Connections {
        target: accountService
        function onLoginSucceeded() {
            if (dialogs.view !== "choices")
                popup.close();
        }
    }
    Component {
        id: choices
        Column {
            spacing: 10
            Repeater {
                model: ["正版", "离线", "第三方登录"]
                LiteButton {
                    required property int index
                    required property string modelData
                    width: parent.width
                    height: 44
                    text: modelData
                    onClicked: {
                        const mode = index === 0 ? "microsoft" : index === 1 ? "offline" : "thirdparty";
                        dialogs.modeSelected(mode);
                        popup.close();
                    }
                }
            }
        }
    }
    Component {
        id: profiles
        Column {
            spacing: 10
            Repeater {
                model: accountService.profiles
                LiteButton {
                    required property int index
                    required property string modelData
                    width: parent.width
                    height: 36
                    text: (dialogs.profileIndex === index ? "✓  " : "") + modelData
                    onClicked: dialogs.profileIndex = index
                }
            }
            Text {
                width: parent.width
                visible: accountService.error.length > 0
                text: accountService.error
                font.pixelSize: 12
                color: "#bc4f4f"
                wrapMode: Text.Wrap
            }
            LiteButton {
                width: parent.width
                height: 36
                text: "使用该角色"
                primary: true
                enabled: accountService.busy && accountService.profiles.length > 0
                onClicked: accountService.chooseProfile(dialogs.profileIndex)
            }
            LiteButton {
                width: parent.width
                height: 36
                text: "取消"
                onClicked: popup.close()
            }
        }
    }
}
