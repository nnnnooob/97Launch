using System.Text.Json.Nodes;
using Launcher.App.Services;
using Launcher.App.Services.Accounts;
using Launcher.App.ViewModels;
using Launcher.Core.Downloads;
using Launcher.Core.Instances;
using Microsoft.Extensions.Logging.Abstractions;

internal static class LaunchStatusAction {
    private static string Text(JsonNode? node,string key)=>node?[key]?.GetValue<string>()??"";
    internal static async Task<object> ReadAsync(JsonNode request,LauncherSettings settings,SettingsStore store,CancellationToken token) {
        var entry=request["instance"]!;
        var path=Text(entry,"path");
        var root=path.Length>0?Text(entry,"root"):Text(request["args"],"root");
        settings.SelectedGameFolderPath=root;
        store.RequestDefaults=settings;
        if(path.Length==0){
            var runtimes=await JavaRuntimeDetector.DetectAsync(new[]{settings.SelectedJavaPath}.Concat(settings.AdditionalJavaPaths),token,settings.ManagedJavaRuntimeDirectories,root);
            var selected=runtimes.FirstOrDefault(runtime=>JavaRuntimeDetector.IsSameRuntime(runtime.Path,settings.SelectedJavaPath))??runtimes.FirstOrDefault();
            return new {runtime=selected,compatible=selected is not null,recommendation="",watched=Array.Empty<string>()};
        }
        path=Path.GetFullPath(path);root=Path.GetFullPath(root);
        InstanceContentService.ValidatePath(root,path);
        if(!GameFolderService.PathsEqual(Path.GetDirectoryName(path)!,Path.Combine(root,"versions"))||!Directory.Exists(path))throw new IOException("实例目录已不存在。");
        var info=(await new InstanceScanner(NullLogger<InstanceScanner>.Instance).ScanAsync(root,token))
            .FirstOrDefault(instance=>GameFolderService.PathsEqual(instance.Path,path))??throw new IOException("无法识别此实例。");
        if(info.Kind==InstanceKind.Error)throw new IOException(info.Info);
        var loaderVersion=info.FabricVersion??info.ForgeVersion??info.NeoForgeVersion??info.OptiFineVersion;
        var loaderDisplay=string.IsNullOrWhiteSpace(loaderVersion)?info.LoaderDisplay:$"{info.LoaderDisplay} {loaderVersion}";
        var gameVersion=info.Info.Split(", ",StringSplitOptions.None)[0];
        var instance=new InstanceItem(info.Name,path,gameVersion,info.LoaderDisplay,info.IsModdable,"",
            IsRelease:info.Kind is not (InstanceKind.Snapshot or InstanceKind.Old or InstanceKind.Fool or InstanceKind.Error),
            LoaderVersionDisplay:loaderDisplay,MinecraftVersionId:info.VanillaName);
        var profiles=new List<JsonObject>();var watched=new List<string>();
        var current=path;var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for(var depth=0; ;depth++){
            token.ThrowIfCancellationRequested();
            if(depth>=16)throw new IOException("版本前置层级超过 16 层。");
            InstanceContentService.ValidatePath(Path.Combine(root,"versions"),current);
            if(!seen.Add(current))throw new IOException("版本 JSON 的前置关系形成循环。");
            var profile=await Task.Run(()=>InstanceProfileReader.Read(current),token);
            profiles.Add(profile);watched.Add(InstanceProfileReader.GetJsonPath(current));
            var parent=Text(profile,"inheritsFrom");if(parent.Length==0)break;
            if(Path.GetFileName(parent)!=parent||parent is "." or "..")throw new IOException("版本 JSON 中的前置版本名称无效。");
            current=Path.Combine(root,"versions",parent);
        }
        var local=new InstanceLaunchSettingsStore().Load(path,settings);
        InstanceContentService.ThrowIfBusy(MinecraftLaunchService.ResolveGameDirectory(instance,root,local,settings));
        var java=new JavaRuntimeService(store,new GameFolderService{Root=root},new PclFileDownloadManager(),new JavaRuntimeChangeNotifier());
        var result=await java.CheckInstalledAsync(instance,profiles,local,request["args"]?["nide"]?.GetValue<bool>()==true?LoginType.Nide:LoginType.Legacy,token);
        return new {runtime=result.Runtime,compatible=result.IsCompatible,recommendation=result.Recommendation,watched,gameVersion,loaderDisplay};
    }
}
