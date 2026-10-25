import QtQuick

LiteSettingsScroll {
    LiteSettingsCard {
        title: "97Launch"
        Repeater {
            model: [
                {
                    label: "版本",
                    value: globalSettings.version
                },
                {
                    label: "数据目录",
                    value: globalSettings.dataDirectory
                },
                {
                    label: "日志目录",
                    value: globalSettings.logDirectory
                },
                {
                    label: "运行环境",
                    value: "Qt 6.6.3 · Qt Quick · .NET 8 任务进程"
                }
            ]
            LiteSettingRow {
                required property var modelData
                label: modelData.label
                height: Math.max(34, valueText.implicitHeight + 10)
                Text {
                    id: valueText
                    width: parent.width
                    anchors.verticalCenter: parent.verticalCenter
                    text: modelData.value
                    font.pixelSize: 12
                    color: "#687384"
                    wrapMode: Text.WrapAnywhere
                }
            }
        }
        Row {
            spacing: 10
            LiteButton {
                height: 32
                text: "打开数据目录"
                onClicked: globalSettings.openDirectory("data")
            }
            LiteButton {
                height: 32
                text: "打开日志目录"
                onClicked: globalSettings.openDirectory("logs")
            }
        }
    }
}
