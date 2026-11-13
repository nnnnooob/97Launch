#include "gamelaunch.h"
#include "apppaths.h"
#include "instances.h"
#include "accounts.h"
#include "globalsettings.h"
#include "launcherflow.h"
#include "launchstatus.h"
#include "instancesettings.h"
#include <QCoreApplication>
#include <QCryptographicHash>
#include <QDesktopServices>
#include <QDir>
#include <QFileInfo>
#include <QJsonDocument>
#include <QJsonObject>
#include <QProcess>
#include <QUrl>
#ifdef Q_OS_WIN
#include <windows.h>
#endif

GameLaunch::GameLaunch(Instances* instances,Accounts* accounts,GlobalSettings* settings,LauncherFlow* flow,LaunchStatus* status,QObject* parent)
    :QObject(parent),m_instances(instances),m_accounts(accounts),m_settings(settings),m_flow(flow),m_status(status){
    const auto hash=QCryptographicHash::hash(QDir::cleanPath(applicationRoot()).toLower().toUtf8(),QCryptographicHash::Sha256).toHex().left(24);
    m_pipe="97LaunchQt-launch-"+QString::fromLatin1(hash);
    m_connect.setInterval(200);
    connect(&m_connect,&QTimer::timeout,this,[this]{
        if(m_socket.state()==QLocalSocket::ConnectedState){m_connect.stop();return;}
        if(++m_attempts>50){m_connect.stop();fail("无法连接游戏启动服务，请重试。");return;}
        if(m_socket.state()==QLocalSocket::UnconnectedState)m_socket.connectToServer(m_pipe);
    });
    connect(&m_socket,&QLocalSocket::connected,this,[this]{
        m_connect.stop();m_input.clear();
        if(!m_pending.isEmpty()){send(m_pending);m_pending.clear();}
        else send({{"action","snapshot"}});
    });
    connect(&m_socket,&QLocalSocket::readyRead,this,&GameLaunch::read);
    connect(&m_socket,&QLocalSocket::errorOccurred,this,[this](QLocalSocket::LocalSocketError){
        if(m_detaching)return;
        if(m_connect.isActive()&&!m_spawned){
            m_spawned=true;
            QProcess process;process.setProgram(QCoreApplication::applicationDirPath()+"/worker/97Launch.InstanceWorker.exe");
            process.setArguments({"--launch-host",m_pipe});
            process.setWorkingDirectory(QCoreApplication::applicationDirPath());
#ifdef Q_OS_WIN
            process.setCreateProcessArgumentsModifier([](QProcess::CreateProcessArguments* args){args->flags|=CREATE_NO_WINDOW;});
#endif
            if(!process.startDetached()){m_connect.stop();fail("无法启动游戏服务，请检查 worker 目录。");}
        }
    });
    connect(&m_socket,&QLocalSocket::disconnected,this,[this]{
        if(m_detaching||m_connect.isActive())return;
        if(m_starting||!m_sessions.isEmpty())fail("游戏启动服务连接中断；请查看游戏进程和日志后重新连接。");
        m_sessions.clear();emit changed();
    });
    for(auto* object:QList<QObject*>{instances,accounts,settings,status}){
        if(object==instances)connect(instances,&Instances::changed,this,&GameLaunch::changed);
        if(object==accounts)connect(accounts,&Accounts::changed,this,&GameLaunch::changed);
        if(object==settings)connect(settings,&GlobalSettings::changed,this,&GameLaunch::changed);
        if(object==status)connect(status,&LaunchStatus::changed,this,&GameLaunch::changed);
    }
    connect(flow,&LauncherFlow::activeChanged,this,[this]{if(m_flow->active())cancel();emit changed();});
    connect(accounts,&Accounts::loginChanged,this,&GameLaunch::changed);
    // Reattach to a game monitor left by a previous launcher window. Do not start one here.
    m_socket.connectToServer(m_pipe);
}
GameLaunch::~GameLaunch(){detach();}
bool GameLaunch::canLaunch()const{
    const auto instance=m_instances->current();
    return !m_starting&&!m_accounts->busy()&&!m_flow->active()
        &&!instance.value("path").toString().isEmpty()&&instance.value("error").toString().isEmpty()
        &&m_accounts->current().value("usable").toBool()&&m_status->values().value("readiness")!="正在检查 Java";
}
void GameLaunch::send(const QVariantMap& command){
    if(m_socket.state()==QLocalSocket::ConnectedState){m_socket.write(QJsonDocument(QJsonObject::fromVariantMap(command)).toJson(QJsonDocument::Compact)+'\n');m_socket.flush();}
}
void GameLaunch::connectHost(){m_detaching=false;m_spawned=false;m_attempts=0;m_connect.start();if(m_socket.state()==QLocalSocket::UnconnectedState)m_socket.connectToServer(m_pipe);}
bool GameLaunch::start(){
    if(!canLaunch())return false;
    const auto instance=m_instances->details(m_instances->current().value("path").toString());
    if(instance.isEmpty())return false;
    m_error.clear();m_detail="正在准备启动…";m_starting=true;
    m_startingId.clear();
    m_preparation={{"name",instance.value("name")},{"path",instance.value("path")},{"accountType",m_accounts->current().value("typeDisplay")}};
    m_pending={{"action","start"},{"instance",instance},{"accountId",m_accounts->current().value("id")},
        {"global",InstanceSettings::defaults()}};
    if(m_socket.state()==QLocalSocket::ConnectedState){send(m_pending);m_pending.clear();}else connectHost();
    emit changed();return true;
}
QVariantMap GameLaunch::preparation()const{
    auto result=m_preparation;
    result["stage"]=m_detail;
    result["progress"]=QVariant();
    for(const auto& value:m_sessions){
        const auto session=value.toMap();
        if(session.value("state").toInt()!=0)continue;
        if(!m_startingId.isEmpty()&&session.value("id").toString()!=m_startingId)continue;
        result["name"]=session.value("instanceName");result["path"]=session.value("instancePath");
        result["stage"]=session.value("stage");result["progress"]=session.value("progress");break;
    }
    return result;
}
void GameLaunch::cancel(){if(m_starting){send({{"action","cancel"}});if(!m_pending.isEmpty()){m_pending.clear();m_starting=false;m_connect.stop();}}m_prompt.clear();emit changed();}
void GameLaunch::stop(const QString& id){send({{"action","stop"},{"id",id}});}
void GameLaunch::stopAll(){send({{"action","stopAll"}});}
void GameLaunch::answer(bool accepted){if(m_prompt.isEmpty())return;send({{"action","answer"},{"id",m_prompt.value("id")},{"answer",accepted}});m_prompt.clear();emit changed();}
void GameLaunch::fail(const QString& error){m_error=error;m_starting=false;m_pending.clear();m_prompt.clear();emit changed();}
void GameLaunch::clearError(){m_error.clear();emit changed();}
void GameLaunch::dismissCrash(){m_crash.clear();emit changed();}
void GameLaunch::openLogs(const QString& path){
    QString target=path;
    if(target.isEmpty())target=m_crash.value("processLogDirectory").toString();
    if(target.isEmpty())target=m_settings->dataDirectory()+"/logs";
    if(QFileInfo::exists(target))QDesktopServices::openUrl(QUrl::fromLocalFile(target));
}
void GameLaunch::detach(){m_detaching=true;m_connect.stop();m_socket.disconnectFromServer();}
void GameLaunch::read(){
    m_input+=m_socket.readAll();
    if(m_input.size()>4*1024*1024){fail("启动服务响应过大。");m_socket.abort();return;}
    while(m_input.contains('\n')){
        const auto end=m_input.indexOf('\n');const auto data=QJsonDocument::fromJson(m_input.left(end)).object();m_input.remove(0,end+1);
        const auto type=data.value("type").toString();const auto value=data.value("data").toObject().toVariantMap();
        if(type=="sessions"){
            m_sessions=data.value("data").toArray().toVariantList();
            m_startingId=data.value("starting").toString();
            if(data.value("preparing").toBool())m_starting=true;
        }
        else if(type=="accountRefreshed"){if(!m_accounts->acceptLaunchRefresh(value))m_error="游戏账号已刷新，但保存失败，请重新登录以保留登录状态。";}
        else if(type=="question")m_prompt=data.toVariantMap();
        else if(type=="stage")m_detail=data.value("detail").toString();
        else if(type=="started"){m_detail="游戏已启动";m_instances->refresh();emit gameStarted();}
        else if(type=="prepared"){m_starting=false;m_prompt.clear();}
        else if(type=="canceled"){m_detail="启动已取消";m_prompt.clear();}
        else if(type=="error"){m_error=data.value("error").toString();m_detail="启动失败";m_prompt.clear();}
        else if(type=="crash"){m_crash=value;emit crashReported();}
        for(const auto& session:m_sessions){const auto row=session.toMap();if(row.value("state").toInt()==0)m_detail=row.value("stage").toString();}
        emit changed();
    }
}
