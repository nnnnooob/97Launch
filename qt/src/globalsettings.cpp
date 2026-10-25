#include "globalsettings.h"
#include "instancesettings.h"
#include "instances.h"
#include "instancejobs.h"
#include <QCoreApplication>
#include <QDesktopServices>
#include <QDir>
#include <QFile>
#include <QFileInfo>
#include <QJsonDocument>
#include <QJsonObject>
#include <QKeyEvent>
#include <QSaveFile>
#include <QSet>
#include <cmath>

namespace {
QVariantMap presetDefaults() {
    return {{"Enabled",false},{"FieldOfView",70.0},{"Brightness",50.0},{"RenderDistance",12.0},{"MouseSensitivity",100.0},
        {"SprintKey","key.keyboard.left.control"},{"SneakKey","key.keyboard.left.shift"},{"PerspectiveKey","key.keyboard.f5"}};
}
}
GlobalSettings::GlobalSettings(Instances* instances,QObject* parent):QObject(parent),m_instances(instances) {
    m_values=InstanceSettings::defaults();
    for(const auto& key:{"UseGlobalLaunchOptions","UseGlobalMemory","UseGlobalAdvancedOptions","JavaMode","JavaPath"})m_values.remove(key);
    const QVariantMap additional{{"SelectNewInstance",true},{"FileDownloadSource","自动选择"},{"VersionDownloadSource","自动选择"},
        {"DownloadThreads",64},{"DownloadSpeedLimitKb",0},{"CommunitySource","自动选择"},{"ResourceFileNameFormat","create-1.21.1-6.0.4.jar"},
        {"UpgradeAuthlib",false},{"AutoSetChinese",true},{"SelectedJavaPath",""},{"AdditionalJavaPaths",QVariantList{}},
        {"ManagedJavaRuntimeDirectories",QVariantList{}},{"GameDefaults",presetDefaults()}};
    for(auto it=additional.begin();it!=additional.end();++it)m_values[it.key()]=it.value();
    for(const auto& path:{qEnvironmentVariable("LOCALAPPDATA")+"/97Launch/settings.json",dataDirectory()+"/settings.json"}) {
        QFile file(path);if(!file.exists())continue;
        if(!file.open(QIODevice::ReadOnly)||file.size()>4*1024*1024){m_valid=false;m_error="无法读取原设置，已停止保存以保护配置。";continue;}
        QJsonParseError error;const auto json=QJsonDocument::fromJson(file.readAll(),&error);
        if(error.error!=QJsonParseError::NoError||!json.isObject()){m_valid=false;m_error="原设置文件格式无效，已停止保存。";continue;}
        const auto stored=json.object().toVariantMap();
        for(auto it=stored.begin();it!=stored.end();++it){
            if(it.key()=="GameDefaults"){
                auto preset=m_values.value("GameDefaults").toMap();
                const auto saved=it.value().toMap();
                for(auto entry=saved.begin();entry!=saved.end();++entry)if(preset.contains(entry.key()))preset[entry.key()]=entry.value();
                m_values[it.key()]=preset;
            }else m_values[it.key()]=it.value();
        }
    }
    auto preset=presetDefaults();const auto saved=m_values.value("GameDefaults").toMap();for(auto it=saved.begin();it!=saved.end();++it)preset[it.key()]=it.value();m_values["GameDefaults"]=preset;
    m_timer.setInterval(2000);connect(&m_timer,&QTimer::timeout,this,&GlobalSettings::updateMemory);
    connect(instances,&Instances::changed,this,[this]{if(m_timer.isActive())updateMemory();});
    updateMemory();
}
QString GlobalSettings::dataDirectory()const{return qEnvironmentVariable("LOCALAPPDATA")+"/97LaunchQt";}
QString GlobalSettings::logDirectory()const{return dataDirectory()+"/logs";}
QString GlobalSettings::version()const{return QCoreApplication::applicationVersion();}
bool GlobalSettings::save(const QVariantMap& values) {
    if(!m_valid){emit changed();return false;}
    // Merge immediately before saving so Java downloads can add managed runtime records.
    auto merged=values;QFile current(dataDirectory()+"/settings.json");
    if(current.exists()) {
        if(!current.open(QIODevice::ReadOnly)||current.size()>4*1024*1024){m_error="无法读取当前设置，未覆盖文件。";emit changed();return false;}
        QJsonParseError error;const auto doc=QJsonDocument::fromJson(current.readAll(),&error);current.close();
        if(error.error!=QJsonParseError::NoError||!doc.isObject()){m_error="当前设置格式无效，未覆盖文件。";emit changed();return false;}
        const auto disk=doc.object().toVariantMap();
        for(auto it=disk.begin();it!=disk.end();++it){
            if(it.key()=="GameDefaults"){
                auto preset=values.value(it.key()).toMap();const auto previous=m_values.value(it.key()).toMap();const auto saved=it.value().toMap();
                for(auto entry=saved.begin();entry!=saved.end();++entry)
                    if(preset.contains(entry.key())&&preset.value(entry.key())==previous.value(entry.key()))preset[entry.key()]=entry.value();
                merged[it.key()]=preset;
            }else if(!m_values.contains(it.key())||values.value(it.key())==m_values.value(it.key()))merged[it.key()]=it.value();
        }
    }
    QDir().mkpath(dataDirectory());QSaveFile file(dataDirectory()+"/settings.json");const auto bytes=QJsonDocument(QJsonObject::fromVariantMap(merged)).toJson();
    if(!file.open(QIODevice::WriteOnly)||file.write(bytes)!=bytes.size()||!file.commit()){m_error="无法保存设置，原文件已保留。";emit changed();return false;}
    m_values=merged;m_error.clear();emit changed();updateMemory();return true;
}
bool GlobalSettings::setOption(const QString& key,const QVariant& value) {
    if(!m_values.contains(key)||key=="GameDefaults"||key=="ManagedJavaRuntimeDirectories")return false;
    auto next=m_values;auto normalized=value;
    const QMap<QString,QStringList> choices{
        {"VersionIsolation",{"关闭","隔离可安装 Mod 的版本","隔离非正式版","隔离可安装 Mod 的版本与非正式版","隔离所有版本"}},
        {"ProcessPriority",{"高（优先保证游戏运行）","中（平衡）","低（优先保证其他程序运行）"}},
        {"IpPreference",{"IPv4 优先","IPv6 优先","系统默认"}}, {"WindowSize",{"默认","854 × 480","1280 × 720","1920 × 1080","自定义"}},
        {"FileDownloadSource",{"自动选择","官方源","BMCLAPI","Mojang 官方 + BMCLAPI 回退"}},
        {"VersionDownloadSource",{"自动选择","官方源","BMCLAPI","Minecraft Wiki"}}, {"CommunitySource",{"自动选择","Modrinth","CurseForge"}},
        {"ResourceFileNameFormat",{"【机械动力】create-1.21.1-6.0.4.jar","[机械动力] create-1.21.1-6.0.4.jar","机械动力-create-1.21.1-6.0.4.jar","create-1.21.1-6.0.4-机械动力.jar","create-1.21.1-6.0.4.jar"}}};
    if(choices.contains(key)&&!choices[key].contains(value.toString()))return false;
    if(key=="DownloadThreads")normalized=qBound(1,value.toInt(),256);
    if(key=="DownloadSpeedLimitKb")normalized=qBound(0,value.toInt(),20480);
    if(key=="CustomMemoryGb"){const auto n=value.toDouble();if(!std::isfinite(n))return false;normalized=qBound(.5,n,std::max(.5,m_values.value("MaxMemoryGb").toDouble()));}
    if(key=="WindowWidth"||key=="WindowHeight"){bool ok;const auto n=value.toString().toInt(&ok);if(!ok||n<1||n>16384){m_error="窗口尺寸请输入 1～16384 的整数。";emit changed();return false;}}
    next[key]=normalized;return save(next);
}
bool GlobalSettings::applyPreset(const QVariantMap& values){auto next=m_values;auto preset=m_values.value("GameDefaults").toMap();const auto defaults=presetDefaults();for(auto it=values.begin();it!=values.end();++it)if(defaults.contains(it.key()))preset[it.key()]=it.value();next["GameDefaults"]=preset;return save(next);}
bool GlobalSettings::setPreset(const QString& key,const QVariant& value){
    if(key=="FieldOfView"||key=="Brightness"||key=="RenderDistance"||key=="MouseSensitivity") {
        const double min=key=="FieldOfView"?30:key=="RenderDistance"?2:0,max=key=="FieldOfView"?110:key=="Brightness"?100:key=="RenderDistance"?32:200;
        const auto number=value.toDouble();if(!std::isfinite(number))return false;return applyPreset({{key,std::round(qBound(min,number,max))}});
    }
    if(key=="Enabled"||key=="SprintKey"||key=="SneakKey"||key=="PerspectiveKey")return applyPreset({{key,value}});return false;
}
bool GlobalSettings::resetPreset(){auto values=presetDefaults();values["Enabled"]=m_values.value("GameDefaults").toMap().value("Enabled");return applyPreset(values);}
void GlobalSettings::setActive(bool active){if(active){updateMemory();m_timer.start();}else m_timer.stop();}
void GlobalSettings::updateMemory(){const auto instance=m_instances->current();auto effective=instance.isEmpty()?m_values:InstanceSettings::read(instance.value("path").toString(),m_values);effective["IsAutoMemory"]=m_values.value("IsAutoMemory");effective["CustomMemoryGb"]=m_values.value("CustomMemoryGb");auto automatic=effective;automatic["IsAutoMemory"]=true;m_memory=InstanceSettings::memoryPlan(instance,effective);m_memory["automatic"]=InstanceSettings::memoryPlan(instance,automatic).value("allocated");emit memoryChanged();}
void GlobalSettings::clearError(){m_error.clear();emit changed();}
void GlobalSettings::clearUpdateError(){m_updates["error"]="";emit changed();}
void GlobalSettings::acceptAuthorization(const QVariantMap& state){
    if(m_authorization.value("busy").toBool())return;
    const auto revision=m_authorization.value("revision").toString();
    if(!revision.isEmpty()&&state.value("revision").toString()!=revision)return;
    m_authorization=state;emit changed();emit authorizationResolved("settingsVerify");
}
void GlobalSettings::captureKey(bool active){
    m_captureKey=active;
    if(active)QCoreApplication::instance()->installEventFilter(this);
    else QCoreApplication::instance()->removeEventFilter(this);
}
bool GlobalSettings::eventFilter(QObject* watched,QEvent* event){
    if(m_captureKey&&event->type()==QEvent::ShortcutOverride){event->accept();return true;}
    if(m_captureKey&&event->type()==QEvent::KeyPress){
        const auto* key=static_cast<QKeyEvent*>(event);
        if(key->isAutoRepeat())return true;
        const auto value=key->key()==Qt::Key_Escape?QString():gameKey(key->nativeVirtualKey(),key->nativeScanCode(),key->key());
        if(key->key()==Qt::Key_Escape||!value.isEmpty())emit keyCaptured(value);
        return true;
    }
    return QObject::eventFilter(watched,event);
}
bool GlobalSettings::openDirectory(const QString& kind){const auto path=kind=="data"?dataDirectory():kind=="logs"?logDirectory():QString();if(path.isEmpty())return false;QDir().mkpath(path);return QDesktopServices::openUrl(QUrl::fromLocalFile(path));}
bool GlobalSettings::openJava(const QString& path){const QFileInfo file(path);if(!file.isFile()||!QStringList{"java.exe","javaw.exe"}.contains(file.fileName().toLower()))return false;return QDesktopServices::openUrl(QUrl::fromLocalFile(file.absolutePath()));}
QString GlobalSettings::gameKey(int key,int scan,int qtKey)const{
    if(key>=65&&key<=90)return "key.keyboard."+QString(QChar(key+32));if(key>=48&&key<=57)return "key.keyboard."+QString(QChar(key));if(key>=112&&key<=123)return "key.keyboard.f"+QString::number(key-111);if(key>=96&&key<=105)return "key.keyboard.keypad."+QString::number(key-96);
    const bool extended=(scan&0x100)!=0||qtKey==Qt::Key_AltGr;
    if(key==16)return scan==54?"key.keyboard.right.shift":"key.keyboard.left.shift";
    if(key==17)return extended?"key.keyboard.right.control":"key.keyboard.left.control";
    if(key==18)return extended?"key.keyboard.right.alt":"key.keyboard.left.alt";
    const QMap<int,QString> keys{{8,"backspace"},{9,"tab"},{13,extended?"keypad.enter":"enter"},{32,"space"},{160,"left.shift"},{161,"right.shift"},{162,"left.control"},{163,"right.control"},{164,"left.alt"},{165,"right.alt"},{91,"left.win"},{92,"right.win"},{33,"page.up"},{34,"page.down"},{35,"end"},{36,"home"},{37,"left"},{38,"up"},{39,"right"},{40,"down"},{45,"insert"},{46,"delete"},{20,"caps.lock"},{144,"num.lock"},{145,"scroll.lock"},{44,"print.screen"},{19,"pause"},{106,"keypad.multiply"},{107,"keypad.add"},{109,"keypad.subtract"},{110,"keypad.decimal"},{111,"keypad.divide"},{186,"semicolon"},{187,"equal"},{188,"comma"},{189,"minus"},{190,"period"},{191,"slash"},{192,"grave.accent"},{219,"left.bracket"},{220,"backslash"},{221,"right.bracket"},{222,"apostrophe"}};
    return keys.contains(key)?"key.keyboard."+keys[key]:QString();
}
QString GlobalSettings::keyLabel(const QString& key)const{
    const QMap<QString,QString> labels{{"key.keyboard.left.control","左 Ctrl"},{"key.keyboard.right.control","右 Ctrl"},{"key.keyboard.left.shift","左 Shift"},{"key.keyboard.right.shift","右 Shift"},{"key.keyboard.left.alt","左 Alt"},{"key.keyboard.right.alt","右 Alt"},{"key.keyboard.space","空格"},{"key.mouse.left","鼠标左键"},{"key.mouse.right","鼠标右键"},{"key.mouse.middle","鼠标中键"},{"key.mouse.4","鼠标侧键 1"},{"key.mouse.5","鼠标侧键 2"}};
    if(labels.contains(key))return labels[key];auto label=key;label.replace("key.keyboard.keypad.","小键盘 ");label.remove("key.keyboard.");return label.replace('.', ' ').toUpper();
}
QVariantMap GlobalSettings::selectedJava()const{const auto path=QDir::cleanPath(m_values.value("SelectedJavaPath").toString());for(const auto& row:m_java)if(QDir::cleanPath(row.toMap().value("path").toString()).compare(path,Qt::CaseInsensitive)==0)return row.toMap();return {};}
void GlobalSettings::attachJobs(InstanceJobs* reader,InstanceJobs* transfers,InstanceJobs* authorization){
    m_reader=reader;m_transfers=transfers;m_authorizationJobs=authorization;
    auto completed=[this](const QString& action,const QVariantMap& result,const QVariantMap&,const QString&){
        if(action=="settingsJava"){
            m_java=result.value("runtimes").toList();m_javaBusy=false;
            if(m_values.value("SelectedJavaPath").toString().isEmpty()&&!m_java.isEmpty())setOption("SelectedJavaPath",m_java.first().toMap().value("path"));
            m_javaStatus=selectedJava().isEmpty()&&!m_values.value("SelectedJavaPath").toString().isEmpty()?QString("已选 Java 不可用 · %1 个可用版本").arg(m_java.size()):m_java.isEmpty()?"未找到 Java，可手动添加 java.exe":QString("检测到 %1 个 Java 运行环境").arg(m_java.size());
        }else if(action=="settingsProbeJava"){
            m_javaBusy=false;const auto runtime=result.value("runtime").toMap();auto paths=m_values.value("AdditionalJavaPaths").toList();const auto path=runtime.value("path").toString();
            bool found=false;for(const auto& item:paths)if(item.toString().compare(path,Qt::CaseInsensitive)==0)found=true;if(!found)paths.append(path);
            auto next=m_values;next["AdditionalJavaPaths"]=paths;next["SelectedJavaPath"]=path;if(save(next))refreshJava();
        }else if(action=="settingsReadPreset"){
            if(applyPreset(result.value("values").toMap())){m_error=QString("已从实例读取 %1 项游戏设置，其余项目保留当前预设。").arg(result.value("count").toInt());}
        }else if(action=="settingsAuthorization"||action=="settingsVerify"||action=="settingsLogout"||action=="settingsPair")m_authorization=result;
        else if(action=="settingsUpdateApply"){emit restartRequested();}
        else if(action.startsWith("settingsUpdate")){
            m_updates=result;
        }
        emit changed();
        if(action=="settingsAuthorization"||action=="settingsVerify"||action=="settingsLogout"||action=="settingsPair")emit authorizationResolved(action);
        if(action.startsWith("settingsUpdate"))emit updatesResolved(action);
    };
    connect(reader,&InstanceJobs::completed,this,completed);connect(transfers,&InstanceJobs::completed,this,completed);connect(authorization,&InstanceJobs::completed,this,completed);
    auto failed=[this](const QString& action,const QString& error,const QString&){
        if(!action.startsWith("settings"))return;
        if(action=="settingsJava"||action=="settingsProbeJava"){m_javaBusy=false;m_javaStatus=error;}
        else if(action.startsWith("settingsUpdate")){m_updates["busy"]=false;m_updates["downloading"]=false;m_updates["error"]=error;m_updates["status"]=action=="settingsUpdateDownload"?(error=="操作已取消"?"更新下载已取消，可继续下载":"更新下载失败，可重试"):"暂时无法完成更新操作";}
        else if(action=="settingsAuthorization"||action=="settingsVerify"||action=="settingsPair"||action=="settingsLogout"){
            m_authorization["busy"]=false;m_authorization["message"]=error;
            m_authorization["status"]=m_authorization.value("hasCredential").toBool()?"NetworkRequired":"Unpaired";m_authorization["authorized"]=false;
        }else m_error=error;
        emit changed();
        if(action.startsWith("settingsUpdate"))emit updatesResolved(action);
        if(action=="settingsAuthorization"||action=="settingsVerify"||action=="settingsPair"||action=="settingsLogout")emit authorizationResolved(action);
    };
    connect(reader,&InstanceJobs::failed,this,failed);connect(transfers,&InstanceJobs::failed,this,failed);connect(authorization,&InstanceJobs::failed,this,failed);
    connect(authorization,&InstanceJobs::partial,this,[this](const QString&,const QVariantMap& state,const QVariantMap&,const QString&){m_authorization=state;m_authorization["busy"]=true;emit changed();});
    auto updateAuthorization=[this](const QString&,const QVariantMap& result,const QVariantMap&,const QString&){
        if(!result.contains("authorization"))return;
        acceptAuthorization(result.value("authorization").toMap());
    };
    connect(reader,&InstanceJobs::partial,this,updateAuthorization);connect(transfers,&InstanceJobs::partial,this,updateAuthorization);
    connect(transfers,&InstanceJobs::changed,this,[this]{for(const auto& value:m_transfers->tasks()){const auto task=value.toMap();if(task.value("applicationUpdate").toBool()&&task.value("state")=="进行中"){
        m_updates["busy"]=true;m_updates["downloading"]=true;m_updates["progress"]=task.value("progress").toDouble()*100;m_updates["detail"]=task.value("detail");m_updates["speed"]=task.value("speed");
        const auto steps=task.value("steps").toList();const bool verifying=steps.size()>1&&steps[0].toMap().value("progress").toDouble()>=1;
        m_updates["status"]=verifying?"正在校验更新":"正在下载更新";emit changed();return;
    }}});
    m_updates={{"status","尚未检查更新"},{"detail",""},{"channel","preview"},{"startupCheck",true}};
    reader->settings("settingsUpdateState",{{"startupTicket",QCoreApplication::arguments().contains("--update-ticket")}});
}
void GlobalSettings::refreshJava(){if(m_javaBusy||!m_reader)return;m_javaBusy=true;m_javaStatus="正在搜索并验证 Java…";m_reader->settings("settingsJava");emit changed();}
void GlobalSettings::addJava(const QUrl& file){if(m_javaBusy||!m_reader)return;m_javaBusy=true;m_javaStatus="正在验证 Java…";m_reader->settings("settingsProbeJava",{{"path",file.toLocalFile()}});emit changed();}
bool GlobalSettings::removeJava(const QString& path) {
    const auto samePath = [](const QString& a, const QString& b) {
        return QDir::cleanPath(QDir::fromNativeSeparators(a)).compare(QDir::cleanPath(QDir::fromNativeSeparators(b)), Qt::CaseInsensitive) == 0;
    };
    QVariantMap runtime = selectedJava();
    if (!path.isEmpty()) {
        runtime.clear();
        for (const auto& row : m_java) if (samePath(row.toMap().value("path").toString(), path)) { runtime = row.toMap(); break; }
    }
    if (m_javaBusy || !runtime.value("isManual").toBool()) return false;
    auto next = m_values;
    QVariantList paths;
    const auto removed = runtime.value("path").toString();
    for (const auto& value : next.value("AdditionalJavaPaths").toList())
        if (!samePath(value.toString(), removed)) paths.append(value);
    next["AdditionalJavaPaths"] = paths;
    if (samePath(next.value("SelectedJavaPath").toString(), removed)) next["SelectedJavaPath"] = "";
    if (!save(next)) return false;
    refreshJava(); return true;
}
void GlobalSettings::readPreset(const QString& path){if(m_reader)m_reader->settings("settingsReadPreset",{},path);}
void GlobalSettings::authorizationAction(const QString& action){
    if(!m_authorizationJobs||m_authorization.value("busy").toBool()||m_updates.value("busy").toBool())return;
    if(action=="load"&&m_authorization.contains("status"))return;
    const auto name=action=="verify"?"settingsVerify":action=="logout"?"settingsLogout":"settingsAuthorization";
    m_authorization["busy"]=true;m_authorization["message"]="";
    m_authorizationJobs->settings(name);emit changed();
}
void GlobalSettings::pairDevice(const QString& token){
    if(!m_authorizationJobs||m_authorizationJobs->busy()||m_authorization.value("busy").toBool()||m_updates.value("busy").toBool())return;
    m_authorization["busy"]=true;m_authorization["message"]="";
    m_authorizationJobs->settings("settingsPair",{{"token",token}});emit changed();
}
void GlobalSettings::cancelAuthorization(){if(m_authorizationJobs&&m_authorization.value("busy").toBool())m_authorizationJobs->cancel();}
void GlobalSettings::configureUpdates(const QString& channel,bool startup){
    if(m_reader&&!m_updates.value("busy").toBool()){
        m_updates["busy"]=true;m_updates["error"]="";emit changed();
        m_reader->settings("settingsUpdateConfig",{{"channel",channel},{"startupCheck",startup}});
    }
}
void GlobalSettings::updateAction(const QString& action){
    if(!m_reader||!m_transfers)return;
    if(action=="cancel"){for(const auto& task:m_transfers->tasks()){const auto row=task.toMap();if(row.value("applicationUpdate").toBool()&&QStringList{"等待中","进行中"}.contains(row.value("state").toString()))m_transfers->taskAction(row.value("id").toInt());}return;}
    if(m_authorizationJobs&&m_authorizationJobs->busy())return;
    if(m_updates.value("busy").toBool())return;
    if(action=="apply"&&m_transfers->mutating()){m_updates["error"]="传输任务仍在运行，请完成后再重启更新。";emit changed();return;}
    m_updates["error"]="";m_updates["busy"]=true;
    if(action=="download"){const auto release=m_updates.value("latest").toMap();if(release.isEmpty()){m_updates["busy"]=false;return;}m_updates["downloading"]=true;m_transfers->settings("settingsUpdateDownload",{{"release",release},{"title","更新 97Launch · "+release.value("version").toString()}});emit transferRequested();}
    else {m_updates["status"]=action=="apply"?"正在准备重启更新":"正在检查更新";m_reader->settings(action=="apply"?"settingsUpdateApply":"settingsUpdateCheck",{{"silent",action=="startup"}});}emit changed();
}
