#include "instancejobs.h"
#include "instances.h"
#include "instancesettings.h"
#include <QClipboard>
#include <QCoreApplication>
#include <QDateTime>
#include <QDesktopServices>
#include <QDir>
#include <QFileInfo>
#include <QFile>
#include <QGuiApplication>
#include <QImageReader>
#include <QJsonDocument>
#include <QJsonObject>
#include <QSaveFile>
#include <QStandardPaths>
#include <QRegularExpression>
#include <memory>
#ifdef Q_OS_WIN
#include <windows.h>
#endif

namespace {
bool isMutation(const QString& action) {
    return QSet<QString>{"dropImport", "import", "delete", "toggle", "update", "repair", "restoreMods", "setPack", "exportFile", "modify", "recoverModification", "exportPack", "installResource", "downloadInstall", "downloadResource", "downloadLocalPack", "downloadServerInstall", "settingsUpdateDownload"}.contains(action);
}
QString caption(const QString& action) {
    return QMap<QString, QString>{{"import", "导入资源"}, {"delete", "移到回收站"}, {"toggle", "切换 Mod 状态"}, {"update", "更新 Mod"}, {"repair", "补齐必要前置"}, {"restoreMods", "回退 Mod 变更"},
        {"setPack", "设置资源"}, {"exportFile", "导出资源"}, {"modify", "修改实例"}, {"recoverModification", "恢复实例备份"}, {"exportPack", "导出整合包"}, {"installResource", "安装资源"}}.value(action, "读取实例内容");
}
}
InstanceJobs::InstanceJobs(Instances* instances, InstanceSettings* settings, QObject* parent) : QObject(parent), m_instances(instances), m_settings(settings) {
#ifdef Q_OS_WIN
    m_process.setCreateProcessArgumentsModifier([](QProcess::CreateProcessArguments* args) { args->flags |= CREATE_NO_WINDOW; });
#endif
    m_watchDebounce.setSingleShot(true); m_watchDebounce.setInterval(650);
    m_progressNotify.setSingleShot(true); m_progressNotify.setInterval(45);
    connect(&m_progressNotify, &QTimer::timeout, this, &InstanceJobs::changed);
    connect(&m_watch, &QFileSystemWatcher::directoryChanged, this, [this] { m_watchDebounce.start(); });
    connect(&m_watch, &QFileSystemWatcher::fileChanged, this, [this] { m_watchDebounce.start(); });
    connect(&m_watchDebounce, &QTimer::timeout, this, [this] {
        if (m_watchingPaused) return;
        if (!m_downloadWatchPath.isEmpty()) watchDownloadTarget(m_downloadWatchPath, m_downloadWatchKind);
        emit resourcesChanged();
    });
    connect(instances, &Instances::changed, this, [this] {
        if (!m_watchingPaused && !m_instances->loading() && !m_downloadWatchPath.isEmpty())
            watchDownloadTarget(m_downloadWatchPath, m_downloadWatchKind);
    });
    connect(&m_process, &QProcess::readyReadStandardOutput, this, &InstanceJobs::readOutput);
    connect(&m_process, &QProcess::readyReadStandardError, this, [this] { m_stderr += m_process.readAllStandardError(); if (m_stderr.size() > 8192) m_stderr = m_stderr.right(8192); });
    connect(&m_process, &QProcess::started, this, [this] {
        const QJsonObject request{{"action", m_current.action}, {"args", QJsonObject::fromVariantMap(m_current.args)},
            {"instance", QJsonObject::fromVariantMap(m_current.instance)}, {"global", QJsonObject::fromVariantMap(m_current.global)}};
        m_process.write(QJsonDocument(request).toJson(QJsonDocument::Compact) + '\n');
        // Pairing tokens travel through stdin only and never remain in task history.
        if(m_current.action=="settingsPair")m_current.args.remove("token");
    });
    connect(&m_process, qOverload<int, QProcess::ExitStatus>(&QProcess::finished), this, [this](int code, QProcess::ExitStatus) { finish(code); });
    connect(&m_process, &QProcess::errorOccurred, this, [this](QProcess::ProcessError error) {
        if (error == QProcess::FailedToStart) { m_error = "无法启动实例任务：" + m_process.errorString(); finish(-1); }
    });
}
InstanceJobs::~InstanceJobs() {
    m_queue.clear();
    if (m_process.state() != QProcess::NotRunning) { m_process.write("cancel\n"); m_process.waitForFinished(-1); }
}
bool InstanceJobs::mutating() const {
    if (m_process.state() != QProcess::NotRunning && isMutation(m_current.action)) return true;
    for (const auto& job : m_queue) if (isMutation(job.action)) return true;
    return false;
}
bool InstanceJobs::fail(const QString& error) { m_error = error; emit changed(); return false; }
bool InstanceJobs::hasPendingTarget(const QString& root,const QString& name)const{
    for(const auto& job:m_retries){
        if(QDir(job.args.value("root").toString())!=QDir(root)||job.args.value("request").toMap().value("name",job.args.value("name")).toString().compare(name,Qt::CaseInsensitive)!=0)continue;
        for(const auto& value:m_tasks){const auto row=value.toMap();if(row.value("id").toInt()==job.id&&QStringList{"等待中","进行中"}.contains(row.value("state").toString()))return true;}
    }
    return false;
}
void InstanceJobs::clearError() { m_error.clear(); emit changed(); }
bool InstanceJobs::importDrop(const QList<QUrl>& urls) {
    if (busy()) return fail("正在处理上一批拖入文件。");
    QStringList paths;
    for (const auto& url : urls) {
        if (!url.isLocalFile()) continue;
        const auto path = QDir::cleanPath(url.toLocalFile());
        if (!paths.contains(path, Qt::CaseInsensitive)) paths.append(path);
    }
    if (paths.isEmpty()) return fail("请拖入本地 Mod 文件或整合包压缩文件。");
    auto root = m_instances->selectedFolder();
    if (root.isEmpty() && !m_instances->folders().isEmpty()) root = m_instances->folders().first().toMap().value("path").toString();
    const auto instance = m_instances->current();
    const auto defaults = InstanceSettings::defaults();
    const auto directory = instance.isEmpty() ? QString() : InstanceSettings::resolveDirectory(instance, InstanceSettings::read(instance.value("path").toString(), defaults));
    m_queue.enqueue(Job{"dropImport", {{"sources", paths}, {"root", root}, {"gameDirectory", directory}}, instance, defaults, ++m_nextId});
    startNext(); emit changed(); return true;
}
QString InstanceJobs::availableDropName(const QString& root, const QString& requested) const {
    bool registered = false;
    for (const auto& folder : m_instances->folders())
        if (QDir(folder.toMap().value("path").toString()) == QDir(root)) registered = true;
    if (root.isEmpty() || !registered) return {};
    auto name = requested;
    name.replace(QRegularExpression("[<>:\"/\\\\|?*\\x00-\\x1f]"), "_");
    name = name.trimmed();
    while (name.endsWith('.') || name.endsWith(' ')) name.chop(1);
    name = name.left(80);
    while (name.endsWith('.') || name.endsWith(' ')) name.chop(1);
    if (name.isEmpty()) name = "导入的整合包";
    if (QRegularExpression("^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\\.|$)", QRegularExpression::CaseInsensitiveOption).match(name).hasMatch()) name.prepend('_');
    auto result = name;
    for (int suffix = 2; QFileInfo::exists(root + "/versions/" + result) || hasPendingTarget(root, result); ++suffix)
        result = name + " (" + QString::number(suffix) + ")";
    return result;
}
void InstanceJobs::answerDrop(const QString& choice, bool applyAll) {
    if (m_current.action != "dropImport" || m_prompt.isEmpty()) return;
    const QJsonObject response{{"id", m_prompt.value("id").toInt()}, {"choice", choice}, {"applyAll", applyAll}};
    m_process.write(QJsonDocument(response).toJson(QJsonDocument::Compact) + '\n');
    m_prompt.clear(); emit changed();
}
bool InstanceJobs::submit(const QString& action, const QVariantMap& args) {
    const QSet<QString> actions{"scan", "importPreview", "import", "delete", "toggle", "checkUpdates", "update", "repair", "restoreMods", "setPack", "exportFile", "metadata", "componentVersions", "modify", "recoverModification", "exportPack", "searchResources", "resourceVersions", "installResource"};
    if (!actions.contains(action)) return fail("未知实例任务。");
    const auto instance = m_instances->details(m_settings->instance().value("path").toString());
    if (instance.isEmpty()) return fail("请先选择有效实例。");
    if (action == "modify") for (const auto& pending : m_retries) if (pending.action == "modify" && pending.instance.value("path") == instance.value("path"))
        for (const auto& row : m_tasks) if (row.toMap().value("id").toInt() == pending.id && QStringList{"等待中", "进行中"}.contains(row.toMap().value("state").toString())) return fail("此实例已有修改任务，请在传输页面查看进度。");
    if (!isMutation(action)) for (qsizetype i = m_queue.size(); i-- > 0;)
        if (m_queue[i].action == action && (action != "componentVersions" || (m_queue[i].args.value("component") == args.value("component") && m_queue[i].args.value("game") == args.value("game")))) m_queue.removeAt(i);
    const Job job{action, args, instance, InstanceSettings::defaults(), ++m_nextId};
    m_queue.enqueue(job);
    if (isMutation(action)) enqueueTask(job);
    startNext(); emit changed(); return true;
}
bool InstanceJobs::settings(const QString& action,const QVariantMap& args,const QString& instancePath) {
    const QSet<QString> actions{"settingsLaunchStatus","settingsJava","settingsProbeJava","settingsReadPreset","settingsAuthorization","settingsVerify","settingsLogout","settingsPair","settingsUpdateState","settingsUpdateConfig","settingsUpdateCheck","settingsUpdateDownload","settingsUpdateApply","settingsAcknowledge"};
    if(!actions.contains(action))return fail("未知设置操作。");
    const auto instance=instancePath.isEmpty()?QVariantMap{}:m_instances->details(instancePath);
    if(!instancePath.isEmpty()&&instance.isEmpty())return fail("实例已不存在，请刷新列表。");
    auto parameters=args;parameters["root"]=m_instances->selectedFolder();parameters["parentId"]=QCoreApplication::applicationPid();
    if(!isMutation(action))for(qsizetype i=m_queue.size();i-->0;)if(m_queue[i].action==action)m_queue.removeAt(i);
    Job job{action,parameters,instance,InstanceSettings::defaults(),++m_nextId};m_queue.enqueue(job);
    if(isMutation(action))enqueueTask(job);startNext();emit changed();return true;
}
bool InstanceJobs::download(const QString& action, const QVariantMap& args, const QString& instancePath) {
    const QSet<QString> actions{"downloadManifest", "downloadComponents", "downloadTarget", "downloadSearch", "downloadDetails", "downloadGroups", "downloadDependencies", "downloadWorlds", "downloadInstalled", "downloadStates", "downloadInstall", "downloadResource", "downloadLocalPack", "downloadServerList", "downloadServerPack", "downloadServerInstall"};
    if (!actions.contains(action)) return fail("未知下载任务。");
    auto instance = instancePath.isEmpty() ? QVariantMap{} : m_instances->details(instancePath);
    if (!instancePath.isEmpty() && instance.isEmpty()) return fail("安装目标已不存在，请刷新实例列表。");
    auto parameters = args;
    auto root = parameters.value("root", m_instances->selectedFolder()).toString();
    bool registered = false;
    for (const auto& value : m_instances->folders()) if (QDir(value.toMap().value("path").toString()) == QDir(root)) registered = true;
    if (!registered) return fail("请选择有效的游戏文件夹。");
    parameters["root"] = root;
    if (isMutation(action)) {
        const auto name = parameters.value("request").toMap().value("name", parameters.value("name")).toString();
        if (!name.isEmpty()) for (const auto& job : m_retries)
            if ((job.action == "downloadInstall" || job.action == "downloadLocalPack" || job.action == "downloadServerInstall" || job.args.value("project").toMap().value("projectType") == "modpack")
                && job.args.value("root") == root && job.args.value("request").toMap().value("name", job.args.value("name")).toString() == name)
                for (const auto& row : m_tasks) if (row.toMap().value("id").toInt() == job.id && QStringList{"等待中", "进行中"}.contains(row.toMap().value("state").toString())) return fail("同名实例正在安装，请修改实例名称。");
    } else {
        for (qsizetype i = m_queue.size(); i-- > 0;) if (m_queue[i].action == action && (action != "downloadComponents" || m_queue[i].args.value("component") == args.value("component"))) m_queue.removeAt(i);
    }
    auto defaults = InstanceSettings::defaults(); const auto preferences = downloadPreferences();
    for (auto it = preferences.begin(); it != preferences.end(); ++it) defaults[it.key()] = it.value();
    const Job job{action, parameters, instance, defaults, ++m_nextId};
    m_queue.enqueue(job);
    if (isMutation(action)) enqueueTask(job);
    startNext(); emit changed(); return true;
}
void InstanceJobs::enqueueTask(const Job& job) {
    QVariantList steps;
    auto add = [&steps](const QString& name, double weight) { steps.append(QVariantMap{{"name", name}, {"weight", weight}, {"progress", 0.0}, {"state", "等待中"}}); };
    if (job.action == "settingsUpdateDownload") { add("下载新版",9);add("校验新版",1); }
    else if (job.action == "downloadInstall") {
        add("检查版本信息", 3); add("下载客户端与依赖库", 14); add("下载资源索引", 3); add("下载游戏资源", 14);
        const auto request = job.args.value("request").toMap();
        if (!request.value("loader").toString().isEmpty() || !request.value("liteLoaderVersion").toString().isEmpty() || !request.value("optiFineVersion").toString().isEmpty() || !request.value("fabricApiVersion").toString().isEmpty() || !request.value("optiFabricVersion").toString().isEmpty()) add("安装所选加载器和组件", 6);
        add("整理实例文件", 0.01);
    } else if (job.action == "downloadServerInstall") { add("下载客户端整合包", 1); add("读取整合包清单", 1); add("安装游戏与资源", 5); add("整理实例文件", 1); }
    else if (job.action == "downloadLocalPack") { add("读取整合包清单", 1); add("安装游戏与资源", 5); add("整理实例文件", 1); }
    else if (job.args.value("project").toMap().value("projectType") == "modpack" && !job.args.contains("destination")) {
        add("下载整合包", 1); add("下载整合包组件", 5); add("安装游戏与加载器", 5); add("整理实例文件", 1);
    } else if (job.action == "modify") { add("准备新版本", 1); add("提交组件修改", 1); }
    else if (job.action == "exportPack") { add("读取实例信息", 1); add("导出整合包", 1); }
    else add("处理文件", 1);
    const QString title = job.args.value("title", caption(job.action) + " · " + job.instance.value("name").toString()).toString();
    m_tasks.append(QVariantMap{{"id", job.id}, {"title", title}, {"target", job.args.value("name", job.instance.value("name"))}, {"state", "等待中"}, {"detail", "等待前序任务完成"}, {"progress", 0.0}, {"speed", 0.0}, {"steps", steps}, {"applicationUpdate",job.action=="settingsUpdateDownload"}});
    m_retries.insert(job.id, job);
    const auto project = job.args.value("project").toMap();
    const bool importPack = job.action == "downloadLocalPack" || job.action == "downloadServerInstall"
        || (job.action == "downloadResource" && project.value("projectType") == "modpack" && !job.args.contains("destination"));
    const bool showTransfer = job.action == "downloadInstall" || importPack || job.action == "modify";
    if (showTransfer || job.action == "downloadResource" || job.action == "installResource"
        || job.action == "update" || job.action == "repair" || job.action == "settingsUpdateDownload")
        emit downloadQueued(project.value("title", title).toString(), showTransfer);
}
void InstanceJobs::startNext() {
    if (m_process.state() != QProcess::NotRunning || m_queue.isEmpty()) return;
    m_current = m_queue.dequeue(); m_output.clear(); m_stderr.clear(); m_result.clear(); m_error.clear(); m_gotResult = false; m_canceled = false; m_silentCancel = false; m_progress = 0;
    m_detail = caption(m_current.action); updateTask("进行中");
    // Windows directory notifications can hold the instance open during rename.
    if (m_current.action == "modify")
        emit modificationStateChanged(true, m_current.instance.value("path").toString(), {});
    m_process.setProgram(QCoreApplication::applicationDirPath() + "/worker/97Launch.InstanceWorker.exe");
    m_process.setWorkingDirectory(QCoreApplication::applicationDirPath());
    m_process.start(); emit changed();
}
void InstanceJobs::readOutput() {
    m_output += m_process.readAllStandardOutput();
    if (m_output.size() > 32 * 1024 * 1024) { m_error = "任务返回的数据过大。"; cancel(); return; }
    qsizetype newline;
    while ((newline = m_output.indexOf('\n')) >= 0) {
        auto line = m_output.left(newline).trimmed(); m_output.remove(0, newline + 1);
        if (line.startsWith("\xef\xbb\xbf")) line.remove(0, 3);
        QJsonParseError error; const auto object = QJsonDocument::fromJson(line, &error).object();
        if (error.error != QJsonParseError::NoError) continue;
        const auto type = object.value("type").toString();
        if (type == "progress") {
            m_detail = object.value("detail").toString();
            if (object.contains("progress")) m_progress = object.value("progress").toDouble();
            applyProgress(object.toVariantMap()); updateTask("进行中"); if (!m_progressNotify.isActive()) m_progressNotify.start();
        } else if (type == "partial") { emit partial(m_current.action, object.value("data").toObject().toVariantMap(), m_current.args, m_current.instance.value("path").toString()); }
        else if (type == "result") { m_result = object.value("data").toObject().toVariantMap(); m_gotResult = true; }
        else if (type == "question") { m_prompt = object.toVariantMap(); emit changed(); }
        else if (type == "error") { m_error = object.value("error").toString(); m_canceled = object.value("canceled").toBool(); }
    }
}
void InstanceJobs::finish(int exitCode) {
    readOutput();
    const auto job = m_current; const auto result = m_result;
    const bool success = exitCode == 0 && m_gotResult && m_error.isEmpty();
    if (success) { m_progress = 1; m_detail = "已完成"; updateTask("已完成"); }
    else { if (m_error.isEmpty()) m_error = "实例任务意外退出：" + QString::fromUtf8(m_stderr).left(500); m_detail = m_error; updateTask(m_canceled ? "已取消" : "失败"); }
    if (success) {
        if (job.action == "downloadInstall" || job.action == "downloadLocalPack" || job.action == "downloadServerInstall" || (job.action == "downloadResource" && job.args.value("project").toMap().value("projectType") == "modpack" && !job.args.contains("destination"))) {
            m_instances->refresh();
            const auto path = result.value("path").toString();
            if ((job.action == "downloadServerInstall" || job.global.value("SelectNewInstance", true).toBool()) && !path.isEmpty()) {
                const auto root = job.args.value("root").toString();
                m_instances->selectFolder(root);
                auto connection = std::make_shared<QMetaObject::Connection>();
                *connection = connect(m_instances, &Instances::changed, this, [this, path, connection] { if (!m_instances->loading()) { disconnect(*connection); m_instances->selectInstance(path); } });
            }
            emit returnToLaunch();
        }
        if (job.action == "modify") {
            m_instances->instanceMoved(job.instance.value("path").toString(), result.value("path").toString());
            emit instanceMoved(job.instance.value("path").toString(), result.value("path").toString());
        }
        if (job.action == "modify" || job.action == "recoverModification") m_instances->refresh();
        if (job.action == "modify") emit returnToLaunch();
        emit completed(job.action, result, job.args, job.instance.value("path").toString());
    } else if (m_silentCancel && m_canceled) { m_error.clear(); m_detail.clear(); }
    else {
        emit failed(job.action, m_error, job.instance.value("path").toString());
        emit failedDetailed(job.action, m_error, job.args, job.instance.value("path").toString());
    }
    m_prompt.clear();
    if (success || m_canceled) m_retries.remove(job.id);
    if (job.action == "modify")
        emit modificationStateChanged(false, job.instance.value("path").toString(), success ? result.value("path").toString() : job.instance.value("path").toString());
    emit changed(); startNext();
}
void InstanceJobs::applyProgress(const QVariantMap& update) {
    for (auto& value : m_tasks) { auto row = value.toMap(); if (row.value("id").toInt() != m_current.id) continue;
        row["speed"] = update.value("speed", 0);
        auto steps = row.value("steps").toList();
        if (update.contains("stage") && !steps.isEmpty()) {
            const auto index = qBound(0, update.value("stage").toInt(), int(steps.size() - 1));
            double sum = 0, weight = 0;
            for (int i = 0; i < steps.size(); ++i) { auto step = steps[i].toMap();
                if (!update.value("preserve").toBool() || i == index) step["progress"] = i < index ? 1.0 : i == index ? qBound(0.0, update.value("fraction").toDouble(), 1.0) : 0.0;
                const double p = step.value("progress").toDouble();
                if (!update.value("preserve").toBool() || i == index) step["state"] = p >= 1 ? "已完成" : i == index ? "进行中" : "等待中";
                steps[i] = step; weight += step.value("weight").toDouble(); sum += p * step.value("weight").toDouble();
            }
            m_progress = weight > 0 ? sum / weight : 0; row["steps"] = steps;
        }
        value = row; break;
    }
}
void InstanceJobs::updateTask(const QString& state) {
    for (auto& value : m_tasks) { auto row = value.toMap(); if (row.value("id").toInt() != m_current.id) continue;
        row["state"] = state; row["detail"] = m_detail; row["progress"] = m_progress;
        if (state != "进行中") row["speed"] = 0;
        if (state == "已完成") { auto steps = row.value("steps").toList(); for (auto& value : steps) { auto step = value.toMap(); step["state"] = "已完成"; step["progress"] = 1; value = step; } row["steps"] = steps; row["output"] = m_result.value("destination"); }
        if (state == "失败" || state == "已取消") { auto steps = row.value("steps").toList(); bool marked = false; for (auto& value : steps) { auto step = value.toMap(); if (step.value("state") != "已完成") { step["state"] = state == "失败" && !marked ? "失败" : "已取消"; marked = true; } value = step; } row["steps"] = steps; }
        value = row; break;
    }
}
void InstanceJobs::taskAction(int id) {
    for (qsizetype i = 0; i < m_tasks.size(); ++i) { auto row = m_tasks[i].toMap(); if (row.value("id").toInt() != id) continue;
        if (row.value("state") == "进行中") { if (m_current.id == id) { m_process.write("cancel\n"); m_detail = "正在取消并整理文件…"; updateTask("进行中"); } }
        else if (row.value("state") == "等待中") { for (qsizetype q = m_queue.size(); q-- > 0;) if (m_queue[q].id == id) m_queue.removeAt(q); row["state"] = "已取消"; row["detail"] = "已取消排队任务"; m_tasks[i] = row; m_retries.remove(id); }
        else { m_tasks.removeAt(i); m_retries.remove(id); }
        emit changed(); return;
    }
}
void InstanceJobs::retry(int id) {
    if (!m_retries.contains(id)) return;
    for (qsizetype i = 0; i < m_tasks.size(); ++i) if (m_tasks[i].toMap().value("id").toInt() == id && m_tasks[i].toMap().value("state") == "失败") {
        const auto source = m_retries.value(id);
        if (source.action == "modify") for (const auto& pending : m_retries) if (pending.id != id && pending.action == "modify" && pending.instance.value("path") == source.instance.value("path"))
            for (const auto& row : m_tasks) if (row.toMap().value("id").toInt() == pending.id && QStringList{"等待中", "进行中"}.contains(row.toMap().value("state").toString())) { fail("此实例已有修改任务，请等待该任务结束。"); return; }
        const auto old = m_retries.take(id); m_tasks.removeAt(i);
        Job job = old; job.id = ++m_nextId; m_queue.enqueue(job); enqueueTask(job); startNext(); emit changed(); return;
    }
}
void InstanceJobs::clearFinished() {
    for (qsizetype i = m_tasks.size(); i-- > 0;) if (!QStringList{"进行中", "等待中"}.contains(m_tasks[i].toMap().value("state").toString())) { m_retries.remove(m_tasks[i].toMap().value("id").toInt()); m_tasks.removeAt(i); }
    emit changed();
}
void InstanceJobs::answer(bool accepted) {
    if (m_prompt.isEmpty()) return;
    const QJsonObject response{{"answer", accepted}, {"id", m_prompt.value("id").toInt()}};
    m_process.write(QJsonDocument(response).toJson(QJsonDocument::Compact) + '\n'); m_prompt.clear(); emit changed();
}
void InstanceJobs::copyText(const QString& text) { QGuiApplication::clipboard()->setText(text); }
bool InstanceJobs::revealOutput(const QString& path) {
    if (path.isEmpty() || !QFileInfo::exists(path)) return false;
    return QProcess::startDetached("explorer.exe", {"/select,", QDir::toNativeSeparators(path)});
}
QUrl InstanceJobs::saveSuggestion(const QString& name) const {
    return QUrl::fromLocalFile(QStandardPaths::writableLocation(QStandardPaths::DownloadLocation) + '/' + QFileInfo(name).fileName());
}
QVariantMap InstanceJobs::downloadPreferences() const {
    QVariantMap values{{"SelectNewInstance", true}, {"CommunitySource", "自动选择"}};
    const auto directory = qEnvironmentVariable("LOCALAPPDATA");
    for (const auto& path : {directory + "/97Launch/settings.json", directory + "/97LaunchQt/settings.json"}) {
        QFile file(path); if (!file.open(QIODevice::ReadOnly) || file.size() > 4 * 1024 * 1024) continue;
        const auto settings = QJsonDocument::fromJson(file.readAll()).object();
        for (const auto& key : {"SelectNewInstance", "CommunitySource"}) if (settings.contains(key)) values[key] = settings.value(key).toVariant();
    }
    return values;
}
void InstanceJobs::cancel() {
    for (const auto& job : m_queue) if (isMutation(job.action)) for (auto& value : m_tasks) { auto row = value.toMap(); if (row.value("id").toInt() == job.id) { row["state"] = "已取消"; value = row; } }
    m_queue.clear();
    if (m_process.state() != QProcess::NotRunning) { m_process.write("cancel\n"); m_detail = "正在取消并整理文件…"; }
    emit changed();
}
void InstanceJobs::cancelRead(const QString& action) {
    if (isMutation(action)) return;
    for (qsizetype i = m_queue.size(); i-- > 0;) if (m_queue[i].action == action) m_queue.removeAt(i);
    if (m_current.action == action && m_process.state() != QProcess::NotRunning) { m_silentCancel = true; m_process.write("cancel\n"); }
    emit changed();
}
void InstanceJobs::setWatchingPaused(bool paused, const QString& oldPath, const QString& newPath) {
    m_watchingPaused = paused;
    const auto old = m_watch.directories() + m_watch.files();
    if (!old.isEmpty()) m_watch.removePaths(old);
    m_watchDebounce.stop();
    if (paused) return;
    if (!m_downloadWatchPath.isEmpty()) {
        if (!newPath.isEmpty() && QDir(m_downloadWatchPath) == QDir(oldPath)) m_downloadWatchPath = newPath;
        watchDownloadTarget(m_downloadWatchPath, m_downloadWatchKind);
    } else watchDirectory(m_watchKind);
    emit resourcesChanged();
}
void InstanceJobs::watchDirectory(const QString& kind) {
    m_watchKind = kind;
    m_downloadWatchPath.clear(); m_downloadWatchKind.clear();
    const auto old = m_watch.directories() + m_watch.files(); if (!old.isEmpty()) m_watch.removePaths(old);
    m_watchDebounce.stop();
    if (m_watchingPaused) return;
    if (!QSet<QString>{"mods", "saves", "screenshots", "resourcepacks", "shaderpacks"}.contains(kind)) return;
    const auto root = m_settings->gameDirectory();
    if (root.isEmpty()) return;
    auto directory = root + '/' + kind;
    if (!QDir(directory).exists()) directory = root;
    if (QDir(directory).exists()) m_watch.addPath(directory);
    if (kind == "saves") for (const auto& world : QDir(root + "/saves").entryInfoList(QDir::Dirs | QDir::NoDotAndDotDot | QDir::NoSymLinks))
        m_watch.addPath(world.absoluteFilePath());
}
void InstanceJobs::watchDownloadTarget(const QString& instancePath, const QString& kind) {
    const auto path = instancePath;
    const auto contentKind = kind;
    const auto old = m_watch.directories() + m_watch.files();
    if (!old.isEmpty()) m_watch.removePaths(old);
    m_watchDebounce.stop();
    m_watchKind.clear();
    m_downloadWatchPath.clear(); m_downloadWatchKind.clear();
    if (!QSet<QString>{"mods", "resourcepacks", "shaderpacks", "saves"}.contains(contentKind)) return;
    m_downloadWatchPath = path; m_downloadWatchKind = contentKind;
    if (m_watchingPaused || path.isEmpty()) return;
    const auto instance = m_instances->details(path);
    if (instance.isEmpty()) return;
    const auto options = InstanceSettings::read(path, InstanceSettings::defaults());
    const auto root = InstanceSettings::resolveDirectory(instance, options);
    QStringList directories{root, root + '/' + contentKind, path + "/97Launch"};
    QStringList paths;
    for (auto directory : directories) {
        while (!directory.isEmpty() && !QDir(directory).exists()) {
            const auto parent = QFileInfo(directory).absolutePath();
            if (parent == directory) break;
            directory = parent;
        }
        if (QDir(directory).exists()) paths << directory;
    }
    for (const auto& file : QStringList{path + "/97Launch/Setup.ini", qEnvironmentVariable("LOCALAPPDATA") + "/97LaunchQt/settings.json"})
        if (QFileInfo::exists(file)) paths << file;
    paths.removeDuplicates();
    if (!paths.isEmpty()) m_watch.addPaths(paths);
}
bool InstanceJobs::knownPath(const QString& path) const {
    const auto root = QFileInfo(m_settings->gameDirectory()).canonicalFilePath();
    const auto info = QFileInfo(path); const auto real = info.canonicalFilePath();
    if (root.isEmpty() || real.isEmpty() || info.isSymLink()) return false;
    return real.compare(root, Qt::CaseInsensitive) == 0 || real.startsWith(root + '/', Qt::CaseInsensitive);
}
bool InstanceJobs::openPath(const QString& path, bool select) {
    if (!knownPath(path)) return fail("文件不属于当前游戏目录。");
    if (select) return QProcess::startDetached("explorer.exe", {"/select,", QDir::toNativeSeparators(path)});
    const auto info = QFileInfo(path);
    if (!info.isDir() && !QStringList{"png", "jpg", "jpeg", "bmp"}.contains(info.suffix().toLower())) return fail("请通过资源管理器查看此文件。");
    return QDesktopServices::openUrl(QUrl::fromLocalFile(path));
}
bool InstanceJobs::copyImage(const QString& path) {
    if (!knownPath(path)) return fail("图片不属于当前游戏目录。");
    QImageReader reader(path); const auto size = reader.size();
    if (!size.isValid() || qint64(size.width()) * size.height() > 50'000'000) return fail("图片尺寸过大或无法读取。");
    const auto image = reader.read(); if (image.isNull()) return fail("无法读取图片。");
    QGuiApplication::clipboard()->setImage(image); return true;
}
QVariantMap InstanceJobs::readConfig(const QUrl& file) {
    QFile input(file.toLocalFile()); if (!input.open(QIODevice::ReadOnly) || input.size() > 1024 * 1024) { fail("无法读取导出配置。"); return {}; }
    QJsonParseError error; const auto json = QJsonDocument::fromJson(input.readAll(), &error);
    if (error.error != QJsonParseError::NoError || !json.isObject()) { fail("导出配置格式无效。"); return {}; }
    QVariantMap result;
    const auto object = json.object();
    for (auto it = object.begin(); it != object.end(); ++it) {
        auto key = it.key(); if (!key.isEmpty()) key[0] = key[0].toLower();
        result[key] = it.value().toVariant();
    }
    return result;
}
bool InstanceJobs::writeConfig(const QUrl& file, const QVariantMap& config) {
    QVariantMap compatible;
    for (auto it = config.begin(); it != config.end(); ++it) { auto key = it.key(); if (!key.isEmpty()) key[0] = key[0].toUpper(); compatible[key] = it.value(); }
    QSaveFile output(file.toLocalFile()); const auto bytes = QJsonDocument(QJsonObject::fromVariantMap(compatible)).toJson();
    return (output.open(QIODevice::WriteOnly) && output.write(bytes) == bytes.size() && output.commit()) || fail("无法保存导出配置。");
}
