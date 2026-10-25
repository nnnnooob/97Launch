using System.Text.Json;
using System.Text.Json.Nodes;
using Launcher.App.Services;
using Launcher.Core.Downloads;

internal static class ServerActions {
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private static string Text(JsonNode? node,string key)=>node?[key]?.GetValue<string>()??"";
    private static object Row(CatalogServer server)=>new {
        id=server.Id,name=server.Name,description=string.IsNullOrWhiteSpace(server.Description)?(string.IsNullOrWhiteSpace(server.Motd)?"Minecraft 多人游戏服务器":server.Motd.Replace('\n',' ')):server.Description,
        address=server.Host.Contains(':')?$"[{server.Host}]:{server.Port}":$"{server.Host}:{server.Port}",
        state=server.State,stateText=server.State switch{"online"=>"在线","starting"=>"启动中","stopped"=>"未运行","stopping"=>"停止中","maintenance"=>"维护中","unreachable"=>"游戏尚未就绪",_=>"状态未知"},
        players=server.Players is {} players&&server.MaxPlayers is {} maximum?$"{players} / {maximum} 人":"人数未知",
        version=string.Join(" · ",new[]{server.Minecraft,server.Loader}.Where(v=>!string.IsNullOrWhiteSpace(v))) is {Length:>0} version?version:"版本未指定",
        canJoin=server.CanJoin,hasPack=server.Pack is not null,latency=(int?)null
    };
    public static async Task<object> RunAsync(JsonNode request,LocalModpackService installer,IProgress<InstallProgress> progress,CancellationToken token){
        try{return await RunCoreAsync(request,installer,progress,token);}
        catch(HttpRequestException){throw new IOException("暂时无法连接服务器目录，请检查网络后重试。");}
        catch(OperationCanceledException)when(!token.IsCancellationRequested){throw new IOException("服务器目录连接超时，请稍后刷新。");}
    }
    private static async Task<object> RunCoreAsync(JsonNode request,LocalModpackService installer,IProgress<InstallProgress> progress,CancellationToken token){
        var action=Text(request,"action");var args=request["args"]!;
        var authorization=new LauncherAuthorizationService();using var cancel=token.Register(authorization.CancelPending);
        authorization.Changed+=(_,_)=>{if(!token.IsCancellationRequested&&!authorization.IsBusy)Program.Send(new{type="partial",data=new{authorization=SettingsActions.AuthorizationState(authorization)}});};
        token.ThrowIfCancellationRequested();
        await authorization.InitializeAsync();token.ThrowIfCancellationRequested();authorization.RequireAuthorization();
        var catalog=new ServerCatalogService(authorization,installer);
        if(action=="downloadServerList"){
            var directory=await catalog.ListAsync(token);
            token.ThrowIfCancellationRequested();
            if(directory.Items is null||directory.Items.Length>100)throw new InvalidDataException("服务器目录超出当前支持的范围。");
            var ids=new HashSet<string>();
            var servers=directory.Items.OrderBy(s=>s.Order).ThenBy(s=>s.Name).ToArray();
            foreach(var server in servers){
                if(!Guid.TryParse(server.Id,out _)||!ids.Add(server.Id))throw new InvalidDataException("服务器目录包含无效条目。");
                ServerCatalogService.ValidateAddress(server);
            }
            if(directory.Stale)servers=servers.Select(s=>s with{CanJoin=false,State="unknown"}).ToArray();
            Program.Send(new{type="partial",data=new{items=servers.Select(Row),stale=directory.Stale}});
            using var limiter=new SemaphoreSlim(4);
            await Task.WhenAll(servers.Select(async server=>{
                await limiter.WaitAsync(token);
                try{
                    var latency=await ServerCatalogService.LatencyAsync(server,token);
                    Program.Send(new{type="partial",data=new{serverId=server.Id,latency}});
                }finally{limiter.Release();}
            }));
            return new{stale=directory.Stale};
        }
        var serverId=Text(args,"serverId");
        if(action=="downloadServerPack")return new{pack=await catalog.GetPackAsync(serverId,token)};
        if(action=="downloadServerInstall"){
            if(!Guid.TryParse(serverId,out _))throw new InvalidDataException("服务器标识无效。");
            var pack=args["pack"]!.Deserialize<ServerPack>(Json)??throw new IOException("缺少关联实例信息，请重新获取。");
            ServerCatalogService.ValidatePack(pack);
            var root=Path.GetFullPath(Text(args,"root"));var name=Text(args,"name");
            ModrinthModpackInstaller.ValidateName(name);
            var destination=InstanceContentService.SafeChild(Path.Combine(root,"versions"),name);
            if(File.Exists(destination)||(Directory.Exists(destination)&&!File.Exists(Path.Combine(destination,".97launch-installing"))))throw new IOException("同名实例已存在，请更换名称。");
            await catalog.InstallAsync(serverId,pack,root,name,progress,token);
            return new{path=destination};
        }
        throw new IOException("未知服务器操作。");
    }
}
