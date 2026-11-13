using System.Text.Json;
using System.Text.Json.Nodes;
using Launcher.App;
using Launcher.App.Services;
using Launcher.App.ViewModels;

internal static class SettingsActions {
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private static string Text(JsonNode? node,string key,string fallback="")=>node?[key]?.GetValue<string>()??fallback;
    private sealed class ProgressSink:IProgress<InstallProgress>{public void Report(InstallProgress p)=>Program.Send(new{type="progress",detail=p.Detail,stage=p.Stage,fraction=p.Progress,preserve=p.PreserveOtherStages,speed=p.BytesPerSecond});}
    public static async Task<object> RunAsync(JsonNode request,CancellationToken token){
        var action=Text(request,"action");var args=request["args"]??new JsonObject();
        if(action=="settingsInstallWebView")return await WebViewRuntimeInstaller.EnsureAsync(token);
        var store=new SettingsStore(Path.Combine(AppInfo.DataDirectory,"settings.json"));
        var settings=DownloadActions.EffectiveSettings(request,store);
        if(action=="settingsLaunchStatus")return await LaunchStatusAction.ReadAsync(request,settings,store,token);
        if(action=="settingsAcknowledge"){
            UpdateBootstrap.HandleArguments(["--update-ticket",Text(args,"ticket")]);UpdateBootstrap.AcknowledgeStartup();return new{ok=true};
        }
        if(action=="settingsJava"){
            var paths=(settings.AdditionalJavaPaths??[]).Concat(new[]{Text(args,"selectedPath")});
            var root=Text(request["instance"],"root",Text(args,"root"));
            return new{runtimes=await JavaRuntimeDetector.DetectAsync(paths,token,settings.ManagedJavaRuntimeDirectories,root)};
        }
        if(action=="settingsProbeJava"){
            var path=Path.GetFullPath(Text(args,"path"));if(Path.GetFileName(path).ToLowerInvariant() is not ("java.exe" or "javaw.exe"))throw new IOException("请选择 java.exe 或 javaw.exe。");
            var runtime=await JavaRuntimeDetector.ProbeAsync(path,true,token)??throw new IOException("所选文件无法识别为可用的 Java 运行环境。");return new{runtime};
        }
        if(action=="settingsReadPreset"){
            var entry=request["instance"]!;var path=Path.GetFullPath(Text(entry,"path"));var root=Path.GetFullPath(Text(entry,"root"));
            InstanceContentService.ValidatePath(root,path);if(!GameFolderService.PathsEqual(Path.GetDirectoryName(path)!,Path.Combine(root,"versions"))||!Directory.Exists(path))throw new IOException("实例已不存在，请刷新列表。");
            var loader=Text(entry,"loaderId");var instance=new InstanceItem(Text(entry,"name"),path,Text(entry,"version"),loader,entry["moddable"]?.GetValue<bool>()??false,"",IsRelease:Text(entry,"releaseType")=="release",MinecraftVersionId:Text(entry,"version"));
            var local=new InstanceLaunchSettingsStore().Load(path,settings);var directory=MinecraftLaunchService.ResolveGameDirectory(instance,root,local,settings);
            var result=await Task.Run(()=>DefaultGameSettingsService.Read(directory,settings.GameDefaults??new()),token);
            if(result.Count==0)throw new IOException("此实例还没有可读取的游戏设置，请先运行游戏并保存设置。");
            return new{values=JsonSerializer.SerializeToNode(result.Settings),count=result.Count};
        }
        if(action is "settingsUpdateState" or "settingsUpdateConfig"){
            // Reading preferences does not contact the update server.
            var updates=new WorkerUpdateService(store);await updates.LoadAsync(token,action=="settingsUpdateState",args["startupTicket"]?.GetValue<bool>()??false);
            if(action=="settingsUpdateConfig")updates.Configure(Text(args,"channel","preview"),args["startupCheck"]?.GetValue<bool>()??true);
            return updates.Snapshot();
        }
        var updater=new WorkerUpdateService(store);await updater.LoadAsync(token);
        if(action=="settingsUpdateCheck")await updater.CheckAsync(token,args["silent"]?.GetValue<bool>()??false);
        else if(action=="settingsUpdateDownload"){
            var release=args["release"]!.Deserialize<UpdateRelease>(Json)??throw new IOException("缺少更新清单。");await updater.DownloadAsync(release,new ProgressSink(),token);
        }else if(action=="settingsUpdateApply"){
            if(new GameSessionManager().ActiveSessions.Count>0)throw new IOException("请先关闭正在启动或运行的游戏，再重启更新。");
            await updater.ApplyAsync(args["parentId"]!.GetValue<int>(),token);
        }
        else throw new IOException("未知设置操作。");
        return updater.Snapshot();
    }
}
