import QtQuick
import QtQuick.Controls.Basic as Controls
import QtQuick.Dialogs

Item {
    id: page
    property var options: ({
            name: instanceSettings.instance.name || "",
            version: "1.0.0",
            summary: "",
            mods: true,
            disabledMods: false,
            options: true,
            configs: true,
            scripts: true,
            resourcePacks: true,
            shaderPacks: true,
            saves: false,
            screenshots: false,
            servers: false,
            personal: false,
            directFiles: false,
            onlyModrinth: false,
            includeLauncher: false,
            javaDirectory: null,
            rules: ""
        })
    property bool advanced: false
    property string status
    property string destination
    function set(key, value) {
        const next = Object.assign({}, options);
        next[key] = value;
        options = next;
    }
    component ExportField: Column {
        id: field
        property string label
        property string option
        property string hint
        width: parent.width
        spacing: 6
        Text {
            text: field.label
            font.pixelSize: 12
            color: "#687384"
        }
        LiteTextField {
            width: parent.width
            height: 32
            placeholderText: field.hint
            text: page.options[field.option] || ""
            onTextEdited: page.set(field.option, text)
        }
    }
    component ExportCheck: LiteCheckBox {
        property string option
        width: parent.width
        checked: !!page.options[option]
        onClicked: page.set(option, checked)
    }
    Flickable {
        id: scroll
        anchors.fill: parent
        anchors.bottomMargin: 52
        contentHeight: column.implicitHeight + 4
        clip: true
        boundsBehavior: Flickable.StopAtBounds
        Controls.ScrollBar.vertical: Controls.ScrollBar {
            width: 4
        }
        Column {
            id: column
            width: scroll.width - 8
            spacing: 12
            LiteSettingsCard {
                title: "导出整合包"
                ExportField {
                    label: "整合包名称"
                    option: "name"
                    hint: "整合包名称"
                }
                ExportField {
                    label: "整合包版本"
                    option: "version"
                    hint: "例如 1.0.0"
                }
                Text {
                    text: "说明"
                    font.pixelSize: 12
                    color: "#687384"
                }
                Controls.TextArea {
                    width: parent.width
                    height: 64
                    padding: 10
                    font.pixelSize: 12
                    color: "#454d59"
                    placeholderText: "整合包说明"
                    text: page.options.summary
                    wrapMode: TextEdit.Wrap
                    onTextChanged: {
                        if (activeFocus)
                            page.set("summary", text);
                    }
                    background: Rectangle {
                        radius: 7
                        color: "#f7f8fa"
                        border.color: parent.activeFocus ? "#929aa6" : "#e2e5ea"
                    }
                }
            }
            LiteSettingsCard {
                title: "导出内容"
                Row {
                    width: parent.width
                    spacing: 16
                    Column {
                        width: (parent.width - 16) / 2
                        spacing: 2
                        ExportCheck {
                            text: "Mod"
                            option: "mods"
                        }
                        ExportCheck {
                            text: "已禁用的 Mod"
                            option: "disabledMods"
                        }
                        ExportCheck {
                            text: "游戏设置"
                            option: "options"
                        }
                        ExportCheck {
                            text: "Mod 配置"
                            option: "configs"
                        }
                        ExportCheck {
                            text: "脚本与整合包数据"
                            option: "scripts"
                        }
                        ExportCheck {
                            text: "命令历史与快捷栏"
                            option: "personal"
                        }
                    }
                    Column {
                        width: (parent.width - 16) / 2
                        spacing: 2
                        ExportCheck {
                            text: "资源包"
                            option: "resourcePacks"
                        }
                        ExportCheck {
                            text: "光影包"
                            option: "shaderPacks"
                        }
                        ExportCheck {
                            text: "单人存档"
                            option: "saves"
                        }
                        ExportCheck {
                            text: "截图"
                            option: "screenshots"
                        }
                        ExportCheck {
                            text: "服务器列表"
                            option: "servers"
                        }
                    }
                }
            }
            LiteSettingsCard {
                title: "高级选项"
                LiteButton {
                    width: parent.width
                    height: 28
                    text: page.advanced ? "收起高级选项" : "展开高级选项"
                    onClicked: page.advanced = !page.advanced
                }
                Column {
                    visible: page.advanced
                    width: parent.width
                    spacing: 8
                    ExportCheck {
                        text: "直接打包资源文件，导入时无需重新下载"
                        option: "directFiles"
                    }
                    ExportCheck {
                        text: "仅使用 Modrinth 托管的资源"
                        option: "onlyModrinth"
                        enabled: !page.options.directFiles
                    }
                    ExportCheck {
                        text: "携带当前 97Launch 程序（导出为 ZIP）"
                        option: "includeLauncher"
                    }
                    LiteCheckBox {
                        width: parent.width
                        text: "携带 Java"
                        checked: !!page.options.javaDirectory
                        onClicked: {
                            if (checked)
                                javaPicker.open();
                            else
                                page.set("javaDirectory", null);
                        }
                    }
                    LiteButton {
                        width: 144
                        height: 28
                        visible: !!page.options.javaDirectory
                        text: "选择 Java 文件夹"
                        onClicked: javaPicker.open()
                    }
                    Text {
                        width: parent.width
                        visible: !!page.options.javaDirectory
                        text: page.options.javaDirectory || ""
                        font.pixelSize: 10
                        color: "#939ba7"
                        wrapMode: Text.Wrap
                    }
                    Text {
                        text: "追加导出规则"
                        font.pixelSize: 12
                        color: "#687384"
                    }
                    Controls.TextArea {
                        width: parent.width
                        height: 90
                        padding: 10
                        font.pixelSize: 12
                        color: "#454d59"
                        placeholderText: "每行一条，例如 config/*；排除项以 ! 开头"
                        text: page.options.rules
                        wrapMode: TextEdit.Wrap
                        onTextChanged: {
                            if (activeFocus)
                                page.set("rules", text);
                        }
                        background: Rectangle {
                            radius: 7
                            color: "#f7f8fa"
                            border.color: parent.activeFocus ? "#929aa6" : "#e2e5ea"
                        }
                    }
                    Row {
                        spacing: 8
                        LiteButton {
                            width: 90
                            height: 28
                            text: "读取配置"
                            onClicked: readPicker.open()
                        }
                        LiteButton {
                            width: 90
                            height: 28
                            text: "保存配置"
                            onClicked: savePicker.open()
                        }
                    }
                }
            }
            Text {
                width: parent.width
                text: "账号凭据、日志、缓存和崩溃报告不会导出。公开分享前，请检查所选内容和资源的分发许可。"
                font.pixelSize: 11
                color: "#939ba7"
                wrapMode: Text.Wrap
            }
        }
    }
    Row {
        anchors.bottom: parent.bottom
        width: parent.width - 8
        spacing: 14
        LiteButton {
            width: 130
            height: 36
            primary: true
            text: "开始导出"
            enabled: !instanceJobs.busy && page.options.name.trim() && page.options.version.trim()
            onClicked: destinationPicker.open()
        }
        Text {
            width: parent.width - 144
            anchors.verticalCenter: parent.verticalCenter
            text: instanceJobs.busy ? instanceJobs.detail : page.status || "任务进度可在传输页面查看"
            font.pixelSize: 11
            color: "#939ba7"
            elide: Text.ElideRight
        }
    }
    FileDialog {
        id: destinationPicker
        title: "导出整合包"
        fileMode: FileDialog.SaveFile
        defaultSuffix: page.options.includeLauncher ? "zip" : "mrpack"
        nameFilters: page.options.includeLauncher ? ["整合包 ZIP (*.zip)"] : ["Modrinth 整合包 (*.mrpack)", "整合包 ZIP (*.zip)"]
        onAccepted: {
            page.destination = instanceJobs.localPath(selectedFile);
            confirmation.open();
        }
    }
    FolderDialog {
        id: javaPicker
        title: "选择 Java 文件夹"
        onAccepted: page.set("javaDirectory", instanceJobs.localPath(selectedFolder))
    }
    FileDialog {
        id: readPicker
        title: "读取导出配置"
        nameFilters: ["JSON 配置 (*.json)"]
        onAccepted: {
            const values = instanceJobs.readConfig(selectedFile);
            const next = Object.assign({}, page.options);
            for (const key in values)
                if (key in next)
                    next[key] = values[key];
            page.options = next;
        }
    }
    FileDialog {
        id: savePicker
        title: "保存导出配置"
        fileMode: FileDialog.SaveFile
        defaultSuffix: "json"
        nameFilters: ["JSON 配置 (*.json)"]
        onAccepted: {
            if (instanceJobs.writeConfig(selectedFile, page.options))
                page.status = "配置已保存";
        }
    }
    LiteConfirmDialog {
        id: confirmation
        heading: "确认导出整合包"
        confirmText: "开始导出"
        message: "导出到：" + page.destination + (!page.options.directFiles && !page.options.onlyModrinth ? "\n\n无法在 Modrinth 识别的资源将直接打包。联网识别失败时也按此方式继续。" : "")
        onConfirmed: instanceJobs.submit("exportPack", {
            options: page.options,
            destination: page.destination,
            allowUnrecognized: true
        })
    }
    Connections {
        target: instanceJobs
        function onCompleted(action, result, args, path) {
            if (action === "exportPack" && path === instanceSettings.instance.path)
                page.status = "已导出：" + result.destination;
        }
    }
}
