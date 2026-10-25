import QtQuick
import QtQuick.Controls.Basic as Controls

LitePopup {
    id: browser
    signal installed
    property string resourceType: "mod"
    property var projects: []
    property var versions: []
    property var project: ({})
    property var version: ({})
    property int offset: 0
    property bool hasMore: false
    property string status
    width: Math.min(720, parent.width - 40)
    height: Math.min(394, parent.height - 32)
    padding: 18
    closePolicy: Controls.Popup.CloseOnEscape
    onOpened: {
        browser.projects = [];
        browser.versions = [];
        browser.project = ({});
        browser.version = ({});
        browser.offset = 0;
        browser.search();
    }
    function search() {
        instanceJobs.submit("searchResources", {
            resourceType: resourceType,
            query: query.text,
            source: sourceChoice.currentText,
            offset: offset
        });
    }
    contentItem: Item {
        Text {
            text: "下载" + (browser.resourceType === "mod" ? " Mod" : browser.resourceType === "shader" ? "光影包" : "资源包")
            color: "#454d59"
            font.pixelSize: 17
            font.weight: Font.DemiBold
        }
        LiteIconButton {
            anchors.right: parent.right
            y: -4
            width: 28
            height: 28
            iconSize: 16
            source: "qrc:/art/icons/close.svg"
            text: "关闭"
            onClicked: browser.close()
        }
        Row {
            id: toolbar
            y: 38
            width: parent.width
            spacing: 8
            LiteTextField {
                id: query
                width: parent.width - 210
                height: 32
                placeholderText: "搜索"
                onAccepted: {
                    browser.offset = 0;
                    browser.search();
                }
            }
            LiteComboBox {
                id: sourceChoice
                width: 124
                model: ["自动选择", "Modrinth", "CurseForge"]
            }
            LiteButton {
                width: 70
                height: 32
                text: "搜索"
                enabled: !instanceJobs.busy
                onClicked: {
                    browser.offset = 0;
                    browser.search();
                }
            }
        }
        Rectangle {
            x: parent.width * 0.55
            y: 82
            width: 1
            height: parent.height - y - 46
            color: "#e8ebef"
        }
        ListView {
            id: projects
            y: 82
            width: parent.width * 0.55 - 10
            height: parent.height - y - 46
            clip: true
            model: browser.projects
            spacing: 4
            Controls.ScrollBar.vertical: Controls.ScrollBar {
                width: 4
            }
            delegate: Controls.ItemDelegate {
                required property var modelData
                width: projects.width
                height: 62
                background: Rectangle {
                    radius: 7
                    color: browser.project.id === parent.modelData.id ? "#edf0f4" : parent.hovered ? "#f6f7f9" : "transparent"
                }
                contentItem: Item {
                    Image {
                        x: 4
                        anchors.verticalCenter: parent.verticalCenter
                        width: 30
                        height: 30
                        sourceSize: Qt.size(30, 30)
                        source: modelData.iconUrl
                        asynchronous: true
                        fillMode: Image.PreserveAspectFit
                    }
                    Column {
                        x: 45
                        width: parent.width - 50
                        anchors.verticalCenter: parent.verticalCenter
                        spacing: 4
                        Text {
                            width: parent.width
                            text: modelData.title
                            color: "#454d59"
                            font.pixelSize: 12
                            elide: Text.ElideRight
                        }
                        Text {
                            width: parent.width
                            text: modelData.description
                            color: "#939ba7"
                            font.pixelSize: 10
                            elide: Text.ElideRight
                        }
                        Text {
                            text: modelData.source
                            color: "#a1a9b4"
                            font.pixelSize: 10
                        }
                    }
                }
                onClicked: {
                    browser.project = modelData;
                    browser.versions = [];
                    browser.version = ({});
                    instanceJobs.submit("resourceVersions", {
                        project: modelData
                    });
                }
            }
        }
        ListView {
            id: versions
            x: parent.width * 0.55 + 10
            y: 82
            width: parent.width - x
            height: parent.height - y - 46
            clip: true
            model: browser.versions
            spacing: 4
            Controls.ScrollBar.vertical: Controls.ScrollBar {
                width: 4
            }
            delegate: Controls.ItemDelegate {
                required property var modelData
                width: versions.width
                height: 48
                background: Rectangle {
                    radius: 7
                    color: browser.version.id === parent.modelData.id ? "#edf0f4" : parent.hovered ? "#f6f7f9" : "transparent"
                }
                contentItem: Column {
                    spacing: 4
                    Text {
                        width: parent.width
                        text: modelData.number
                        color: "#454d59"
                        font.pixelSize: 12
                        elide: Text.ElideRight
                    }
                    Text {
                        width: parent.width
                        text: (modelData.type === "release" ? "正式版" : modelData.type) + " · " + Qt.formatDateTime(new Date(modelData.published), "yyyy-MM-dd")
                        color: "#939ba7"
                        font.pixelSize: 10
                        elide: Text.ElideRight
                    }
                }
                onClicked: browser.version = modelData
            }
            Text {
                anchors.centerIn: parent
                width: parent.width - 24
                visible: !browser.versions.length
                text: instanceJobs.busy ? instanceJobs.detail : browser.project.id ? "没有兼容版本" : "先选择左侧项目"
                color: "#939ba7"
                font.pixelSize: 11
                wrapMode: Text.Wrap
                horizontalAlignment: Text.AlignHCenter
            }
        }
        Row {
            anchors.bottom: parent.bottom
            width: parent.width
            spacing: 8
            LiteButton {
                width: 70
                height: 30
                text: "更多"
                enabled: browser.hasMore && !instanceJobs.busy
                onClicked: {
                    browser.offset += 50;
                    browser.search();
                }
            }
            Text {
                width: parent.width - 194
                anchors.verticalCenter: parent.verticalCenter
                text: instanceJobs.busy ? instanceJobs.detail : browser.status
                color: "#939ba7"
                font.pixelSize: 10
                elide: Text.ElideRight
            }
            LiteButton {
                width: 108
                height: 30
                primary: true
                text: "安装所选版本"
                enabled: !!browser.version.id && !instanceJobs.busy
                onClicked: confirmation.open()
            }
        }
    }
    LiteConfirmDialog {
        id: confirmation
        heading: "安装资源"
        message: (browser.project.title || "") + "\n" + (browser.version.number || "") + (browser.resourceType === "mod" ? "\n\n将一并检查必要前置，文件变更可在 Mod 页面回退。" : "")
        confirmText: "安装"
        onConfirmed: instanceJobs.submit("installResource", {
            project: browser.project,
            version: browser.version
        })
    }
    Connections {
        target: instanceJobs
        function onFailed(action, error, path) {
            if (browser.opened && path === instanceSettings.instance.path)
                browser.status = error;
        }
        function onCompleted(action, result, args, path) {
            if (!browser.opened || path !== instanceSettings.instance.path)
                return;
            if (action === "searchResources" && args.resourceType === browser.resourceType) {
                browser.projects = args.offset ? browser.projects.concat(result.projects) : result.projects;
                browser.hasMore = result.hasMore;
                browser.status = result.warning || browser.projects.length + " 个项目";
            } else if (action === "resourceVersions" && args.project.id === browser.project.id)
                browser.versions = result.versions;
            else if (action === "installResource") {
                browser.status = "安装完成";
                browser.installed();
            }
        }
    }
}
