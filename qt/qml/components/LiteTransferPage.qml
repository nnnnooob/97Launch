import QtQuick
import QtQuick.Controls.Basic as Controls

Item {
    id: page
    property var expanded: ({})
    readonly property var tasks: instanceJobs.tasks
    ListModel {
        id: queueModel
    }
    function syncQueue() {
        const tasks = instanceJobs.tasks;
        for (let i = queueModel.count - 1; i >= 0; --i)
            if (!tasks.some(t => t.id === queueModel.get(i).jobId))
                queueModel.remove(i);
        for (let i = 0; i < tasks.length; ++i) {
            if (i >= queueModel.count)
                queueModel.append({
                    jobId: tasks[i].id,
                    payload: JSON.stringify(tasks[i])
                });
            else
                queueModel.setProperty(i, "payload", JSON.stringify(tasks[i]));
        }
    }
    Component.onCompleted: syncQueue()
    Connections {
        target: instanceJobs
        function onChanged() {
            page.syncQueue();
        }
    }
    Column {
        anchors.centerIn: parent
        spacing: 8
        visible: !page.tasks.length
        Text {
            anchors.horizontalCenter: parent.horizontalCenter
            text: "暂无任务"
            color: "#7f8998"
            font.pixelSize: 18
        }
        Text {
            text: "下载游戏、模组或整合包后，可在这里查看进度。"
            color: "#a0a7b2"
            font.pixelSize: 11
        }
    }
    ListView {
        id: list
        anchors.fill: parent
        visible: page.tasks.length > 0
        model: queueModel
        spacing: 10
        clip: true
        boundsBehavior: Flickable.StopAtBounds
        Controls.ScrollBar.vertical: Controls.ScrollBar {
            width: 4
        }
        header: Item {
            width: list.width - 8
            height: 42
            Text {
                anchors.verticalCenter: parent.verticalCenter
                text: "传输队列"
                font.pixelSize: 19
                font.weight: Font.DemiBold
                color: "#454d59"
            }
            LiteButton {
                anchors.right: parent.right
                width: 98
                height: 28
                anchors.verticalCenter: parent.verticalCenter
                text: "清除已结束任务"
                onClicked: instanceJobs.clearFinished()
            }
        }
        delegate: LiteSettingsCard {
            id: card
            required property string payload
            property var modelData: JSON.parse(payload)
            width: list.width - 8
            property bool expanded: page.expanded[modelData.id] !== false && modelData.state !== "等待中"
            Item {
                width: parent.width
                height: 42
                Controls.AbstractButton {
                    anchors.fill: parent
                    anchors.rightMargin: 72
                    hoverEnabled: true
                    background: null
                    contentItem: Column {
                        spacing: 5
                        Text {
                            width: parent.width
                            text: card.modelData.title
                            color: "#454d59"
                            font.pixelSize: 13
                            font.weight: Font.DemiBold
                            elide: Text.ElideRight
                        }
                        Text {
                            width: parent.width
                            text: card.modelData.state + " · " + Math.floor(card.modelData.progress * 100) + " %" + (card.modelData.speed > 0 ? " · " + summary.formatSpeed(card.modelData.speed) : "")
                            color: card.modelData.state === "失败" ? "#b27d53" : "#939ba7"
                            font.pixelSize: 11
                            elide: Text.ElideRight
                        }
                    }
                    onClicked: {
                        const next = Object.assign({}, page.expanded);
                        next[card.modelData.id] = !card.expanded;
                        page.expanded = next;
                    }
                }
                LiteIconButton {
                    anchors.right: remove.left
                    width: 30
                    height: 30
                    iconSize: 17
                    source: "qrc:/art/icons/refresh.svg"
                    visible: card.modelData.state === "失败"
                    text: "重试任务"
                    onClicked: instanceJobs.retry(card.modelData.id)
                }
                LiteIconButton {
                    id: remove
                    anchors.right: parent.right
                    width: 30
                    height: 30
                    iconSize: 17
                    source: card.modelData.state === "进行中" || card.modelData.state === "等待中" ? "qrc:/art/icons/trash.svg" : "qrc:/art/icons/close.svg"
                    text: card.modelData.state === "进行中" || card.modelData.state === "等待中" ? "取消任务" : "移除记录"
                    onClicked: instanceJobs.taskAction(card.modelData.id)
                }
            }
            Rectangle {
                width: parent.width
                height: 3
                radius: 1.5
                color: "#edf0f4"
                Rectangle {
                    height: parent.height
                    width: parent.width * card.modelData.progress
                    radius: 1.5
                    color: card.modelData.state === "失败" ? "#b89a7f" : "#788393"
                    Behavior on width {
                        NumberAnimation {
                            duration: 120
                            easing.type: Easing.OutCubic
                        }
                    }
                }
            }
            Text {
                width: parent.width
                visible: card.expanded
                text: card.modelData.detail || ""
                color: card.modelData.state === "失败" ? "#b27d53" : "#939ba7"
                font.pixelSize: 11
                wrapMode: Text.Wrap
            }
            Column {
                width: parent.width
                visible: card.expanded
                spacing: 6
                Repeater {
                    model: card.modelData.steps || []
                    Item {
                        required property var modelData
                        width: parent.width
                        height: 20
                        Text {
                            width: 18
                            text: card.modelData.state === "已取消" && modelData.state !== "已完成" ? "×" : modelData.state === "已完成" ? "✓" : modelData.state === "进行中" ? "↓" : "○"
                            font.pixelSize: 12
                            color: "#939ba7"
                        }
                        Text {
                            x: 24
                            width: parent.width - 66
                            text: modelData.name
                            font.pixelSize: 11
                            color: modelData.state === "进行中" ? "#454d59" : "#939ba7"
                            elide: Text.ElideRight
                        }
                        Text {
                            anchors.right: parent.right
                            text: modelData.state === "已完成" ? "完成" : Math.floor(modelData.progress * 100) + "%"
                            font.pixelSize: 10
                            color: "#939ba7"
                        }
                    }
                }
            }
            LiteButton {
                visible: !!card.modelData.applicationUpdate && card.modelData.state === "已完成" && !!globalSettings.updates.ready
                height: 28
                text: "重启并更新"
                enabled: !globalSettings.updates.busy
                onClicked: globalSettings.updateAction("apply")
            }
            LiteButton {
                visible: !!card.modelData.output
                width: 108
                height: 28
                text: "查看导出文件"
                onClicked: instanceJobs.revealOutput(card.modelData.output)
            }
        }
        footer: Item {
            width: 1
            height: 14
        }
    }
    LiteTransferSummary {
        id: summary
        visible: false
    }
    LiteBackToTop {
        anchors.right: parent.right
        anchors.rightMargin: 20
        anchors.bottom: parent.bottom
        anchors.bottomMargin: 14
        flickable: list
    }
}
