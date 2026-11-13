import QtQuick
import QtQuick.Controls.Basic as Controls

Item {
    id: dropHost
    property bool importEnabled: false
    property bool recognizing: false
    property bool hovering: false
    property string recognitionText: "正在检查文件"
    property real pointerX: width / 2
    property real pointerY: height / 2
    property real cardOffset: 8
    property bool motion: true
    property bool highContrast: false
    signal finished(string message, bool failed)

    function hide() { hovering = false; recognizing = false; leave.stop(); }
    function hasFiles(drag) {
        return drag.hasUrls && drag.urls.some(url => String(url).toLowerCase().indexOf("file:") === 0);
    }
    function answer(choice) {
        if (conflict.answered || dropJobs.prompt.id !== conflict.promptId) return;
        conflict.answered = true;
        dropJobs.answerDrop(choice, applyAll.checked);
        conflict.close();
    }
    function syncQuestion() {
        const prompt = dropJobs.prompt;
        if (prompt.id && !conflict.visible) {
            conflict.promptId = prompt.id;
            conflict.message = prompt.message;
            conflict.answered = false;
            applyAll.checked = false;
            conflict.open();
        } else if ((!prompt.id || prompt.id !== conflict.promptId) && conflict.visible) {
            conflict.close();
        }
    }
    onImportEnabledChanged: {
        if (!importEnabled) {
            hide();
            if (dropJobs.busy) dropJobs.cancel();
            conflict.close();
        }
    }

    DropArea {
        anchors.fill: parent
        enabled: dropHost.importEnabled && !dropJobs.busy
        onEntered: function (drag) {
            if (!dropHost.hasFiles(drag)) { drag.accepted = false; return; }
            drag.accept(Qt.CopyAction);
            dropHost.motion = windowActions.animationsEnabled();
            dropHost.highContrast = windowActions.highContrast();
            leave.stop();
            dropHost.pointerX = drag.x;
            dropHost.pointerY = drag.y;
            dropHost.hovering = true;
        }
        onPositionChanged: function (drag) {
            leave.stop();
            dropHost.pointerX = drag.x;
            dropHost.pointerY = drag.y;
        }
        onExited: leave.restart()
        onDropped: function (drop) {
            if (!dropHost.importEnabled || dropJobs.busy || !dropHost.hasFiles(drop)) { drop.accepted = false; return; }
            drop.accept(Qt.CopyAction);
            leave.stop();
            dropHost.hovering = false;
            dropHost.recognizing = true;
            dropHost.recognitionText = "正在检查 " + drop.urls.length + " 个文件";
            if (!dropJobs.importDrop(drop.urls)) {
                dropHost.hide();
                dropHost.finished(dropJobs.error, true);
            }
        }
    }
    Timer {
        id: leave
        interval: 100
        onTriggered: if (!dropJobs.busy) dropHost.hovering = false
    }
    Item {
        id: overlay
        y: 56
        width: parent.width
        height: parent.height - y
        clip: true
        opacity: dropHost.hovering || dropHost.recognizing ? 1 : 0
        visible: opacity > 0
        onOpacityChanged: if (opacity === 0) dropHost.cardOffset = 8
        Behavior on opacity {
            NumberAnimation { duration: !dropHost.motion ? 0 : dropHost.hovering || dropHost.recognizing ? 160 : 120; easing.type: Easing.OutCubic }
        }
        Rectangle { anchors.fill: parent; color: "#1a778393"; visible: !dropHost.highContrast }
        Image {
            x: dropHost.pointerX - width / 2
            y: dropHost.pointerY - overlay.y - height / 2
            width: 360; height: 280
            source: "qrc:/art/drop-glow.svg"
            visible: dropHost.hovering && dropHost.motion && !dropHost.highContrast
        }
        MouseArea { anchors.fill: parent; onWheel: function (wheel) { wheel.accepted = true; } }
        Rectangle {
            id: card
            width: Math.min(480, parent.width - 48)
            height: 104
            x: (parent.width - width) / 2
            y: parent.height - height - 24 + (overlay.opacity > 0 ? dropHost.cardOffset : 8)
            radius: 12
            color: "#ffffff"
            border.color: "#dce1e7"
            clip: true
            Image {
                x: (dropHost.pointerX / Math.max(1, dropHost.width)) * parent.width - width / 2
                y: 0
                width: 280; height: 150
                source: "qrc:/art/drop-glow.svg"
                visible: dropHost.hovering && dropHost.motion && !dropHost.highContrast
            }
            Rectangle {
                x: 18; y: 30; width: 44; height: 44; radius: 10; color: "#f0f2f5"
                Image {
                    anchors.centerIn: parent
                    width: 23; height: 23
                    source: "qrc:/art/icons/download.svg"
                    visible: !dropHost.recognizing
                }
                Controls.BusyIndicator { anchors.centerIn: parent; width: 28; height: 28; running: dropHost.recognizing; visible: running }
            }
            Column {
                x: 78; y: 19; width: parent.width - x - 18; spacing: 6
                Text {
                    text: dropHost.recognizing ? "正在识别文件…" : "松开以导入"
                    color: "#454d59"; font.pixelSize: 17; font.weight: Font.DemiBold
                }
                Text {
                    width: parent.width
                    text: dropHost.recognizing ? dropHost.recognitionText : "Mod → " + (instanceService.current.name || "请先选择目标实例")
                    color: "#778292"; font.pixelSize: 12; elide: Text.ElideRight
                }
                Text {
                    text: dropHost.recognizing ? "识别后自动导入或加入安装队列" : "整合包 → 创建新实例"
                    color: "#939ba7"; font.pixelSize: 11
                }
            }
        }
        NumberAnimation {
            id: cardEnter
            target: dropHost; property: "cardOffset"; to: 0; duration: dropHost.motion ? 160 : 0; easing.type: Easing.OutCubic
        }
        NumberAnimation {
            id: cardExit
            target: dropHost; property: "cardOffset"; to: 6; duration: dropHost.motion ? 120 : 0; easing.type: Easing.OutCubic
        }
    }
    onHoveringChanged: animateCard()
    onRecognizingChanged: animateCard()
    function animateCard() {
        cardEnter.stop(); cardExit.stop();
        if (hovering || recognizing) cardEnter.restart(); else cardExit.restart();
    }
    LitePopup {
        id: conflict
        property var promptId
        property string message
        property bool answered: false
        width: Math.min(460, parent.width - 48)
        closePolicy: Controls.Popup.CloseOnEscape
        onOpened: cancelRemaining.forceActiveFocus()
        onAboutToHide: dropHost.answer("cancel")
        onClosed: Qt.callLater(dropHost.syncQuestion)
        contentItem: Column {
            spacing: 16
            Text { text: "存在同名 Mod"; color: "#454d59"; font.pixelSize: 16; font.weight: Font.DemiBold }
            Text { width: parent.width; text: conflict.message; color: "#778292"; font.pixelSize: 12; wrapMode: Text.WrapAnywhere }
            LiteCheckBox { id: applyAll; text: "对本批其他同名文件应用此选择" }
            Row {
                anchors.right: parent.right
                spacing: 8
                LiteButton { id: cancelRemaining; height: 32; text: "取消剩余导入"; onClicked: dropHost.answer("cancel") }
                LiteButton { height: 32; text: "跳过"; onClicked: dropHost.answer("skip") }
                LiteButton { height: 32; text: "替换"; primary: true; onClicked: dropHost.answer("replace") }
            }
        }
    }
    Connections {
        target: dropJobs
        function onChanged() { dropHost.syncQuestion(); }
        function onPartial(action, result, args, path) {
            if (result.phase === "recognized") dropHost.hide();
        }
        function onFailed(action, error, path) {
            dropHost.hide();
            if (dropHost.importEnabled) dropHost.finished("无法导入文件\n" + error, true);
        }
        function onCompleted(action, result, args, path) {
            dropHost.hide();
            if (!dropHost.importEnabled) return;
            const errors = (result.errors || []).slice();
            let packCount = 0;
            for (const pack of result.packs || []) {
                const name = instanceJobs.availableDropName(result.root, pack.name);
                if (!name) {
                    const error = pack.name + "：请先选择游戏文件夹。";
                    errors.push(error); console.warn("拖拽导入：" + error); continue;
                }
                if (instanceJobs.download("downloadLocalPack", { archive: pack.path, name: name, root: result.root, title: "导入整合包 · " + name })) packCount++;
                else {
                    const error = pack.name + "：" + instanceJobs.error;
                    errors.push(error); console.warn("拖拽导入：" + error);
                }
            }
            const detail = [];
            if (result.modCount) detail.push("已将 " + result.modCount + " 个 Mod 添加到「" + result.instanceName + "」" + (result.shared ? "的共享 Mod 目录" : ""));
            if (packCount) detail.push(packCount + " 个整合包已加入安装队列");
            if (result.skipped) detail.push("跳过 " + result.skipped + " 个文件");
            detail.push.apply(detail, errors.slice(0, 3));
            if (errors.length > 3) detail.push("另有 " + (errors.length - 3) + " 个文件未导入，详情见运行日志");
            dropHost.finished((errors.length ? "部分文件未导入" : packCount ? "开始安装整合包" : "Mod 导入完成") + "\n" + detail.join("\n"), errors.length > 0);
        }
    }
}
