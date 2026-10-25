import QtQuick
import QtQuick.Controls.Basic as Controls
import "LiteInstanceRules.js" as Rules

Item {
    id: page
    property bool installMode: false
    property string installRoot: instanceService.selectedFolder
    property var reader: installMode ? catalogJobs : instanceJobs
    signal queued
    property var original: ({})
    property var chosen: ({})
    property var originalRows: ({})
    property var pendingAutomatic: ({})
    property bool fabricApiAutoAllowed: false
    property bool optiFabricAutoAllowed: false
    property bool nameWasEdited: true
    property bool confirmAfterAutomatic: false
    property var versionCache: ({})
    property string game: instanceSettings.instance.version || ""
    property string instanceName: instanceSettings.instance.name || ""
    property string expanded: ""
    property string loadingComponent: ""
    property string notice: "正在读取已安装组件…"
    property bool recovery: !installMode && !!instanceSettings.instance.recovery
    readonly property var specs: [
        {
            key: "Forge",
            icon: "forge"
        },
        {
            key: "NeoForge",
            icon: "neoforge"
        },
        {
            key: "Fabric",
            icon: "fabric"
        },
        {
            key: "LiteLoader",
            icon: "egg"
        },
        {
            key: "OptiFine",
            icon: "grasspath"
        },
        {
            key: "Fabric API",
            icon: "fabric"
        },
        {
            key: "OptiFabric",
            icon: "fabric"
        }
    ]
    readonly property var differences: differencesFor()
    Component.onCompleted: {
        if (installMode) {
            original = {
                minecraftVersion: game
            };
            instanceName = game;
            nameWasEdited = false;
            notice = "选择需要安装的组件";
        } else
            instanceJobs.submit("metadata");
    }
    function requestVersions(args) {
        if (installMode)
            reader.download("downloadComponents", Object.assign({
                root: installRoot
            }, args));
        else
            reader.submit("componentVersions", args);
    }
    Component.onDestruction: {
        if (!installMode)
            reader.cancelRead("metadata");
        reader.cancelRead(installMode ? "downloadComponents" : "componentVersions");
    }
    function valuesFrom(manifest) {
        const result = {};
        if (manifest.loader)
            result[manifest.loader] = {
                version: manifest.loaderVersion,
                id: manifest.loaderVersion
            };
        const fields = {
            LiteLoader: "liteLoaderVersion",
            OptiFine: "optiFineVersion",
            "Fabric API": "fabricApiVersion",
            OptiFabric: "optiFabricVersion"
        };
        for (const key in fields)
            if (manifest[fields[key]])
                result[key] = {
                    version: manifest[fields[key]],
                    id: manifest[fields[key]]
                };
        return result;
    }
    function primary() {
        return Rules.primary(chosen);
    }
    function blocked(key) {
        return Rules.blocked(game, key, chosen, versionCache);
    }
    function loadVersions(key) {
        if (blocked(key)) {
            notice = blocked(key);
            return;
        }
        expanded = expanded === key ? "" : key;
        if (expanded && !versionCache[game + "/" + key]) {
            loadingComponent = key;
            page.requestVersions({
                component: key,
                game: game
            });
        }
    }
    function select(key, value) {
        if (value && blocked(key)) {
            notice = blocked(key);
            return;
        }
        const wasFabric = primary() === "Fabric";
        const hadOptiFine = !!chosen.OptiFine;
        const next = Object.assign({}, chosen);
        if (value)
            next[key] = value;
        else
            delete next[key];
        if (key === "Fabric" && !value) {
            delete next["Fabric API"];
            delete next.OptiFabric;
            fabricApiAutoAllowed = false;
            optiFabricAutoAllowed = false;
        }
        if (key === "Fabric API" && !value)
            fabricApiAutoAllowed = false;
        if (key === "OptiFabric" && !value)
            optiFabricAutoAllowed = false;
        if (key === "OptiFine" && !value) {
            delete next.OptiFabric;
            optiFabricAutoAllowed = false;
        }
        if (value && ["Forge", "NeoForge", "Fabric"].indexOf(key) >= 0) {
            if (key !== "Fabric") {
                delete next["Fabric API"];
                delete next.OptiFabric;
                fabricApiAutoAllowed = false;
                optiFabricAutoAllowed = false;
            }
            if (key === "NeoForge")
                delete next.OptiFine;
            else if (key === "Fabric" && !Rules.supportsOptiFineWithFabric(game)) {
                delete next.OptiFine;
                delete next.OptiFabric;
                optiFabricAutoAllowed = false;
            } else if (key === "Forge" && next.OptiFine && !Rules.forgeCompatible(next.OptiFine, game, value.id || value.version))
                delete next.OptiFine;
            if (key === "Fabric" && !wasFabric)
                fabricApiAutoAllowed = true;
        }
        if (value && key === "OptiFine" && wasFabric) {
            optiFabricAutoAllowed = true;
            if (Rules.supportsOptiFabric(game))
                delete next.OptiFabric;
        }
        chosen = next;
        if (value)
            expanded = "";
        if (!nameWasEdited)
            instanceName = Rules.suggestedName(game, chosen);
        notice = installMode ? "选择需要安装的组件" : "修改前会保留备份";
        if (value && key === "Fabric") {
            if (fabricApiAutoAllowed && !chosen["Fabric API"])
                requestAutomatic("Fabric API");
            if (chosen.OptiFine && (!wasFabric || !hadOptiFine))
                optiFabricAutoAllowed = true;
        }
        if (primary() === "Fabric" && chosen.OptiFine && optiFabricAutoAllowed && Rules.supportsOptiFabric(game) && !chosen.OptiFabric)
            requestAutomatic("OptiFabric");
    }
    function requestAutomatic(key) {
        if (pendingAutomatic[key])
            return;
        const next = Object.assign({}, pendingAutomatic);
        next[key] = true;
        pendingAutomatic = next;
        if (versionCache[game + "/" + key] !== undefined)
            finishAutomatic(key);
        else
            page.requestVersions({
                component: key,
                game: game
            });
    }
    function finishAutomatic(key) {
        if (!pendingAutomatic[key])
            return;
        const pending = Object.assign({}, pendingAutomatic);
        delete pending[key];
        pendingAutomatic = pending;
        const allowed = key === "Fabric API" ? fabricApiAutoAllowed && primary() === "Fabric" : optiFabricAutoAllowed && primary() === "Fabric" && !!chosen.OptiFine;
        if (!allowed || chosen[key])
            return;
        const row = (versionCache[game + "/" + key] || []).find(v => !!v.id);
        if (row) {
            const next = Object.assign({}, chosen);
            next[key] = row;
            chosen = next;
            if (!nameWasEdited)
                instanceName = Rules.suggestedName(game, chosen);
        }
    }
    function installId(key) {
        const row = chosen[key];
        if (!row)
            return null;
        return originalRows[key] && originalRows[key].version === row.version ? originalRows[key].id || originalRows[key].version : row.id || row.version;
    }
    function prepareModification(skipAutomatic) {
        if (!skipAutomatic) {
            if (primary() === "Fabric" && fabricApiAutoAllowed && !chosen["Fabric API"])
                requestAutomatic("Fabric API");
            if (primary() === "Fabric" && chosen.OptiFine && optiFabricAutoAllowed && Rules.supportsOptiFabric(game) && !chosen.OptiFabric)
                requestAutomatic("OptiFabric");
            if (Object.keys(pendingAutomatic).length) {
                confirmAfterAutomatic = true;
                return;
            }
        }
        confirmAfterAutomatic = false;
        const error = Rules.validate(game, chosen);
        if (error) {
            notice = error;
            return;
        }
        if (!instanceName.trim()) {
            notice = "请填写有效的实例名称。";
            return;
        }
        if (installMode) {
            if (instanceJobs.download("downloadInstall", {
                root: installRoot,
                request: request(),
                title: "安装 Minecraft " + game + " · " + instanceName.trim()
            }))
                queued();
            else
                notice = instanceJobs.error;
            return;
        }
        confirmation.heading = "确认修改实例";
        confirmation.message = differences.map(d => d.text).join("\n") + "\n\n提交前会保留备份。已有 Mod、资源包和存档可能与修改后的组件不兼容。" + (instanceSettings.gameDirectory !== instanceSettings.instance.path ? "\n此实例使用共享目录，修改不会自动迁移 Mod 与存档。" : "");
        confirmation.confirmText = "开始修改";
        confirmation.action = "modify";
        confirmation.open();
    }
    function differencesFor() {
        if (!original.minecraftVersion)
            return [];
        const result = [];
        if (instanceName.trim() !== instanceSettings.instance.name)
            result.push({
                key: "name",
                text: "名称：" + instanceSettings.instance.name + " → " + instanceName.trim()
            });
        const old = originalRows;
        for (const spec of specs) {
            const a = old[spec.key] ? old[spec.key].version : "";
            const b = chosen[spec.key] ? chosen[spec.key].version : "";
            if (a !== b)
                result.push({
                    key: spec.key,
                    text: spec.key + "：" + (old[spec.key] ? old[spec.key].version : "无") + " → " + (chosen[spec.key] ? chosen[spec.key].version : "无")
                });
        }
        return result;
    }
    function request() {
        const selected = primary();
        function id(key) {
            return page.installId(key);
        }
        return {
            version: game,
            name: instanceName.trim(),
            gameDirectory: installMode ? installRoot : instanceSettings.instance.root,
            loader: selected || null,
            loaderVersion: selected ? chosen[selected].id || chosen[selected].version : null,
            liteLoaderVersion: id("LiteLoader"),
            optiFineVersion: id("OptiFine"),
            fabricApiVersion: id("Fabric API"),
            optiFabricVersion: id("OptiFabric")
        };
    }
    Flickable {
        id: scroll
        anchors.fill: parent
        anchors.bottomMargin: 52
        contentHeight: column.implicitHeight + 4
        clip: true
        boundsBehavior: Flickable.StopAtBounds
        Controls.ScrollBar.vertical: Controls.ScrollBar {
            width: 4
        }
        Column {
            id: column
            width: scroll.width - 8
            spacing: 12
            LiteSettingsCard {
                title: page.installMode ? "安装 Minecraft " + page.game : "修改实例"
                LiteTextField {
                    width: parent.width
                    height: 34
                    placeholderText: "实例名称"
                    text: page.instanceName
                    onTextEdited: {
                        page.nameWasEdited = true;
                        page.instanceName = text;
                    }
                }
            }
            Repeater {
                model: page.specs
                Rectangle {
                    id: componentCard
                    required property var modelData
                    property var versions: Rules.rows(page.game, modelData.key, page.chosen, page.versionCache)
                    property bool expanded: page.expanded === modelData.key
                    property string reason: page.blocked(modelData.key)
                    visible: Rules.visible(page.game, modelData.key, page.chosen)
                    width: column.width
                    height: 46 + (expanded ? Math.max(1, versions.length) * 46 + 8 : 0)
                    Behavior on height {
                        NumberAnimation {
                            duration: 100
                            easing.type: Easing.OutCubic
                        }
                    }
                    radius: 8
                    color: header.enabled && header.down ? "#f0f2f5" : header.enabled && header.hovered ? "#fafbfc" : "#ffffff"
                    border.color: header.enabled && (header.hovered || header.visualFocus) ? "#cfd5de" : "#e2e5ea"
                    Behavior on color {
                        ColorAnimation {
                            duration: 90
                        }
                    }
                    Behavior on border.color {
                        ColorAnimation {
                            duration: 90
                        }
                    }
                    clip: true
                    Controls.AbstractButton {
                        id: header
                        Accessible.name: modelData.key
                        width: parent.width - (page.chosen[modelData.key] ? 36 : 0)
                        height: 46
                        hoverEnabled: enabled
                        enabled: !componentCard.reason && !!page.original.minecraftVersion
                        background: null
                        contentItem: Item {
                            opacity: header.enabled ? 1 : 0.45
                            Image {
                                x: 12
                                anchors.verticalCenter: parent.verticalCenter
                                width: 22
                                height: 22
                                source: "qrc:/art/blocks/" + modelData.icon + ".png"
                                sourceSize: Qt.size(22, 22)
                                smooth: false
                                fillMode: Image.PreserveAspectFit
                            }
                            Text {
                                x: 44
                                width: 94
                                anchors.verticalCenter: parent.verticalCenter
                                text: modelData.key
                                color: "#454d59"
                                font.pixelSize: 12
                                font.weight: Font.DemiBold
                            }
                            Text {
                                x: 142
                                width: parent.width - x - 28
                                anchors.verticalCenter: parent.verticalCenter
                                text: page.chosen[modelData.key] ? page.chosen[modelData.key].version : componentCard.reason || (page.versionCache[page.game + "/" + modelData.key] && !componentCard.versions.length ? "无可用版本" : "点击选择版本")
                                color: "#939ba7"
                                font.pixelSize: 11
                                elide: Text.ElideRight
                            }
                            Image {
                                anchors.right: parent.right
                                anchors.rightMargin: 12
                                anchors.verticalCenter: parent.verticalCenter
                                width: 12
                                height: 12
                                source: "qrc:/art/icons/chevron-down.svg"
                                rotation: componentCard.expanded ? 180 : 0
                            }
                        }
                        onClicked: page.loadVersions(modelData.key)
                    }
                    LiteIconButton {
                        anchors.right: parent.right
                        width: 32
                        height: 46
                        iconSize: 14
                        source: "qrc:/art/icons/close.svg"
                        text: "清除 " + modelData.key
                        visible: !!page.chosen[modelData.key]
                        onClicked: page.select(modelData.key, null)
                    }
                    Rectangle {
                        x: 12
                        y: 45
                        width: parent.width - 24
                        height: 1
                        color: "#e8ebef"
                        visible: componentCard.expanded
                    }
                    Item {
                        id: versionViewport
                        x: 6
                        y: Math.max(46, scroll.contentY - componentCard.y)
                        width: parent.width - 12
                        height: componentCard.expanded ? Math.max(0, Math.min(componentCard.height - 8, scroll.contentY + scroll.height - componentCard.y) - y) : 0
                        clip: true
                        // Expand the full list while creating delegates only for the outer viewport.
                        ListView {
                            anchors.fill: parent
                            contentY: Math.max(0, versionViewport.y - 46)
                            interactive: false
                            cacheBuffer: 76
                            reuseItems: true
                            model: versionViewport.height > 0 ? componentCard.versions : []
                            delegate: Controls.ItemDelegate {
                                required property var modelData
                                width: ListView.view.width
                                height: 46
                                enabled: !modelData.section
                                Accessible.name: modelData.section ? modelData.title : modelData.version + " " + (modelData.badge || "")
                                contentItem: Item {
                                    Image {
                                        x: 4
                                        anchors.verticalCenter: parent.verticalCenter
                                        width: 26
                                        height: 26
                                        sourceSize: Qt.size(26, 26)
                                        source: "qrc:/art/blocks/" + componentCard.modelData.icon + ".png"
                                        fillMode: Image.PreserveAspectFit
                                        smooth: false
                                        visible: !modelData.section
                                    }
                                    Column {
                                        x: 42
                                        width: Math.max(0, parent.width - x - 76)
                                        anchors.verticalCenter: parent.verticalCenter
                                        spacing: 3
                                        visible: !modelData.section
                                        Text {
                                            width: parent.width
                                            text: modelData.version || ""
                                            font.pixelSize: 12
                                            color: "#454d59"
                                            elide: Text.ElideRight
                                        }
                                        Text {
                                            width: parent.width
                                            text: modelData.detail || ""
                                            font.pixelSize: 10
                                            color: "#939ba7"
                                            elide: Text.ElideRight
                                        }
                                    }
                                    Text {
                                        x: 4
                                        anchors.verticalCenter: parent.verticalCenter
                                        text: modelData.title || ""
                                        font.pixelSize: 11
                                        color: "#939ba7"
                                        visible: !!modelData.section
                                    }
                                    Text {
                                        anchors.right: parent.right
                                        anchors.verticalCenter: parent.verticalCenter
                                        text: modelData.badge || ""
                                        font.pixelSize: 10
                                        color: "#778292"
                                    }
                                }
                                background: Rectangle {
                                    radius: 5
                                    color: !parent.enabled ? "transparent" : parent.down ? "#e7ebf0" : modelData.badge === "✓" ? "#edf0f4" : parent.hovered ? "#f5f6f8" : "transparent"
                                }
                                onClicked: page.select(componentCard.modelData.key, modelData)
                            }
                            Text {
                                anchors.centerIn: parent
                                visible: !componentCard.versions.length
                                text: page.loadingComponent === componentCard.modelData.key && page.reader.busy ? "正在获取版本…" : "暂无可用版本"
                                font.pixelSize: 11
                                color: "#939ba7"
                            }
                        }
                    }
                }
            }
            LiteButton {
                width: 156
                height: 30
                visible: page.recovery
                text: "恢复未完成修改的备份"
                onClicked: {
                    confirmation.heading = "恢复实例备份";
                    confirmation.message = "将从备份恢复上次未完成修改涉及的文件。";
                    confirmation.confirmText = "恢复";
                    confirmation.action = "recoverModification";
                    confirmation.open();
                }
            }
        }
    }
    LiteBackToTop {
        anchors.right: parent.right
        anchors.rightMargin: 20
        anchors.bottom: parent.bottom
        anchors.bottomMargin: 62
        flickable: scroll
    }
    Row {
        anchors.bottom: parent.bottom
        width: parent.width - 8
        spacing: 14
        LiteButton {
            width: 130
            height: 36
            primary: true
            text: page.installMode ? "开始安装" : "开始修改"
            enabled: !!page.original.minecraftVersion && (page.installMode || page.differences.length > 0) && !!page.instanceName.trim() && !page.recovery && (page.installMode || !instanceJobs.busy)
            onClicked: page.prepareModification(false)
        }
        Text {
            width: parent.width - 144
            anchors.verticalCenter: parent.verticalCenter
            text: !page.installMode && instanceJobs.busy ? instanceJobs.detail : page.notice
            font.pixelSize: 11
            color: "#939ba7"
            elide: Text.ElideRight
        }
    }
    LiteConfirmDialog {
        id: confirmation
        property string action
        onConfirmed: instanceJobs.submit(action, action === "modify" ? {
            request: page.request()
        } : {})
    }
    Connections {
        target: page.reader
        function onFailedDetailed(action, error, args, path) {
            if (!page.installMode && path !== instanceSettings.instance.path)
                return;
            if (action === "componentVersions" || action === "downloadComponents") {
                if (page.loadingComponent === args.component)
                    page.loadingComponent = "";
                const pending = Object.assign({}, page.pendingAutomatic);
                const wasAutomatic = !!pending[args.component];
                delete pending[args.component];
                page.pendingAutomatic = pending;
                page.notice = (wasAutomatic ? "无法自动选择 " : "无法获取 ") + args.component + "：" + error;
                if (page.confirmAfterAutomatic && !Object.keys(page.pendingAutomatic).length)
                    Qt.callLater(function () {
                        page.prepareModification(true);
                    });
            } else if (action === "metadata")
                page.notice = "无法读取已安装组件：" + error;
        }
        function onCompleted(action, result, args, path) {
            if (action === "downloadComponents" && args.game !== page.game)
                return;
            if (!page.installMode && path !== instanceSettings.instance.path)
                return;
            if (action === "metadata") {
                page.original = result.installed;
                page.originalRows = result.selected;
                page.chosen = result.selected;
                page.versionCache = result.caches;
                page.game = result.installed.minecraftVersion;
                page.recovery = result.recovery;
                page.nameWasEdited = Rules.suggestedName(page.game, page.chosen) !== instanceSettings.instance.name;
                page.fabricApiAutoAllowed = false;
                page.optiFabricAutoAllowed = false;
                page.notice = result.warnings.length ? "部分组件列表未能刷新，已安装组件可保留或移除。" : "修改前会保留备份";
            } else if (action === "componentVersions" || action === "downloadComponents") {
                const cache = Object.assign({}, page.versionCache);
                cache[args.game + "/" + args.component] = result.versions;
                page.versionCache = cache;
                if (page.loadingComponent === args.component)
                    page.loadingComponent = "";
                page.finishAutomatic(args.component);
                if (page.confirmAfterAutomatic && !Object.keys(page.pendingAutomatic).length)
                    Qt.callLater(function () {
                        page.prepareModification(true);
                    });
            } else if (action === "recoverModification")
                instanceJobs.submit("metadata");
        }
    }
}
