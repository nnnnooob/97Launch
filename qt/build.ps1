param(
    [string]$QtSdk = '',
    [int]$BuildNumber = 0,
    [string]$VersionSuffix = 'alpha.4',
    [string]$AuthorizationConfigPath = '',
    [switch]$Run
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
if (!$QtSdk) { $QtSdk = Join-Path (Split-Path $PSScriptRoot -Parent) '_toolchains\Qt\6.6.3\msvc2019_64' }
if (!(Test-Path -LiteralPath (Join-Path $QtSdk 'lib\cmake\Qt6Quick'))) {
    throw 'Qt 6.6.3 MSVC x64 SDK with Qt Quick is required. Pass -QtSdk with its directory.'
}
$webpPlugin = Join-Path $QtSdk 'plugins\imageformats\qwebp.dll'
if (!(Test-Path -LiteralPath $webpPlugin)) {
    throw 'Qt Image Formats for Qt 6.6.3 is required to display community resource icons.'
}
$vsWhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vsPath = & $vsWhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$cmake = Join-Path $vsPath 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
if (!(Test-Path -LiteralPath $cmake)) { throw 'Visual Studio C++ tools and CMake are required.' }
$workerPath = Join-Path $PSScriptRoot 'worker\97Launch.InstanceWorker.exe'
if (Get-Process -Name 97Launch.InstanceWorker -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $workerPath }) {
    throw 'An instance task is running. Let it finish or cancel it in the app before rebuilding.'
}
if (!$AuthorizationConfigPath) {
    $AuthorizationConfigPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'authorization-server.json'
    if (!(Test-Path -LiteralPath $AuthorizationConfigPath)) { $AuthorizationConfigPath = Join-Path (Split-Path $PSScriptRoot -Parent) '97Launch/authorization-server.json' }
}
if (!(Test-Path -LiteralPath $AuthorizationConfigPath)) { throw 'Provide -AuthorizationConfigPath for this deployment.' }
& dotnet publish (Join-Path $PSScriptRoot 'backend\InstanceWorker.csproj') -c Release -o (Join-Path $PSScriptRoot 'worker') "-p:AuthorizationConfigPath=$AuthorizationConfigPath" --nologo
if ($LASTEXITCODE) { throw 'Instance worker build failed.' }
if (!(Test-Path -LiteralPath (Join-Path $PSScriptRoot 'worker\authorization-server.json') -PathType Leaf)) {
    throw 'Authorization server configuration was not deployed. Provide ../97Launch/authorization-server.json before building.'
}
Get-Process -Name 97LaunchQt -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $PSScriptRoot '97LaunchQt.exe') } | Stop-Process
& $cmake -S . -B build-basic -G 'Visual Studio 17 2022' -A x64 "-DCMAKE_PREFIX_PATH=$QtSdk" "-DLAUNCH_BUILD_NUMBER=$BuildNumber" "-DLAUNCH_VERSION_SUFFIX=$VersionSuffix"
if ($LASTEXITCODE) { throw 'CMake configuration failed.' }
& $cmake --build build-basic --config Release --target 97LaunchQt --parallel 6
if ($LASTEXITCODE) { throw 'C++ build failed.' }
Copy-Item -LiteralPath build-basic\bin\97LaunchQt.exe -Destination . -Force
$deployQml = Join-Path $PSScriptRoot 'build-basic\lite-qml'
New-Item -ItemType Directory -Force -Path (Join-Path $deployQml 'components') | Out-Null
Copy-Item -LiteralPath qml\Main.qml -Destination $deployQml -Force
Remove-Item -LiteralPath (Join-Path $deployQml 'components\LiteNavigation.qml') -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $deployQml 'components\LiteSidebarMenu.qml') -ErrorAction SilentlyContinue
Copy-Item -LiteralPath qml\components\LiteWelcomeFlow.qml,qml\components\LiteRetainedLoader.qml,qml\components\LiteFilterChips.qml,qml\components\LiteSlider.qml,qml\components\LiteRadioButton.qml,qml\components\LiteSpinBox.qml,qml\components\LiteSettingsScroll.qml,qml\components\LiteSettingRow.qml,qml\components\LiteSettingsField.qml,qml\components\LiteSettingsNote.qml,qml\components\LiteGlobalLaunch.qml,qml\components\LiteGlobalManage.qml,qml\components\LiteGlobalJava.qml,qml\components\LiteGlobalDefaults.qml,qml\components\LiteGlobalAbout.qml,qml\components\LiteGlobalUpdate.qml,qml\components\LiteGlobalSettingsPage.qml,qml\components\LiteGlobalSettingsSidebar.qml,qml\components\LiteListGroupHeader.qml,qml\components\LiteListGroupRow.qml,qml\components\LiteEditableComboBox.qml,qml\components\LiteDownloadSidebar.qml,qml\components\LiteDownloadPage.qml,qml\components\LiteDownloadDetail.qml,qml\components\LiteTransferSummary.qml,qml\components\LiteBackToTop.qml,qml\components\LiteComboBox.qml,qml\components\LiteCheckBox.qml,qml\components\LiteSettingsCard.qml,qml\components\LitePopupTransition.qml,qml\components\LitePopup.qml,qml\components\LiteToast.qml,qml\components\LiteDropImport.qml,qml\components\LiteFlyout.qml,qml\components\LiteMenu.qml,qml\components\LiteToolTip.qml,qml\components\LiteConfirmDialog.qml,qml\components\LiteInstanceSettingsSidebar.qml,qml\components\LiteInstanceSettingsPage.qml,qml\components\LiteInstanceModify.qml,qml\components\LiteInstanceExport.qml,qml\components\LiteInstanceContent.qml,qml\components\LiteResourceBrowser.qml,qml\components\LiteTransferPage.qml,qml\components\WindowButton.qml,qml\components\LiteButton.qml,qml\components\LiteIconButton.qml,qml\components\LiteMotion.js,qml\components\LiteInstanceRules.js,qml\components\LiteTextField.qml,qml\components\LiteHome.qml,qml\components\LiteInstancesPage.qml,qml\components\LiteInstanceSidebar.qml,qml\components\LiteMenuItem.qml,qml\components\LiteTopNavigation.qml,qml\components\LiteServers.qml,qml\components\LiteAccountPane.qml,qml\components\LitePageHost.qml,qml\components\AccountDialogs.qml,qml\components\AccountHead.qml -Destination (Join-Path $deployQml 'components') -Force
& (Join-Path $QtSdk 'bin\windeployqt.exe') --release --qmldir $deployQml --no-translations --no-opengl-sw --no-compiler-runtime --dir $PSScriptRoot (Join-Path $PSScriptRoot '97LaunchQt.exe')
if ($LASTEXITCODE) { throw 'Qt runtime deployment failed.' }
Copy-Item -LiteralPath $webpPlugin -Destination (Join-Path $PSScriptRoot 'imageformats\qwebp.dll') -Force
Write-Output ('Basic preview: ' + (Join-Path $PSScriptRoot '97LaunchQt.exe'))
if ($Run) { Start-Process -FilePath (Join-Path $PSScriptRoot '97LaunchQt.exe') -WorkingDirectory $PSScriptRoot }

