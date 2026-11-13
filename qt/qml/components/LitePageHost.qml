import QtQuick
import "LiteMotion.js" as Motion

Item {
    id: host
    signal addAccountRequested
    signal switchAccountRequested
    signal navigateRequested(int page)
    signal settingsRequested(string path)
    signal downloadRequested(var detail)
    signal dependencyRequested(var detail, var previous)
    signal downloadCategoryRequested
    property var downloadDetail: ({})
    property string downloadSection: "minecraft"
    property string pendingDownloadTargetPath: ""
    property string detailKey: ""
    ListModel {
        id: detailPages
    }
    function retainDetail() {
        if (displayedPage !== 6 || !downloadDetail.type)
            return;
        const project = downloadDetail.project || {};
        const target = downloadDetail.target || {};
        const world = downloadDetail.world || {};
        detailKey = JSON.stringify([downloadDetail.type, project.source, project.id, downloadDetail.version, downloadDetail.root, target.path, world.path, downloadDetail.game, downloadDetail.loader]);
        for (let i = 0; i < detailPages.count; ++i)
            if (detailPages.get(i).pageKey === detailKey)
                return;
        detailPages.append({
            pageKey: detailKey,
            payload: JSON.stringify(downloadDetail)
        });
    }
    onDownloadDetailChanged: retainDetail()
    onDisplayedPageChanged: retainDetail()

    property string settingsSection: "settings"
    property string globalSection: "launch"
    onSettingsSectionChanged: {
        if (settingsPage.item)
            settingsPage.item.section = settingsSection;
    }
    property string accountView: "identity"
    property int requestedPage: 0
    property int displayedPage: 0
    property int phase: 0
    property real sidebarWidth: 244
    property real sidebarTargetWidth: 244
    property real contentScale: 1
    property bool flowShown: true
    property real flowExpansion: 1
    property real flowOpacity: 1
    property real flowFormScale: 1
    property real shellOpacity: 0
    property real shellScale: 1
    property real pageOpacity: 1
    property real sidebarOpacity: 1
    readonly property real renderedSidebarWidth: Math.max(0, sidebarWidth + (width - sidebarWidth) * Math.max(0, Math.min(1, flowExpansion)))
    readonly property int transferWidth: instanceJobs.tasks.length ? 224 : 0
    readonly property var sidebarWidths: [244, 264, 172, transferWidth, 168, 172, 0]
    clip: true

    onFlowShownChanged: {
        flowEnter.stop();
        flowExit.stop();
        if (flowShown) {
            if (flowOpacity < 0.01)
                flowFormScale = 0.94;
            flowEnter.restart();
        } else {
            if (shellOpacity < 0.01)
                shellScale = 0.94;
            flowExit.restart();
        }
    }
    Component.onCompleted: {
        if (flowShown) {
            flowOpacity = 0;
            flowFormScale = 0.94;
            flowEnter.restart();
        }
    }

    onSidebarWidthsChanged: {
        const target = sidebarWidths[requestedPage];
        if (Math.abs(sidebarWidth - target) > 0.1) {
            sidebarTargetWidth = target;
            widthTransition.restart();
        }
    }
    onRequestedPageChanged: {
        if (displayedPage === 3 && requestedPage !== 3)
            instanceJobs.clearFinished();
        if (requestedPage === displayedPage && phase === 0)
            return;
        enterTransition.stop();
        widthTransition.stop();
        const targetWidth = sidebarWidths[requestedPage];
        if (Math.abs(sidebarWidth - targetWidth) > 0.1) {
            sidebarTargetWidth = targetWidth;
            widthTransition.restart();
        } else {
            sidebarWidth = targetWidth;
        }
        if (phase === 1)
            return;
        phase = 1;
        exitTransition.restart();
    }

    function showRequestedPage() {
        displayedPage = requestedPage;
        sidebarOpacity = 0;
        pageOpacity = 0;
        contentScale = 0.96;
        phase = 2;
        if (!widthTransition.running)
            enterTransition.restart();
    }

    Item {
        id: pageContent
        x: Math.max(0, host.sidebarWidth) + 16
        y: 8
        width: host.width - host.sidebarWidths[host.displayedPage] - 32
        height: parent.height - y - 12
        enabled: host.phase === 0 && !host.flowShown && host.flowExpansion < 0.01
        opacity: host.pageOpacity * host.shellOpacity
        visible: host.shellOpacity > 0
        scale: host.contentScale * host.shellScale

        LiteHome {
            y: 4
            width: parent.width
            height: parent.height - y
            visible: host.displayedPage === 0
            onSettingsRequested: function (path) {
                host.settingsRequested(path);
            }
        }
        LiteRetainedLoader {
            id: instancePage
            anchors.fill: parent
            shown: host.displayedPage === 1
            source: "LiteInstancesPage.qml"
        }
        Connections {
            target: instancePage.item
            function onNavigateRequested(page) {
                host.navigateRequested(page);
            }
            function onSettingsRequested(path) {
                host.settingsRequested(path);
            }
        }
        LiteRetainedLoader {
            id: downloadPage
            anchors.fill: parent
            shown: host.displayedPage === 2
            sourceComponent: Component {
                LiteDownloadPage {
                    section: host.downloadSection
                }
            }
            onLoaded: {
                if (host.pendingDownloadTargetPath) {
                    item.requestTarget(host.pendingDownloadTargetPath);
                    host.pendingDownloadTargetPath = "";
                }
            }
        }
        Connections {
            target: downloadPage.item
            function onDetailsRequested(detail) {
                host.downloadRequested(detail);
            }
        }
        Repeater {
            model: detailPages
            Loader {
                id: detailLoader
                required property string pageKey
                required property string payload
                anchors.fill: parent
                visible: host.displayedPage === 6 && host.detailKey === pageKey
                sourceComponent: Component {
                    LiteDownloadDetail {
                        detail: JSON.parse(detailLoader.payload)
                        activeView: detailLoader.visible
                        onDependencyRequested: function (detail, previous) {
                            host.dependencyRequested(detail, previous);
                        }
                    }
                }
            }
        }
        LiteRetainedLoader {
            anchors.fill: parent
            shown: host.displayedPage === 4
            sourceComponent: Component {
                LiteGlobalSettingsPage {
                    section: host.globalSection
                }
            }
        }
        Loader {
            id: settingsPage
            anchors.fill: parent
            active: host.displayedPage === 5
            source: "LiteInstanceSettingsPage.qml"
            onLoaded: item.section = host.settingsSection
        }
        Connections {
            target: settingsPage.item
            function onDownloadRequested(category, instancePath) {
                host.pendingDownloadTargetPath = instancePath;
                host.downloadSection = category;
                if (downloadPage.item) {
                    downloadPage.item.requestTarget(instancePath);
                    host.pendingDownloadTargetPath = "";
                }
                host.downloadCategoryRequested();
            }
            function onSectionChanged() {
                if (settingsPage.item)
                    host.settingsSection = settingsPage.item.section;
            }
        }
        LiteRetainedLoader {
            anchors.fill: parent
            shown: host.displayedPage === 3
            source: "LiteTransferPage.qml"
        }
    }

    Rectangle {
        id: sidebarBackground
        width: host.renderedSidebarWidth
        height: parent.height
        color: "#ffffff"
    }
    Rectangle {
        x: host.renderedSidebarWidth
        width: 12
        height: parent.height
        opacity: Math.min(1, host.renderedSidebarWidth / 24)
        gradient: Gradient {
            orientation: Gradient.Horizontal
            GradientStop {
                position: 0
                color: "#0c303640"
            }
            GradientStop {
                position: 0.4
                color: "#04303640"
            }
            GradientStop {
                position: 1
                color: "#00303640"
            }
        }
    }
    Item {
        width: host.renderedSidebarWidth
        height: parent.height
        clip: true
        visible: host.shellOpacity > 0
        enabled: host.phase === 0 && host.sidebarWidth > 0 && !host.flowShown && host.flowExpansion < 0.01
        Item {
            id: sidebarContent
            opacity: host.sidebarOpacity * host.shellOpacity
            scale: host.contentScale * host.shellScale
            width: host.sidebarWidths[host.displayedPage]
            height: parent.height
            LiteAccountPane {
                anchors.fill: parent
                visible: host.displayedPage === 0
                activeView: visible && enabled && host.requestedPage === 0 && host.phase === 0 && !host.flowShown && host.flowExpansion < 0.01
                view: host.accountView
                onViewRequested: function (view) {
                    host.accountView = view;
                }
                onAddRequested: host.addAccountRequested()
                onSwitchRequested: host.switchAccountRequested()
            }
            LiteRetainedLoader {
                id: instanceSidebar
                anchors.fill: parent
                shown: host.displayedPage === 1
                source: "LiteInstanceSidebar.qml"
            }
            Connections {
                target: instanceSidebar.item
                function onActionRequested(action, record) {
                    if (instancePage.item)
                        instancePage.item.requestAction(action, record);
                }
            }
            LiteRetainedLoader {
                id: downloadSidebar
                anchors.fill: parent
                shown: host.displayedPage === 2
                source: "LiteDownloadSidebar.qml"
                onLoaded: item.section = Qt.binding(function () {
                    return host.downloadSection;
                })
            }
            Connections {
                target: downloadSidebar.item
                function onSectionRequested(section) {
                    host.downloadSection = section;
                    host.downloadCategoryRequested();
                }
            }
            LiteRetainedLoader {
                anchors.fill: parent
                shown: host.displayedPage === 3 && instanceJobs.tasks.length > 0
                source: "LiteTransferSummary.qml"
            }
            LiteRetainedLoader {
                id: globalSidebar
                anchors.fill: parent
                shown: host.displayedPage === 4
                sourceComponent: Component {
                    LiteGlobalSettingsSidebar {
                        section: host.globalSection
                        onSectionRequested: function (section) {
                            host.globalSection = section;
                        }
                    }
                }
            }
            Loader {
                id: settingsSidebar
                anchors.fill: parent
                active: host.displayedPage === 5
                source: "LiteInstanceSettingsSidebar.qml"
                onLoaded: item.section = Qt.binding(function () {
                    return host.settingsSection;
                })
            }
            Connections {
                target: settingsSidebar.item
                function onSectionRequested(section) {
                    host.settingsSection = section;
                }
            }
        }
    }

    SequentialAnimation {
        id: exitTransition
        ParallelAnimation {
            NumberAnimation {
                target: host
                property: "sidebarOpacity"
                to: 0
                duration: 110
            }
            NumberAnimation {
                target: host
                property: "pageOpacity"
                to: 0
                duration: 110
            }
            NumberAnimation {
                target: host
                property: "contentScale"
                to: 0.95
                duration: 110
                easing.type: Easing.InQuad
            }
        }
        ScriptAction {
            script: host.showRequestedPage()
        }
    }
    NumberAnimation {
        id: widthTransition
        target: host
        property: "sidebarWidth"
        to: host.sidebarTargetWidth
        duration: 180
        easing.type: host.sidebarTargetWidth > 0 ? Easing.OutQuint : Easing.OutCubic
        onFinished: {
            if (host.phase === 2)
                enterTransition.restart();
        }
    }
    SequentialAnimation {
        id: enterTransition
        PauseAnimation {
            duration: 20
        }
        ParallelAnimation {
            NumberAnimation {
                target: host
                property: "sidebarOpacity"
                to: 1
                duration: 100
            }
            NumberAnimation {
                target: host
                property: "pageOpacity"
                to: 1
                duration: 100
            }
            NumberAnimation {
                target: host
                property: "contentScale"
                to: 1
                duration: 400
                easing.type: Easing.BezierSpline
                easing.bezierCurve: Motion.outBackWeak
            }
        }
        ScriptAction {
            script: host.phase = 0
        }
    }
    Item {
        width: host.renderedSidebarWidth
        height: parent.height
        clip: true
        visible: host.flowShown || host.flowExpansion > 0.001 || host.flowOpacity > 0.001
        enabled: host.flowShown
        LiteWelcomeFlow {
            width: host.width
            height: parent.height
            activeView: host.flowShown
            formOpacity: host.flowOpacity
            formScale: host.flowFormScale
        }
    }
    ParallelAnimation {
        id: flowEnter
        NumberAnimation {
            target: host
            property: "shellOpacity"
            to: 0
            duration: 120
        }
        NumberAnimation {
            target: host
            property: "flowExpansion"
            to: 1
            duration: 240
            easing.type: Easing.BezierSpline
            easing.bezierCurve: Motion.outBackWeak
        }
        SequentialAnimation {
            PauseAnimation {
                duration: 100
            }
            ParallelAnimation {
                NumberAnimation {
                    target: host
                    property: "flowOpacity"
                    to: 1
                    duration: 180
                }
                NumberAnimation {
                    target: host
                    property: "flowFormScale"
                    to: 1
                    duration: 380
                    easing.type: Easing.BezierSpline
                    easing.bezierCurve: Motion.outBackWeak
                }
            }
        }
    }
    SequentialAnimation {
        id: flowExit
        ParallelAnimation {
            NumberAnimation {
                target: host
                property: "flowOpacity"
                to: 0
                duration: 120
            }
            NumberAnimation {
                target: host
                property: "flowFormScale"
                to: 0.96
                duration: 120
                easing.type: Easing.InQuad
            }
        }
        ParallelAnimation {
            NumberAnimation {
                target: host
                property: "flowExpansion"
                to: 0
                duration: 260
                easing.type: Easing.BezierSpline
                easing.bezierCurve: Motion.outBackWeak
            }
            SequentialAnimation {
                PauseAnimation {
                    duration: 80
                }
                ParallelAnimation {
                    NumberAnimation {
                        target: host
                        property: "shellOpacity"
                        to: 1
                        duration: 180
                    }
                    NumberAnimation {
                        target: host
                        property: "shellScale"
                        to: 1
                        duration: 400
                        easing.type: Easing.BezierSpline
                        easing.bezierCurve: Motion.outBackWeak
                    }
                }
            }
        }
    }
}
