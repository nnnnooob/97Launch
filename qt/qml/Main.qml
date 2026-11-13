import QtQuick
import QtQuick.Window
import QtQuick.Controls.Basic as Controls
import "components"

Window {
    id: window
    readonly property int frameWidth: 800
    readonly property int frameHeight: 450
    width: frameWidth
    height: frameHeight
    minimumWidth: frameWidth
    maximumWidth: frameWidth
    minimumHeight: frameHeight
    maximumHeight: frameHeight
    flags: Qt.Window | Qt.FramelessWindowHint | Qt.WindowSystemMenuHint | Qt.WindowMinimizeButtonHint | Qt.WindowCloseButtonHint | Qt.MSWindowsFixedSizeDialogHint
    title: "97Launch"
    color: "#f7f8fa"
    property bool settingsShown: false
    property bool downloadShown: false
    readonly property bool headerShown: settingsShown || downloadShown
    property var downloadDetail: ({})
    property var downloadHistory: []
    property string detailTitle: ""
    function updateDownloadTitle() {
        detailTitle = downloadDetail.type === "minecraft" ? "安装 Minecraft · " + downloadDetail.version : "下载 · " + ((downloadDetail.project || {}).title || "");
    }
    function openDownload(detail, dependency, previous) {
        if (dependency) {
            if (downloadHistory.length >= 16)
                return;
            downloadHistory = downloadHistory.concat([previous || downloadDetail]);
        } else
            downloadHistory = [];
        downloadDetail = detail;
        updateDownloadTitle();
        downloadShown = true;
    }
    function back() {
        if (downloadShown) {
            if (downloadHistory.length) {
                const history = downloadHistory.slice();
                downloadDetail = history.pop();
                updateDownloadTitle();
                downloadHistory = history;
            } else
                downloadShown = false;
        } else
            closeInstanceSettings();
    }
    onHeaderShownChanged: {
        titleExit.stop();
        titleEnter.stop();
        const incoming = headerShown ? instanceHeader : mainHeader;
        if (incoming.opacity < 0.01)
            incoming.x = headerShown ? -18 : 12;
        titleExit.restart();
        titleEnter.restart();
    }
    property bool quitAfterJobs: false
    function openInstanceSettings(path) {
        if (instanceSettings.open(path)) {
            detailTitle = "实例设置 · " + (instanceSettings.instance.name || "");
            settingsCleanup.stop();
            settingsShown = true;
            pageHost.settingsSection = "settings";
        }
    }
    function closeInstanceSettings() {
        pageHost.forceActiveFocus();
        settingsShown = false;
        settingsCleanup.restart();
    }
    onClosing: function (event) {
        pageHost.forceActiveFocus();
        accountService.cancelLogin();
        catalogJobs.cancel();
        resourceStateJobs.cancel();
        settingsJobs.cancel();
        updateJobs.cancel();
        launchStatusJobs.cancel();
        instanceJavaJobs.cancel();
        dropJobs.cancel();
        if (instanceJobs.busy) {
            event.accepted = false;
            quitAfterJobs = true;
            instanceJobs.cancel();
        }
        if (event.accepted)
            gameLaunch.detach();
    }
    function syncLaunchPrompt() {
        const prompt = gameLaunch.prompt;
        if (prompt.id && !launchConfirm.visible) {
            launchConfirm.promptId = prompt.id;
            launchConfirm.heading = prompt.title || "确认启动";
            launchConfirm.message = prompt.message || "";
            launchConfirm.confirmText = prompt.confirm || "继续";
            launchConfirm.open();
        } else if ((!prompt.id || prompt.id !== launchConfirm.promptId) && launchConfirm.visible) {
            launchConfirm.close();
        }
    }
    Connections {
        target: gameLaunch
        function onChanged() { window.syncLaunchPrompt(); }
        function onCrashReported() { crashDialog.open(); }
    }
    LiteConfirmDialog {
        id: launchConfirm
        property var promptId
        onConfirmed: { if (gameLaunch.prompt.id === promptId) gameLaunch.answer(true); }
        onRejected: { if (gameLaunch.prompt.id === promptId) gameLaunch.answer(false); }
        onClosed: Qt.callLater(window.syncLaunchPrompt)
    }
    LiteConfirmDialog {
        id: crashDialog
        heading: "游戏意外退出"
        message: (gameLaunch.crash.instanceName || "") + "\n退出码：" + (gameLaunch.crash.exitCode || 0) + "\n" + (gameLaunch.crash.reason || "请查看进程日志和崩溃报告。")
        confirmText: "查看日志"
        onConfirmed: gameLaunch.openLogs()
        onClosed: gameLaunch.dismissCrash()
    }
    Timer {
        id: settingsCleanup
        interval: 600
        onTriggered: {
            if (!window.settingsShown)
                instanceSettings.close();
        }
    }
    Connections {
        target: globalSettings
        function onRestartRequested() {
            window.close();
        }
    }
    Connections {
        target: launcherFlow
        function onActiveChanged() {
            if (launcherFlow.active) {
                accountService.cancelLogin();
                accountDialogs.active = false;
                pageHost.forceActiveFocus();
            }
        }
    }
    Shortcut {
        sequence: "Alt+Left"
        enabled: window.headerShown && !launcherFlow.active
        onActivated: window.back()
    }

    onVisibilityChanged: function (newVisibility) {
        if (newVisibility === Window.Maximized || newVisibility === Window.FullScreen)
            showNormal();
    }

    Rectangle {
        id: titleBar
        z: 1
        width: parent.width
        height: 56
        color: "#edf0f4"
        MouseArea {
            anchors.fill: parent
            acceptedButtons: Qt.LeftButton
            onPressed: windowActions.startMove(window)
            onDoubleClicked: function (mouse) {
                mouse.accepted = true;
            }
        }
        Item {
            anchors.fill: parent
            opacity: pageHost.shellOpacity
            visible: opacity > 0
            enabled: !launcherFlow.active
            Item {
                id: mainHeader
                width: parent.width
                height: parent.height
                visible: opacity > 0
                enabled: !window.headerShown
                Text {
                    x: 20
                    anchors.verticalCenter: parent.verticalCenter
                    anchors.verticalCenterOffset: 1
                    text: "97Launch"
                    color: "#30343b"
                    font.pixelSize: 21
                    font.bold: true
                }
                LiteTopNavigation {
                    id: topNavigation
                    anchors.horizontalCenter: parent.horizontalCenter
                    anchors.verticalCenter: parent.verticalCenter
                }
            }
            Item {
                id: instanceHeader
                width: parent.width
                height: parent.height
                opacity: 0
                visible: opacity > 0
                enabled: window.headerShown
                LiteIconButton {
                    x: 16
                    anchors.verticalCenter: parent.verticalCenter
                    width: 28
                    height: 32
                    source: "qrc:/art/icons/back.svg"
                    text: "返回"
                    onClicked: window.back()
                }
                Text {
                    x: 56
                    anchors.verticalCenter: parent.verticalCenter
                    width: parent.width - x - 96
                    text: window.detailTitle
                    font.pixelSize: 16
                    font.weight: Font.DemiBold
                    color: "#454d59"
                    elide: Text.ElideRight
                }
            }
        }
        WindowButton {
            id: minimizeButton
            x: parent.width - 76
            y: 12
            text: "最小化"
            onClicked: windowActions.minimize(window)
        }
        WindowButton {
            id: closeButton
            x: parent.width - 44
            y: 12
            text: "关闭"
            closeAction: true
            onClicked: window.close()
        }
    }
    ParallelAnimation {
        id: titleExit
        NumberAnimation {
            target: window.headerShown ? mainHeader : instanceHeader
            property: "opacity"
            to: 0
            duration: 150
        }
        NumberAnimation {
            target: window.headerShown ? mainHeader : instanceHeader
            property: "x"
            to: window.headerShown ? 12 : -18
            duration: 150
            easing.type: Easing.InQuad
        }
    }
    SequentialAnimation {
        id: titleEnter
        PauseAnimation {
            duration: 200
        }
        ParallelAnimation {
            NumberAnimation {
                target: window.headerShown ? instanceHeader : mainHeader
                property: "opacity"
                to: 1
                duration: 150
            }
            NumberAnimation {
                target: window.headerShown ? instanceHeader : mainHeader
                property: "x"
                to: 0
                duration: 350
                easing.type: Easing.OutBack
                easing.overshoot: 0.8
            }
        }
    }
    LitePageHost {
        id: pageHost
        y: titleBar.height
        width: parent.width
        height: parent.height - y
        flowShown: launcherFlow.active
        requestedPage: window.settingsShown ? 5 : window.downloadShown ? 6 : topNavigation.currentIndex
        downloadDetail: window.downloadDetail
        onDownloadRequested: function (detail) {
            window.openDownload(detail, false);
        }
        onDependencyRequested: function (detail, previous) {
            window.openDownload(detail, true, previous);
        }
        onDownloadCategoryRequested: {
            window.downloadShown = false;
            window.downloadHistory = [];
            if (window.settingsShown)
                window.closeInstanceSettings();
            topNavigation.currentIndex = 2;
        }
        onAddAccountRequested: window.openAccounts("choices")
        onSwitchAccountRequested: accountView = "accounts"
        onNavigateRequested: function (page) {
            window.downloadShown = false;
            if (window.settingsShown)
                window.closeInstanceSettings();
            topNavigation.currentIndex = page;
        }
        onSettingsRequested: function (path) {
            window.openInstanceSettings(path);
        }
    }
    LiteConfirmDialog {
        id: transferQuestion
        property var promptId
        onConfirmed: {
            if (instanceJobs.prompt.id === promptId)
                instanceJobs.answer(true);
        }
        onRejected: {
            if (instanceJobs.prompt.id === promptId)
                instanceJobs.answer(false);
        }
        onClosed: Qt.callLater(window.syncTransferPrompt)
    }
    function syncTransferPrompt() {
        const prompt = instanceJobs.prompt;
        if (prompt.id && !transferQuestion.visible) {
            transferQuestion.promptId = prompt.id;
            transferQuestion.heading = prompt.title;
            transferQuestion.message = prompt.message;
            transferQuestion.confirmText = prompt.confirm;
            transferQuestion.open();
        } else if ((!prompt.id || prompt.id !== transferQuestion.promptId) && transferQuestion.visible) {
            transferQuestion.close();
        }
    }
    Connections {
        target: instanceJobs
        function onChanged() { window.syncTransferPrompt(); }
    }
    function openAccounts(view) {
        accountDialogs.requestedView = view;
        if (accountDialogs.status === Loader.Ready)
            accountDialogs.item.open(view);
        else
            accountDialogs.active = true;
    }
    Loader {
        id: accountDialogs
        anchors.fill: parent
        property string requestedView: "choices"
        active: false
        source: "components/AccountDialogs.qml"
        onLoaded: item.open(requestedView)
    }
    Connections {
        target: instanceJobs
        function onDownloadQueued(title, showTransfer) {
            if (launcherFlow.active)
                return;
            if (showTransfer) {
                window.downloadShown = false;
                window.downloadHistory = [];
                if (window.settingsShown)
                    window.closeInstanceSettings();
                topNavigation.currentIndex = 3;
            } else {
                downloadToast.show("已开始下载：" + title);
            }
        }
        function onChanged() {
            if (window.quitAfterJobs && !instanceJobs.busy)
                window.close();
        }
        function onReturnToLaunch() {
            window.downloadShown = false;
            if (window.settingsShown)
                window.closeInstanceSettings();
            topNavigation.currentIndex = 0;
        }
        function onInstanceMoved(oldPath, newPath) {
            window.closeInstanceSettings();
        }
    }
    LiteToast { id: downloadToast }
    LiteDropImport {
        anchors.fill: parent
        z: 300
        importEnabled: !launcherFlow.active
        onFinished: function (message, failed) { downloadToast.show(message, failed); }
    }
    Connections {
        target: accountDialogs.item
        function onFinished() {
            accountDialogs.active = false;
        }
        function onModeSelected(view) {
            pageHost.accountView = view;
        }
    }
    Connections {
        target: instanceService
        function onInstanceSelected() {
            topNavigation.currentIndex = 0;
        }
    }
    Connections {
        target: accountService
        function onProfilesRequired() {
            window.openAccounts("profiles");
        }
        function onLoginSucceeded() {
            pageHost.accountView = "identity";
        }
    }
}
