#include "launcherflow.h"
#include "globalsettings.h"
#include "instances.h"
#include "accounts.h"
#include "webviewruntime.h"
#include <QDateTime>
#include <QDir>
#include <QFile>
#include <QImageReader>
#include <QJsonDocument>
#include <QJsonObject>
#include <QSaveFile>
#include <QTemporaryFile>
#include <QUrl>
#include <QDebug>

LauncherFlow::LauncherFlow(GlobalSettings* settings,Instances* instances,Accounts* accounts,WebViewRuntime* runtime,QObject* parent)
    :QObject(parent),m_settings(settings),m_instances(instances),m_accounts(accounts),m_runtime(runtime){
    m_verification.setInterval(15*60*1000);
    connect(&m_verification,&QTimer::timeout,this,[this]{m_settings->authorizationAction("verify");});
    connect(settings,&GlobalSettings::authorizationResolved,this,[this](const QString&){
        const auto status=m_settings->authorization().value("status").toString();
        const bool wasAuthorized=m_authorized;
        m_initialized=true;m_authorized=status=="Authorized"||status=="Offline";
        if(m_authorized){
            if(!wasAuthorized)emit homeRequested();
            m_verification.start();
            if(m_view=="pairing"){
                if(m_startup)prepareStartup();else enterMain();
            }
        }else{
            m_preparationBusy=false;
            m_verification.stop();
            if(!m_active||m_view!="update"){setView("pairing");setActive(true);}
        }
        emit changed();
    });
    connect(instances,&Instances::changed,this,[this]{
        if(!m_preparationBusy||m_preparationStep!=1||m_instances->loading())return;
        int count=0;for(const auto& folder:m_instances->folders())count+=folder.toMap().value("count").toInt();
        if(!m_instances->error().isEmpty())qWarning()<<"首次准备实例扫描提示："<<m_instances->error();
        finishPreparationStep(QString("已检查 %1 个游戏文件夹，发现 %2 个实例").arg(m_instances->folders().size()).arg(count));
    });
    connect(settings,&GlobalSettings::javaResolved,this,[this](bool success){
        if(!m_preparationBusy||m_preparationStep!=2)return;
        if(!success){failPreparation(m_settings->javaStatus());return;}
        const auto count=m_settings->javaRuntimes().size();
        finishPreparationStep(count?QString("发现 %1 个 Java 环境").arg(count):"未发现 Java，启动游戏时可按需下载");
    });
    connect(runtime,&WebViewRuntime::changed,this,[this]{
        if(m_preparationBusy&&m_preparationStep==3){m_preparationDetail=m_runtime->detail();emit changed();}
    });
    connect(runtime,&WebViewRuntime::finished,this,[this](bool success){
        if(!m_preparationBusy||m_preparationStep!=3)return;
        if(!success){failPreparation(m_runtime->error());return;}
        prepareAvatar();
    });
    connect(settings,&GlobalSettings::updatesResolved,this,[this](const QString& action){
        if(action=="settingsUpdateState"){
            m_updateStateReady=true;
            if(m_startup&&m_authorized&&m_view=="pairing")prepareStartup();
        }
        if(action=="settingsUpdateCheck"&&m_waitingStartupCheck){
            m_waitingStartupCheck=false;
            const auto state=m_settings->updates();
            if(state.value("error").toString().isEmpty()&&state.value("latest").toMap().isEmpty()&&!state.value("ready").toBool()){
                QTimer::singleShot(350,this,[this]{if(!m_manualUpdate&&m_active&&m_view=="update"&&m_authorized)continueFromUpdates();});
            }
        }
    });
    QTimer::singleShot(0,this,[this]{m_settings->authorizationAction("verify");});
}
void LauncherFlow::setActive(bool active){if(m_active==active)return;m_active=active;emit activeChanged();}
void LauncherFlow::setView(const QString& view){if(m_view==view)return;m_view=view;emit changed();}
void LauncherFlow::prepareStartup(){
    if(!m_updateStateReady)return;
    const auto state=m_settings->updates();
    if(state.value("startupCheck",true).toBool()){
        m_manualUpdate=false;m_waitingStartupCheck=true;setView("update");m_settings->updateAction("startup");
    }else if(state.value("ready").toBool())setView("update");
    else enterMain();
}
void LauncherFlow::openUpdates(bool check){
    m_manualUpdate=true;m_waitingStartupCheck=false;setView("update");setActive(true);
    if(check)m_settings->updateAction("check");
}
void LauncherFlow::openPairing(){setView("pairing");setActive(true);}
void LauncherFlow::continueFromUpdates(){
    if(m_settings->updates().value("busy").toBool())return;
    m_waitingStartupCheck=false;
    if(m_authorized)enterMain();
    else setView("pairing");
}
QVariantList LauncherFlow::preparationSteps()const{
    const QStringList labels{"检查本地数据","扫描游戏实例","检查 Java 环境","准备头像与登录环境"};
    QVariantList rows;
    for(int i=0;i<labels.size();++i)rows.append(QVariantMap{{"label",labels[i]},{"state",i<m_preparationCompleted?"done":i==m_preparationStep?m_preparationBusy?"active":"error":"waiting"}});
    return rows;
}
void LauncherFlow::enterMain(){
    if(!m_authorized)return;
    if(!m_preparationDone){
        QFile marker(m_settings->dataDirectory()+"/startup-v1.json");
        if(marker.open(QIODevice::ReadOnly)&&marker.size()<=4096)
            m_preparationDone=QJsonDocument::fromJson(marker.readAll()).object().value("schema").toInt()==1;
    }
    if(!m_preparationDone){retryPreparation();return;}
    m_startup=false;setActive(false);
}
void LauncherFlow::retryPreparation(){
    if(!m_authorized||m_preparationBusy)return;
    m_preparationStep=m_preparationCompleted=0;m_preparationBusy=true;m_preparationError.clear();
    m_preparationDetail="正在准备本地环境…";setView("preparation");setActive(true);emit changed();
    QTimer::singleShot(0,this,&LauncherFlow::runPreparationStep);
}
void LauncherFlow::runPreparationStep(){
    if(!m_authorized||!m_preparationBusy)return;
    m_stepTime.start();
    if(m_preparationStep==4){
        m_preparationDone=true;m_preparationBusy=false;m_preparationDetail="准备完成，正在打开主界面…";
        QSaveFile marker(m_settings->dataDirectory()+"/startup-v1.json");
        const auto bytes=QJsonDocument(QJsonObject{{"schema",1},{"completed_at",QDateTime::currentDateTimeUtc().toString(Qt::ISODateWithMs)}}).toJson();
        if(!marker.open(QIODevice::WriteOnly)||marker.write(bytes)!=bytes.size()||!marker.commit())qWarning()<<"首次初始化标记未能保存，下次启动将重新准备";
        emit changed();enterMain();return;
    }
    m_preparationDetail=preparationSteps()[m_preparationStep].toMap().value("label").toString()+"…";emit changed();
    if(m_preparationStep==0){
        if(!m_settings->storageValid()||!m_instances->storageValid()||!m_accounts->storageValid()){
            failPreparation("本地设置或账号文件无法读取，已保留原文件，请备份并修复后重启应用。");return;
        }
        if(!QDir().mkpath(m_settings->dataDirectory())||!QDir().mkpath(m_settings->logDirectory())){failPreparation("无法创建本地数据或日志目录。");return;}
        QTemporaryFile probe(m_settings->dataDirectory()+"/.startup-XXXXXX");
        if(!probe.open()){failPreparation("本地数据目录无法写入，请检查目录权限或磁盘空间。");return;}
        finishPreparationStep("本地数据已就绪");
    }else if(m_preparationStep==1){
        if(!m_instances->loading())m_instances->refresh();
    }else if(m_preparationStep==2)m_settings->refreshJava();
    else m_runtime->ensure();
}
void LauncherFlow::finishPreparationStep(const QString& detail){
    qInfo().noquote()<<"首次初始化："<<preparationSteps()[m_preparationStep].toMap().value("label").toString()<<"，耗时"<<m_stepTime.elapsed()<<"ms，"<<detail;
    ++m_preparationCompleted;++m_preparationStep;m_preparationDetail=detail;emit changed();
    const auto step=m_preparationStep;
    QTimer::singleShot(0,this,[this,step]{if(step==m_preparationStep)runPreparationStep();});
}
void LauncherFlow::failPreparation(const QString& error){
    m_preparationBusy=false;m_preparationError=error;m_preparationDetail="准备未完成，可以重试。";qWarning().noquote()<<"首次初始化失败："<<error;emit changed();
}
void LauncherFlow::prepareAvatar(){
    const auto account=m_accounts->current();auto source=account.value("skin",account.value("avatar")).toString();
    if(source.startsWith("qrc:"))source=":"+QUrl(source).path();else if(source.startsWith("file:"))source=QUrl(source).toLocalFile();
    QImageReader reader(source);const auto size=reader.size();
    const bool ready=size.isValid()&&size.width()<=4096&&size.height()<=4096&&!reader.read().isNull();
    if(!ready){QImageReader fallback(":/art/steve_head.png");if(fallback.read().isNull()){failPreparation("无法加载兼容头像资源，请重新准备。");return;}}
    finishPreparationStep(!m_runtime->available()?"头像已就绪，微软登录时可重试安装 WebView2":ready?"头像预览与登录环境已就绪":"已准备兼容头像，可继续使用启动器");
}
void LauncherFlow::skipPreview(){
    if(!m_authorized||!canSkipPreview())return;
    m_preparationBusy=true;m_preparationError.clear();prepareAvatar();
}
