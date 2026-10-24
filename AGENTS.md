# 本机构建与交付

- 应用统一使用仓库根目录的 `build.ps1` 发布。需要重启验收时运行 `./build.ps1 -Restart`。
- 交付产物为根目录 `97Launch.exe`，采用自包含、压缩的单文件发布。中间产物保留在 `src/**/bin`、`src/**/obj` 和 `artifacts/portable`。
- 启动验收使用根目录的 EXE；内置游戏文件夹为同级 `.minecraft`。游戏数据路径使用真实 EXE 目录，资源路径使用单文件解压后的资源目录。
- 不将真实部署配置、凭据、运行数据或构建产物提交到 Git；`authorization-server.json` 由本地提供并嵌入发布包。
- 未经用户要求，不添加或运行测试；按请求完成构建和实际应用启动检查。
- 对外发布使用 `./publish-release.ps1 -BuildNumber <递增编号> -Channel preview -Restart`。签名私钥仅保存在用户私有目录，禁止提交或上传到服务器。
