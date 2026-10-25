#include "launchstatus.h"
#include "instances.h"
#include "accounts.h"
#include "instancesettings.h"
#include "globalsettings.h"
#include "instancejobs.h"
#include <QDateTime>
#include <QDir>
#include <QFileInfo>
#include <QFile>
#include <QJsonDocument>
#include <QJsonObject>

LaunchStatus::LaunchStatus(Instances* instances,Accounts* accounts,InstanceSettings* settings,GlobalSettings* globals,InstanceJobs* reader,QObject* parent)
    :QObject(parent),m_instances(instances),m_accounts(accounts),m_reader(reader),m_globals(globals){
    m_debounce.setSingleShot(true);m_debounce.setInterval(180);
    connect(&m_debounce,&QTimer::timeout,this,&LaunchStatus::refresh);
    const auto schedule=[this]{
        const auto path=m_instances->current().value("path").toString();
        if(path!=m_checkedPath){
            ++m_generation;m_reader->cancelRead("settingsLaunchStatus");m_checkedPath=path;m_key.clear();m_files.clear();m_java.clear();m_failed=false;m_checking=m_active;
        }
        summarize();if(m_active&&!m_watchingPaused)m_debounce.start();
    };
    connect(instances,&Instances::changed,this,schedule);
    connect(accounts,&Accounts::changed,this,schedule);
    connect(settings,&InstanceSettings::settingsSaved,this,schedule);
    connect(globals,&GlobalSettings::changed,this,schedule);
    const auto changedFile=[this]{m_key.clear();if(m_active&&!m_watchingPaused)m_debounce.start();};
    connect(&m_watcher,&QFileSystemWatcher::fileChanged,this,changedFile);
    connect(&m_watcher,&QFileSystemWatcher::directoryChanged,this,changedFile);
    m_age.setInterval(30000);connect(&m_age,&QTimer::timeout,this,&LaunchStatus::refresh);
    connect(reader,&InstanceJobs::completed,this,[this](const QString& action,const QVariantMap& result,const QVariantMap& args,const QString& path){
        if(action!="settingsLaunchStatus"||args.value("_generation").toInt()!=m_generation||path!=m_checkedPath)return;
        m_java=result;m_checking=false;m_failed=false;
        m_files.clear();for(const auto& file:result.value("watched").toList())m_files.append(file.toString());
        const auto runtime=result.value("runtime").toMap();if(!runtime.value("path").toString().isEmpty())m_files.append(runtime.value("path").toString());
        m_key=fingerprint();watchFiles();summarize();
    });
    connect(reader,&InstanceJobs::failedDetailed,this,[this](const QString& action,const QString& error,const QVariantMap& args,const QString&){
        if(action!="settingsLaunchStatus"||args.value("_generation").toInt()!=m_generation)return;
        m_checking=false;m_failed=true;m_failure=error;m_java.clear();watchFiles();summarize();
    });
    summarize();
}
void LaunchStatus::setActive(bool active){
    m_active=active;
    if(active&&!m_watchingPaused){if(m_failed||!m_java.value("compatible").toBool())m_key.clear();m_age.start();m_debounce.start();watchFiles();}
    else {m_age.stop();m_debounce.stop();const auto files=m_watcher.files(),dirs=m_watcher.directories();if(!files.isEmpty())m_watcher.removePaths(files);if(!dirs.isEmpty())m_watcher.removePaths(dirs);}
}
void LaunchStatus::setWatchingPaused(bool paused){
    m_watchingPaused=paused;m_key.clear();
    if(paused){++m_generation;m_reader->cancelRead("settingsLaunchStatus");m_debounce.stop();m_age.stop();}
    watchFiles();
    if(!paused&&m_active){m_age.start();m_debounce.start();}
}
QString LaunchStatus::fingerprint()const{
    const auto instance=m_instances->current();const auto path=instance.value("path").toString();
    const auto options=path.isEmpty()?InstanceSettings::defaults():InstanceSettings::read(path,InstanceSettings::defaults());
    const auto preferences=m_globals->values();
    QVariantMap key{{"path",path},{"version",instance.value("version")},{"loader",instance.value("loader")},
        {"nide",m_accounts->current().value("type").toInt()==3},{"mode",options.value("JavaMode")},{"java",options.value("JavaPath")},
        {"root",m_instances->selectedFolder()}};
    for(const auto& name:{"SelectedJavaPath","AdditionalJavaPaths","ManagedJavaRuntimeDirectories"})key[name]=preferences.value(name);
    QStringList files=m_files;
    if(!path.isEmpty()){files<<path<<path+"/97Launch/Setup.ini";const auto profiles=QDir(path).entryList({"*.json"},QDir::Files);for(const auto& profile:profiles)files<<path+"/"+profile;}
    QVariantList stamps;for(const auto& file:files){const QFileInfo info(file);stamps.append(file+":"+QString::number(info.exists())+":"+QString::number(info.lastModified().toMSecsSinceEpoch())+":"+QString::number(info.size()));}key["files"]=stamps;
    return QString::fromUtf8(QJsonDocument(QJsonObject::fromVariantMap(key)).toJson(QJsonDocument::Compact));
}
void LaunchStatus::refresh(){
    summarize();if(!m_active||m_watchingPaused)return;
    const auto instance=m_instances->current();const auto path=instance.value("path").toString();
    if(!path.isEmpty()&&(!instance.value("error").toString().isEmpty()||m_instances->details(path).isEmpty())){
        ++m_generation;m_reader->cancelRead("settingsLaunchStatus");m_checking=false;m_java.clear();m_failed=false;watchFiles();summarize();return;
    }
    const auto key=fingerprint();if(key==m_key)return;
    m_key=key;++m_generation;m_checkedPath=path;m_checking=true;m_failed=false;m_failure.clear();m_java.clear();
    m_reader->cancelRead("settingsLaunchStatus");
    if(!m_reader->settings("settingsLaunchStatus",{{"_generation",m_generation},{"nide",m_accounts->current().value("type").toInt()==3}},path)){
        m_checking=false;m_failed=true;m_failure=m_reader->error();
    }
    watchFiles();summarize();
}
void LaunchStatus::watchFiles(){
    const auto old=m_watcher.files()+m_watcher.directories();if(!old.isEmpty())m_watcher.removePaths(old);
    if(!m_active||m_watchingPaused)return;
    auto paths=m_files;const auto path=m_instances->current().value("path").toString();
    if(!path.isEmpty())paths<<path<<QFileInfo(path).absolutePath()<<path+"/97Launch"<<path+"/97Launch/Setup.ini";
    paths.removeDuplicates();QStringList valid;for(const auto& file:paths)if(QFileInfo::exists(file))valid<<file;
    if(!valid.isEmpty())m_watcher.addPaths(valid);
}
void LaunchStatus::summarize(){
    const auto instance=m_instances->current();const auto path=instance.value("path").toString();const auto account=m_accounts->current();
    const bool valid=!path.isEmpty()&&instance.value("error").toString().isEmpty()&&!m_instances->details(path).isEmpty();
    const auto runtime=m_java.value("runtime").toMap();const bool compatible=m_java.value("compatible").toBool();
    const bool mismatch=!runtime.isEmpty()&&!compatible;const auto recommendation=m_java.value("recommendation").toString();
    QString java="暂无对应 Java",hint=path.isEmpty()?"未找到 Java，请手动添加或启动时按需安装。":"未找到适用于此实例的 Java。";
    if(m_checking){java="正在检查 Java…";hint="正在检查实例所需的 Java 版本。";}
    else if(m_failed){java="Java 检查失败";hint=m_failure;}
    else if(!runtime.isEmpty()){
        java="Java "+runtime.value("version").toString()+(mismatch?" · 不兼容":"");
        hint=mismatch?"Java "+runtime.value("version").toString()+" 版本不匹配。"+recommendation+"。\n"+runtime.value("path").toString():runtime.value("path").toString();
    }else if(!recommendation.isEmpty())hint+=recommendation+"。";
    QString readiness=path.isEmpty()?"未选择实例":!valid?"实例文件异常":!account.value("usable").toBool()?"未选择可用账号":m_checking?"正在检查 Java":!compatible?m_failed?"Java 检查失败":mismatch?"Java 版本不匹配，"+recommendation.section(QChar(0xff08),0,0):"未检测到 Java":"就绪";
    bool isolated=false;if(!path.isEmpty()){
        const auto options=InstanceSettings::read(path,InstanceSettings::defaults());
        isolated=QDir(InstanceSettings::resolveDirectory(instance,options))==QDir(path);
    }
    QString lastPlayed=instance.value("lastPlayed").toString();
    if(!path.isEmpty()){
        QFile usage(path+"/97Launch/Usage.json");
        if(usage.open(QIODevice::ReadOnly)&&usage.size()<1024*1024){
            const auto data=QJsonDocument::fromJson(usage.readAll());
            if(data.isObject())lastPlayed=data.object().value("LastPlayed").toString();
        }
    }
    const auto played=QDateTime::fromString(lastPlayed,Qt::ISODate);QString age="从未启动",playedHint="此实例尚无成功启动记录。";
    if(played.isValid()){
        const auto seconds=played.secsTo(QDateTime::currentDateTime());playedHint=played.toLocalTime().toString("yyyy-MM-dd HH:mm:ss");
        age=seconds < -60?played.toLocalTime().toString("yyyy-MM-dd"):seconds<60?"刚刚":seconds<3600?QString::number(seconds/60)+" 分钟前":seconds<86400?QString::number(seconds/3600)+" 小时前":seconds<7*86400?QString::number(seconds/86400)+" 天前":played.toLocalTime().toString("yyyy-MM-dd");
    }
    int count=0;for(const auto& folder:m_instances->folders())count+=folder.toMap().value("count").toInt();
    QString loader=instance.value("loader").toString();if(loader.isEmpty())loader=instance.value("kind")=="snapshot"?"快照":instance.value("kind")=="cobblestone"?"远古版":instance.value("kind")=="fools"?"愚人节版":"原版";
    m_values={{"readiness",readiness},{"ready",readiness=="就绪"},{"javaVersion",java},{"javaHint",hint},
        {"gameVersion",path.isEmpty()?"—":m_java.value("gameVersion",instance.value("version"))},{"loaderDisplay",path.isEmpty()?"—":m_java.value("loaderDisplay",loader)},
        {"isolationDisplay",isolated?"版本隔离":"未隔离"},{"isolationEnabled",isolated},{"instanceCount",count},{"lastPlayedDisplay",age},{"lastPlayedHint",playedHint}};
    emit changed();
}
