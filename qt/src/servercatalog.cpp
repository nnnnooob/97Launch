#include "servercatalog.h"
#include "instances.h"
#include "instancejobs.h"
#include "globalsettings.h"
#include "launcherflow.h"
#include <QClipboard>
#include <QGuiApplication>
#include <QDir>
#include <QFileInfo>
#include <QRegularExpression>

ServerCatalog::ServerCatalog(Instances* instances,GlobalSettings* settings,LauncherFlow* flow,InstanceJobs* reader,InstanceJobs* transfers,QObject* parent)
    :QAbstractListModel(parent),m_instances(instances),m_settings(settings),m_flow(flow),m_reader(reader),m_transfers(transfers){
    m_timer.setInterval(30000);connect(&m_timer,&QTimer::timeout,this,&ServerCatalog::refresh);
    m_noticeTimer.setSingleShot(true);m_noticeTimer.setInterval(3000);connect(&m_noticeTimer,&QTimer::timeout,this,[this]{m_notice.clear();emit changed();});
    connect(settings,&GlobalSettings::changed,this,&ServerCatalog::syncAccess);
    connect(flow,&LauncherFlow::activeChanged,this,&ServerCatalog::syncAccess);
    connect(flow,&LauncherFlow::changed,this,&ServerCatalog::syncAccess);
    connect(reader,&InstanceJobs::partial,this,[this](const QString& action,const QVariantMap& value,const QVariantMap& args,const QString&){
        if(args.value("epoch").toInt()!=m_epoch)return;
        if(value.contains("authorization")){m_settings->acceptAuthorization(value.value("authorization").toMap());return;}
        if(action!="downloadServerList")return;
        if(value.contains("items")){
            m_stale=value.value("stale").toBool();merge(value.value("items").toList());
            m_error=m_stale?"服务器状态暂时无法更新，请稍后刷新。":"";emit changed();
        }else if(value.contains("serverId")){
            const auto rowIndex=find(value.value("serverId").toString());if(rowIndex<0)return;
            auto row=m_rows[rowIndex].toMap();row["latency"]=value.value("latency");m_rows[rowIndex]=row;
            emit dataChanged(index(rowIndex),index(rowIndex),{Qt::UserRole+1});
        }
    });
    connect(reader,&InstanceJobs::completed,this,[this](const QString& action,const QVariantMap& value,const QVariantMap& args,const QString&){
        if(args.value("epoch").toInt()!=m_epoch)return;
        if(action=="downloadServerList"){m_busy=false;emit changed();}
        else if(action=="downloadServerPack"){
            m_preparing.clear();m_pack=value.value("pack").toMap();m_packServer=args.value("serverId").toString();emit changed();emit installDialogRequested();
        }
    });
    connect(reader,&InstanceJobs::failedDetailed,this,[this](const QString& action,const QString& error,const QVariantMap& args,const QString&){
        if(args.value("epoch").toInt()!=m_epoch)return;
        m_error=error;
        if(action=="downloadServerList"){m_busy=false;invalidateRows();}
        if(action=="downloadServerPack")m_preparing.clear();
        emit changed();
    });
}
QVariant ServerCatalog::data(const QModelIndex& index,int role)const{
    return index.isValid()&&index.row()>=0&&index.row()<m_rows.size()&&role==Qt::UserRole+1?m_rows[index.row()]:QVariant{};
}
int ServerCatalog::find(const QString& id)const{for(int i=0;i<m_rows.size();++i)if(m_rows[i].toMap().value("id").toString()==id)return i;return -1;}
void ServerCatalog::merge(const QVariantList& rows){
    QSet<QString> ids;for(const auto& value:rows)ids.insert(value.toMap().value("id").toString());
    for(int i=m_rows.size()-1;i>=0;--i)if(!ids.contains(m_rows[i].toMap().value("id").toString())){beginRemoveRows({},i,i);m_rows.removeAt(i);endRemoveRows();}
    for(int i=0;i<rows.size();++i){
        auto row=rows[i].toMap();const auto old=find(row.value("id").toString());
        if(old<0){beginInsertRows({},i,i);m_rows.insert(i,row);endInsertRows();}
        else{
            if(old!=i){beginMoveRows({},old,old,{},old<i?i+1:i);m_rows.move(old,i);endMoveRows();}
            const auto previous=m_rows[i].toMap();
            if(row.value("canJoin").toBool()&&previous.value("canJoin").toBool())row["latency"]=previous.value("latency");
            if(row!=previous){m_rows[i]=row;emit dataChanged(index(i),index(i),{Qt::UserRole+1});}
        }
    }
}
void ServerCatalog::invalidateRows(){
    m_stale=true;for(auto& value:m_rows){auto row=value.toMap();row["state"]="unknown";row["stateText"]="状态未知";row["canJoin"]=false;row["latency"]=QVariant();value=row;}
    if(!m_rows.isEmpty())emit dataChanged(index(0),index(m_rows.size()-1),{Qt::UserRole+1});
}
void ServerCatalog::setActive(bool active){m_active=active;syncAccess();}
void ServerCatalog::syncAccess(){
    const auto authorization=m_settings->authorization();const auto device=authorization.value("deviceId").toString();
    const bool allowed=m_active&&m_flow->authorized()&&!m_flow->active()&&!authorization.value("busy").toBool();
    const bool reset=!m_flow->authorized()||device!=m_device;
    if(reset&&!m_rows.isEmpty()){beginResetModel();m_rows.clear();endResetModel();emit changed();}
    if((m_allowed&&!allowed)||device!=m_device){
        ++m_epoch;m_reader->cancelRead("downloadServerList");m_reader->cancelRead("downloadServerPack");m_busy=false;m_preparing.clear();
        if(!m_active||m_flow->active()||reset)discardPrepared();else emit changed();
    }
    m_device=device;
    const bool resume=allowed&&!m_allowed;m_allowed=allowed;
    if(!allowed)m_timer.stop();
    else {if(!m_timer.isActive())m_timer.start();if(resume)refresh();}
}
void ServerCatalog::refresh(){
    if(!m_allowed||m_busy||!m_preparing.isEmpty())return;
    m_busy=true;m_error.clear();emit changed();
    if(!m_reader->download("downloadServerList",{{"epoch",m_epoch}})){m_busy=false;m_error=m_reader->error();emit changed();}
}
void ServerCatalog::copyAddress(const QString& id){
    const auto row=find(id);if(row<0)return;
    QGuiApplication::clipboard()->setText(m_rows[row].toMap().value("address").toString());m_notice="已复制服务器 IP 地址";m_noticeTimer.start();emit changed();
}
void ServerCatalog::requestInstall(const QString& id){
    const auto row=find(id);if(!m_allowed||row<0||!m_preparing.isEmpty()||!m_rows[row].toMap().value("hasPack").toBool())return;
    m_reader->cancelRead("downloadServerList");m_busy=false;m_error.clear();m_preparing=id;emit changed();
    if(!m_reader->download("downloadServerPack",{{"serverId",id},{"epoch",m_epoch}})){m_preparing.clear();m_error=m_reader->error();emit changed();}
}
QVariantMap ServerCatalog::preparedPack()const{
    QVariantMap info;for(const auto& key:{"name","version","minecraft","loader","size"})info[key]=m_pack.value(key);return info;
}
QString ServerCatalog::suggestedName(const QString& root)const{
    auto clean=(m_pack.value("name").toString()+"-"+m_pack.value("version").toString()).replace(QRegularExpression("[<>:\"/\\\\|?*\\x00-\\x1f]"),"_").trimmed();
    while(clean.endsWith('.')||clean.endsWith(' '))clean.chop(1);
    clean=clean.left(70);if(clean.isEmpty())clean="服务器整合包";
    if(QRegularExpression("^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\\.|$)",QRegularExpression::CaseInsensitiveOption).match(clean).hasMatch())clean.prepend('_');
    auto name=clean;for(int i=2;QFileInfo::exists(QDir(root).filePath("versions/"+name))||m_transfers->hasPendingTarget(root,name);++i)name=clean+QString(" (%1)").arg(i);
    return name;
}
bool ServerCatalog::installRelated(const QString& root,const QString& requested){
    const auto name=requested.trimmed();
    auto fail=[this](const QString& message){m_error=message;emit changed();return false;};
    if(m_settings->authorization().value("busy").toBool())return fail("设备授权正在验证，请稍候再安装。");
    if(!m_allowed||m_pack.isEmpty()||m_packServer.isEmpty())return fail("关联实例信息已过期，请重新获取。");
    if(name.isEmpty()||name=="."||name==".."||name.endsWith('.')||name.contains(QRegularExpression("[<>:\"/\\\\|?*\\x00-\\x1f]")))return fail("请填写有效的实例名称。");
    const auto target=QDir(root).filePath("versions/"+name);
    if(QFileInfo::exists(target)&&(!QFileInfo(target).isDir()||!QFileInfo::exists(target+"/.97launch-installing")))return fail("同名实例已存在，请更换名称。");
    if(m_transfers->hasPendingTarget(root,name))return fail("该实例已有安装任务，请在传输页面查看。");
    if(!m_transfers->download("downloadServerInstall",{{"root",root},{"name",name},{"serverId",m_packServer},{"pack",m_pack},{"title","安装相关实例 · "+m_pack.value("name").toString()}}))return fail(m_transfers->error());
    discardPrepared();emit queued();return true;
}
void ServerCatalog::discardPrepared(){m_pack.clear();m_packServer.clear();emit changed();}
