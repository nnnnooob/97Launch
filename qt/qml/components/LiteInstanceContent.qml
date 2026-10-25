import QtQuick
import QtQuick.Controls.Basic as Controls
import QtQuick.Dialogs

Item {
    id: page
    signal downloadRequested(string category, string instancePath)
    property string kind: "mods"
    property var rows: []
    property var selected: []
    property var online: ({})
    property var updateRows: []
    property var updateSelection: []
    property var updateCandidates: []
    property string query
    property int filter: 0
    property string directory
    property bool shared: false
    property bool refreshAfter: false
    property bool updateAfterCheck: false
    property var menuRow: ({
            file: {}
        })
    property var pendingArgs: ({})
    property string pendingAction
    property string status
    property string lastFailure
    property var previewRows: []
    property int previewIndex: 0
    readonly property string heading: ({
            mods: "Mod",
            saves: "存档",
            screenshots: "截图",
            resourcepacks: "资源包",
            shaderpacks: "光影包"
        })[kind]
    readonly property bool isPack: kind === "resourcepacks" || kind === "shaderpacks"
    readonly property var filtered: rows.filter(function (row) {
        const file = row.file;
        if (query.trim() && (file.name + " " + file.fileName + " " + file.description).toLowerCase().indexOf(query.trim().toLowerCase()) < 0)
            return false;
        if (!filter)
            return true;
        if (kind === "mods")
            return filter === 1 ? file.enabled : filter === 2 ? !file.enabled : filter === 3 ? !!(online[file.path] && online[file.path].online && online[file.path].online.update) : !!file.error || (row.diagnostics || []).length > 0;
        return filter === 1 ? row.active : filter === 2 ? !row.active : row.compatible === false;
    })
    onKindChanged: {
        query = "";
        filter = 0;
        rows = [];
        selected = [];
        online = {};
        lastFailure = "";
        Qt.callLater(function () {
            list.positionViewAtBeginning();
            shots.positionViewAtBeginning();
            refresh();
        });
    }
    Component.onCompleted: Qt.callLater(refresh)
    Component.onDestruction: instanceJobs.watchDirectory("")
    function refresh() {
        if (!instanceSettings.instance.path)
            return;
        refreshAfter = false;
        instanceJobs.submit("scan", {
            kind: kind
        });
    }
    function fileUrl(path) {
        return "file:///" + path.replace(/\\/g, "/");
    }
    function checked(path) {
        return selected.indexOf(path) >= 0;
    }
    function toggle(path) {
        const next = selected.slice();
        const index = next.indexOf(path);
        if (index >= 0)
            next.splice(index, 1);
        else
            next.push(path);
        selected = next;
    }
    function summary(row) {
        const file = row.file;
        return [file.version, file.description || file.fileName].filter(Boolean).join(" · ");
    }
    function state(row) {
        if (row.file.error)
            return row.file.error;
        if (kind === "mods") {
            const found = online[row.file.path];
            const info = found && found.online;
            return (row.file.enabled ? "已启用" : "已禁用") + (info ? " · " + info.project.source : "") + (info && info.update ? " · 可更新至 " + info.update.number : "") + (found && found.error ? " · 更新检查失败" : "") + (row.diagnostics && row.diagnostics.length ? " · " + row.diagnostics.join("；") : "");
        }
        if (isPack)
            return (row.active ? "已选用" : "未选用") + (row.compatible === false ? " · 格式不兼容" : "");
        return Qt.formatDateTime(new Date(row.file.modified), "yyyy-MM-dd hh:mm");
    }
    function run(action, paths, extra) {
        lastFailure = "";
        const args = Object.assign({
            kind: kind,
            paths: paths || []
        }, extra || {});
        refreshAfter = true;
        instanceJobs.submit(action, args);
    }
    function confirm(action, paths, extra, heading, message, button) {
        lastFailure = "";
        pendingAction = action;
        pendingArgs = Object.assign({
            kind: kind,
            paths: paths || []
        }, extra || {});
        confirmation.heading = heading;
        confirmation.message = message;
        confirmation.confirmText = button || "确定";
        confirmation.open();
    }
    function deletePaths(paths) {
        confirm("delete", paths, {}, "移到回收站", "将 " + paths.length + " 项" + heading + "移到回收站。" + (shared ? "\n这些文件位于共享目录，其他实例也可能使用。" : ""), "移到回收站");
    }
    function checkUpdates(paths, thenUpdate) {
        updateAfterCheck = !!thenUpdate;
        updateCandidates = paths.length ? paths : rows.map(r => r.file.path);
        instanceJobs.submit("checkUpdates", {
            kind: kind,
            paths: paths.length ? paths : rows.map(r => r.file.path)
        });
    }
    function showUpdates() {
        updateRows = rows.filter(r => updateCandidates.indexOf(r.file.path) >= 0 && online[r.file.path] && online[r.file.path].online && online[r.file.path].online.update && !online[r.file.path].online.isStale);
        updateSelection = updateRows.map(r => r.file.path);
        if (updateRows.length)
            updateDialog.open();
        else
            status = "没有适用的更新";
    }
    function setPack(row, enabled, move) {
        const extra = {
            enabled: enabled,
            move: move || 0,
            incompatible: row.compatible === false
        };
        if (enabled && row.compatible === false && !row.active)
            confirm("setPack", [row.file.path], extra, "资源包格式不兼容", "此资源包声明的格式与当前 Minecraft 不一致，可能出现纹理缺失。是否仍要选用？", "仍然选用");
        else
            run("setPack", [row.file.path], extra);
    }
    function showDetails(row) {
        menuRow = row;
        if (kind === "screenshots") {
            previewRows = filtered;
            previewIndex = Math.max(0, previewRows.findIndex(r => r.file.path === row.file.path));
            preview.open();
        } else
            details.open();
    }
    function showMenu(row, button) {
        menuRow = row;
        const point = button.mapToItem(page, 0, button.height);
        rowMenu.x = Math.max(0, Math.min(page.width - rowMenu.width, point.x));
        rowMenu.y = Math.min(page.height - rowMenu.height, point.y);
        rowMenu.open();
    }
    component Tool: LiteIconButton {
        width: 30
        height: 30
        iconSize: 17
        enabled: !instanceJobs.busy
    }
    Component {
        id: resourceHeader
        Column {
            id: header
            width: page.width - 8
            spacing: 7
            Row {
                width: parent.width
                height: 24
                Text {
                    width: parent.width - 30
                    anchors.verticalCenter: parent.verticalCenter
                    text: (page.shared ? "共享目录 · " : "独立目录 · ") + (page.directory || instanceSettings.gameDirectory + "/" + page.kind)
                    color: "#939ba7"
                    font.pixelSize: 10
                    elide: Text.ElideMiddle
                }
                Tool {
                    width: 24
                    height: 24
                    iconSize: 16
                    source: "qrc:/art/icons/refresh.svg"
                    text: "刷新"
                    onClicked: page.refresh()
                }
            }
            Row {
                width: parent.width
                spacing: 5
                Tool {
                    source: "qrc:/art/icons/folder.svg"
                    text: "打开目录"
                    onClicked: instanceSettings.openDirectory(page.kind)
                }
                Tool {
                    visible: page.kind !== "screenshots"
                    source: "qrc:/art/icons/pack-import.svg"
                    text: "从文件导入"
                    onClicked: filePicker.open()
                }
                Tool {
                    visible: page.kind === "saves" || page.isPack
                    source: "qrc:/art/icons/folder-add.svg"
                    text: "导入文件夹"
                    onClicked: folderPicker.open()
                }
                Tool {
                    visible: page.kind === "mods" || page.isPack
                    source: "qrc:/art/icons/download.svg"
                    text: "下载"
                    onClicked: page.downloadRequested(page.kind === "mods" ? "mod" : page.kind === "shaderpacks" ? "shader" : "resourcepack", instanceSettings.instance.path)
                }
                Rectangle {
                    visible: page.kind === "mods"
                    width: 1
                    height: 18
                    anchors.verticalCenter: parent.verticalCenter
                    color: "#e2e5ea"
                }
                Tool {
                    visible: page.kind === "mods"
                    source: "qrc:/art/icons/update.svg"
                    text: "检查更新"
                    onClicked: page.checkUpdates([], false)
                }
                Tool {
                    visible: page.kind === "mods"
                    source: "qrc:/art/icons/pack-export.svg"
                    text: "批量更新"
                    onClicked: page.checkUpdates([], true)
                }
                Tool {
                    visible: page.kind === "mods"
                    source: "qrc:/art/icons/undo.svg"
                    text: "回退上次变更"
                    onClicked: page.confirm("restoreMods", [], {}, "回退上次 Mod 变更", "恢复上次安装或批量更新前的文件，并撤回当次新增的文件。后续手动修改过的文件会阻止回退。", "回退变更")
                }
                Item {
                    width: Math.max(0, header.width - (page.kind === "mods" ? 258 : page.isPack ? 145 : page.kind === "saves" ? 110 : 40))
                    height: 30
                }
                Text {
                    anchors.verticalCenter: parent.verticalCenter
                    text: page.filtered.length + " 项"
                    color: "#939ba7"
                    font.pixelSize: 11
                }
            }
            Row {
                width: parent.width
                spacing: 8
                LiteTextField {
                    width: parent.width - (page.kind === "mods" || page.isPack ? 112 : 0)
                    height: 32
                    placeholderText: "搜索名称或文件名"
                    text: page.query
                    onTextEdited: page.query = text
                }
                LiteComboBox {
                    width: 104
                    visible: page.kind === "mods" || page.isPack
                    model: page.kind === "mods" ? ["全部", "已启用", "已禁用", "可更新", "存在问题"] : ["全部", "已选用", "未选用", "不兼容"]
                    currentIndex: page.filter
                    onActivated: page.filter = currentIndex
                }
            }
            Item {
                width: 1
                height: 3
            }
        }
    }
    ListView {
        id: list
        x: 0
        y: 0
        width: parent.width - 8
        height: parent.height - y - 58
        visible: page.kind !== "screenshots"
        model: page.kind !== "screenshots" ? page.filtered : []
        header: page.kind !== "screenshots" ? resourceHeader : null
        clip: true
        spacing: 5
        reuseItems: true
        Controls.ScrollBar.vertical: Controls.ScrollBar {
            width: 4
        }
        delegate: Rectangle {
            id: row
            required property var modelData
            width: list.width
            height: 68
            radius: 8
            color: rowHover.hovered ? "#f1f3f6" : "#ffffff"
            border.color: "#e2e5ea"
            HoverHandler {
                id: rowHover
            }
            TapHandler {
                acceptedButtons: Qt.LeftButton
                onTapped: page.toggle(row.modelData.file.path)
                onDoubleTapped: page.showDetails(row.modelData)
            }
            TapHandler {
                acceptedButtons: Qt.RightButton
                onTapped: page.showMenu(row.modelData, menuButton)
            }
            LiteCheckBox {
                id: check
                x: 10
                anchors.verticalCenter: parent.verticalCenter
                width: 16
                checked: page.checked(row.modelData.file.path)
                onClicked: page.toggle(row.modelData.file.path)
            }
            Rectangle {
                x: 36
                anchors.verticalCenter: parent.verticalCenter
                width: 36
                height: 36
                radius: 6
                color: "#f1f3f6"
                Image {
                    anchors.centerIn: parent
                    width: 30
                    height: 30
                    sourceSize: Qt.size(30, 30)
                    source: row.modelData.file.iconPath ? page.fileUrl(row.modelData.file.iconPath) : "qrc:/art/icons/" + (page.kind === "saves" ? "world" : page.kind === "mods" ? "puzzle" : page.kind === "shaderpacks" ? "sun" : "instances") + ".svg"
                    asynchronous: true
                    fillMode: Image.PreserveAspectFit
                    opacity: row.modelData.file.enabled ? 1 : 0.4
                }
            }
            Column {
                x: 82
                width: parent.width - x - 42
                anchors.verticalCenter: parent.verticalCenter
                spacing: 3
                Text {
                    width: parent.width
                    text: row.modelData.file.name
                    font.pixelSize: 12
                    color: row.modelData.file.enabled ? "#454d59" : "#a1a9b4"
                    font.strikeout: !row.modelData.file.enabled
                    elide: Text.ElideRight
                }
                Text {
                    width: parent.width
                    text: page.summary(row.modelData)
                    font.pixelSize: 10
                    color: "#939ba7"
                    elide: Text.ElideRight
                }
                Text {
                    width: parent.width
                    text: page.state(row.modelData)
                    font.pixelSize: 10
                    color: row.modelData.file.error || (row.modelData.diagnostics || []).length ? "#a58257" : "#8994a4"
                    elide: Text.ElideRight
                }
            }
            Tool {
                id: menuButton
                anchors.right: parent.right
                anchors.rightMargin: 5
                anchors.verticalCenter: parent.verticalCenter
                source: "qrc:/art/icons/more.svg"
                text: "更多操作"
                revealed: rowHover.hovered
                enabled: true
                onClicked: page.showMenu(row.modelData, menuButton)
            }
        }
    }
    GridView {
        id: shots
        anchors.fill: list
        visible: page.kind === "screenshots"
        model: page.kind === "screenshots" ? page.filtered : []
        header: page.kind === "screenshots" ? resourceHeader : null
        clip: true
        cellWidth: width / 3
        cellHeight: 135
        Controls.ScrollBar.vertical: Controls.ScrollBar {
            width: 4
        }
        delegate: Rectangle {
            required property var modelData
            width: shots.cellWidth - 6
            height: shots.cellHeight - 6
            radius: 8
            color: "white"
            border.color: "#e2e5ea"
            TapHandler {
                onTapped: page.toggle(modelData.file.path)
                onDoubleTapped: page.showDetails(modelData)
            }
            Image {
                x: 5
                y: 5
                width: parent.width - 10
                height: 82
                sourceSize: Qt.size(280, 164)
                source: page.fileUrl(modelData.file.path)
                asynchronous: true
                fillMode: Image.PreserveAspectCrop
                clip: true
            }
            LiteCheckBox {
                x: 9
                y: 8
                width: 16
                checked: page.checked(modelData.file.path)
                onClicked: page.toggle(modelData.file.path)
            }
            Text {
                x: 9
                y: 93
                width: parent.width - 40
                text: modelData.file.name
                font.pixelSize: 10
                color: "#687384"
                elide: Text.ElideRight
            }
            Tool {
                id: shotMenu
                anchors.right: parent.right
                y: 87
                source: "qrc:/art/icons/more.svg"
                text: "更多操作"
                enabled: true
                onClicked: page.showMenu(modelData, shotMenu)
            }
        }
    }
    Column {
        x: 20
        y: {
            const activeView = page.kind === "screenshots" ? shots : list;
            const top = activeView.headerItem ? activeView.headerItem.height : 0;
            return top + Math.max(0, (list.height - top - implicitHeight) / 2);
        }
        width: list.width - 40
        spacing: 8
        visible: !page.filtered.length
        Text {
            width: parent.width
            text: instanceJobs.busy ? instanceJobs.detail : page.query || page.filter ? "没有匹配的内容" : "暂无" + page.heading
            color: "#8994a4"
            font.pixelSize: 13
            horizontalAlignment: Text.AlignHCenter
            wrapMode: Text.Wrap
        }
        Text {
            width: parent.width
            visible: !instanceJobs.busy
            text: page.kind === "screenshots" ? "在游戏中按 F2 拍摄截图后，会在这里显示。" : "可以从文件导入，或打开目录放入文件。"
            color: "#a1a9b4"
            font.pixelSize: 11
            horizontalAlignment: Text.AlignHCenter
            wrapMode: Text.Wrap
        }
    }
    Column {
        anchors.bottom: parent.bottom
        width: parent.width - 8
        spacing: 5
        Text {
            width: parent.width
            text: instanceJobs.busy ? instanceJobs.detail : page.status || "共 " + page.rows.length + " 项"
            color: "#939ba7"
            font.pixelSize: 10
            elide: Text.ElideRight
        }
        Row {
            spacing: 8
            height: 30
            LiteButton {
                width: 62
                height: 28
                text: "全选"
                enabled: page.filtered.length > 0
                onClicked: page.selected = page.filtered.map(r => r.file.path)
            }
            LiteButton {
                width: 62
                height: 28
                visible: page.kind === "mods"
                text: "启用"
                enabled: page.selected.length > 0 && !instanceJobs.busy
                onClicked: page.run("toggle", page.selected, {
                    enabled: true
                })
            }
            LiteButton {
                width: 62
                height: 28
                visible: page.kind === "mods"
                text: "禁用"
                enabled: page.selected.length > 0 && !instanceJobs.busy
                onClicked: page.run("toggle", page.selected, {
                    enabled: false
                })
            }
            LiteButton {
                width: 62
                height: 28
                visible: page.kind === "mods"
                text: "更新"
                enabled: page.selected.length > 0 && !instanceJobs.busy
                onClicked: page.checkUpdates(page.selected, true)
            }
            Tool {
                source: "qrc:/art/icons/trash.svg"
                text: "删除选中项"
                enabled: page.selected.length > 0 && !instanceJobs.busy
                onClicked: page.deletePaths(page.selected)
            }
            Tool {
                source: "qrc:/art/icons/close.svg"
                text: "取消选择"
                enabled: page.selected.length > 0
                onClicked: page.selected = []
            }
            Text {
                anchors.verticalCenter: parent.verticalCenter
                text: "已选 " + page.selected.length + " 项"
                color: "#939ba7"
                font.pixelSize: 10
            }
        }
    }
    LiteBackToTop {
        anchors.right: parent.right
        anchors.rightMargin: 20
        anchors.bottom: parent.bottom
        anchors.bottomMargin: 68
        flickable: page.kind === "screenshots" ? shots : list
    }
    FileDialog {
        id: filePicker
        title: "导入" + page.heading
        fileMode: FileDialog.OpenFiles
        nameFilters: page.kind === "mods" ? ["Mod (*.jar *.zip *.litemod *.disabled)"] : ["压缩包 (*.zip)"]
        onAccepted: {
            const sources = selectedFiles.map(u => instanceJobs.localPath(u));
            instanceJobs.submit("importPreview", {
                kind: page.kind,
                sources: sources
            });
        }
    }
    FolderDialog {
        id: folderPicker
        title: "导入" + page.heading + "文件夹"
        onAccepted: instanceJobs.submit("importPreview", {
            kind: page.kind,
            sources: [instanceJobs.localPath(selectedFolder)]
        })
    }
    FileDialog {
        id: exportPicker
        title: "导出" + page.heading
        fileMode: FileDialog.SaveFile
        defaultSuffix: page.kind === "saves" ? "zip" : ""
        onAccepted: page.run("exportFile", [page.menuRow.file.path], {
            destination: instanceJobs.localPath(selectedFile)
        })
    }
    LiteMenu {
        id: rowMenu
        width: 202
        padding: 6
        LiteMenuItem {
            text: page.kind === "screenshots" ? "预览" : "详情"
            icon.source: "qrc:/art/icons/image.svg"
            onTriggered: page.showDetails(page.menuRow)
        }
        LiteMenuItem {
            visible: page.kind === "mods"
            height: visible ? implicitHeight : 0
            text: page.menuRow.file.enabled ? "禁用" : "启用"
            icon.source: "qrc:/art/icons/puzzle.svg"
            enabled: !instanceJobs.busy
            onTriggered: page.run("toggle", [page.menuRow.file.path], {
                enabled: !page.menuRow.file.enabled
            })
        }
        LiteMenuItem {
            visible: page.kind === "mods"
            height: visible ? implicitHeight : 0
            text: "检查并更新"
            icon.source: "qrc:/art/icons/update.svg"
            enabled: !instanceJobs.busy
            onTriggered: page.checkUpdates([page.menuRow.file.path], true)
        }
        LiteMenuItem {
            visible: page.kind === "mods"
            height: visible ? implicitHeight : 0
            text: "补齐必要前置"
            icon.source: "qrc:/art/icons/folder-add.svg"
            enabled: !instanceJobs.busy
            onTriggered: page.confirm("repair", [page.menuRow.file.path], {}, "补齐必要前置", "为「" + page.menuRow.file.name + "」检查并下载必要前置，提交前会保留可回退记录。", "开始检查")
        }
        LiteMenuItem {
            visible: page.isPack
            height: visible ? implicitHeight : 0
            text: page.menuRow.active ? "停用" : "选用"
            icon.source: "qrc:/art/icons/check-dark.svg"
            enabled: !instanceJobs.busy
            onTriggered: page.setPack(page.menuRow, !page.menuRow.active, 0)
        }
        LiteMenuItem {
            visible: page.kind === "resourcepacks" && !!page.menuRow.active
            height: visible ? implicitHeight : 0
            text: "提高优先级"
            icon.source: "qrc:/art/icons/pack-export.svg"
            enabled: !instanceJobs.busy
            onTriggered: page.setPack(page.menuRow, true, 1)
        }
        LiteMenuItem {
            visible: page.kind === "resourcepacks" && !!page.menuRow.active
            height: visible ? implicitHeight : 0
            text: "降低优先级"
            icon.source: "qrc:/art/icons/download.svg"
            enabled: !instanceJobs.busy
            onTriggered: page.setPack(page.menuRow, true, -1)
        }
        LiteMenuItem {
            visible: page.kind === "saves"
            height: visible ? implicitHeight : 0
            text: "备份存档"
            icon.source: "qrc:/art/icons/undo.svg"
            enabled: !instanceJobs.busy
            onTriggered: page.run("exportFile", [page.menuRow.file.path], {
                backup: true
            })
        }
        LiteMenuItem {
            text: page.kind === "screenshots" ? "另存为" : "导出"
            icon.source: "qrc:/art/icons/pack-export.svg"
            enabled: !instanceJobs.busy
            onTriggered: exportPicker.open()
        }
        LiteMenuItem {
            visible: page.kind === "screenshots"
            height: visible ? implicitHeight : 0
            text: "复制图片"
            icon.source: "qrc:/art/icons/image.svg"
            onTriggered: instanceJobs.copyImage(page.menuRow.file.path)
        }
        LiteMenuItem {
            text: "在资源管理器中定位"
            icon.source: "qrc:/art/icons/folder.svg"
            onTriggered: instanceJobs.openPath(page.menuRow.file.path, true)
        }
        Controls.MenuSeparator {
            contentItem: Rectangle {
                implicitWidth: 180
                implicitHeight: 1
                color: "#e8ebef"
            }
        }
        LiteMenuItem {
            text: "移到回收站"
            icon.source: "qrc:/art/icons/trash.svg"
            enabled: !instanceJobs.busy
            onTriggered: page.deletePaths([page.menuRow.file.path])
        }
    }
    LiteConfirmDialog {
        id: confirmation
        onConfirmed: {
            page.refreshAfter = true;
            instanceJobs.submit(page.pendingAction, page.pendingArgs);
        }
    }
    LitePopup {
        id: details
        width: 460
        padding: 18
        contentItem: Column {
            spacing: 14
            Text {
                width: parent.width
                text: page.menuRow.file.name || ""
                color: "#454d59"
                font.pixelSize: 16
                elide: Text.ElideRight
            }
            Controls.ScrollView {
                width: parent.width
                height: 220
                clip: true
                Controls.TextArea {
                    width: parent.width
                    readOnly: true
                    selectByMouse: true
                    wrapMode: TextEdit.Wrap
                    font.pixelSize: 12
                    color: "#778292"
                    text: "文件：" + (page.menuRow.file.fileName || "") + "\n版本：" + (page.menuRow.file.version || "未知") + "\n位置：" + (page.menuRow.file.path || "") + "\n\n" + (page.menuRow.file.description || "") + "\n" + (page.menuRow.file.error || "") + "\n" + (page.menuRow.diagnostics || []).join("\n")
                    background: null
                }
            }
            LiteButton {
                anchors.right: parent.right
                width: 80
                height: 30
                text: "关闭"
                onClicked: details.close()
            }
        }
    }
    LitePopup {
        id: preview
        width: Math.min(740, parent.width - 32)
        height: Math.min(390, parent.height - 32)
        padding: 14
        contentItem: Item {
            Image {
                anchors.fill: parent
                anchors.bottomMargin: 40
                source: page.previewRows.length ? page.fileUrl(page.previewRows[page.previewIndex].file.path) : ""
                sourceSize: Qt.size(1400, 650)
                asynchronous: true
                fillMode: Image.PreserveAspectFit
            }
            Row {
                anchors.bottom: parent.bottom
                anchors.horizontalCenter: parent.horizontalCenter
                spacing: 12
                LiteButton {
                    width: 65
                    height: 28
                    text: "上一张"
                    enabled: page.previewIndex > 0
                    onClicked: --page.previewIndex
                }
                Text {
                    width: 270
                    anchors.verticalCenter: parent.verticalCenter
                    text: page.previewRows.length ? page.previewRows[page.previewIndex].file.fileName : ""
                    font.pixelSize: 11
                    color: "#778292"
                    horizontalAlignment: Text.AlignHCenter
                    elide: Text.ElideMiddle
                }
                LiteButton {
                    width: 65
                    height: 28
                    text: "下一张"
                    enabled: page.previewIndex + 1 < page.previewRows.length
                    onClicked: ++page.previewIndex
                }
                LiteIconButton {
                    width: 28
                    height: 28
                    iconSize: 16
                    source: "qrc:/art/icons/close.svg"
                    text: "关闭"
                    onClicked: preview.close()
                }
            }
        }
    }
    LitePopup {
        id: updateDialog
        width: 440
        height: Math.min(350, parent.height - 40)
        padding: 18
        contentItem: Item {
            Text {
                text: "更新 Mod（" + page.updateRows.length + "）"
                color: "#454d59"
                font.pixelSize: 16
                font.weight: Font.DemiBold
            }
            Text {
                y: 30
                width: parent.width
                text: "必要前置会一起检查。下载完成后统一替换，旧文件可回退。"
                font.pixelSize: 11
                color: "#939ba7"
                wrapMode: Text.Wrap
            }
            ListView {
                y: 64
                width: parent.width
                height: parent.height - y - 48
                clip: true
                model: page.updateRows
                delegate: LiteCheckBox {
                    required property var modelData
                    width: ListView.view.width
                    text: modelData.file.name + " → " + page.online[modelData.file.path].online.update.number
                    checked: page.updateSelection.indexOf(modelData.file.path) >= 0
                    onClicked: {
                        const next = page.updateSelection.slice();
                        const index = next.indexOf(modelData.file.path);
                        if (index >= 0)
                            next.splice(index, 1);
                        else
                            next.push(modelData.file.path);
                        page.updateSelection = next;
                    }
                }
            }
            Row {
                anchors.bottom: parent.bottom
                anchors.right: parent.right
                spacing: 10
                LiteButton {
                    width: 80
                    height: 30
                    text: "取消"
                    onClicked: updateDialog.close()
                }
                LiteButton {
                    width: 100
                    height: 30
                    text: "开始更新"
                    primary: true
                    enabled: page.updateSelection.length > 0
                    onClicked: {
                        updateDialog.close();
                        page.run("update", page.updateSelection, {
                            updates: page.updateSelection.map(p => ({
                                        path: p,
                                        online: page.online[p].online
                                    }))
                        });
                    }
                }
            }
        }
    }
    Connections {
        target: instanceJobs
        function onResourcesChanged() {
            if (instanceJobs.busy)
                page.refreshAfter = true;
            else
                page.refresh();
        }
        function onChanged() {
            if (page.refreshAfter && !instanceJobs.busy)
                Qt.callLater(page.refresh);
        }
        function onFailed(action, error, path) {
            if (path !== instanceSettings.instance.path)
                return;
            page.lastFailure = error;
            page.status = error;
            if (page.refreshAfter)
                page.refresh();
        }
        function onCompleted(action, result, args, path) {
            if (path !== instanceSettings.instance.path)
                return;
            if (action === "scan" && args.kind === page.kind) {
                page.rows = result.rows;
                page.directory = result.directory;
                page.shared = result.shared;
                instanceJobs.watchDirectory(page.kind);
                page.selected = page.selected.filter(p => result.rows.some(r => r.file.path === p));
                page.status = page.lastFailure || (result.interrupted && page.kind === "mods" ? "上次 Mod 变更未完成，请先回退上次变更。" : "共 " + result.rows.length + " 项");
            } else if (action === "importPreview" && args.kind === page.kind) {
                if (result.conflicts.length)
                    page.confirm("import", [], {
                        sources: args.sources,
                        overwrite: true
                    }, "替换同名资源", result.conflicts.join("\n") + "\n\n原文件将先移到回收站，再导入所选资源。", "替换");
                else
                    page.run("import", [], {
                        sources: args.sources
                    });
            } else if (action === "checkUpdates" && args.kind === page.kind) {
                const next = Object.assign({}, page.online);
                for (const row of result.rows)
                    next[row.path] = row;
                page.online = next;
                const count = result.rows.filter(r => r.online && r.online.update).length;
                page.status = count + " 项可更新" + (result.rows.some(r => r.error) ? "，部分项目联网失败" : "");
                if (page.updateAfterCheck) {
                    page.updateAfterCheck = false;
                    page.showUpdates();
                }
            } else if (page.refreshAfter && ["import", "delete", "toggle", "update", "repair", "restoreMods", "setPack", "installResource"].indexOf(action) >= 0) {
                page.refresh();
            } else if (action === "exportFile")
                page.status = "已保存：" + result.destination;
        }
    }
}
