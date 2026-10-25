# Qt 应用

- 开发构建：`./build.ps1 -Run`；需要时显式指定 `-QtSdk` 和 `-AuthorizationConfigPath`。
- 对外发布：`./publish-release.ps1 -BuildNumber <递增编号> -Channel preview`。
- 发布产物为 `artifacts/releases/<版本>-build.<编号>/97Launch.exe`，单个 EXE 包含 Qt、MSVC 运行库及自包含 .NET worker。
- 发布签名私钥只在用户私有目录使用，不提交或上传。真实授权配置仅进入发布包，不进入仓库。
- 原 WinUI 源码位于仓库 `src/`，当前 Qt 源码位于 `qt/`；不要混用两者构建入口。
- 未经用户要求，不添加或运行测试。
