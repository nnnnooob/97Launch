import QtQuick
import QtQuick.Controls.Basic as Controls

Item {
    id: summary
    readonly property var tasks: instanceJobs.tasks
    readonly property var active: tasks.find(t => t.state === "进行中") || ({})
    readonly property int waiting: tasks.filter(t => t.state === "等待中").length
    readonly property int failed: tasks.filter(t => t.state === "失败").length
    readonly property int completed: tasks.filter(t => t.state === "已完成").length
    function formatSpeed(value) {
        let n = value, unit = 0;
        const units = ["B/s", "KB/s", "MB/s", "GB/s"];
        while (n >= 1024 && unit < 3) {
            n /= 1024;
            unit++;
        }
        return n.toFixed(unit ? 1 : 0) + " " + units[unit];
    }
    Flickable {
        anchors.fill: parent
        contentHeight: column.implicitHeight + 36
        clip: true
        boundsBehavior: Flickable.StopAtBounds
        Controls.ScrollBar.vertical: Controls.ScrollBar {
            width: 3
        }
        Column {
            id: column
            x: 22
            y: 26
            width: Math.max(0, parent.width - 44)
            spacing: 22
            Column {
                width: parent.width
                spacing: 7
                Text {
                    width: parent.width
                    text: summary.active.id ? "正在传输" : summary.failed ? "部分任务未完成" : summary.waiting ? "等待开始" : summary.completed ? "传输已完成" : "传输已结束"
                    color: "#454d59"
                    font.pixelSize: 17
                    font.weight: Font.DemiBold
                    wrapMode: Text.Wrap
                }
                Text {
                    width: parent.width
                    text: summary.active.title || (summary.failed ? "可在右侧查看错误并重试。" : "可以返回实例页面继续游戏。")
                    color: "#939ba7"
                    font.pixelSize: 11
                    wrapMode: Text.Wrap
                }
            }
            Column {
                width: parent.width
                visible: !!summary.active.id
                spacing: 10
                Text {
                    text: ((summary.active.progress || 0) * 100).toFixed(2) + " %"
                    color: "#454d59"
                    font.pixelSize: 28
                    font.weight: Font.Light
                }
                Rectangle {
                    width: parent.width
                    height: 4
                    radius: 2
                    color: "#edf0f4"
                    Rectangle {
                        width: parent.width * (summary.active.progress || 0)
                        height: 4
                        radius: 2
                        color: "#788393"
                        Behavior on width {
                            NumberAnimation {
                                duration: 120
                                easing.type: Easing.OutCubic
                            }
                        }
                    }
                }
                Text {
                    text: summary.formatSpeed(summary.active.speed || 0)
                    font.pixelSize: 12
                    color: "#7f8998"
                }
            }
            Grid {
                width: parent.width
                columns: 2
                columnSpacing: 22
                rowSpacing: 10
                Text {
                    text: "进行中"
                    font.pixelSize: 11
                    color: "#939ba7"
                }
                Text {
                    text: summary.active.id ? "1" : "0"
                    font.pixelSize: 12
                    color: "#454d59"
                }
                Text {
                    text: "等待中"
                    font.pixelSize: 11
                    color: "#939ba7"
                }
                Text {
                    text: summary.waiting
                    font.pixelSize: 12
                    color: "#454d59"
                }
                Text {
                    text: "已完成"
                    font.pixelSize: 11
                    color: "#939ba7"
                }
                Text {
                    text: summary.completed
                    font.pixelSize: 12
                    color: "#454d59"
                }
                Text {
                    text: "失败"
                    font.pixelSize: 11
                    color: "#939ba7"
                }
                Text {
                    text: summary.failed
                    font.pixelSize: 12
                    color: "#454d59"
                }
            }
        }
    }
}
