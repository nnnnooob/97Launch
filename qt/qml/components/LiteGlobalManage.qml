import QtQuick

LiteSettingsScroll {
    id: page
    readonly property var options: globalSettings.values
    LiteSettingsCard {
        title: "游戏资源获取偏好"
        LiteSettingsField {
            label: "文件下载源"
            option: "FileDownloadSource"
            choices: ["自动选择", "官方源", "BMCLAPI", "Mojang 官方 + BMCLAPI 回退"]
        }
        LiteSettingsField {
            label: "版本下载源"
            option: "VersionDownloadSource"
            choices: ["自动选择", "官方源", "BMCLAPI", "Minecraft Wiki"]
        }
        LiteSettingRow {
            label: "最大线程数"
            LiteSlider {
                width: parent.width - 66
                from: 1
                to: 256
                value: page.options.DownloadThreads
                onMoved: globalSettings.setOption("DownloadThreads", Math.round(value))
            }
            Text {
                anchors.right: parent.right
                height: 32
                verticalAlignment: Text.AlignVCenter
                text: page.options.DownloadThreads
                font.pixelSize: 12
                color: "#687384"
            }
        }
        LiteSettingRow {
            label: "速度限制"
            LiteSlider {
                width: parent.width - 96
                from: 0
                to: 20480
                stepSize: 128
                value: page.options.DownloadSpeedLimitKb
                onMoved: globalSettings.setOption("DownloadSpeedLimitKb", Math.round(value / 128) * 128)
            }
            Text {
                anchors.right: parent.right
                height: 32
                verticalAlignment: Text.AlignVCenter
                text: page.options.DownloadSpeedLimitKb > 0 ? page.options.DownloadSpeedLimitKb + " KB/s" : "不限速"
                font.pixelSize: 12
                color: "#687384"
            }
        }
        LiteCheckBox {
            text: "安装新实例后自动选定该实例"
            checked: page.options.SelectNewInstance
            onClicked: globalSettings.setOption("SelectNewInstance", checked)
        }
        LiteCheckBox {
            text: "升级部分版本的 Authlib"
            checked: page.options.UpgradeAuthlib
            onClicked: globalSettings.setOption("UpgradeAuthlib", checked)
        }
    }
    LiteSettingsCard {
        title: "社区资源获取偏好"
        LiteSettingsField {
            label: "下载源"
            option: "CommunitySource"
            choices: ["自动选择", "Modrinth", "CurseForge"]
        }
        LiteSettingsField {
            label: "文件名格式"
            option: "ResourceFileNameFormat"
            choices: ["【机械动力】create-1.21.1-6.0.4.jar", "[机械动力] create-1.21.1-6.0.4.jar", "机械动力-create-1.21.1-6.0.4.jar", "create-1.21.1-6.0.4-机械动力.jar", "create-1.21.1-6.0.4.jar"]
        }
    }
    LiteSettingsCard {
        title: "辅助功能"
        LiteCheckBox {
            text: "自动设置为中文"
            checked: page.options.AutoSetChinese
            onClicked: globalSettings.setOption("AutoSetChinese", checked)
        }
    }
}
