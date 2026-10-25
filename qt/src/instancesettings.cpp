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
#include <QSaveFile>
#include <QUrl>
#include <algorithm>
#include <cmath>
#ifdef Q_OS_WIN
#include <windows.h>
#endif

namespace {
const QStringList launchKeys{"VersionIsolation", "WindowTitle", "ProcessPriority", "IpPreference", "WindowSize", "WindowWidth", "WindowHeight"};
const QStringList memoryKeys{"IsAutoMemory", "CustomMemoryGb"};
const QStringList advancedKeys{"JvmArgsHead", "GameArgsTail", "PreLaunchCommand", "RequireHighPerformanceGpu", "UseJavaExe"};
const QStringList flags{"UseGlobalLaunchOptions", "UseGlobalMemory", "UseGlobalAdvancedOptions"};
const QList<QStringList> groups{launchKeys, memoryKeys, advancedKeys};
QVariantMap jsonObject(const QString& path) {
    QFile file(path);
    return file.open(QIODevice::ReadOnly) && file.size() < 4 * 1024 * 1024 ? QJsonDocument::fromJson(file.readAll()).object().toVariantMap() : QVariantMap{};
}
QVariant parse(const QString& text, const QVariant& fallback) {
    if (fallback.metaType().id() == QMetaType::Bool) {
        if (text.compare("true", Qt::CaseInsensitive) == 0) return true;
        if (text.compare("false", Qt::CaseInsensitive) == 0) return false;
        return fallback;
    }
    if (fallback.metaType().id() == QMetaType::Double) {
        bool ok; const auto number = text.toDouble(&ok);
        return ok && std::isfinite(number) ? number : fallback;
    }
    return text;
}
QString serialized(const QVariant& value) {
    if (value.metaType().id() == QMetaType::Bool) return value.toBool() ? "True" : "False";
    return value.toString();
}
}

QVariantMap InstanceSettings::defaults() {
    QVariantMap values{{"UseGlobalLaunchOptions", true}, {"UseGlobalMemory", true}, {"UseGlobalAdvancedOptions", true},
        {"VersionIsolation", "隔离所有版本"}, {"WindowTitle", ""}, {"ProcessPriority", "中（平衡）"}, {"IpPreference", "IPv4 优先"},
        {"WindowSize", "默认"}, {"WindowWidth", "854"}, {"WindowHeight", "480"}, {"IsAutoMemory", true}, {"CustomMemoryGb", 3.0},
        {"JvmArgsHead", "-XX:-OmitStackTraceInFastThrow -Djdk.lang.Process.allowAmbiguousCommands=True -Dfml.ignoreInvalidMinecraftCertificates=True -Dfml.ignorePatchDiscrepancies=True"},
        {"GameArgsTail", ""}, {"PreLaunchCommand", ""}, {"RequireHighPerformanceGpu", true}, {"UseJavaExe", false},
        {"JavaMode", "自动选择并按需下载"}, {"JavaPath", ""}, {"MaxMemoryGb", 16.0}};
    const auto local = qEnvironmentVariable("LOCALAPPDATA");
    for (const auto& path : {local + "/97Launch/settings.json", local + "/97LaunchQt/settings.json"}) {
        const auto settings = jsonObject(path);
        for (auto it = settings.begin(); it != settings.end(); ++it)
            if (values.contains(it.key()) && !flags.contains(it.key()) && it.key() != "JavaMode" && it.key() != "JavaPath")
                values[it.key()] = parse(serialized(it.value()), values[it.key()]);
    }
    return values;
}
QVariantMap InstanceSettings::read(const QString& instance, const QVariantMap& global) {
    auto values = global;
    QFile file(instance + "/97Launch/Setup.ini");
    if (file.open(QIODevice::ReadOnly) && file.size() <= 1024 * 1024) {
        const auto lines = QString::fromUtf8(file.readAll()).remove(QChar(0xfeff)).split('\n');
        for (auto line : lines) {
            if (line.endsWith('\r')) line.chop(1);
            const auto split = line.indexOf('=');
            const auto key = line.left(split).trimmed();
            if (split > 0 && values.contains(key) && key != "MaxMemoryGb") values[key] = parse(line.mid(split + 1), values[key]);
        }
    }
    for (qsizetype i = 0; i < flags.size(); ++i) if (values[flags[i]].toBool())
        for (const auto& key : groups[i]) values[key] = global[key];
    return values;
}
QString InstanceSettings::resolveDirectory(const QVariantMap& instance, const QVariantMap& options) {
    const auto path = instance.value("path").toString();
    if (path.isEmpty()) return {};
    const auto mode = options.value("VersionIsolation").toString();
    bool isolated = mode != "关闭";
    if (isolated && !QDir(path + "/mods").exists() && !QDir(path + "/saves").exists()) {
        const bool moddable = instance.value("moddable").toBool();
        const bool release = instance.value("releaseType").toString() == "release";
        if (mode == "隔离可安装 Mod 的版本") isolated = moddable;
        else if (mode == "隔离非正式版") isolated = !release;
        else if (mode == "隔离可安装 Mod 的版本与非正式版") isolated = moddable || !release;
    }
    return isolated ? path : instance.value("root").toString();
}
InstanceSettings::InstanceSettings(Instances* instances, QObject* parent) : QObject(parent), m_instances(instances) {
    m_timer.setInterval(2000);
    connect(&m_timer, &QTimer::timeout, this, &InstanceSettings::updateMemory);
}
void InstanceSettings::attachJavaJobs(InstanceJobs* jobs) {
    m_javaJobs = jobs;
    connect(jobs, &InstanceJobs::completed, this, [this](const QString& action, const QVariantMap& result, const QVariantMap& args, const QString& path) {
        if (action != "settingsJava" || args.value("epoch").toInt() != m_javaEpoch || path != m_instance.value("path").toString()) return;
        m_java = result.value("runtimes").toList();
        for (auto& value : m_java) {
            auto row = value.toMap();
            row["name"] = row.value("name").toString() + " · " + row.value("architecture").toString() + " · " + row.value("path").toString();
            value = row;
        }
        const auto selected = m_values.value("JavaPath").toString();
        if (!selected.isEmpty() && std::none_of(m_java.cbegin(), m_java.cend(), [&](const QVariant& row) { return QDir::cleanPath(row.toMap().value("path").toString()).compare(QDir::cleanPath(selected), Qt::CaseInsensitive) == 0; }))
            m_java.prepend(QVariantMap{{"name", "当前指定（未检测到可用运行时） · " + selected}, {"path", selected}});
        emit javaChanged();
    });
    connect(jobs, &InstanceJobs::failedDetailed, this, [this](const QString& action, const QString& error, const QVariantMap& args, const QString& path) {
        if (action == "settingsJava" && args.value("epoch").toInt() == m_javaEpoch && path == m_instance.value("path").toString()) fail("Java 检索失败：" + error);
    });
}
bool InstanceSettings::fail(const QString& error) { m_error = error; emit changed(); return false; }
void InstanceSettings::clearError() { if (!m_error.isEmpty()) { m_error.clear(); emit changed(); } }
QString InstanceSettings::gameDirectory() const { return resolveDirectory(m_instance, m_values); }
bool InstanceSettings::open(const QString& path) {
    const auto row = m_instances->details(path);
    if (row.isEmpty()) return fail("实例已不存在或版本信息无效，请刷新实例列表。");
    QFile source(path + "/97Launch/Setup.ini");
    if (source.exists() && (!source.open(QIODevice::ReadOnly) || source.size() > 1024 * 1024))
        return fail("无法读取实例设置，已停止修改以保护原文件。");
    m_instance = row;
    m_defaults = defaults();
    m_values = read(path, m_defaults);
    m_dirty = false;
    const auto usage = jsonObject(path + "/97Launch/Usage.json");
    m_instance["launchCount"] = usage.value("LaunchCount", 0);
    m_error.clear();
    emit changed();
    updateMemory(); m_timer.start(); discoverJava();
    return true;
}
void InstanceSettings::close() { m_timer.stop(); ++m_javaEpoch; if (m_javaJobs) m_javaJobs->cancelRead("settingsJava"); m_instance.clear(); m_values.clear(); m_error.clear(); if (m_dirty) { m_dirty = false; m_instances->refresh(); } emit changed(); }
void InstanceSettings::setMemoryPreviewActive(bool active) { if (active && !m_instance.isEmpty()) { updateMemory(); m_timer.start(); } else m_timer.stop(); }
bool InstanceSettings::save(const QVariantMap& values) {
    if (m_instances->details(m_instance.value("path").toString()).isEmpty()) return fail("实例目录已改变，请重新进入设置。");
    const auto directory = m_instance.value("path").toString() + "/97Launch";
    if (!QDir().mkpath(directory)) return fail("无法创建实例设置目录。");
    const auto path = directory + "/Setup.ini";
    QStringList lines; QSet<QString> written;
    QFile source(path);
    if (source.exists()) {
        if (!source.open(QIODevice::ReadOnly) || source.size() > 1024 * 1024) return fail("无法读取原设置，未覆盖文件。");
        lines = QString::fromUtf8(source.readAll()).remove(QChar(0xfeff)).split('\n');
        // Windows cannot replace the file while this read handle is open.
        source.close();
    }
    for (auto& line : lines) {
        if (line.endsWith('\r')) line.chop(1);
        const auto split = line.indexOf('='); const auto key = line.left(split).trimmed();
        if (split > 0 && values.contains(key) && key != "MaxMemoryGb") {
            line = key + "=" + serialized(values[key]); written.insert(key);
        }
    }
    for (auto it = values.begin(); it != values.end(); ++it)
        if (it.key() != "MaxMemoryGb" && !written.contains(it.key())) lines << it.key() + "=" + serialized(it.value());
    QSaveFile target(path);
    const auto bytes = lines.join('\n').toUtf8();
    if (!target.open(QIODevice::WriteOnly) || target.write(bytes) != bytes.size() || !target.commit()) return fail("无法保存设置：" + target.errorString());
    return true;
}
bool InstanceSettings::setOption(const QString& key, const QVariant& value) {
    if (!m_values.contains(key) || key == "MaxMemoryGb") return fail("未知设置项。");
    if (value.toString().contains('\n') || value.toString().contains('\r')) return fail("此设置不能包含换行。");
    auto next = m_values; next[key] = parse(serialized(value), m_values[key]);
    const QMap<QString, QStringList> choices{
        {"VersionIsolation", {"关闭", "隔离可安装 Mod 的版本", "隔离非正式版", "隔离可安装 Mod 的版本与非正式版", "隔离所有版本"}},
        {"ProcessPriority", {"高（优先保证游戏运行）", "中（平衡）", "低（优先保证其他程序运行）"}},
        {"IpPreference", {"IPv4 优先", "IPv6 优先", "系统默认"}}, {"WindowSize", {"默认", "854 × 480", "1280 × 720", "1920 × 1080"}},
        {"JavaMode", {"自动选择并按需下载", "使用实例文件夹中的 Java", "强制指定 Java"}}};
    if (choices.contains(key) && !choices[key].contains(next[key].toString())) return fail("不支持的设置值。");
    if (key == "CustomMemoryGb") next[key] = std::clamp(next[key].toDouble(), .5, std::max(.5, m_defaults.value("MaxMemoryGb").toDouble()));
    for (qsizetype i = 0; i < flags.size(); ++i) {
        if (groups[i].contains(key)) next[flags[i]] = false;
        if (key == flags[i] && next[key].toBool()) for (const auto& field : groups[i]) next[field] = m_defaults[field];
    }
    if (key == "WindowSize" && next[key].toString() != "默认") {
        const auto sizes = next[key].toString().split(" × "); next["WindowWidth"] = sizes[0]; next["WindowHeight"] = sizes[1];
    }
    if (!save(next)) return false;
    m_values = next; m_dirty = true; m_error.clear(); emit changed(); emit settingsSaved(); updateMemory();
    return true;
}
bool InstanceSettings::addJava(const QUrl& file) {
    const auto path = QFileInfo(file.toLocalFile()).canonicalFilePath();
    if (path.isEmpty() || !QStringList{"java.exe", "javaw.exe"}.contains(QFileInfo(path).fileName().toLower())) return fail("请选择 java.exe 或 javaw.exe。");
    if (!setOption("JavaPath", path) || !setOption("JavaMode", "强制指定 Java")) return false;
    discoverJava(); return true;
}
void InstanceSettings::discoverJava() {
    if (!m_javaJobs || m_instance.isEmpty()) return;
    m_javaJobs->cancelRead("settingsJava");
    m_javaJobs->settings("settingsJava", {{"selectedPath", m_values.value("JavaPath")}, {"epoch", ++m_javaEpoch}}, m_instance.value("path").toString());
}
bool InstanceSettings::openDirectory(const QString& kind) {
    const QSet<QString> kinds{"", "saves", "screenshots", "mods", "resourcepacks", "shaderpacks"};
    if (!kinds.contains(kind) || m_instances->details(m_instance.value("path").toString()).isEmpty()) return fail("实例目录无效。");
    const auto directory = kind.isEmpty() ? m_instance.value("path").toString() : gameDirectory() + "/" + kind;
    if (!QDir().mkpath(directory)) return fail("无法创建目录。");
    return QDesktopServices::openUrl(QUrl::fromLocalFile(directory)) || fail("无法打开目录。");
}
QVariantMap InstanceSettings::memoryPlan(const QVariantMap& instance, const QVariantMap& values) {
    double available = 0, total = 0, commit = 0;
#ifdef Q_OS_WIN
    MEMORYSTATUSEX status{}; status.dwLength = sizeof(status);
    if (!GlobalMemoryStatusEx(&status)) return {};
    constexpr double gb = 1024.0 * 1024 * 1024;
    available = status.ullAvailPhys / gb; total = status.ullTotalPhys / gb; commit = status.ullAvailPageFile / gb;
#endif
    const bool modded = instance.value("moddable").toBool();
    const int mods = modded ? QDir(resolveDirectory(instance, values) + "/mods").entryList({"*.jar", "*.zip", "*.litemod"}, QDir::Files | QDir::NoSymLinks).size() : 0;
    const bool optifine = instance.value("loader").toString().contains("OptiFine");
    const double min = modded ? .5 + mods / 150.0 : .5;
    const double t1 = modded ? 1.5 + mods / 90.0 : 1.5;
    const double t2 = modded ? 2.7 + mods / 50.0 : optifine ? 3 : 2.5;
    const double t3 = modded ? 4.5 + mods / 25.0 : optifine ? 5 : 4;
    double give = values.value("CustomMemoryGb").toDouble();
    if (values.value("IsAutoMemory").toBool()) {
        auto remaining = std::round(available * 10) / 10; give = 0;
        for (const auto& tier : {QPair<double, double>{t1, 1}, {t2 - t1, .7}, {t3 - t2, .4}, {t3, .15}}) {
            give += std::min(remaining * tier.second, tier.first); remaining -= tier.first / tier.second;
            if (remaining < .1) break;
        }
        give = std::round(std::max(give, min) * 10) / 10;
    }
    give = std::clamp(give, .5, std::max(.5, values.value("MaxMemoryGb").toDouble()));
    return {{"total", total}, {"available", available}, {"allocated", give}, {"max", std::max(.5, values.value("MaxMemoryGb").toDouble())}, {"mods", mods}, {"low", give + .5 > available || give < min || give + 1 > commit}};
}

void InstanceSettings::updateMemory() {
    if(m_instance.isEmpty()) return;
    m_memory = memoryPlan(m_instance, m_values);
    emit memoryChanged();
}
