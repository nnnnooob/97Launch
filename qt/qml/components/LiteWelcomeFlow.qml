import QtQuick
import QtQuick.Shapes
import QtQuick.Controls.Basic as Controls
import "LiteMotion.js" as Motion

Item {
    id: page
    property real formOpacity: 1
    property real formScale: 1
    property bool activeView: true
    readonly property var authorization: globalSettings.authorization
    readonly property var updates: globalSettings.updates
    readonly property var release: updates.latest || ({})
    readonly property bool verifying: !!authorization.busy || !launcherFlow.initialized
    readonly property bool retry: !!authorization.hasCredential && !showPairing
    readonly property bool updating: displayedView === "update"
    readonly property bool preparing: displayedView === "preparation"
    property bool showPairing: false
    property bool messageDismissed: false
    property string displayedView: "pairing"
    readonly property real formWidth: width * 0.51 - 68

    component LinkButton: Controls.AbstractButton {
        id: link
        implicitWidth: contentItem.implicitWidth
        implicitHeight: 28
        hoverEnabled: true
        focusPolicy: Qt.StrongFocus
        opacity: enabled ? 1 : 0.45
        Accessible.name: text
        contentItem: Text {
            text: link.text
            color: link.down ? "#30343b" : link.hovered ? "#454d59" : "#778292"
            font.pixelSize: 12
            font.underline: link.visualFocus
            verticalAlignment: Text.AlignVCenter
        }
        background: null
    }
    component ProgressTrack: Controls.ProgressBar {
        height: 4
        palette.highlight: "#798493"
        palette.midlight: "#edf0f4"
    }
    function submitPairing() {
        if (page.verifying || launcherFlow.authorized)
            return;
        messageDismissed = false;
        if (retry)
            globalSettings.authorizationAction("verify");
        else
            globalSettings.pairDevice(tokenInput.text);
    }
    Connections {
        target: launcherFlow
        function onChanged() {
            if (launcherFlow.authorized) {
                tokenInput.clear();
                page.showPairing = false;
            }
            if (page.displayedView !== launcherFlow.view) {
                if (page.activeView)
                    viewExit.restart();
                else {
                    viewExit.stop();
                    page.displayedView = launcherFlow.view;
                    formBody.opacity = 1;
                    formBody.scale = 1;
                }
            }
        }
    }
    Connections {
        target: globalSettings
        function onAuthorizationResolved(action) {
            page.messageDismissed = false;
        }
    }
    Component.onCompleted: displayedView = launcherFlow.view
    onActiveViewChanged: {
        if (!activeView) {
            help.close();
            tokenInput.clear();
        }
    }
    onVisibleChanged: if (!visible) {
        scene.offsetX = 0;
        scene.offsetY = 0;
    }

    Item {
        id: scene
        x: parent.width * 0.51
        width: parent.width - x
        height: parent.height
        clip: true
        property real offsetX: 0
        property real offsetY: 0
        Image {
            x: -8 + scene.offsetX
            y: -8 + scene.offsetY
            width: parent.width + 16
            height: parent.height + 16
            source: "qrc:/art/welcome-valley.png"
            fillMode: Image.PreserveAspectCrop
            mipmap: true
            Accessible.name: "清晨的 Minecraft 山谷与蓝色湖泊"
            Behavior on x {
                NumberAnimation {
                    duration: 180
                    easing.type: Easing.OutCubic
                }
            }
            Behavior on y {
                NumberAnimation {
                    duration: 180
                    easing.type: Easing.OutCubic
                }
            }
        }
        Item {
            id: wave
            width: 62
            height: parent.height
            Shape {
                width: 84
                height: 636
                preferredRendererType: Shape.CurveRenderer
                transform: Scale {
                    xScale: wave.width / 84
                    yScale: wave.height / 636
                }
                ShapePath {
                    strokeWidth: -1
                    fillColor: "#ffffff"
                    PathSvg {
                        path: "M-2,0 L84,0 C25,26 28,74 51,127 C100,228 8,282 42,387 C69,482 -4,547 34,636 L-2,636 Z"
                    }
                }
            }
        }
    }
    HoverHandler {
        enabled: page.activeView
        onPointChanged: {
            scene.offsetX = (point.position.x / Math.max(1, page.width) - 0.5) * 8;
            scene.offsetY = (point.position.y / Math.max(1, page.height) - 0.5) * 8;
        }
        onHoveredChanged: if (!hovered && page.activeView) {
            scene.offsetX = 0;
            scene.offsetY = 0;
        }
    }
    Item {
        width: page.width * 0.51
        height: parent.height
        opacity: page.formOpacity
        scale: page.formScale
        Row {
            x: 38
            y: 24
            spacing: 10
            Image {
                width: 26
                height: 26
                source: "qrc:/art/app-icon.png"
                mipmap: true
            }
            Text {
                height: 26
                text: "97Launch"
                font.pixelSize: 18
                font.weight: Font.DemiBold
                color: "#454d59"
                verticalAlignment: Text.AlignVCenter
            }
        }

        Item {
            id: formBody
            enabled: !viewExit.running
            x: 38
            y: 78
            width: page.formWidth
            height: parent.height - y - 46
            Flickable {
                id: pairingScroll
                x: -8
                width: parent.width + 16
                height: parent.height
                contentHeight: pairingForm.implicitHeight
                boundsBehavior: Flickable.StopAtBounds
                clip: true
                visible: !page.updating && !page.preparing
                Controls.ScrollBar.vertical: Controls.ScrollBar {
                    width: 3
                }
                Column {
                    id: pairingForm
                    x: 8
                    width: formBody.width
                    y: Math.max(0, (pairingScroll.height - implicitHeight) / 2)
                    Text {
                        text: "Welcome"
                        font.pixelSize: 34
                        font.weight: Font.DemiBold
                        color: "#454d59"
                        height: 58
                    }
                    Item {
                        width: parent.width
                        height: visible ? 23 : 0
                        visible: !page.retry && launcherFlow.initialized
                        Text {
                            text: "配对 Token"
                            color: "#687384"
                            font.pixelSize: 12
                        }
                        Text {
                            anchors.right: parent.right
                            text: "5 分钟内有效"
                            color: "#939ba7"
                            font.pixelSize: 11
                        }
                    }
                    LiteTextField {
                        id: tokenInput
                        width: parent.width
                        height: visible ? 40 : 0
                        visible: !page.retry && launcherFlow.initialized
                        enabled: !page.verifying && !launcherFlow.authorized
                        maximumLength: 128
                        placeholderText: "输入 Token"
                        inputMethodHints: Qt.ImhNoPredictiveText | Qt.ImhSensitiveData
                        Accessible.name: "配对 Token"
                        onAccepted: page.submitPairing()
                        onTextEdited: page.messageDismissed = true
                    }
                    Text {
                        width: parent.width
                        topPadding: 8
                        bottomPadding: 2
                        visible: !!text
                        text: !launcherFlow.initialized ? "正在读取本机授权…" : page.messageDismissed && !page.verifying ? "" : page.authorization.message || ""
                        font.pixelSize: 11
                        color: ["Unpaired", "Revoked"].indexOf(page.authorization.status) >= 0 ? "#c36969" : "#939ba7"
                        wrapMode: Text.Wrap
                    }
                    Item {
                        width: 1
                        height: 16
                    }
                    LiteButton {
                        width: parent.width
                        height: 40
                        primary: true
                        text: page.verifying ? "正在验证…" : launcherFlow.authorized ? "验证通过" : page.retry ? "重新连接" : "配对并进入"
                        enabled: !page.verifying && !launcherFlow.authorized
                        onClicked: page.submitPairing()
                    }
                    ProgressTrack {
                        width: parent.width
                        topPadding: 0
                        visible: page.verifying
                        indeterminate: visible
                    }
                    Item {
                        width: parent.width
                        height: 34
                        LinkButton {
                            y: 4
                            visible: !page.retry
                            text: "如何获取 Token？"
                            onClicked: help.open()
                        }
                        LinkButton {
                            y: 4
                            visible: page.retry && !page.verifying
                            text: "重新配对"
                            onClicked: {
                                page.showPairing = true;
                                tokenInput.forceActiveFocus();
                            }
                        }
                        LinkButton {
                            anchors.right: parent.right
                            y: 4
                            visible: !!page.authorization.busy
                            text: "取消验证"
                            onClicked: globalSettings.cancelAuthorization()
                        }
                    }
                }
            }
            Flickable {
                width: parent.width
                height: parent.height
                visible: page.preparing
                contentHeight: preparationForm.implicitHeight
                boundsBehavior: Flickable.StopAtBounds
                clip: true
                Controls.ScrollBar.vertical: Controls.ScrollBar { width: 3 }
                Column {
                    id: preparationForm
                    width: parent.width
                    spacing: 12
                    Text {
                        text: "首次准备"
                        font.pixelSize: 30
                        font.weight: Font.DemiBold
                        color: "#454d59"
                    }
                    Text {
                        width: parent.width
                        text: "正在准备本地环境，完成后自动进入。"
                        font.pixelSize: 12
                        color: "#939ba7"
                        wrapMode: Text.Wrap
                    }
                    Column {
                        width: parent.width
                        spacing: 14
                        Repeater {
                            model: launcherFlow.preparationSteps
                            delegate: Row {
                                required property var modelData
                                spacing: 10
                                Text {
                                    width: 14
                                    text: modelData.state === "done" ? "✓" : modelData.state === "active" ? "●" : modelData.state === "error" ? "!" : "○"
                                    color: modelData.state === "error" ? "#c36969" : modelData.state === "waiting" ? "#a4acb7" : "#687384"
                                    font.pixelSize: 13
                                }
                                Text {
                                    text: modelData.label
                                    color: modelData.state === "waiting" ? "#939ba7" : "#454d59"
                                    font.pixelSize: 12
                                }
                            }
                        }
                    }
                    ProgressTrack {
                        width: parent.width
                        from: 0
                        to: 1
                        value: launcherFlow.preparationProgress
                        indeterminate: launcherFlow.preparationBusy
                    }
                    Text {
                        width: parent.width
                        text: launcherFlow.preparationDetail
                        color: "#939ba7"
                        font.pixelSize: 11
                        wrapMode: Text.Wrap
                        Accessible.role: Accessible.StaticText
                    }
                    Text {
                        width: parent.width
                        visible: text.length > 0
                        text: launcherFlow.preparationError
                        color: "#c36969"
                        font.pixelSize: 11
                        wrapMode: Text.WrapAnywhere
                    }
                    Row {
                        spacing: 8
                        visible: !launcherFlow.preparationBusy && !!launcherFlow.preparationError
                        LiteButton { text: "重新准备"; primary: true; onClicked: launcherFlow.retryPreparation() }
                        LiteButton { text: "稍后安装"; visible: launcherFlow.canSkipPreview; onClicked: launcherFlow.skipPreview() }
                    }
                    Text {
                        width: parent.width
                        text: "首次完成后，下次将直接进入。"
                        color: "#a4acb7"
                        font.pixelSize: 10
                        wrapMode: Text.Wrap
                    }
                }
            }
            Flickable {
                id: updateScroll
                width: parent.width
                height: parent.height - 58
                visible: page.updating
                contentHeight: updateForm.implicitHeight
                boundsBehavior: Flickable.StopAtBounds
                clip: true
                Controls.ScrollBar.vertical: Controls.ScrollBar {
                    width: 3
                }
                Column {
                    id: updateForm
                    width: parent.width
                    spacing: 10
                    Text {
                        text: "软件更新"
                        font.pixelSize: 28
                        font.weight: Font.DemiBold
                        color: "#454d59"
                    }
                    Text {
                        width: parent.width
                        text: page.updates.status || "尚未检查更新"
                        color: "#687384"
                        font.pixelSize: 14
                        font.weight: Font.DemiBold
                        wrapMode: Text.Wrap
                    }
                    Text {
                        width: parent.width
                        text: page.updates.detail || "当前版本 " + globalSettings.version
                        color: "#939ba7"
                        font.pixelSize: 11
                        wrapMode: Text.WrapAnywhere
                    }
                    ProgressTrack {
                        width: parent.width
                        visible: !!page.updates.busy
                        indeterminate: visible && !page.updates.downloading
                        from: 0
                        to: 100
                        value: Number(page.updates.progress || 0)
                        Accessible.name: "应用更新进度"
                    }
                    Text {
                        visible: !!page.updates.downloading
                        text: Number(page.updates.progress || 0).toFixed(0) + "%" + (Number(page.updates.speed || 0) > 0 ? " · " + (Number(page.updates.speed) / 1048576).toFixed(1) + " MiB/s" : "")
                        color: "#939ba7"
                        font.pixelSize: 11
                    }
                    Text {
                        visible: !!text
                        width: parent.width
                        text: page.updates.error || ""
                        font.pixelSize: 11
                        color: "#c36969"
                        wrapMode: Text.WrapAnywhere
                    }
                    Column {
                        width: parent.width
                        visible: !!page.release.id
                        spacing: 7
                        Text {
                            text: "更新说明 · v" + (page.release.version || "")
                            color: "#687384"
                            font.pixelSize: 12
                            font.weight: Font.DemiBold
                        }
                        Controls.TextArea {
                            width: parent.width
                            readOnly: true
                            selectByMouse: true
                            wrapMode: TextEdit.Wrap
                            text: page.release.notes || "此版本暂无更新说明。"
                            font.pixelSize: 11
                            color: "#939ba7"
                            padding: 0
                            background: null
                        }
                    }
                    Item {
                        width: parent.width
                        height: 32
                        Text {
                            anchors.verticalCenter: parent.verticalCenter
                            text: "更新通道"
                            color: "#687384"
                            font.pixelSize: 12
                        }
                        LiteComboBox {
                            anchors.right: parent.right
                            width: 132
                            model: ["稳定版", "测试版"]
                            currentIndex: page.updates.channel === "stable" ? 0 : 1
                            enabled: !page.updates.busy
                            onActivated: globalSettings.configureUpdates(currentIndex === 0 ? "stable" : "preview", !!page.updates.startupCheck)
                        }
                    }
                    LiteCheckBox {
                        text: "启动时检查更新"
                        checked: !!page.updates.startupCheck
                        enabled: !page.updates.busy
                        onClicked: globalSettings.configureUpdates(page.updates.channel || "preview", checked)
                    }
                    Text {
                        width: parent.width
                        text: "下载完成后由你选择重启时间。"
                        color: "#939ba7"
                        font.pixelSize: 11
                        wrapMode: Text.Wrap
                    }
                }
            }
            Row {
                anchors.bottom: parent.bottom
                width: parent.width
                spacing: 10
                visible: page.updating
                LiteButton {
                    width: (parent.width - 10) * 0.6
                    height: 38
                    primary: true
                    enabled: !page.updates.busy
                    text: page.updates.busy ? "处理中…" : page.updates.ready ? "重启并更新" : page.release.id ? "下载更新" : "检查更新"
                    onClicked: globalSettings.updateAction(page.updates.ready ? "apply" : page.release.id ? "download" : "check")
                }
                LiteButton {
                    width: (parent.width - 10) * 0.4
                    height: 38
                    enabled: !page.updates.busy || !!page.updates.downloading
                    text: page.updates.downloading ? "取消下载" : launcherFlow.authorized ? "继续进入" : "返回验证"
                    onClicked: {
                        if (page.updates.downloading)
                            globalSettings.updateAction("cancel");
                        else
                            launcherFlow.continueFromUpdates();
                    }
                }
            }
        }
        Item {
            x: 38
            width: page.formWidth
            height: 28
            y: parent.height - 34
            Text {
                anchors.verticalCenter: parent.verticalCenter
                text: "v" + globalSettings.version
                color: "#939ba7"
                font.pixelSize: 10
            }
            LinkButton {
                anchors.right: parent.right
                visible: !page.updating && !page.preparing
                enabled: !page.verifying
                text: "检查更新"
                onClicked: launcherFlow.openUpdates(true)
            }
            LinkButton {
                anchors.right: parent.right
                visible: page.updating && !!page.release.id
                enabled: !page.updates.busy
                text: "重新检查"
                onClicked: globalSettings.updateAction("check")
            }
        }
    }
    SequentialAnimation {
        id: viewExit
        ParallelAnimation {
            NumberAnimation {
                target: formBody
                property: "opacity"
                to: 0
                duration: 100
            }
            NumberAnimation {
                target: formBody
                property: "scale"
                to: 0.96
                duration: 100
                easing.type: Easing.InQuad
            }
        }
        ScriptAction {
            script: {
                page.displayedView = launcherFlow.view;
                pairingScroll.contentY = 0;
                updateScroll.contentY = 0;
            }
        }
        ParallelAnimation {
            NumberAnimation {
                target: formBody
                property: "opacity"
                to: 1
                duration: 160
            }
            NumberAnimation {
                target: formBody
                property: "scale"
                to: 1
                duration: 380
                easing.type: Easing.BezierSpline
                easing.bezierCurve: Motion.outBackWeak
            }
        }
    }
    LitePopup {
        id: help
        width: 400
        padding: 20
        closePolicy: Controls.Popup.CloseOnEscape | Controls.Popup.CloseOnPressOutside
        contentItem: Column {
            spacing: 16
            Text {
                text: "获取配对 Token"
                font.pixelSize: 16
                font.weight: Font.DemiBold
                color: "#454d59"
            }
            Text {
                width: parent.width
                text: "1. 打开已允许使用 97Launch 的 QQ 群。\n\n2. @群里的机器人，发送“获取配对Token”。\n\n3. 打开机器人私聊，完整复制 97p_ 开头的 Token，粘贴到这里。\n\n私聊发送失败时，请添加机器人好友后重新申请。Token 区分大小写，5 分钟内有效，仅可使用一次，请勿转发。"
                font.pixelSize: 12
                color: "#778292"
                wrapMode: Text.Wrap
            }
            LiteButton {
                anchors.right: parent.right
                height: 34
                text: "知道了"
                onClicked: help.close()
            }
        }
    }
}
