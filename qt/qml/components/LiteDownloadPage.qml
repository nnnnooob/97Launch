import QtQuick
import QtQuick.Controls.Basic as Controls
import "LiteMotion.js" as Motion

Item {
    id: page
    property string section: "minecraft"
    property string displayedSection: "minecraft"
    property bool ready: false
    signal detailsRequested(var detail)
    property var manifest: []
    property var games: ["全部", "1.21.4", "1.21.1", "1.20.6", "1.20.1", "1.19.2", "1.16.5", "1.12.2"]
    property var expanded: ({
            "0": true
        })
    property var projects: []
    property var installed: ({})
    property var worlds: []
    property int targetIndex: 0
    property int worldIndex: 0
    property int resultPage: 0
    property bool hasMore: false
    property bool loading: false
    property string notice: ""
    property string owner: "catalog-" + Date.now()
    property string query: ""
    property string source: "全部"
    property string tag: "全部"
    property string sort: "热度"
    property string game: "全部"
    property string loader: "任意加载器"
    property string resolution: "全部分辨率"
    property var sectionCache: ({})
    property bool loaded: false
    property string targetSignature: ""
    property int requestId: 0
    property int installedRevision: 0
    property string requestedTargetPath: ""
    readonly property var targets: displayedSection === "modpack" ? instanceService.folders : instanceService.available
    readonly property var target: targets[targetIndex] || ({})
    readonly property string heading: ({
            minecraft: "Minecraft",
            mod: "Mod",
            modpack: "整合包",
            shader: "光影包",
            resourcepack: "资源包",
            datapack: "数据包"
        })[displayedSection]
    readonly property string targetLoader: displayedSection === "mod" ? (["Fabric", "Forge", "NeoForge", "Quilt"].indexOf(target.loaderId) >= 0 ? target.loaderId.toLowerCase() : "") : displayedSection === "datapack" ? "datapack" : displayedSection === "shader" && loader !== "任意加载器" ? loader.toLowerCase() : ""
    readonly property var rows: {
        if (displayedSection !== "minecraft")
            return projects;
        const out = [];
        for (let g = 0; g < manifest.length; ++g) {
            out.push({
                header: true,
                group: g,
                title: manifest[g].title,
                count: manifest[g].rows.length
            });
            for (let r = 0; r < manifest[g].rows.length; ++r)
                out.push(Object.assign({
                    group: g,
                    last: r === manifest[g].rows.length - 1
                }, manifest[g].rows[r]));
        }
        return out;
    }
    function groupExpanded(group) {
        return group === 0 || !!expanded[group];
    }
    function toggleGroup(group) {
        if (group === 0)
            return;
        const next = Object.assign({}, expanded);
        next[group] = !next[group];
        expanded = next;
    }
    function call(action, args, path) {
        return catalogJobs.download(action, Object.assign({
            _owner: owner,
            _section: displayedSection
        }, args || {}), path || "");
    }
    function loadManifest(force) {
        loading = true;
        notice = "";
        call("downloadManifest", {
            force: !!force
        });
    }
    function scanInstalled() {
        installedRevision++;
        resourceStateJobs.cancelRead("downloadStates");
        installed = ({});
        if (visible && displayedSection === "mod" && target.path) {
            resourceStateJobs.watchDownloadTarget(target.path, "mods");
            resourceStateJobs.download("downloadStates", {
                _owner: owner,
                _revision: installedRevision
            }, target.path);
        }
    }
    function invalidateInstalled() {
        installedRevision++;
        installed = ({});
        const cached = Object.assign({}, sectionCache);
        for (const key in cached)
            cached[key] = Object.assign({}, cached[key], { installed: ({}), installedDirty: true });
        sectionCache = cached;
        if (visible && ready && displayedSection === "mod") installedDelay.restart();
    }
    function requestTarget(path) {
        requestedTargetPath = path;
        if (!ready || displayedSection !== section || instanceService.loading) return;
        const index = targets.findIndex(row => samePath(row.path, path));
        if (index < 0) return;
        targetIndex = index;
        requestedTargetPath = "";
        applyTarget();
    }
    function samePath(left, right) {
        return (left || "").replace(/\\/g, "/").replace(/\/+$/, "").toLowerCase()
            === (right || "").replace(/\\/g, "/").replace(/\/+$/, "").toLowerCase();
    }
    onVisibleChanged: {
        if (!ready) return;
        if (visible) scanInstalled();
        else {
            installedRevision++;
            installed = ({});
            installedDelay.stop();
            resourceStateJobs.cancelRead("downloadStates");
        }
    }
    function applyTarget() {
        targetSignature = (target.path || "") + "|" + (target.version || "") + "|" + (target.loaderId || "");
        worlds = [];
        worldIndex = 0;
        if (displayedSection !== "modpack")
            game = target.version || "全部";
        if (displayedSection === "mod")
            loader = ["Fabric", "Forge", "NeoForge", "Quilt"].indexOf(target.loaderId) >= 0 ? target.loaderId : "任意加载器";
        else if (displayedSection === "shader")
            loader = target.loaderId === "OptiFine" ? "OptiFine" : "任意加载器";
        if (displayedSection === "datapack" && target.path)
            call("downloadWorlds", {}, target.path);
        scanInstalled();
        scheduleSearch();
    }
    function scheduleSearch() {
        if (displayedSection === "minecraft")
            return;
        loaded = false;
        resultPage = 0;
        searchDelay.restart();
    }
    function search() {
        if (displayedSection === "minecraft")
            return;
        loaded = false;
        loading = true;
        notice = "";
        requestId++;
        catalogJobs.cancelRead("downloadSearch");
        const categories = {
            科技: "technology",
            魔法: "magic",
            冒险: "adventure",
            优化: "optimization",
            装饰: "decoration",
            实用: "utility",
            世界生成: "worldgen",
            原版风格: "vanilla-like",
            写实风格: "realistic",
            幻想风格: "fantasy"
        };
        call("downloadSearch", {
            resourceType: displayedSection,
            query: query,
            source: source,
            category: categories[tag] || "",
            sort: sort,
            game: game,
            loader: loader,
            resolution: displayedSection === "resourcepack" && resolution !== "全部分辨率" ? resolution : "",
            offset: resultPage * 50,
            _section: displayedSection,
            _page: resultPage,
            _request: requestId
        });
    }
    function openProject(project) {
        page.detailsRequested({
            type: "resource",
            project: project,
            target: target,
            world: worlds[worldIndex] || ({}),
            loader: targetLoader,
            game: target.version || "",
            root: displayedSection === "modpack" ? target.path : target.root || instanceService.selectedFolder
        });
    }
    Component.onCompleted: {
        const preference = catalogJobs.downloadPreferences().CommunitySource;
        source = preference === "CurseForge" || preference === "Modrinth" ? preference : "全部";
        displayedSection = section;
        ready = true;
        activateSection();
    }
    Component.onDestruction: {
        catalogJobs.cancelRead("downloadSearch");
        catalogJobs.cancelRead("downloadManifest");
        catalogJobs.cancelRead("downloadWorlds");
        resourceStateJobs.cancelRead("downloadStates");
    }
    onSectionChanged: {
        if (!ready)
            return;
        rememberSection();
        forceActiveFocus();
        searchDelay.stop();
        enterMotion.stop();
        exitMotion.restart();
    }
    function rememberSection() {
        const cached = Object.assign({}, sectionCache);
        cached[displayedSection] = {
            projects: projects,
            installed: installed,
            worlds: worlds,
            worldIndex: worldIndex,
            targetPath: target.path || "",
            targetSignature: targetSignature,
            resultPage: resultPage,
            hasMore: hasMore,
            loaded: loaded && !loading && !searchDelay.running,
            notice: notice,
            query: query,
            source: source,
            tag: tag,
            sort: sort,
            game: game,
            loader: loader,
            resolution: resolution
        };
        sectionCache = cached;
    }
    function activateSection() {
        searchDelay.stop();
        requestId++;
        catalogJobs.cancelRead("downloadSearch");
        catalogJobs.cancelRead("downloadWorlds");
        const cached = sectionCache[displayedSection];
        const preference = catalogJobs.downloadPreferences().CommunitySource;
        const selectedPath = displayedSection === "modpack" ? instanceService.selectedFolder : instanceService.current.path;
        const wanted = requestedTargetPath || (cached ? cached.targetPath : selectedPath);
        const wantedIndex = targets.findIndex(row => samePath(row.path, wanted));
        const explicitTarget = !!requestedTargetPath && wantedIndex >= 0;
        targetIndex = Math.max(0, wantedIndex);
        projects = cached ? cached.projects : [];
        installed = ({});
        worlds = cached ? cached.worlds : [];
        worldIndex = cached ? cached.worldIndex : 0;
        resultPage = cached ? cached.resultPage : 0;
        hasMore = cached ? cached.hasMore : false;
        loading = false;
        loaded = !!cached && cached.loaded;
        notice = cached ? cached.notice : "";
        query = cached ? cached.query : "";
        source = cached ? cached.source : preference === "CurseForge" || preference === "Modrinth" ? preference : "全部";
        tag = cached ? cached.tag : "全部";
        sort = cached ? cached.sort : "热度";
        game = cached ? cached.game : "全部";
        loader = cached ? cached.loader : "任意加载器";
        resolution = cached ? cached.resolution : "全部分辨率";
        targetSignature = (target.path || "") + "|" + (target.version || "") + "|" + (target.loaderId || "");
        let scanned = false;
        if (displayedSection === "minecraft") {
            loaded = manifest.length > 0;
            if (!loaded)
                loadManifest(false);
        } else if (explicitTarget || !cached || cached.targetSignature !== targetSignature) {
            applyTarget();
            scanned = true;
        } else if (!loaded) {
            searchDelay.restart();
        }
        if (wantedIndex >= 0) requestedTargetPath = "";
        if (!scanned) scanInstalled();
        Qt.callLater(function () {
            list.cancelFlick();
            list.positionViewAtBeginning();
        });
    }
    function resetFilters() {
        const preference = catalogJobs.downloadPreferences().CommunitySource;
        source = preference === "CurseForge" || preference === "Modrinth" ? preference : "全部";
        tag = "全部";
        sort = "热度";
        game = "全部";
        loader = "任意加载器";
        resolution = "全部分辨率";
        query = "";
        scheduleSearch();
    }
    Connections {
        target: instanceService
        function onChanged() {
            if (page.ready && !instanceService.loading && page.requestedTargetPath) {
                page.requestTarget(page.requestedTargetPath);
                return;
            }
            if (page.targetIndex >= page.targets.length)
                page.targetIndex = 0;
            const signature = (page.target.path || "") + "|" + (page.target.version || "") + "|" + (page.target.loaderId || "");
            if (page.ready && !instanceService.loading && page.displayedSection !== "minecraft" && signature !== page.targetSignature)
                page.applyTarget();
        }
    }
    Timer {
        id: installedDelay
        interval: 120
        onTriggered: page.scanInstalled()
    }
    Timer {
        id: searchDelay
        interval: 250
        onTriggered: page.search()
    }
    Item {
        id: content
        anchors.fill: parent
        enabled: !exitMotion.running && page.section === page.displayedSection
        ListView {
            id: list
            anchors.fill: parent
            clip: true
            model: page.rows
            spacing: page.displayedSection === "minecraft" ? 0 : 8
            boundsBehavior: Flickable.StopAtBounds
            reuseItems: true
            Controls.ScrollBar.vertical: Controls.ScrollBar {
                width: 4
            }
            header: Column {
                width: list.width - 8
                spacing: 10
                LiteSettingsCard {
                    visible: page.displayedSection !== "minecraft"
                    width: parent.width
                    LiteSettingRow {
                        label: page.displayedSection === "modpack" ? "游戏文件夹" : "安装到"
                        labelWidth: 64
                        LiteComboBox {
                            width: parent.width
                            model: page.targets
                            textRole: "name"
                            currentIndex: page.targetIndex
                            displayText: page.targets.length ? currentText : "选择安装目标"
                            onActivated: {
                                page.targetIndex = currentIndex;
                                page.applyTarget();
                            }
                        }
                    }
                    LiteSettingRow {
                        visible: page.displayedSection === "datapack"
                        label: "存档世界"
                        labelWidth: 64
                        LiteComboBox {
                            width: parent.width
                            model: page.worlds
                            textRole: "name"
                            currentIndex: page.worldIndex
                            displayText: page.worlds.length ? currentText : "暂无可用存档，可在详情中另存为"
                            onActivated: page.worldIndex = currentIndex
                        }
                    }
                }
                Row {
                    visible: page.displayedSection !== "minecraft"
                    width: parent.width
                    spacing: 8
                    LiteTextField {
                        width: parent.width - 132
                        height: 34
                        placeholderText: "搜索" + page.heading
                        text: page.query
                        onTextEdited: {
                            page.query = text;
                            page.scheduleSearch();
                        }
                        onAccepted: {
                            searchDelay.stop();
                            page.resultPage = 0;
                            page.search();
                        }
                    }
                    LiteComboBox {
                        width: 124
                        height: 34
                        Accessible.name: "来源"
                        displayText: "来源：" + page.source
                        hint: "资源来源：" + page.source
                        model: ["全部", "Modrinth", "CurseForge"]
                        currentIndex: Math.max(0, model.indexOf(page.source))
                        onActivated: {
                            page.source = currentText;
                            page.scheduleSearch();
                        }
                    }
                }
                Row {
                    id: filters
                    visible: page.displayedSection !== "minecraft"
                    width: parent.width
                    spacing: 7
                    readonly property bool showLoader: ["mod", "modpack", "shader"].indexOf(page.displayedSection) >= 0
                    readonly property bool showResolution: page.displayedSection === "resourcepack"
                    readonly property int count: 2 + (showLoader ? 1 : 0) + (showResolution ? 1 : 0)
                    readonly property real fieldWidth: (width - 60 - count * spacing) / count
                    LiteEditableComboBox {
                        width: filters.fieldWidth
                        height: 32
                        model: page.game !== "全部" && page.games.indexOf(page.game) < 0 ? ["全部", page.game].concat(page.games.filter(value => value !== "全部")) : page.games
                        Accessible.name: "游戏版本"
                        fieldLabel: "版本"
                        hint: "游戏版本，支持直接输入版本号"
                        currentIndex: Math.max(0, model.indexOf(page.game))
                        editText: page.game
                        onAccepted: {
                            page.game = editText.trim() || "全部";
                            page.scheduleSearch();
                        }
                        onActivated: {
                            page.game = currentText;
                            page.scheduleSearch();
                        }
                    }
                    LiteComboBox {
                        visible: filters.showLoader
                        width: filters.fieldWidth
                        Accessible.name: "加载器"
                        displayText: "加载器：" + page.loader
                        hint: "加载器：" + page.loader
                        model: page.displayedSection === "shader" ? ["任意加载器", "Iris", "OptiFine"] : ["任意加载器", "Fabric", "Forge", "NeoForge", "Quilt"]
                        currentIndex: Math.max(0, model.indexOf(page.loader))
                        onActivated: {
                            page.loader = currentText;
                            page.scheduleSearch();
                        }
                    }
                    LiteComboBox {
                        width: filters.fieldWidth
                        Accessible.name: "分类"
                        displayText: "分类：" + page.tag
                        hint: "资源分类：" + page.tag
                        model: page.displayedSection === "shader" || page.displayedSection === "resourcepack" ? ["全部", "原版风格", "写实风格", "幻想风格", "装饰"] : ["全部", "科技", "魔法", "冒险", "优化", "装饰", "实用", "世界生成"]
                        currentIndex: Math.max(0, model.indexOf(page.tag))
                        onActivated: {
                            page.tag = currentText;
                            page.scheduleSearch();
                        }
                    }
                    LiteComboBox {
                        visible: filters.showResolution
                        width: filters.fieldWidth
                        Accessible.name: "分辨率"
                        displayText: "分辨率：" + page.resolution
                        hint: "资源包分辨率：" + page.resolution
                        model: ["全部分辨率", "16x", "32x", "64x", "128x", "256x", "512x+"]
                        currentIndex: Math.max(0, model.indexOf(page.resolution))
                        onActivated: {
                            page.resolution = currentText;
                            page.scheduleSearch();
                        }
                    }
                    LiteButton {
                        width: 60
                        height: 32
                        text: "重置"
                        onClicked: page.resetFilters()
                    }
                }
                Item {
                    visible: page.displayedSection !== "minecraft"
                    width: parent.width
                    height: 32
                    Text {
                        anchors.verticalCenter: parent.verticalCenter
                        width: parent.width - 160
                        text: "第 " + (page.resultPage + 1) + " 页 · " + page.projects.length + " 项"
                        font.pixelSize: 11
                        color: "#939ba7"
                    }
                    LiteComboBox {
                        anchors.right: refresh.left
                        anchors.rightMargin: 8
                        width: 112
                        model: ["热度", "最新", "下载量"]
                        Accessible.name: "排序"
                        displayText: "排序：" + page.sort
                        hint: "结果排序：" + page.sort
                        currentIndex: Math.max(0, model.indexOf(page.sort))
                        onActivated: {
                            page.sort = currentText;
                            page.scheduleSearch();
                        }
                    }
                    LiteIconButton {
                        id: refresh
                        anchors.right: parent.right
                        width: 28
                        height: 28
                        iconSize: 17
                        source: "qrc:/art/icons/refresh.svg"
                        text: "刷新"
                        onClicked: {
                            searchDelay.stop();
                            page.search();
                        }
                    }
                }
                Text {
                    width: parent.width
                    visible: page.loading || !!page.notice
                    text: page.loading ? "正在加载…" : page.notice
                    color: page.loading ? "#939ba7" : "#b27d53"
                    font.pixelSize: 11
                    wrapMode: Text.Wrap
                }
                Item {
                    width: 1
                    height: 2
                }
            }
            delegate: Loader {
                required property var modelData
                width: list.width - 8
                height: page.displayedSection === "minecraft" ? modelData.header ? 44 : page.groupExpanded(modelData.group) ? (modelData.last ? 64 : 54) : 0 : 92
                active: modelData.header || height > 0
                visible: height > 0
                clip: true
                Behavior on height {
                    NumberAnimation {
                        duration: 100
                        easing.type: Easing.OutCubic
                    }
                }
                sourceComponent: modelData.header ? versionGroupHeader : downloadRow
            }
            footer: Item {
                width: list.width
                height: page.displayedSection !== "minecraft" ? 64 : 12
                Row {
                    visible: page.displayedSection !== "minecraft"
                    anchors.centerIn: parent
                    spacing: 12
                    LiteButton {
                        width: 70
                        height: 30
                        text: "上一页"
                        enabled: page.resultPage > 0 && !page.loading
                        onClicked: {
                            page.resultPage--;
                            page.search();
                            list.positionViewAtBeginning();
                        }
                    }
                    Text {
                        anchors.verticalCenter: parent.verticalCenter
                        text: "第 " + (page.resultPage + 1) + " 页 · " + page.projects.length + " 项"
                        font.pixelSize: 11
                        color: "#939ba7"
                    }
                    LiteButton {
                        width: 70
                        height: 30
                        text: "下一页"
                        enabled: page.hasMore && !page.loading
                        onClicked: {
                            page.resultPage++;
                            page.search();
                            list.positionViewAtBeginning();
                        }
                    }
                }
            }
        }
        LiteBackToTop {
            anchors.right: parent.right
            anchors.rightMargin: 20
            anchors.bottom: parent.bottom
            anchors.bottomMargin: 16
            flickable: list
        }
    }
    Component {
        id: versionGroupHeader
        Item {
            property var entry: parent ? parent.modelData : ({})
            LiteListGroupHeader {
                y: 8
                width: parent.width
                height: 36
                title: parent.entry.title || ""
                count: parent.entry.count || 0
                collapsible: parent.entry.group !== 0
                expanded: page.groupExpanded(parent.entry.group)
                onClicked: page.toggleGroup(parent.entry.group)
            }
            LiteIconButton {
                visible: parent.entry.group === 0
                anchors.right: parent.right
                anchors.rightMargin: 6
                y: 12
                width: 28
                height: 28
                iconSize: 16
                text: "刷新版本列表"
                source: "qrc:/art/icons/refresh.svg"
                onClicked: page.loadManifest(true)
            }
        }
    }
    Component {
        id: downloadRow
        Item {
            property var entry: parent ? parent.modelData : ({})
            LiteListGroupRow {
                width: parent.width
                height: page.displayedSection === "minecraft" ? 54 : 92
                Accessible.name: page.displayedSection === "minecraft" ? entry.id || "版本" : entry.title || "资源"
                grouped: page.displayedSection === "minecraft"
                last: !!parent.entry.last
                property var entry: parent.entry
                contentItem: Item {
                    Image {
                        x: 13
                        anchors.verticalCenter: parent.verticalCenter
                        width: page.displayedSection === "minecraft" ? 30 : 44
                        height: width
                        source: page.displayedSection === "minecraft" ? "qrc:/art/blocks/" + (entry.icon || "vanilla") + ".png" : entry.iconUrl || "qrc:/art/blocks/vanilla.png"
                        fillMode: Image.PreserveAspectFit
                        smooth: page.displayedSection !== "minecraft"
                        asynchronous: true
                    }
                    Column {
                        x: page.displayedSection === "minecraft" ? 54 : 70
                        anchors.verticalCenter: parent.verticalCenter
                        width: parent.width - x - 20
                        spacing: 4
                        Text {
                            width: parent.width
                            text: page.displayedSection === "minecraft" ? (entry.id || "") : (entry.title || "") + (page.installed[(entry.source || "") + ":" + entry.id] ? " · " + page.installed[(entry.source || "") + ":" + entry.id] : "")
                            font.pixelSize: 13
                            color: "#454d59"
                            elide: Text.ElideRight
                        }
                        Text {
                            width: parent.width
                            text: page.displayedSection === "minecraft" ? (entry.type || "") + " · " + (entry.date || "") : (entry.description || "")
                            font.pixelSize: 11
                            color: "#939ba7"
                            elide: Text.ElideRight
                        }
                        Text {
                            visible: page.displayedSection !== "minecraft"
                            width: parent.width
                            text: (entry.source || "") + " · " + (entry.downloads || 0).toLocaleString(Qt.locale(), "f", 0) + " 次下载" + (entry.categories ? " · " + entry.categories.slice(0, 3).join(" / ") : "")
                            font.pixelSize: 10
                            color: "#939ba7"
                            elide: Text.ElideRight
                        }
                    }
                }
                onClicked: {
                    if (page.displayedSection === "minecraft")
                        page.detailsRequested({
                            type: "minecraft",
                            version: entry.id,
                            root: instanceService.selectedFolder
                        });
                    else
                        page.openProject(entry);
                }
            }
        }
    }
    Connections {
        target: resourceStateJobs
        function onCompleted(action, result, args, path) {
            if (action !== "downloadStates" || args._owner !== page.owner || args._revision !== page.installedRevision || path !== page.target.path || page.displayedSection !== "mod" || !page.visible)
                return;
            const states = {};
            for (const row of result.rows)
                states[row.source + ":" + row.id] = row.state;
            page.installed = states;
        }
        function onResourcesChanged() { page.invalidateInstalled(); }
    }
    Connections {
        target: instanceJobs
        function onResourcesChanged() { page.invalidateInstalled(); }
        function onCompleted(action, result, args, path) {
            if (["downloadResource", "installResource", "import", "delete", "toggle", "update", "repair", "restoreMods", "modify", "recoverModification", "setPack"].indexOf(action) < 0)
                return;
            page.invalidateInstalled();
        }
    }
    Connections {
        target: catalogJobs
        function onPartial(action, result, args, path) {
            if (action === "downloadSearch" && args._owner === page.owner && args._section === page.displayedSection && args._page === page.resultPage && args._request === page.requestId) {
                page.projects = result.projects;
                page.hasMore = result.hasMore;
                page.notice = result.warning;
            }
        }
        function onCompleted(action, result, args, path) {
            if (args._owner !== page.owner)
                return;
            if (action === "downloadManifest") {
                page.manifest = result.groups;
                page.games = ["全部"].concat(result.games);
                if (page.displayedSection === "minecraft") {
                    page.loading = false;
                    page.loaded = true;
                }
            }
            if (action === "downloadSearch" && args._section === page.displayedSection && args._page === page.resultPage && args._request === page.requestId) {
                page.projects = result.projects;
                page.hasMore = result.hasMore;
                page.notice = result.warning || (!result.projects.length ? "没有匹配结果，试试放宽筛选条件。" : "");
                page.loading = false;
                page.loaded = true;
            }
            if (action === "downloadWorlds" && path === page.target.path)
                page.worlds = result.worlds;
        }
        function onFailedDetailed(action, error, args, path) {
            if (args._owner === page.owner && (action === "downloadManifest" ? page.displayedSection === "minecraft" : args._section === page.displayedSection) && (action !== "downloadSearch" || args._request === page.requestId)) {
                page.notice = error;
                page.loading = false;
            }
        }
    }
    SequentialAnimation {
        id: exitMotion
        ParallelAnimation {
            NumberAnimation {
                target: content
                property: "opacity"
                to: 0
                duration: 75
            }
            NumberAnimation {
                target: content
                property: "scale"
                to: 0.97
                duration: 75
                easing.type: Easing.InQuad
            }
        }
        ScriptAction {
            script: {
                list.cancelFlick();
                page.displayedSection = page.section;
                page.activateSection();
                enterMotion.restart();
            }
        }
    }
    ParallelAnimation {
        id: enterMotion
        NumberAnimation {
            target: content
            property: "opacity"
            to: 1
            duration: 100
        }
        NumberAnimation {
            target: content
            property: "scale"
            to: 1
            duration: 350
            easing.type: Easing.BezierSpline
            easing.bezierCurve: Motion.outBackWeak
        }
    }
}
