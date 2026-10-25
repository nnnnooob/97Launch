import QtQuick

LiteInstanceSettingsSidebar {
    section: "launch"
    showOpenDirectory: false
    sections: [
        {
            title: "游戏"
        },
        {
            key: "launch",
            title: "启动",
            icon: "rocket"
        },
        {
            key: "java",
            title: "Java",
            icon: "chip"
        },
        {
            key: "manage",
            title: "管理",
            icon: "sliders"
        },
        {
            key: "gameDefaults",
            title: "默认游戏设置",
            icon: "game"
        },
        {
            title: "关于"
        },
        {
            key: "about",
            title: "软件信息",
            icon: "info"
        },
        {
            key: "update",
            title: "软件更新",
            icon: "update"
        }
    ]
}
