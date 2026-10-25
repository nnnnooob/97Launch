import QtQuick
import QtQuick.Controls.Basic as Controls
import "LiteMotion.js" as Motion

Item {
    id: pane
    signal addRequested
    signal switchRequested
    signal viewRequested(string view)
    property string view: "identity"
    property string nameText: ""
    property string serverText: ""
    property string userText: ""
    property string passwordText: ""
    property bool remember: true
    property var removing: ({})
    property bool activeView: visible && enabled
    readonly property bool launching: gameLaunch.starting
    onLaunchingChanged: {
        if (launching)
            pane.viewRequested("identity");
    }
    HoverHandler {
        id: paneHover
        enabled: pane.activeView
    }

    function back() {
        accountService.cancelLogin();
        accountService.clearError();
        passwordText = "";
        viewRequested("accounts");
    }
    function selectAccount(id) {
        if (accountService.selectAccount(id))
            viewRequested("identity");
    }
    onViewChanged: {
        if (paneViews && paneViews.item)
            bounce.restart();
    }
    NumberAnimation {
        id: bounce
        target: paneViews
        property: "scale"
        from: 0.96
        to: 1
        duration: 400
        easing.type: Easing.BezierSpline
        easing.bezierCurve: Motion.outBackWeak
    }
    Loader {
        id: paneViews
        anchors.fill: parent
        sourceComponent: pane.launching ? launchingView : pane.view === "accounts" ? pickerView : pane.view === "microsoft" ? microsoftView : pane.view === "offline" ? offlineView : pane.view === "thirdparty" ? serverView : pane.view === "remove" ? removeView : identityView
        onLoaded: bounce.restart()
    }
    Connections {
        target: accountService
        function onLoginSucceeded() {
            pane.passwordText = "";
        }
    }
    Component {
        id: launchingView
        Item {
            id: launchingPanel
            readonly property var preparation: gameLaunch.preparation
            readonly property bool hasProgress: preparation.progress !== null && preparation.progress !== undefined && isFinite(Number(preparation.progress))
            readonly property real progress: hasProgress ? Math.max(0, Math.min(1, Number(preparation.progress))) : 0
            Column {
                anchors.centerIn: parent
                anchors.verticalCenterOffset: -26
                width: parent.width - 40
                spacing: 12
                Item {
                    anchors.horizontalCenter: parent.horizontalCenter
                    width: 56
                    height: 56
                    Image {
                        id: launchBolt
                        anchors.fill: parent
                        source: "qrc:/art/icons/launch-lightning.svg"
                        sourceSize: Qt.size(56, 56)
                        SequentialAnimation {
                            loops: Animation.Infinite
                            running: launchingPanel.visible && pane.activeView
                            NumberAnimation { target: launchBolt; property: "opacity"; to: 0.45; duration: 550; easing.type: Easing.InOutSine }
                            NumberAnimation { target: launchBolt; property: "opacity"; to: 1; duration: 550; easing.type: Easing.InOutSine }
                        }
                    }
                }
                Column {
                    width: parent.width
                    spacing: 6
                    Text {
                        width: parent.width
                        text: "正在启动游戏"
                        font.pixelSize: 18
                        font.weight: Font.DemiBold
                        color: "#454d59"
                        horizontalAlignment: Text.AlignHCenter
                    }
                    Text {
                        width: parent.width
                        text: launchingPanel.preparation.name || "正在准备实例"
                        font.pixelSize: 12
                        color: "#778292"
                        horizontalAlignment: Text.AlignHCenter
                        wrapMode: Text.Wrap
                        maximumLineCount: 2
                        elide: Text.ElideRight
                    }
                }
                Rectangle {
                    id: launchTrack
                    width: parent.width
                    height: 4
                    radius: 2
                    color: "#e8ebf0"
                    clip: true
                    Rectangle {
                        visible: launchingPanel.hasProgress
                        width: parent.width * launchingPanel.progress
                        height: parent.height
                        radius: 2
                        color: "#8792a2"
                        Behavior on width { NumberAnimation { duration: 100; easing.type: Easing.OutCubic } }
                    }
                    Rectangle {
                        id: launchPulse
                        width: parent.width * 0.3
                        height: parent.height
                        radius: 2
                        color: "#8792a2"
                        visible: !launchingPanel.hasProgress
                        NumberAnimation on x {
                            from: -launchPulse.width; to: launchTrack.width; duration: 1000
                            loops: Animation.Infinite
                            running: launchPulse.visible && launchingPanel.visible && pane.activeView
                        }
                    }
                }
                Item { width: 1; height: 4 }
                Column {
                    width: parent.width
                    spacing: 7
                    Repeater {
                        model: [
                            { label: "当前步骤", value: launchingPanel.preparation.stage || "准备启动" },
                            { label: "验证方式", value: launchingPanel.preparation.accountType || "游戏账号" },
                            { label: "启动进度", value: launchingPanel.hasProgress ? (launchingPanel.progress * 100).toFixed(2) + "%" : "处理中…" }
                        ]
                        Item {
                            required property var modelData
                            width: parent.width
                            height: 17
                            Text {
                                width: 62
                                text: modelData.label
                                font.pixelSize: 11
                                color: "#9aa1ac"
                                horizontalAlignment: Text.AlignRight
                            }
                            Text {
                                x: 74
                                width: parent.width - x
                                text: modelData.value
                                font.pixelSize: 11
                                color: "#687384"
                                elide: Text.ElideRight
                                LiteToolTip {
                                    visible: stepHover.hovered
                                    delay: 400
                                    text: parent.text
                                }
                                HoverHandler { id: stepHover }
                            }
                        }
                    }
                }
            }
            LiteButton {
                anchors.left: parent.left
                anchors.right: parent.right
                anchors.bottom: parent.bottom
                anchors.margins: 20
                height: 34
                text: "取消启动"
                onClicked: gameLaunch.cancel()
            }
        }
    }
    Component {
        id: identityView
        Item {
            Column {
                id: identity
                anchors.centerIn: parent
                spacing: 8
                width: parent.width - 24
                visible: accountService.count > 0
                AccountHead {
                    anchors.horizontalCenter: parent.horizontalCenter
                    width: 64
                    height: 64
                    account: accountService.current
                }
                Text {
                    width: parent.width
                    topPadding: 4
                    text: accountService.current.name || ""
                    font.pixelSize: 15
                    color: "#454d59"
                    horizontalAlignment: Text.AlignHCenter
                    elide: Text.ElideRight
                }
                Text {
                    width: parent.width
                    text: accountService.current.typeDisplay || ""
                    font.pixelSize: 11
                    color: "#89919d"
                    horizontalAlignment: Text.AlignHCenter
                }
            }
            LiteIconButton {
                id: switchButton
                anchors.top: identity.bottom
                anchors.topMargin: 14
                anchors.horizontalCenter: parent.horizontalCenter
                text: "切换账号"
                source: "qrc:/art/icons/switch-account.svg"
                visible: accountService.count > 0 && pane.activeView
                revealed: pane.activeView && paneHover.hovered
                onClicked: pane.switchRequested()
            }
            Column {
                anchors.centerIn: parent
                width: parent.width - 36
                spacing: 24
                visible: accountService.count === 0
                Rectangle {
                    width: parent.width
                    height: 64
                    radius: 4
                    color: "#e6effb"
                    Text {
                        anchors.fill: parent
                        anchors.margins: 12
                        text: "新建并选择一个账号以启动游戏"
                        font.pixelSize: 12
                        color: "#4779b0"
                        wrapMode: Text.Wrap
                        verticalAlignment: Text.AlignVCenter
                    }
                }
                LiteIconButton {
                    id: addButton
                    anchors.horizontalCenter: parent.horizontalCenter
                    text: "添加账号"
                    source: "qrc:/art/icons/account-add.svg"
                    onClicked: pane.addRequested()
                }
            }
        }
    }
    Component {
        id: pickerView
        Item {
            Column {
                anchors.centerIn: parent
                width: parent.width - 28
                spacing: 12
                ListView {
                    id: list
                    width: parent.width
                    height: Math.min(208, count * 60)
                    model: accountService.accounts
                    spacing: 4
                    clip: true
                    boundsBehavior: Flickable.StopAtBounds
                    delegate: Rectangle {
                        id: row
                        required property var modelData
                        width: list.width
                        height: 56
                        radius: 6
                        color: "#f8f9fb"
                        HoverHandler {
                            id: rowHover
                        }
                        LiteButton {
                            width: parent.width - 32
                            height: parent.height
                            padding: 6
                            text: "切换到 " + row.modelData.name
                            background: Item {}
                            contentItem: Item {
                                AccountHead {
                                    x: 0
                                    anchors.verticalCenter: parent.verticalCenter
                                    width: 36
                                    height: 36
                                    account: row.modelData
                                }
                                Column {
                                    x: 42
                                    width: parent.width - x
                                    anchors.verticalCenter: parent.verticalCenter
                                    spacing: 3
                                    Text {
                                        width: parent.width
                                        text: row.modelData.name
                                        font.pixelSize: 13
                                        color: "#454d59"
                                        elide: Text.ElideRight
                                    }
                                    Text {
                                        width: parent.width
                                        text: row.modelData.typeDisplay
                                        font.pixelSize: 10
                                        color: "#89919d"
                                        elide: Text.ElideRight
                                    }
                                }
                            }
                            onClicked: pane.selectAccount(row.modelData.id)
                        }
                        LiteIconButton {
                            id: deleteAccount
                            anchors.right: parent.right
                            anchors.rightMargin: 3
                            anchors.verticalCenter: parent.verticalCenter
                            width: 28
                            height: 28
                            text: "移除账号"
                            source: "qrc:/art/icons/trash.svg"
                            iconSize: 18
                            revealed: rowHover.hovered
                            onClicked: {
                                pane.removing = row.modelData;
                                pane.viewRequested("remove");
                            }
                        }
                    }
                }
                LiteIconButton {
                    id: addAccount
                    anchors.horizontalCenter: parent.horizontalCenter
                    text: "添加账号"
                    source: "qrc:/art/icons/account-add.svg"
                    onClicked: pane.addRequested()
                }
                Text {
                    width: parent.width
                    visible: accountService.error.length > 0
                    text: accountService.error
                    wrapMode: Text.Wrap
                    font.pixelSize: 11
                    color: "#bc4f4f"
                }
            }
        }
    }
    Component {
        id: microsoftView
        Item {
            Column {
                anchors.centerIn: parent
                width: parent.width - 36
                spacing: 16
                Image {
                    anchors.horizontalCenter: parent.horizontalCenter
                    width: 48
                    height: 48
                    source: "qrc:/art/icons/user.svg"
                    sourceSize: Qt.size(48, 48)
                }
                LiteButton {
                    anchors.horizontalCenter: parent.horizontalCenter
                    width: 140
                    height: 36
                    text: accountService.busy ? "正在登录……" : "登录"
                    enabled: !accountService.busy
                    onClicked: accountService.startMicrosoft()
                }
                Text {
                    width: parent.width
                    visible: accountService.busy || accountService.error.length > 0
                    text: accountService.busy ? accountService.status : accountService.error
                    wrapMode: Text.Wrap
                    font.pixelSize: 11
                    color: accountService.error.length > 0 ? "#bc4f4f" : "#89919d"
                }
                LiteButton {
                    anchors.horizontalCenter: parent.horizontalCenter
                    width: 80
                    height: 28
                    text: "返回"
                    background: Item {}
                    onClicked: pane.back()
                }
            }
        }
    }
    Component {
        id: offlineView
        Item {
            Column {
                anchors.centerIn: parent
                width: parent.width - 32
                spacing: 14
                LiteTextField {
                    width: parent.width
                    text: pane.nameText
                    placeholderText: "游戏用户名"
                    onTextEdited: pane.nameText = text
                    onAccepted: accountService.addOffline(text)
                }
                Text {
                    width: parent.width
                    visible: accountService.error.length > 0
                    text: accountService.error
                    wrapMode: Text.Wrap
                    font.pixelSize: 11
                    color: "#bc4f4f"
                }
                Row {
                    anchors.horizontalCenter: parent.horizontalCenter
                    spacing: 10
                    LiteButton {
                        width: 76
                        height: 34
                        text: "返回"
                        onClicked: pane.back()
                    }
                    LiteButton {
                        width: 76
                        height: 34
                        text: "创建"
                        primary: true
                        enabled: pane.nameText.trim().length > 0
                        onClicked: accountService.addOffline(pane.nameText)
                    }
                }
            }
        }
    }
    Component {
        id: serverView
        Item {
            Flickable {
                anchors.fill: parent
                anchors.margins: 16
                clip: true
                contentHeight: Math.max(height, serverFields.implicitHeight)
                boundsBehavior: Flickable.StopAtBounds
                Column {
                    id: serverFields
                    width: parent.width
                    y: Math.max(0, (parent.height - implicitHeight) / 2)
                    spacing: 10
                    LiteTextField {
                        width: parent.width
                        placeholderText: "认证地址 / 通行证 ID"
                        text: pane.serverText
                        font.pixelSize: 12
                        enabled: !accountService.busy
                        onTextEdited: pane.serverText = text
                    }
                    LiteTextField {
                        width: parent.width
                        placeholderText: "邮箱 / 登录账号"
                        text: pane.userText
                        font.pixelSize: 12
                        enabled: !accountService.busy
                        onTextEdited: pane.userText = text
                    }
                    LiteTextField {
                        width: parent.width
                        placeholderText: "密码"
                        text: pane.passwordText
                        echoMode: TextInput.Password
                        font.pixelSize: 12
                        enabled: !accountService.busy
                        onTextEdited: pane.passwordText = text
                        onAccepted: accountService.loginThirdParty(pane.serverText, pane.userText, text, pane.remember)
                    }
                    Text {
                        width: parent.width
                        visible: accountService.busy || accountService.error.length > 0
                        text: accountService.busy ? accountService.status : accountService.error
                        font.pixelSize: 11
                        color: accountService.error.length > 0 ? "#bc4f4f" : "#89919d"
                        wrapMode: Text.Wrap
                    }
                    Row {
                        anchors.horizontalCenter: parent.horizontalCenter
                        spacing: 10
                        LiteButton {
                            width: 76
                            height: 34
                            text: "返回"
                            onClicked: pane.back()
                        }
                        LiteButton {
                            width: 90
                            height: 34
                            text: accountService.busy ? "登录中…" : "登录"
                            primary: true
                            enabled: !accountService.busy && pane.serverText.trim().length > 0 && pane.userText.trim().length > 0
                            onClicked: accountService.loginThirdParty(pane.serverText, pane.userText, pane.passwordText, pane.remember)
                        }
                    }
                }
            }
        }
    }
    Component {
        id: removeView
        Item {
            Column {
                anchors.centerIn: parent
                width: parent.width - 32
                spacing: 18
                Text {
                    width: parent.width
                    text: "移除账号「" + (pane.removing.name || "") + "」？"
                    font.pixelSize: 13
                    color: "#454d59"
                    wrapMode: Text.Wrap
                    horizontalAlignment: Text.AlignHCenter
                }
                Row {
                    anchors.horizontalCenter: parent.horizontalCenter
                    spacing: 10
                    LiteButton {
                        width: 76
                        height: 34
                        text: "取消"
                        onClicked: pane.viewRequested("accounts")
                    }
                    LiteButton {
                        width: 76
                        height: 34
                        text: "移除"
                        primary: true
                        onClicked: {
                            if (accountService.removeAccount(pane.removing.id))
                                pane.viewRequested(accountService.count > 0 ? "accounts" : "identity");
                        }
                    }
                }
                Text {
                    width: parent.width
                    visible: accountService.error.length > 0
                    text: accountService.error
                    wrapMode: Text.Wrap
                    font.pixelSize: 11
                    color: "#bc4f4f"
                }
            }
        }
    }
}
