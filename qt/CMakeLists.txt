cmake_minimum_required(VERSION 3.24)
project(97LaunchQt VERSION 0.1.0 LANGUAGES CXX)
set(CMAKE_CXX_STANDARD 17)
set(LAUNCH_BUILD_NUMBER 0 CACHE STRING "Signed release build number")
set(LAUNCH_VERSION_SUFFIX "alpha.4" CACHE STRING "Release suffix")
set(LAUNCH_RELEASE_VERSION "${PROJECT_VERSION}-${LAUNCH_VERSION_SUFFIX}")
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_AUTOMOC ON)
set(CMAKE_AUTORCC ON)
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY_RELEASE "${CMAKE_BINARY_DIR}/bin")
add_compile_options("$<$<CXX_COMPILER_ID:MSVC>:/utf-8>")
find_package(Qt6 6.6.3 EXACT REQUIRED COMPONENTS Quick Network Concurrent)
qt_add_executable(97LaunchQt WIN32 src/gamelaunch.cpp src/gamelaunch.h src/servercatalog.cpp src/servercatalog.h src/launcherflow.cpp src/launcherflow.h src/main.cpp src/accounts.cpp src/accounts.h src/authbrowser.cpp src/authbrowser.h src/instances.cpp src/instances.h src/instancesettings.cpp src/instancesettings.h src/instancejobs.cpp src/instancejobs.h src/globalsettings.cpp src/globalsettings.h src/launchstatus.cpp src/launchstatus.h)
target_sources(97LaunchQt PRIVATE src/webviewruntime.cpp src/webviewruntime.h)
qt_add_resources(97LaunchQt "basic_ui" PREFIX "/" FILES
    qml/components/LiteWelcomeFlow.qml
    qml/components/LiteRetainedLoader.qml
    qml/components/LiteFilterChips.qml
    qml/components/LiteSlider.qml
    qml/components/LiteRadioButton.qml
    qml/components/LiteSpinBox.qml
    qml/components/LiteSettingsScroll.qml
    qml/components/LiteSettingRow.qml
    qml/components/LiteSettingsField.qml
    qml/components/LiteSettingsNote.qml
    qml/components/LiteGlobalLaunch.qml
    qml/components/LiteGlobalManage.qml
    qml/components/LiteGlobalJava.qml
    qml/components/LiteGlobalDefaults.qml
    qml/components/LiteGlobalAbout.qml
    qml/components/LiteGlobalUpdate.qml
    qml/components/LiteGlobalSettingsPage.qml
    qml/components/LiteGlobalSettingsSidebar.qml

    qml/components/LiteListGroupHeader.qml
    qml/components/LiteListGroupRow.qml
    qml/components/LiteDownloadSidebar.qml
    qml/components/LiteEditableComboBox.qml
    qml/components/LiteDownloadPage.qml
    qml/components/LiteDownloadDetail.qml
    qml/components/LiteTransferSummary.qml
    qml/components/LiteComboBox.qml
    qml/components/LiteBackToTop.qml
    qml/components/LiteCheckBox.qml
    qml/components/LiteSettingsCard.qml
    qml/components/LitePopupTransition.qml
    qml/components/LitePopup.qml
    qml/components/LiteToast.qml
    qml/components/LiteDropImport.qml
    qml/components/LiteFlyout.qml
    qml/components/LiteMenu.qml
    qml/components/LiteToolTip.qml
    qml/components/LiteConfirmDialog.qml
    qml/components/LiteInstanceSettingsSidebar.qml
    qml/components/LiteInstanceSettingsPage.qml
    qml/components/LiteInstanceModify.qml
    qml/components/LiteInstanceExport.qml
    qml/components/LiteInstanceContent.qml
    qml/components/LiteResourceBrowser.qml
    qml/components/LiteTransferPage.qml
    qml/Main.qml
    qml/components/WindowButton.qml
    qml/components/LiteButton.qml
    qml/components/LiteIconButton.qml
    qml/components/LiteMotion.js
    qml/components/LiteInstanceRules.js
    qml/components/LiteTextField.qml
    qml/components/LiteHome.qml
    qml/components/LiteInstancesPage.qml
    qml/components/LiteInstanceSidebar.qml
    qml/components/LiteMenuItem.qml
    qml/components/LiteTopNavigation.qml
    qml/components/LiteServers.qml
    qml/components/LiteAccountPane.qml
    qml/components/LitePageHost.qml
    qml/components/AccountDialogs.qml
    qml/components/AccountHead.qml
)
qt_add_resources(97LaunchQt "lite_art" PREFIX "/art" BASE "assets" FILES
    assets/welcome-valley.png
    assets/drop-glow.svg
    assets/icons/coffee.svg
    assets/icons/rocket.svg
    assets/icons/chip.svg
    assets/icons/sliders.svg
    assets/icons/game.svg
    assets/icons/info.svg

    assets/icons/download-minecraft.svg
    assets/icons/download-mod.svg
    assets/icons/download-package.svg
    assets/icons/download-shader.svg
    assets/icons/download-resourcepack.svg
    assets/icons/download-datapack.svg
    assets/icons/copy.svg
    assets/icons/link.svg
    assets/icons/image.svg
    assets/icons/pack-export.svg
    assets/icons/puzzle.svg
    assets/icons/check-dark.svg
    assets/icons/sun.svg
    assets/icons/update.svg
    assets/icons/check.svg
    assets/icons/back.svg
    assets/icons/undo.svg
    assets/icons/world.svg
    assets/app-icon.ico
    assets/app-icon.png
    assets/icons/back-top.svg
    assets/icons/play.svg
    assets/icons/stop.svg
    assets/icons/launch-lightning.svg
    assets/icons/play-white.svg
    assets/icons/instances.svg
    assets/icons/download.svg
    assets/icons/transfer.svg
    assets/icons/settings.svg
    assets/icons/refresh.svg
    assets/icons/avatar-shadow.svg
    assets/steve.png
    assets/alex.png
    assets/steve_head.png
    assets/alex_head.png
    assets/icons/account-add.svg
    assets/icons/user.svg
    assets/icons/switch-account.svg
    assets/icons/trash.svg
    assets/icons/close.svg
    assets/icons/folder.svg
    assets/icons/folder-add.svg
    assets/icons/pack-import.svg
    assets/icons/search.svg
    assets/icons/more.svg
    assets/icons/chevron-down.svg
    assets/icons/rename.svg
    assets/blocks/vanilla.png
    assets/blocks/fabric.png
    assets/blocks/forge.png
    assets/blocks/neoforge.png
    assets/blocks/grasspath.png
    assets/blocks/egg.png
    assets/blocks/cobblestone.png
    assets/blocks/fools.png
    assets/blocks/snapshot.png
    assets/blocks/redstone.png
)
if(WIN32)
    configure_file(src/app.rc.in "${CMAKE_CURRENT_BINARY_DIR}/app.rc" @ONLY)
    target_sources(97LaunchQt PRIVATE "${CMAKE_CURRENT_BINARY_DIR}/app.rc")
    set(WEBVIEW2_SDK "${CMAKE_CURRENT_SOURCE_DIR}/third_party/WebView2-1.0.3719.77")
    target_include_directories(97LaunchQt PRIVATE "${WEBVIEW2_SDK}/include")
    target_compile_definitions(97LaunchQt PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
    target_link_libraries(97LaunchQt PRIVATE dwmapi crypt32 ole32 oleaut32 shlwapi version
        "${WEBVIEW2_SDK}/lib/x64/WebView2LoaderStatic.lib")
endif()
target_link_libraries(97LaunchQt PRIVATE Qt6::Quick Qt6::Network Qt6::Concurrent Qt6::CorePrivate)
target_compile_definitions(97LaunchQt PRIVATE LAUNCH_PREVIEW_VERSION="${LAUNCH_RELEASE_VERSION}")
if(WIN32 AND LAUNCH_PAYLOAD_CAB)
    configure_file(src/bootstrap.rc.in "${CMAKE_CURRENT_BINARY_DIR}/bootstrap.rc" @ONLY)
    add_executable(97Launch WIN32 src/bootstrap.cpp "${CMAKE_CURRENT_BINARY_DIR}/bootstrap.rc")
    set_property(TARGET 97Launch PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
    target_compile_definitions(97Launch PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN UNICODE _UNICODE)
    target_link_libraries(97Launch PRIVATE cabinet bcrypt shell32 ole32)
endif()

