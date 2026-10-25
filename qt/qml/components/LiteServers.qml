import QtQuick
import QtQuick.Controls.Basic as Controls

Item {
    id: servers
    property var selectedServer: ({})
    Component.onCompleted: serverCatalog.setActive(visible)
    onVisibleChanged: {
        serverCatalog.setActive(visible);
        if (!visible) {
            installDialog.close();
            serverActions.close();
        }
    }
    Component.onDestruction: serverCatalog.setActive(false)

    Text {
        anchors.verticalCenter: refreshButton.verticalCenter
        text: "服务器列表"
        font.pixelSize: 13
        font.weight: Font.DemiBold
        color: "#454d59"
    }
    Text {
        anchors.right: refreshButton.left
        anchors.rightMargin: 8
        anchors.verticalCenter: refreshButton.verticalCenter
        text: serverCatalog.busy ? "刷新中…" : serverCatalog.count + " 个"
        font.pixelSize: 11
        color: "#89919d"
    }
    LiteIconButton {
        id: refreshButton
        anchors.right: parent.right
        width: 26
        height: 26
        iconSize: 16
        source: "qrc:/art/icons/refresh.svg"
        text: "刷新服务器"
        enabled: !serverCatalog.busy && !serverCatalog.preparingId
        onClicked: serverCatalog.refresh()
    }
    Text {
        id: status
        y: 31
        width: parent.width
        height: visible ? implicitHeight : 0
        visible: !!text
        text: serverCatalog.notice || serverCatalog.error
        font.pixelSize: 10
        color: serverCatalog.notice ? "#778292" : "#ad825c"
        wrapMode: Text.Wrap
    }
    ListView {
        id: serverList
        y: status.visible ? status.y + status.height + 8 : 36
        width: parent.width
        height: parent.height - y
        clip: true
        spacing: 8
        boundsBehavior: Flickable.StopAtBounds
        model: serverCatalog
        Controls.ScrollBar.vertical: Controls.ScrollBar {
            width: 3
            policy: Controls.ScrollBar.AsNeeded
        }
        delegate: Rectangle {
            id: serverCard
            required property var record
            function openActions() {
                servers.selectedServer = record;
                serverActions.open();
            }
            activeFocusOnTab: true
            Accessible.role: Accessible.Button
            Accessible.name: record.name + "，服务器操作"
            Accessible.onPressAction: openActions()
            Keys.onReturnPressed: openActions()
            Keys.onEnterPressed: openActions()
            Keys.onSpacePressed: openActions()
            width: serverList.width
            height: 84
            radius: 8
            color: cardHover.hovered ? "#f7f8fa" : "#ffffff"
            border.color: cardHover.hovered ? "#cbd2dc" : "#e2e5ea"
            Behavior on color {
                ColorAnimation {
                    duration: 100
                }
            }
            Behavior on border.color {
                ColorAnimation {
                    duration: 100
                }
            }
            HoverHandler {
                id: cardHover
                cursorShape: Qt.PointingHandCursor
            }
            TapHandler {
                acceptedButtons: Qt.LeftButton
                onTapped: serverCard.openActions()
            }
            Rectangle {
                x: 1
                y: 1
                width: parent.width - 2
                height: 31
                radius: 7
                color: cardHover.hovered ? "#e7ecf2" : "#edf1f5"
                Behavior on color {
                    ColorAnimation {
                        duration: 100
                    }
                }
                Rectangle {
                    y: 23
                    width: parent.width
                    height: 8
                    color: parent.color
                }
            }
            Text {
                x: 12
                y: 7
                width: parent.width - 37
                text: serverCard.record.name
                font.pixelSize: 13
                font.weight: Font.DemiBold
                color: "#454d59"
                elide: Text.ElideRight
                LiteToolTip {
                    visible: titleHover.hovered
                    delay: 500
                    text: serverCard.record.name + "\n" + serverCard.record.description + "\n" + serverCard.record.address
                }
                HoverHandler {
                    id: titleHover
                }
            }
            Rectangle {
                anchors.right: parent.right
                anchors.rightMargin: 13
                y: 13
                width: 6
                height: 6
                radius: 3
                color: serverCard.record.state === "online" ? "#339454" : ["starting", "stopping"].indexOf(serverCard.record.state) >= 0 ? "#c3932c" : "#9ba3af"
                LiteToolTip {
                    visible: stateHover.hovered
                    delay: 400
                    text: serverCard.record.stateText
                }
                HoverHandler {
                    id: stateHover
                }
            }
            Text {
                x: 12
                y: 40
                width: parent.width - 50
                text: (serverCard.record.canJoin && serverCard.record.latency !== null && serverCard.record.latency !== undefined ? serverCard.record.latency + " ms" : serverCard.record.stateText) + "  ·  " + serverCard.record.players
                font.pixelSize: 11
                color: "#737d8a"
                elide: Text.ElideRight
                LiteToolTip {
                    visible: latencyHover.hovered
                    delay: 500
                    text: serverCard.record.stateText + "\n" + (serverCard.record.latency !== null && serverCard.record.latency !== undefined ? "连接延迟 " + serverCard.record.latency + " ms" : "暂时无法测得连接延迟")
                }
                HoverHandler {
                    id: latencyHover
                }
            }
            Text {
                x: 12
                y: 60
                width: parent.width - 50
                text: serverCard.record.version
                font.pixelSize: 10
                color: "#89919d"
                elide: Text.ElideRight
            }
            Image {
                anchors.right: parent.right
                anchors.rightMargin: 12
                y: 48
                width: 18
                height: 18
                sourceSize: Qt.size(18, 18)
                source: "qrc:/art/icons/chevron-down.svg"
                rotation: -90
                Accessible.ignored: true
            }
        }
    }
    Column {
        anchors.centerIn: serverList
        width: parent.width - 24
        spacing: 9
        visible: serverCatalog.count === 0
        Text {
            width: parent.width
            text: serverCatalog.busy ? "正在获取服务器…" : serverCatalog.error ? "暂时无法获取服务器" : "暂无已开放的服务器"
            color: "#89919d"
            font.pixelSize: 12
            wrapMode: Text.Wrap
            horizontalAlignment: Text.AlignHCenter
        }
        Text {
            width: parent.width
            visible: !serverCatalog.busy
            text: serverCatalog.error ? "点击右上角刷新，重新连接。" : "管理员发布服务器后会显示在这里。"
            color: "#a0a7b1"
            font.pixelSize: 10
            wrapMode: Text.Wrap
            horizontalAlignment: Text.AlignHCenter
        }
    }
    LitePopup {
        id: serverActions
        width: 310
        padding: 20
        closePolicy: Controls.Popup.CloseOnEscape | Controls.Popup.CloseOnPressOutside
        contentItem: Column {
            spacing: 10
            Text {
                width: parent.width
                text: servers.selectedServer.name || "服务器操作"
                font.pixelSize: 16
                font.weight: Font.DemiBold
                color: "#454d59"
                wrapMode: Text.Wrap
            }
            Text {
                width: parent.width
                text: servers.selectedServer.address || ""
                color: "#939ba7"
                font.pixelSize: 11
                elide: Text.ElideMiddle
                bottomPadding: 6
            }
            LiteButton {
                width: parent.width; height: 36; scale: 1
                text: "复制 IP"
                onClicked: {
                    serverCatalog.copyAddress(servers.selectedServer.id);
                    serverActions.close();
                }
            }
            LiteButton {
                width: parent.width; height: 36; scale: 1
                text: serverCatalog.preparingId === servers.selectedServer.id ? "正在获取…" : "获取实例"
                enabled: !!servers.selectedServer.hasPack && !serverCatalog.preparingId
                onClicked: {
                    const id = servers.selectedServer.id;
                    serverActions.close();
                    serverCatalog.requestInstall(id);
                }
            }
            LiteButton {
                width: parent.width; height: 36; scale: 1; primary: true
                text: "启动并加入服务器"
                enabled: !!servers.selectedServer.canJoin && gameLaunch.canLaunch && !serverCatalog.preparingId
                onClicked: {
                    const id = servers.selectedServer.id;
                    serverActions.close();
                    gameLaunch.start(id);
                }
            }
        }
    }
    Connections {
        target: serverCatalog
        function onChanged() {
            if (installDialog.visible && !serverCatalog.preparedPack.name)
                installDialog.close();
        }
        function onInstallDialogRequested() {
            const folders = instanceService.folders;
            let selected = 0;
            for (let i = 0; i < folders.length; ++i)
                if (folders[i].path === instanceService.selectedFolder)
                    selected = i;
            folderChoice.currentIndex = selected;
            installName.text = serverCatalog.suggestedName(folderChoice.currentValue || instanceService.selectedFolder);
            installDialog.open();
        }
    }
    LitePopup {
        id: installDialog
        width: 410
        padding: 20
        closePolicy: Controls.Popup.CloseOnEscape
        onClosed: serverCatalog.discardPrepared()
        contentItem: Column {
            spacing: 12
            Text {
                text: "获取相关实例"
                font.pixelSize: 16
                font.weight: Font.DemiBold
                color: "#454d59"
            }
            Text {
                width: parent.width
                text: (serverCatalog.preparedPack.name || "") + " / " + (serverCatalog.preparedPack.version || "") + "\n" + [serverCatalog.preparedPack.minecraft, serverCatalog.preparedPack.loader].filter(Boolean).join(" · ") + "\n" + (Number(serverCatalog.preparedPack.size || 0) / 1048576).toFixed(1) + " MiB"
                color: "#778292"
                font.pixelSize: 12
                wrapMode: Text.Wrap
            }
            Text {
                text: "安装到"
                font.pixelSize: 12
                color: "#687384"
            }
            LiteComboBox {
                id: folderChoice
                width: parent.width
                model: instanceService.folders
                textRole: "name"
                valueRole: "path"
                onActivated: installName.text = serverCatalog.suggestedName(currentValue)
            }
            Text {
                text: "新实例名称"
                font.pixelSize: 12
                color: "#687384"
            }
            LiteTextField {
                id: installName
                width: parent.width
                maximumLength: 100
                placeholderText: "实例名称"
                Accessible.name: "新实例名称"
                onAccepted: if (installButton.enabled)
                    installButton.clicked()
            }
            Text {
                width: parent.width
                text: "安装成功后自动选中新实例并返回启动页。"
                color: "#939ba7"
                font.pixelSize: 11
                wrapMode: Text.Wrap
            }
            Text {
                width: parent.width
                visible: !!text
                text: serverCatalog.error
                color: "#ad825c"
                font.pixelSize: 11
                wrapMode: Text.Wrap
            }
            Row {
                anchors.right: parent.right
                spacing: 10
                LiteButton {
                    width: 84
                    height: 32
                    text: "取消"
                    onClicked: installDialog.close()
                }
                LiteButton {
                    id: installButton
                    width: 104
                    height: 32
                    text: "安装"
                    primary: true
                    enabled: folderChoice.currentIndex >= 0 && !!installName.text.trim() && !globalSettings.authorization.busy
                    onClicked: if (serverCatalog.installRelated(folderChoice.currentValue, installName.text))
                        installDialog.close()
                }
            }
        }
    }
}
