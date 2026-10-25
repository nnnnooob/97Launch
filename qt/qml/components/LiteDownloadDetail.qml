import QtQuick
import QtQuick.Controls.Basic as Controls
import QtQuick.Dialogs as Dialogs

Item {
    id: page
    property var detail: ({})
    property bool activeView: true
    property string targetDirectory: ""
    property bool isolated: false
    signal dependencyRequested(var detail, var previous)
    readonly property var project: detail.project || ({})
    readonly property var target: detail.target || ({})
    readonly property string kind: project.projectType || "mod"
    readonly property string projectUrl: project.websiteUrl || (project.source === "CurseForge" ? "https://www.curseforge.com/minecraft/" : "https://modrinth.com/project/" + project.id)
    property string owner: "detail-" + Date.now()
    property var versions: []
    property var fileNames: ({})
    property var groups: []
    property var rows: []
    property var selected: ({})
    property var installed: []
    property int installedRevision: 0
    property var dependencies: []
    property var expanded: ({})
    property var games: ["全部"]
    property var loaders: ["全部"]
    property string gameFilter: ""
    property string loaderFilter: ""
    property bool stableOnly: false
    property bool filterExpanded: false
    property string packName: ""
    property string generatedPackName: ""
    property bool loading: false
    property bool ready: false
    property bool restoreMemory: false
    property string notice: ""
    readonly property int selectedGroup: groups.findIndex(group => group.versions.some(version => version.id === selected.id))
    readonly property bool alreadyInstalled: installed.some(v => v.id === selected.id)
    readonly property bool canInstall: !!selected.primaryFile && compatible(selected) && !alreadyInstalled && (kind === "modpack" ? !!detail.root : !!target.path && (kind !== "datapack" || !!(detail.world || {}).path))
    readonly property string compatibilityHint: alreadyInstalled ? "此版本已安装，无需重复下载。" : !selected.id ? "请选择发布版本" : !compatible(selected) ? "与目标实例不兼容，仅可另存为" : kind !== "modpack" && !target.path ? "选择安装目标后可安装，或使用另存为" : kind === "datapack" && !(detail.world || {}).path ? "未选择存档，仅可另存为" : ""
    function formatCount(count) {
        return count >= 100000000 ? (count / 100000000).toFixed(1) + " 亿" : count >= 10000 ? (count / 10000).toFixed(1) + " 万" : Number(count || 0).toLocaleString(Qt.locale(), "f", 0);
    }
    function age(value) {
        if (!value)
            return "";
        const days = Math.floor((Date.now() - new Date(value).getTime()) / 86400000);
        return days < 1 ? "今天" : days < 31 ? days + " 天前" : new Date(value).toLocaleDateString(Qt.locale());
    }
    function refreshTarget() {
        if (kind === "modpack") {
            targetDirectory = detail.root || "";
            return;
        }
        targetDirectory = "";
        if (target.path && (kind !== "datapack" || (detail.world || {}).path))
            call("downloadTarget", {
                resourceType: kind,
                world: (detail.world || {}).path || ""
            }, target.path);
    }
    function refreshInstalled() {
        installedRevision++;
        installed = [];
        if (activeView && kind === "mod" && target.path) {
            resourceStateJobs.watchDownloadTarget(target.path, "mods");
            catalogJobs.cancelRead("downloadInstalled");
            if (versions.length)
                call("downloadInstalled", {
                    project: project,
                    versions: versions,
                    _revision: installedRevision
                }, target.path);
        }
    }
    function invalidateInstalled() {
        installedRevision++;
        installed = [];
        if (ready && activeView && kind === "mod") installedDelay.restart();
    }
    onActiveViewChanged: {
        if (!ready) return;
        if (activeView) {
            refreshTarget();
            refreshInstalled();
        } else {
            installedRevision++;
            installed = [];
            installedDelay.stop();
        }
    }
    Timer {
        id: installedDelay
        interval: 120
        onTriggered: {
            page.refreshTarget();
            page.refreshInstalled();
        }
    }
    Connections {
        target: resourceStateJobs
        function onResourcesChanged() { page.invalidateInstalled(); }
    }
    Connections {
        target: instanceJobs
        function onResourcesChanged() { page.invalidateInstalled(); }
        function onCompleted(action, result, args, path) {
            if (["downloadResource", "installResource", "import", "delete", "toggle", "update", "repair", "restoreMods", "modify", "recoverModification", "setPack"].indexOf(action) >= 0)
                page.invalidateInstalled();
        }
    }
    function compatible(version) {
        return kind === "mod" || kind === "modpack" || !!version.gameVersions && version.gameVersions.indexOf(detail.game) >= 0 && (!detail.loader || project.source === "CurseForge" || version.loaders.map(v => v.toLowerCase()).indexOf(detail.loader.toLowerCase()) >= 0);
    }
    function call(action, args, path) {
        return catalogJobs.download(action, Object.assign({
            _owner: owner,
            root: detail.root || instanceService.selectedFolder
        }, args || {}), path || "");
    }
    function load() {
        if (detail.type === "minecraft" || !project.id)
            return;
        for (const action of ["downloadDetails", "downloadGroups", "downloadDependencies", "downloadInstalled", "downloadTarget"])
            catalogJobs.cancelRead(action);
        restoreMemory = !!detail.memory;
        owner = "detail-" + Date.now() + "-" + Math.random();
        versions = [];
        rows = [];
        selected = {
            id: (detail.memory || {}).selectedId
        };
        dependencies = [];
        installed = [];
        notice = "";
        loading = true;
        gameFilter = (detail.memory || {}).gameFilter || "";
        loaderFilter = (detail.memory || {}).loaderFilter || "";
        stableOnly = !!(detail.memory || {}).stableOnly;
        packName = (detail.memory || {}).packName || "";
        generatedPackName = "";
        expanded = ({});
        call("downloadDetails", {
            project: project
        });
        refreshTarget();
    }
    function regroup() {
        if (!versions.length)
            return;
        call("downloadGroups", {
            versions: versions,
            game: detail.game || "",
            loader: detail.loader || "",
            gameFilter: gameFilter,
            loaderFilter: loaderFilter,
            stable: stableOnly,
            resourceType: kind
        });
    }
    function select(version) {
        selected = version;
        if (kind === "modpack" && (!packName || packName === generatedPackName)) {
            generatedPackName = (project.title + "-" + version.number).replace(/[<>:"/\\|?*]/g, "").trim();
            packName = generatedPackName;
        }
        dependencies = [];
        if (kind !== "modpack" && version.dependencies && version.dependencies.some(d => d.type === "required"))
            call("downloadDependencies", {
                project: project,
                version: version,
                _version: version.id
            });
    }
    function openDependency(row) {
        const project = row.project;
        const loader = project.projectType === "mod" ? (["Fabric", "Forge", "NeoForge", "Quilt"].indexOf(target.loaderId) >= 0 ? target.loaderId.toLowerCase() : "") : project.projectType === "datapack" ? "datapack" : "";
        const next = Object.assign({}, detail, {
            project: project,
            loader: loader,
            preferred: row.preferred,
            memory: null
        });
        const previous = Object.assign({}, detail, {
            memory: {
                scrollY: list.contentY,
                selectedId: selected.id,
                gameFilter: gameFilter,
                loaderFilter: loaderFilter,
                stableOnly: stableOnly,
                packName: packName
            }
        });
        dependencyRequested(next, previous);
    }
    function submit(destination) {
        const args = {
            project: project,
            version: selected,
            root: detail.root || instanceService.selectedFolder,
            loader: detail.loader || "",
            world: (detail.world || {}).path || "",
            name: packName.trim(),
            title: (destination ? "下载 " : kind === "modpack" ? "安装整合包 · " : "安装 ") + project.title
        };
        if (destination)
            args.destination = destination;
        if (!instanceJobs.download("downloadResource", args, destination || kind === "modpack" ? "" : target.path || ""))
            notice = instanceJobs.error;
    }
    Component.onCompleted: {
        ready = true;
        load();
    }
    onDetailChanged: if (ready)
        load()
    Component.onDestruction: {
        for (const action of ["downloadDetails", "downloadGroups", "downloadDependencies", "downloadInstalled", "downloadTarget"])
            catalogJobs.cancelRead(action);
    }
    Loader {
        anchors.fill: parent
        active: page.detail.type === "minecraft"
        sourceComponent: Component {
            LiteInstanceModify {
                installMode: true
                game: page.detail.version || ""
                installRoot: instanceService.selectedFolder
            }
        }
    }
    Item {
        anchors.fill: parent
        visible: page.detail.type !== "minecraft"
        ListView {
            id: list
            anchors.fill: parent
            anchors.bottomMargin: footer.height + 10
            model: page.rows
            clip: true
            boundsBehavior: Flickable.StopAtBounds
            reuseItems: true
            Controls.ScrollBar.vertical: Controls.ScrollBar {
                width: 4
            }
            header: Column {
                width: list.width - 8
                spacing: 12
                LiteSettingsCard {
                    width: parent.width
                    Item {
                        width: parent.width
                        height: 46
                        Image {
                            width: 40
                            height: 40
                            anchors.verticalCenter: parent.verticalCenter
                            source: page.project.iconUrl || "qrc:/art/blocks/vanilla.png"
                            fillMode: Image.PreserveAspectFit
                            asynchronous: true
                        }
                        Column {
                            x: 52
                            width: parent.width - x
                            spacing: 4
                            anchors.verticalCenter: parent.verticalCenter
                            Text {
                                width: parent.width
                                text: page.project.title || ""
                                color: "#454d59"
                                font.pixelSize: 19
                                font.weight: Font.DemiBold
                                elide: Text.ElideRight
                            }
                            Text {
                                width: parent.width
                                text: page.project.description || ""
                                color: "#939ba7"
                                font.pixelSize: 11
                                elide: Text.ElideRight
                            }
                        }
                    }
                    Item {
                        width: parent.width
                        height: 30
                        Text {
                            width: parent.width - 274
                            anchors.verticalCenter: parent.verticalCenter
                            text: (page.project.source || "") + " · 下载 " + page.formatCount(page.project.downloads) + " 次" + (page.project.updated ? " · 更新于 " + page.age(page.project.updated) : "")
                            color: "#939ba7"
                            font.pixelSize: 11
                            elide: Text.ElideRight
                        }
                        Row {
                            anchors.right: parent.right
                            spacing: 8
                            LiteButton {
                                width: 82
                                height: 28
                                text: "项目主页"
                                onClicked: Qt.openUrlExternally(page.projectUrl)
                            }
                            LiteButton {
                                width: 82
                                height: 28
                                text: "复制名称"
                                onClicked: instanceJobs.copyText(page.project.title || "")
                            }
                            LiteButton {
                                width: 82
                                height: 28
                                text: "复制链接"
                                onClicked: instanceJobs.copyText(page.projectUrl)
                            }
                        }
                    }
                }
                LiteSettingRow {
                    visible: page.kind === "modpack"
                    label: "实例名称"
                    labelWidth: 64
                    LiteTextField {
                        width: parent.width
                        height: 34
                        placeholderText: "为新实例命名"
                        text: page.packName
                        onTextEdited: page.packName = text
                    }
                }
                LiteSettingsCard {
                    width: parent.width
                    LiteFilterChips {
                        label: "游戏版本"
                        choices: page.games
                        selected: page.gameFilter || "全部"
                        onSelectedValue: function (value) {
                            page.gameFilter = value === "全部" ? "" : value;
                            page.regroup();
                        }
                    }
                    Item {
                        width: parent.width
                        height: Math.max(30, loaderChips.implicitHeight)
                        LiteFilterChips {
                            id: loaderChips
                            width: parent.width - 116
                            label: "加载器"
                            choices: page.loaders
                            selected: page.loaderFilter || "全部"
                            onSelectedValue: function (value) {
                                page.loaderFilter = value === "全部" ? "" : value;
                                page.regroup();
                            }
                        }
                        LiteCheckBox {
                            anchors.right: parent.right
                            anchors.verticalCenter: parent.verticalCenter
                            text: "仅正式版"
                            checked: page.stableOnly
                            onClicked: {
                                page.stableOnly = checked;
                                page.regroup();
                            }
                        }
                    }
                }
                Text {
                    width: parent.width
                    visible: page.loading || !!page.notice
                    text: page.loading ? "正在获取发布版本…" : page.notice
                    color: page.loading ? "#939ba7" : "#b27d53"
                    wrapMode: Text.Wrap
                    font.pixelSize: 11
                }
                LiteButton {
                    width: 84
                    height: 28
                    visible: !page.loading && !page.versions.length
                    text: "重新加载"
                    onClicked: page.load()
                }
                Item {
                    width: 1
                    height: 1
                }
            }
            delegate: Loader {
                required property var modelData
                width: list.width - 8
                height: modelData.header ? 44 : !page.expanded[modelData.group] ? 0 : modelData.dependency ? page.dependencies.length && page.selectedGroup === modelData.group && item ? item.implicitHeight : 0 : modelData.label ? 28 : modelData.empty ? 44 : modelData.last ? 72 : 62
                active: modelData.header || !!page.expanded[modelData.group] && (!modelData.dependency || page.dependencies.length > 0 && page.selectedGroup === modelData.group)
                visible: height > 0
                clip: true
                Behavior on height {
                    NumberAnimation {
                        duration: 100
                        easing.type: Easing.OutCubic
                    }
                }
                sourceComponent: modelData.header ? releaseGroupHeader : modelData.dependency ? dependencyGroup : modelData.label || modelData.empty ? groupLabel : releaseRow
            }
        }
        LiteBackToTop {
            anchors.right: parent.right
            anchors.rightMargin: 20
            anchors.bottom: parent.bottom
            anchors.bottomMargin: footer.height + 20
            flickable: list
        }
        Item {
            id: footer
            anchors.bottom: parent.bottom
            width: parent.width - 8
            height: page.compatibilityHint ? 68 : 54
            Rectangle {
                width: parent.width
                height: 1
                color: "#e2e5ea"
            }
            Column {
                y: 10
                width: parent.width - 200
                spacing: 3
                Text {
                    width: parent.width
                    text: page.selected.number || "请选择发布版本"
                    color: "#454d59"
                    font.pixelSize: 12
                    font.weight: Font.DemiBold
                    elide: Text.ElideRight
                }
                Text {
                    width: parent.width
                    text: page.targetDirectory || (page.kind === "modpack" ? page.detail.root || "" : "未选择安装目录")
                    color: "#939ba7"
                    font.pixelSize: 10
                    elide: Text.ElideMiddle
                    LiteToolTip {
                        visible: targetHover.containsMouse
                        text: parent.text
                    }
                    MouseArea {
                        id: targetHover
                        anchors.fill: parent
                        hoverEnabled: true
                        acceptedButtons: Qt.NoButton
                    }
                }
                Text {
                    width: parent.width
                    visible: !!page.compatibilityHint
                    text: page.compatibilityHint
                    color: "#939ba7"
                    font.pixelSize: 10
                    elide: Text.ElideRight
                }
            }
            Row {
                anchors.right: parent.right
                anchors.verticalCenter: parent.verticalCenter
                spacing: 10
                LiteButton {
                    width: 86
                    height: 34
                    text: "另存为"
                    enabled: !!page.selected.primaryFile
                    onClicked: {
                        const name = page.fileNames[page.selected.id] || page.selected.primaryFile.name;
                        const suffix = name.indexOf(".") >= 0 ? name.slice(name.lastIndexOf(".") + 1) : "zip";
                        saveFile.nameFilters = ["资源文件 (*." + suffix + ")", "所有文件 (*)"];
                        saveFile.defaultSuffix = suffix;
                        saveFile.selectedFile = instanceJobs.saveSuggestion(name);
                        saveFile.open();
                    }
                }
                LiteButton {
                    width: 86
                    height: 34
                    primary: true
                    text: "安装"
                    enabled: page.canInstall && !!page.targetDirectory && (page.kind !== "modpack" || !!page.packName.trim())
                    onClicked: page.submit("")
                }
            }
        }
    }
    Component {
        id: dependencyGroup
        Item {
            implicitHeight: card.implicitHeight + 8
            LiteSettingsCard {
                id: card
                width: parent.width
                title: "必选前置资源"
                Repeater {
                    model: page.dependencies
                    Controls.ItemDelegate {
                        id: dependencyButton
                        required property var modelData
                        width: parent.width
                        height: 52
                        contentItem: Item {
                            Image {
                                width: 30
                                height: 30
                                anchors.verticalCenter: parent.verticalCenter
                                source: (modelData.project || {}).iconUrl || "qrc:/art/blocks/fabric.png"
                                fillMode: Image.PreserveAspectFit
                                asynchronous: true
                            }
                            Column {
                                x: 42
                                width: parent.width - x
                                anchors.verticalCenter: parent.verticalCenter
                                spacing: 4
                                Text {
                                    width: parent.width
                                    text: modelData.project ? modelData.project.title : "前置信息获取失败"
                                    elide: Text.ElideRight
                                    font.pixelSize: 12
                                    color: "#454d59"
                                }
                                Text {
                                    width: parent.width
                                    text: modelData.project ? "安装时自动检查并补齐" : modelData.error + "，点击重试"
                                    elide: Text.ElideRight
                                    font.pixelSize: 10
                                    color: "#939ba7"
                                }
                            }
                        }
                        background: Rectangle {
                            radius: 6
                            color: dependencyButton.down ? "#e7ebf0" : dependencyButton.hovered ? "#f4f6f8" : "transparent"
                        }
                        onClicked: {
                            if (modelData.project)
                                page.openDependency(modelData);
                            else
                                page.call("downloadDependencies", {
                                    project: page.project,
                                    version: page.selected,
                                    _version: page.selected.id
                                });
                        }
                    }
                }
            }
        }
    }
    Component {
        id: groupLabel
        Rectangle {
            property var entry: parent ? parent.modelData : ({})
            color: "#ffffff"
            border.color: "#e2e5ea"
            Text {
                x: 14
                anchors.verticalCenter: parent.verticalCenter
                width: parent.width - 28
                text: parent.entry.empty ? page.stableOnly ? "暂无匹配的正式版本，可取消“仅正式版”查看。" : "暂无适合此实例的版本。" : "版本列表"
                font.pixelSize: 11
                color: "#939ba7"
                wrapMode: Text.Wrap
            }
        }
    }
    Component {
        id: releaseGroupHeader
        Item {
            property var entry: parent ? parent.modelData : ({})
            LiteListGroupHeader {
                y: 8
                width: parent.width
                height: 36
                title: parent.entry.title || ""
                count: parent.entry.count || 0
                expanded: !!page.expanded[parent.entry.group]
                onClicked: {
                    const next = Object.assign({}, page.expanded);
                    next[parent.entry.group] = !next[parent.entry.group];
                    page.expanded = next;
                }
            }
        }
    }
    Component {
        id: releaseRow
        Item {
            property var entry: parent ? parent.modelData : ({})
            LiteListGroupRow {
                id: releaseButton
                property var entry: parent.entry
                width: parent.width
                height: 62
                last: !!entry.last
                Accessible.name: entry.name || "发布版本"
                selected: page.selected.id === entry.id
                LiteToolTip {
                    visible: parent.hovered
                    delay: 500
                    text: (entry.name || "") + "\n" + ((entry.primaryFile || {}).name || "需前往项目主页下载") + (entry.published ? "\n发布时间：" + new Date(entry.published).toLocaleDateString(Qt.locale()) : "") + (entry.primaryFile ? " · " + (entry.primaryFile.size / 1048576).toFixed(2) + " MB" : "")
                }
                contentItem: Item {
                    Rectangle {
                        x: 13
                        width: 30
                        height: 34
                        radius: 6
                        anchors.verticalCenter: parent.verticalCenter
                        color: "#edf0f4"
                        Text {
                            anchors.centerIn: parent
                            text: releaseButton.entry.type === "release" ? "R" : releaseButton.entry.type === "beta" ? "B" : "A"
                            color: "#778292"
                            font.pixelSize: 16
                            font.weight: Font.DemiBold
                        }
                    }
                    Column {
                        x: 54
                        width: parent.width - x - 38
                        anchors.verticalCenter: parent.verticalCenter
                        spacing: 4
                        Text {
                            width: parent.width
                            text: releaseButton.entry.name || ""
                            font.pixelSize: 12
                            color: "#454d59"
                            elide: Text.ElideRight
                        }
                        Text {
                            width: parent.width
                            text: ((releaseButton.entry.primaryFile || {}).name || releaseButton.entry.number || "") + (releaseButton.entry.downloads !== undefined && releaseButton.entry.downloads !== null ? " · 下载 " + page.formatCount(releaseButton.entry.downloads) + " 次" : "") + (releaseButton.entry.published ? " · 更新于 " + page.age(releaseButton.entry.published) : "") + " · " + (releaseButton.entry.type === "release" ? "正式版" : releaseButton.entry.type === "beta" ? "测试版" : "预览版")
                            font.pixelSize: 10
                            color: "#939ba7"
                            elide: Text.ElideRight
                        }
                        Text {
                            visible: page.installed.some(v => v.id === releaseButton.entry.id)
                            text: "已安装"
                            font.pixelSize: 10
                            color: "#778292"
                        }
                    }
                    Text {
                        anchors.right: parent.right
                        anchors.rightMargin: 12
                        anchors.verticalCenter: parent.verticalCenter
                        text: releaseButton.selected ? "✓" : ""
                        font.pixelSize: 14
                        color: "#7f8998"
                    }
                }
                onClicked: page.select(entry)
            }
        }
    }
    Dialogs.FileDialog {
        id: saveFile
        title: "保存资源文件"
        fileMode: Dialogs.FileDialog.SaveFile
        nameFilters: ["资源文件 (*.jar *.zip *.mrpack)", "所有文件 (*)"]
        onAccepted: page.submit(instanceJobs.localPath(selectedFile))
    }
    Connections {
        target: catalogJobs
        function onFailedDetailed(action, error, args, path) {
            if (args._owner === page.owner) {
                page.loading = false;
                page.notice = error;
            }
        }
        function onCompleted(action, result, args, path) {
            if (args._owner !== page.owner)
                return;
            if (action === "downloadDetails") {
                page.versions = result.versions;
                page.fileNames = result.fileNames;
                page.games = ["全部"].concat(result.games);
                page.loaders = ["全部"].concat(result.loaders);
                page.loading = false;
                page.regroup();
                page.refreshInstalled();
            } else if (action === "downloadTarget") {
                page.targetDirectory = result.directory;
                page.isolated = result.isolated;
            } else if (action === "downloadGroups") {
                if (args.gameFilter !== page.gameFilter || args.loaderFilter !== page.loaderFilter || args.stable !== page.stableOnly)
                    return;
                page.groups = result.groups;
                const out = [], expanded = {};
                let options = [];
                for (let g = 0; g < result.groups.length; ++g) {
                    const group = result.groups[g];
                    expanded[g] = group.isExpanded;
                    out.push({
                        header: true,
                        group: g,
                        title: (g === 0 && page.kind !== "modpack" && page.detail.game && (group.title === page.detail.game || group.title.endsWith(" " + page.detail.game)) ? "所选版本：" : "") + group.title,
                        count: group.versions.length
                    });
                    out.push({
                        dependency: true,
                        group: g
                    });
                    out.push({
                        label: group.versions.length > 0,
                        empty: group.versions.length === 0,
                        group: g
                    });
                    for (let r = 0; r < group.versions.length; ++r) {
                        const version = group.versions[r];
                        out.push(Object.assign({
                            group: g,
                            last: r === group.versions.length - 1
                        }, version));
                        options.push(version);
                    }
                }
                page.rows = out;
                page.expanded = expanded;
                if (page.restoreMemory && page.detail.memory && page.detail.memory.scrollY)
                    Qt.callLater(function () {
                        list.contentY = Math.max(list.originY, Math.min(page.detail.memory.scrollY, list.contentHeight - list.height));
                    });
                page.restoreMemory = false;
                const next = options.find(v => v.id === (page.selected.id || page.detail.preferred)) || options.find(v => page.compatible(v) && v.type === "release") || options.find(v => page.compatible(v)) || options[0];
                if (next)
                    page.select(next);
                else {
                    page.selected = ({});
                    page.notice = "没有匹配的发布版本，请调整筛选条件。";
                }
            } else if (action === "downloadDependencies" && args._version === page.selected.id)
                page.dependencies = result.rows;
            else if (action === "downloadInstalled" && args._revision === page.installedRevision && page.activeView && path === page.target.path)
                page.installed = result.rows;
        }
    }
}
