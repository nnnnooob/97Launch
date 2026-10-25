import QtQuick
import QtQuick.Controls.Basic as Controls
import QtQuick.Dialogs
import QtQuick.Templates as T

Item {
    id: page
    readonly property var selected: globalSettings.selectedJava
    property var contextJava: ({})
    function metadata(runtime) {
        return [runtime.vendor || "未知厂商", runtime.architecture || "未知架构", runtime.isManual ? "手动添加" : String(runtime.path || "").replace(/\//g, "\\").toLowerCase().indexOf("\\.97launch\\runtimes\\") >= 0 ? "启动器下载" : "自动发现"].join(" · ");
    }
    Component.onCompleted: {
        if (!globalSettings.javaRuntimes.length)
            globalSettings.refreshJava();
    }
    Column {
        id: heading
        width: parent.width
        spacing: 7
        Text {
            text: "Java 运行环境"
            color: "#454d59"
            font.pixelSize: 20
            font.weight: Font.DemiBold
        }
        LiteSettingsNote {
            text: "启动时自动匹配兼容版本；需要固定 Java 时，请在实例设置中指定。"
        }
        Item {
            width: parent.width
            height: 36
            Text {
                width: parent.width - actions.width - 12
                anchors.verticalCenter: parent.verticalCenter
                text: globalSettings.javaStatus
                color: "#939ba7"
                font.pixelSize: 11
                elide: Text.ElideRight
            }
            Row {
                id: actions
                anchors.right: parent.right
                spacing: 8
                LiteButton {
                    height: 32
                    text: globalSettings.javaBusy ? "搜索中…" : "重新搜索"
                    enabled: !globalSettings.javaBusy
                    onClicked: globalSettings.refreshJava()
                }
                LiteButton {
                    height: 32
                    text: "手动添加"
                    enabled: !globalSettings.javaBusy
                    onClicked: javaFile.open()
                }
            }
        }
    }
    ListView {
        id: list
        anchors.top: heading.bottom
        anchors.topMargin: 10
        anchors.bottom: parent.bottom
        width: parent.width - 8
        model: globalSettings.javaRuntimes
        spacing: 8
        clip: true
        boundsBehavior: Flickable.StopAtBounds
        Controls.ScrollBar.vertical: Controls.ScrollBar {
            width: 4
        }
        delegate: T.Button {
            id: row
            required property var modelData
            width: list.width
            height: 78
            padding: 0
            hoverEnabled: true
            readonly property bool selected: String(page.selected.path || "").toLowerCase() === modelData.path.toLowerCase()
            background: Rectangle {
                radius: 8
                color: row.selected ? "#edf0f4" : row.hovered ? "#f2f4f7" : "#ffffff"
                border.color: "#e2e5ea"
            }
            contentItem: Item {
                Rectangle {
                    x: 12
                    anchors.verticalCenter: parent.verticalCenter
                    width: 38
                    height: 38
                    radius: 8
                    color: "#f2f4f7"
                    Image {
                        anchors.centerIn: parent
                        width: 23
                        height: 23
                        source: "qrc:/art/icons/chip.svg"
                    }
                }
                Column {
                    x: 62
                    width: parent.width - 76
                    anchors.verticalCenter: parent.verticalCenter
                    spacing: 4
                    Text {
                        width: parent.width
                        text: row.modelData.name
                        color: "#454d59"
                        font.pixelSize: 13
                        elide: Text.ElideRight
                    }
                    LiteSettingsNote {
                        text: page.metadata(row.modelData)
                        maximumLineCount: 1
                        elide: Text.ElideRight
                    }
                    LiteSettingsNote {
                        text: String(row.modelData.path).replace(/[\\/]bin[\\/]javaw?\.exe$/i, "")
                        maximumLineCount: 1
                        elide: Text.ElideMiddle
                    }
                }
            }
            Accessible.name: modelData.name + "，" + modelData.path
            LiteToolTip {
                visible: row.hovered && !runtimeMenu.visible
                delay: 700
                text: row.modelData.path
            }
            MouseArea {
                anchors.fill: parent
                acceptedButtons: Qt.RightButton
                onClicked: function (mouse) {
                    page.contextJava = row.modelData;
                    const point = mapToItem(page, mouse.x, mouse.y);
                    runtimeMenu.x = Math.max(0, Math.min(point.x, page.width - runtimeMenu.width));
                    runtimeMenu.y = Math.max(0, Math.min(point.y, page.height - runtimeMenu.implicitHeight));
                    runtimeMenu.open();
                }
            }
            onClicked: globalSettings.setOption("SelectedJavaPath", modelData.path)
        }
        Text {
            anchors.centerIn: parent
            width: parent.width - 32
            visible: !list.count && !globalSettings.javaBusy
            text: "暂无可用 Java\n重新搜索或手动添加 java.exe。"
            horizontalAlignment: Text.AlignHCenter
            color: "#a2aab6"
            font.pixelSize: 12
            lineHeight: 1.5
        }
    }
    LiteMenu {
        id: runtimeMenu
        parent: page
        width: 190
        LiteMenuItem {
            text: "打开目录"
            icon.source: "qrc:/art/icons/folder.svg"
            onTriggered: globalSettings.openJava(page.contextJava.path)
        }
        LiteMenuItem {
            text: "复制路径"
            icon.source: "qrc:/art/icons/copy.svg"
            onTriggered: instanceJobs.copyText(page.contextJava.path)
        }
        Controls.MenuSeparator {
            contentItem: Rectangle { implicitHeight: 1; color: "#e9ecf0" }
        }
        LiteMenuItem {
            text: "移除手动记录"
            icon.source: "qrc:/art/icons/trash.svg"
            enabled: !!page.contextJava.isManual && !globalSettings.javaBusy
            onTriggered: globalSettings.removeJava(page.contextJava.path)
        }
    }
    FileDialog {
        id: javaFile
        title: "选择 Java 可执行文件"
        nameFilters: ["Java 程序 (java.exe javaw.exe)"]
        onAccepted: globalSettings.addJava(selectedFile)
    }
}
