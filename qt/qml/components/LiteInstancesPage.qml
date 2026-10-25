import QtQuick
import QtQuick.Templates as T
import QtQuick.Controls.Basic as Controls
import QtQuick.Dialogs as Dialogs
import "LiteMotion.js" as Motion

Item {
    id: page
    signal navigateRequested(int page)
    signal settingsRequested(string path)
    property string action: ""
    property var record: ({})
    property string pendingFolder: ""
    property string animatedGroupKey: ""

    Item {
        id: groupMotion
        visible: false
        property bool animate: false
        property real api: 0
        property real regular: 0
        property real rare: 0
        property real error: 0
        Behavior on api {
            enabled: groupMotion.animate
            NumberAnimation {
                duration: 100
                easing.type: Easing.OutCubic
            }
        }
        Behavior on regular {
            enabled: groupMotion.animate
            NumberAnimation {
                duration: 100
                easing.type: Easing.OutCubic
            }
        }
        Behavior on rare {
            enabled: groupMotion.animate
            NumberAnimation {
                duration: 100
                easing.type: Easing.OutCubic
            }
        }
        Behavior on error {
            enabled: groupMotion.animate
            NumberAnimation {
                duration: 100
                easing.type: Easing.OutCubic
            }
        }
    }
    function groupHeight(key) {
        return groupMotion[key] || 0;
    }
    function syncGroupHeights(key) {
        const heights = {
            api: 0,
            regular: 0,
            rare: 0,
            error: 0
        };
        for (const entry of instanceService.entries) {
            if (entry.header && entry.expanded)
                heights[entry.key] = entry.count * 54 + 10;
        }
        groupMotion.animate = key.length > 0;
        if (key)
            groupMotion[key] = heights[key];
        else
            for (const group of ["api", "regular", "rare", "error"])
                groupMotion[group] = heights[group];
    }
    Component.onCompleted: syncGroupHeights("")

    function switchFolder(path) {
        if (path === instanceService.selectedFolder && !folderExit.running && !folderEnter.running)
            return;
        pendingFolder = path;
        folderEnter.stop();
        if (!folderExit.running)
            folderExit.restart();
    }
    SequentialAnimation {
        id: folderExit
        ParallelAnimation {
            NumberAnimation {
                target: page
                property: "opacity"
                to: 0
                duration: 100
            }
            NumberAnimation {
                target: page
                property: "scale"
                to: 0.95
                duration: 110
                easing.type: Easing.InQuad
            }
        }
        ScriptAction {
            script: {
                instanceService.clearError();
                instanceService.selectFolder(page.pendingFolder);
                list.positionViewAtBeginning();
                folderEnter.restart();
            }
        }
    }
    ParallelAnimation {
        id: folderEnter
        NumberAnimation {
            target: page
            property: "opacity"
            from: 0
            to: 1
            duration: 100
        }
        NumberAnimation {
            target: page
            property: "scale"
            from: 0.96
            to: 1
            duration: 400
            easing.type: Easing.BezierSpline
            easing.bezierCurve: Motion.outBackWeak
        }
    }

    function toggleGroup(key) {
        const offset = list.contentY;
        animatedGroupKey = key;
        instanceService.toggleGroup(key);
        animatedGroupKey = "";
        Qt.callLater(function () {
            list.contentY = Math.max(0, Math.min(offset, list.contentHeight - list.height));
        });
    }

    function requestAction(kind, item) {
        if (kind === "switch") {
            switchFolder(item.path);
            return;
        }
        instanceService.clearError();
        if (kind === "add") {
            folderPicker.open();
            return;
        }
        if (kind === "import") {
            packPicker.open();
            return;
        }
        action = kind;
        record = item;
        nameInput.text = item.name || "";
        confirm.open();
    }
    Dialogs.FolderDialog {
        id: folderPicker
        title: "选择已有游戏文件夹或空文件夹"
        currentFolder: "file:///" + instanceService.selectedFolder
        onAccepted: {
            const candidate = instanceService.folderCandidate(selectedFolder);
            if (candidate.path)
                page.requestAction("addConfirm", candidate);
        }
    }
    Dialogs.FileDialog {
        id: packPicker
        title: "选择整合包"
        nameFilters: ["Minecraft 整合包 (*.mrpack *.zip)"]
        fileMode: Dialogs.FileDialog.OpenFile
        onAccepted: instanceService.inspectModpack(selectedFile)
    }
    Connections {
        target: instanceService
        function onEntriesChanged() {
            page.syncGroupHeights(page.animatedGroupKey);
        }
        function onModpackReady(info) {
            page.requestAction("pack", info);
        }
    }
    LiteTextField {
        id: search
        width: parent.width - 42
        height: 36
        leftPadding: 34
        placeholderText: "搜索游戏实例"
        text: instanceService.query
        onTextEdited: instanceService.query = text
        Image {
            x: 11
            anchors.verticalCenter: parent.verticalCenter
            width: 16
            height: 16
            sourceSize: Qt.size(16, 16)
            source: "qrc:/art/icons/search.svg"
        }
    }
    LiteIconButton {
        x: parent.width - width
        width: 34
        height: 36
        source: "qrc:/art/icons/refresh.svg"
        text: "刷新实例列表"
        enabled: !instanceService.loading
        onClicked: instanceService.refresh()
    }
    ListView {
        id: list
        y: 40
        width: parent.width
        height: parent.height - y - (errorLabel.visible ? errorLabel.height + 8 : 0)
        model: instanceService.entries
        visible: !instanceService.loading && count > 0
        clip: true
        reuseItems: true
        enabled: !folderExit.running
        boundsBehavior: Flickable.StopAtBounds
        Controls.ScrollBar.vertical: Controls.ScrollBar {}
        delegate: Loader {
            required property var modelData
            width: list.width - 8
            height: modelData.header ? 44 : Math.max(0, Math.min(modelData.last ? 64 : 54, page.groupHeight(modelData.group) - modelData.offset))
            active: modelData.header || height > 0
            clip: true
            sourceComponent: modelData.header ? groupHeader : instanceRow
        }
    }
    Component {
        id: groupHeader
        Item {
            id: headerEntry
            property var entry: parent ? parent.modelData : ({})
            LiteListGroupHeader {
                y: 8
                width: parent.width
                height: 36
                title: headerEntry.entry.title || ""
                count: headerEntry.entry.count || 0
                expanded: page.groupHeight(headerEntry.entry.key) > 0
                expansion: Math.min(1, page.groupHeight(headerEntry.entry.key) / (count * 54 + 10))
                onClicked: page.toggleGroup(headerEntry.entry.key)
            }
        }
    }
    Component {
        id: instanceRow
        Item {
            id: instanceEntry
            property var entry: parent ? parent.modelData : ({})
            LiteListGroupRow {
                id: rowButton
                last: !!instanceEntry.entry.last
                width: parent.width
                height: 54
                padding: 0
                hoverEnabled: true
                Accessible.name: instanceEntry.entry.name + "，" + instanceEntry.entry.version + "，" + instanceEntry.entry.loader
                contentItem: Item {
                    Image {
                        x: 13
                        anchors.verticalCenter: parent.verticalCenter
                        width: 30
                        height: 30
                        sourceSize: Qt.size(30, 30)
                        source: instanceEntry.entry.icon
                        fillMode: Image.PreserveAspectFit
                        smooth: false
                    }
                    Column {
                        x: 54
                        anchors.verticalCenter: parent.verticalCenter
                        width: parent.width - x - 94
                        spacing: 4
                        Text {
                            width: parent.width
                            text: instanceEntry.entry.name
                            font.pixelSize: 13
                            color: "#454d59"
                            elide: Text.ElideRight
                        }
                        Row {
                            visible: !instanceEntry.entry.error
                            spacing: 4
                            Repeater {
                                model: instanceEntry.entry.loader ? [instanceEntry.entry.version, instanceEntry.entry.loader] : [instanceEntry.entry.version]
                                Rectangle {
                                    required property string modelData
                                    width: Math.min(chip.implicitWidth + 8, instanceEntry.width - 145)
                                    height: 15
                                    radius: 3
                                    color: "#eef0f3"
                                    Text {
                                        id: chip
                                        anchors.fill: parent
                                        anchors.leftMargin: 4
                                        anchors.rightMargin: 4
                                        text: modelData
                                        font.pixelSize: 10
                                        color: "#89919d"
                                        elide: Text.ElideRight
                                        verticalAlignment: Text.AlignVCenter
                                    }
                                }
                            }
                        }
                        Text {
                            visible: !!instanceEntry.entry.error
                            width: parent.width
                            text: instanceEntry.entry.error || ""
                            font.pixelSize: 10
                            color: "#bc6666"
                            elide: Text.ElideRight
                        }
                    }
                    Row {
                        anchors.right: parent.right
                        anchors.rightMargin: 6
                        anchors.verticalCenter: parent.verticalCenter
                        LiteIconButton {
                            width: 28
                            height: 28
                            iconSize: 16
                            source: "qrc:/art/icons/settings.svg"
                            text: "实例设置"
                            revealed: rowHover.hovered
                            enabled: !instanceEntry.entry.error || !!instanceEntry.entry.recovery
                            onClicked: page.settingsRequested(instanceEntry.entry.path)
                        }
                        LiteIconButton {
                            width: 28
                            height: 28
                            iconSize: 16
                            source: "qrc:/art/icons/folder.svg"
                            text: "打开目录"
                            revealed: rowHover.hovered
                            onClicked: instanceService.openFolder(instanceEntry.entry.path)
                        }
                        LiteIconButton {
                            width: 28
                            height: 28
                            iconSize: 16
                            source: "qrc:/art/icons/trash.svg"
                            text: "移到回收站"
                            revealed: rowHover.hovered
                            onClicked: page.requestAction("delete", instanceEntry.entry)
                        }
                    }
                }
                HoverHandler {
                    id: rowHover
                }
                LiteToolTip {
                    visible: parent.hovered
                    delay: 700
                    text: instanceEntry.entry.error || instanceEntry.entry.name
                }
                onClicked: instanceService.selectInstance(instanceEntry.entry.path)
            }
        }
    }
    Column {
        anchors.centerIn: list
        width: parent.width - 48
        spacing: 12
        visible: instanceService.loading || list.count === 0
        Controls.BusyIndicator {
            anchors.horizontalCenter: parent.horizontalCenter
            width: 30
            height: 30
            running: instanceService.loading || instanceService.readingPack
            visible: running
        }
        Text {
            width: parent.width
            text: instanceService.loading ? "正在获取实例列表" : instanceService.query.trim() ? "没有匹配的实例" : "无可用实例"
            font.pixelSize: 14
            color: "#454d59"
            horizontalAlignment: Text.AlignHCenter
        }
        Text {
            width: parent.width
            text: instanceService.loading ? "正在读取游戏目录，请稍候。" : instanceService.query.trim() ? "试试其他名称、版本或加载器。" : "可以在左侧添加已有游戏文件夹，\n或前往下载页安装游戏。"
            font.pixelSize: 11
            color: "#939ba7"
            horizontalAlignment: Text.AlignHCenter
            wrapMode: Text.Wrap
        }
        LiteButton {
            anchors.horizontalCenter: parent.horizontalCenter
            width: 104
            height: 32
            text: "前往下载"
            visible: !instanceService.loading && !instanceService.query.trim()
            onClicked: page.navigateRequested(2)
        }
    }
    LiteIconButton {
        anchors.right: list.right
        anchors.bottom: list.bottom
        source: "qrc:/art/icons/chevron-down.svg"
        rotation: 180
        text: "回到顶部"
        visible: list.contentY > 200
        onClicked: list.positionViewAtBeginning()
    }
    Text {
        id: errorLabel
        anchors.bottom: parent.bottom
        width: parent.width
        text: instanceService.readingPack ? "正在读取整合包信息……" : instanceService.error
        visible: text.length > 0
        font.pixelSize: 11
        color: instanceService.readingPack ? "#89919d" : "#bc6666"
        wrapMode: Text.Wrap
        maximumLineCount: 3
        elide: Text.ElideRight
    }
    LitePopup {
        id: confirm
        width: 370
        height: Math.min(parent.height - 32, form.implicitHeight + 36)
        padding: 18
        closePolicy: Controls.Popup.CloseOnEscape
        contentItem: Column {
            id: form
            spacing: 14
            Text {
                width: parent.width
                text: page.action === "addConfirm" ? "添加游戏文件夹" : page.action === "rename" ? "修改显示名称" : page.action === "remove" ? "移除文件夹" : page.action === "delete" ? "删除实例" : "整合包信息"
                color: "#343940"
                font.pixelSize: 17
                font.weight: Font.DemiBold
            }
            Text {
                width: parent.width
                text: page.action === "pack" ? page.record.name + "\n" + page.record.format + " · Minecraft " + page.record.version + (page.record.loader ? "\n" + page.record.loader : "") + "\n资源文件：" + page.record.files : page.action === "remove" ? "从列表移除「" + page.record.name + "」，保留其中的全部游戏文件。" : page.action === "delete" ? "将「" + page.record.name + "」的实例目录移到回收站。" : "输入该文件夹在侧栏中显示的名称。"
                color: "#737d8a"
                font.pixelSize: 12
                wrapMode: Text.Wrap
            }
            LiteTextField {
                id: nameInput
                width: parent.width
                maximumLength: 40
                visible: page.action === "addConfirm" || page.action === "rename" || page.action === "pack"
                placeholderText: "文件夹名称"
            }
            Text {
                width: parent.width
                text: page.record.path || ""
                font.pixelSize: 10
                color: "#939ba7"
                wrapMode: Text.WrapAnywhere
                maximumLineCount: 3
                elide: Text.ElideMiddle
            }
            Text {
                width: parent.width
                visible: page.action === "pack"
                text: "将导入当前游戏文件夹，保留整合包配置并安装所需游戏组件。"
                color: "#89919d"
                font.pixelSize: 11
                wrapMode: Text.Wrap
            }
            Text {
                width: parent.width
                visible: instanceService.error.length > 0 || instanceJobs.error.length > 0
                text: instanceService.error || instanceJobs.error
                color: "#bc6666"
                font.pixelSize: 11
                wrapMode: Text.Wrap
            }
            Row {
                anchors.right: parent.right
                spacing: 10
                LiteButton {
                    width: 76
                    height: 34
                    text: page.action === "pack" ? "关闭" : "取消"
                    onClicked: confirm.close()
                }
                LiteButton {
                    width: 96
                    height: 34
                    primary: true
                    visible: true
                    text: page.action === "pack" ? "导入并安装" : page.action === "addConfirm" ? "添加" : page.action === "rename" ? "保存" : page.action === "remove" ? "移除" : "移到回收站"
                    enabled: !(page.action === "addConfirm" || page.action === "rename" || page.action === "pack") || nameInput.text.trim().length > 0
                    onClicked: {
                        let saved = false;
                        if (page.action === "pack") {
                            saved = instanceJobs.download("downloadLocalPack", {
                                archive: page.record.path,
                                root: instanceService.selectedFolder,
                                name: nameInput.text.trim(),
                                title: "导入整合包 · " + nameInput.text.trim()
                            });
                            if (saved)
                                page.navigateRequested(3);
                        } else if (page.action === "addConfirm")
                            saved = instanceService.addFolder(page.record.path, nameInput.text);
                        else if (page.action === "rename")
                            saved = instanceService.renameFolder(page.record.path, nameInput.text);
                        else if (page.action === "remove")
                            saved = instanceService.removeFolder(page.record.path);
                        else if (page.action === "delete")
                            saved = instanceService.recycleInstance(page.record.path);
                        if (saved)
                            confirm.close();
                    }
                }
            }
        }
    }
}
