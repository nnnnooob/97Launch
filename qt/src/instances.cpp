#include "instances.h"
#include "apppaths.h"
#include "instancesettings.h"
#include <QCoreApplication>
#include <QCryptographicHash>
#include <QDateTime>
#include <QDesktopServices>
#include <QDir>
#include <QFile>
#include <QFileInfo>
#include <QJsonArray>
#include <QJsonDocument>
#include <QRegularExpression>
#include <QSaveFile>
#include <QTemporaryFile>
#include <QTimer>
#include <QtConcurrent/QtConcurrentRun>
#include <private/qzipreader_p.h>
#include <algorithm>
#include <stdexcept>

namespace {
QString pathOf(const QString& path) {
    const QFileInfo info(path);
    const auto canonical = info.canonicalFilePath();
    return QDir::cleanPath(canonical.isEmpty() ? info.absoluteFilePath() : canonical);
}
QString pathKey(const QString& path) { return QDir::cleanPath(QDir::fromNativeSeparators(path)).toCaseFolded(); }
bool samePath(const QString& a, const QString& b) { return !a.isEmpty() && !b.isEmpty() && pathKey(pathOf(a)) == pathKey(pathOf(b)); }
bool safeName(const QString& name) {
    return !name.isEmpty() && name != "." && name != ".." && !name.contains(QRegularExpression(R"([<>:"/\\|?*\x00-\x1f])"));
}
QJsonDocument readJson(const QString& path, qint64 limit = 16 * 1024 * 1024) {
    QFile file(path);
    if (!file.open(QIODevice::ReadOnly)) throw std::runtime_error("无法读取文件");
    if (file.size() > limit) throw std::runtime_error("JSON 文件过大");
    QJsonParseError error;
    const auto json = QJsonDocument::fromJson(file.readAll(), &error);
    if (error.error != QJsonParseError::NoError) throw std::runtime_error("JSON 格式错误");
    return json;
}
QJsonObject optionalObject(const QString& path) { try { return readJson(path).object(); } catch (...) { return {}; } }
QJsonObject merge(QJsonObject parent, const QJsonObject& child) {
    for (auto it = child.begin(); it != child.end(); ++it) {
        const auto old = parent.value(it.key());
        if (it->isObject() && old.isObject()) parent[it.key()] = merge(old.toObject(), it->toObject());
        else if (it->isArray() && old.isArray()) {
            auto array = it.key() == "libraries" ? it->toArray() : old.toArray();
            const auto rest = it.key() == "libraries" ? old.toArray() : it->toArray();
            for (const auto& value : rest) array.append(value);
            parent[it.key()] = array;
        } else if (!it->isNull()) parent[it.key()] = *it;
    }
    return parent;
}
QJsonObject profile(const QString& dir) {
    const auto name = QFileInfo(dir).fileName();
    auto file = QDir(dir).filePath(name + ".json");
    if (!QFile::exists(file)) {
        for (const auto& candidate : QDir(dir).entryInfoList({"*.json"}, QDir::Files | QDir::NoSymLinks)) {
            const auto json = optionalObject(candidate.absoluteFilePath());
            if (json.contains("mainClass") && json.contains("type") && json.contains("id")) { file = candidate.absoluteFilePath(); break; }
        }
    }
    auto json = readJson(file).object();
    if (json.isEmpty()) throw std::runtime_error("版本 JSON 必须为对象");
    if (json.value("patches").isArray() && !json.contains("time")) {
        auto patches = json.value("patches").toArray().toVariantList();
        std::stable_sort(patches.begin(), patches.end(), [](const QVariant& a, const QVariant& b) {
            return a.toMap().value("priority").toInt() < b.toMap().value("priority").toInt();
        });
        QJsonObject combined;
        QString game;
        for (const auto& patch : patches) {
            const auto object = QJsonObject::fromVariantMap(patch.toMap());
            if (object.value("id").toString() == "game") game = object.value("version").toString();
            if (object.contains("id")) combined = merge(combined, object);
        }
        json = combined; json["id"] = name; json.remove("inheritsFrom");
        if (!game.isEmpty()) json["clientVersion"] = game;
    }
    if (json.value("inheritsFrom").toString().compare(name, Qt::CaseInsensitive) == 0) json.remove("inheritsFrom");
    if (!json.contains("inheritsFrom") && !json.contains("logging")
        && QJsonDocument(json.value("libraries").toArray()).toJson().contains("liteloader")) {
        auto candidate = json.value("jar").toString();
        if (candidate.isEmpty()) candidate = QRegularExpression(R"((?:1|[2-9][0-9])\.[0-9]+(?:\.[0-9]+)?)").match(name).captured();
        if (!candidate.isEmpty() && candidate.compare(name, Qt::CaseInsensitive)) json["inheritsFrom"] = candidate;
    }
    return json;
}
QString argument(const QJsonObject& json, const QString& key) {
    const auto args = json.value("arguments").toObject().value("game").toArray();
    for (qsizetype i = 0; i + 1 < args.size(); ++i) if (args[i].toString() == key) return args[i + 1].toString();
    return {};
}
bool marker(const QString& dir, const QString& name) {
    QFile file(QDir(dir).filePath("PCL/Setup.ini"));
    if (!file.open(QIODevice::ReadOnly) || file.size() > 1024 * 1024) return false;
    const auto match = QRegularExpression("(?:^|[\r\n])\\s*" + name + R"(\s*:\s*(true|1)\s*(?:$|[\r\n]))", QRegularExpression::CaseInsensitiveOption)
        .match(QString::fromUtf8(file.readAll()));
    return match.hasMatch();
}
const QSet<QString> fools{"2.0", "1.rv-pre1", "3d shareware v1.34", "20w14infinite", "20w14∞", "22w13oneblockatatime", "23w13a_or_b", "24w14potato", "25w14craftmine", "15w14a", "26w14a"};
QVariantMap readInstance(const QString& dir, const QString& root) {
    const auto name = QFileInfo(dir).fileName();
    auto normalized = QDir::toNativeSeparators(pathOf(dir)).toUpper();
    const auto id = QString::fromLatin1(QCryptographicHash::hash(normalized.toUtf8(), QCryptographicHash::Sha256).left(16).toHex());
    QVariantMap row{{"id", id}, {"name", name}, {"path", pathOf(dir)}, {"root", root}, {"error", ""},
        {"kind", "vanilla"}, {"version", ""}, {"loader", ""}, {"loaderVersion", ""}, {"group", "regular"},
        {"releaseTime", qint64(0)}, {"star", marker(dir, "IsStar")}};
    try {
        auto json = profile(dir);
        const auto inherit = json.value("inheritsFrom").toString();
        auto parent = inherit;
        QList<QJsonObject> chain{json};
        QSet<QString> seen{name.toCaseFolded()};
        while (!parent.isEmpty()) {
            if (!safeName(parent) || chain.size() >= 16 || seen.contains(parent.toCaseFolded())) throw std::runtime_error("前置版本名称无效或依赖循环");
            seen.insert(parent.toCaseFolded());
            const auto parentPath = QDir(root).filePath("versions/" + parent);
            if (!samePath(QFileInfo(pathOf(parentPath)).absolutePath(), QDir(root).filePath("versions"))) throw std::runtime_error("前置版本路径无效");
            chain.append(profile(parentPath));
            parent = chain.last().value("inheritsFrom").toString();
        }
        QJsonObject merged;
        for (auto it = chain.crbegin(); it != chain.crend(); ++it) merged = merge(merged, *it);
        if (merged.value("mainClass").toString().isEmpty()) throw std::runtime_error("版本 JSON 中没有 mainClass 项");
        const auto manifest = optionalObject(QDir(dir).filePath("97Launch/InstallManifest.json"));
        QString version = inherit.isEmpty() ? manifest.value("minecraftVersion").toString() : QString();
        if (version.isEmpty()) version = merged.value("clientVersion").toString();
        if (version.isEmpty()) version = argument(merged, "--fml.mcVersion");
        if (version.isEmpty() && !inherit.isEmpty()) version = merged.value("jar").toString(inherit);
        QString fabric, forge, neo, optifine, lite;
        QString intermediary;
        for (const auto& entry : merged.value("libraries").toArray()) {
            const auto coord = entry.toObject().value("name").toString().split(':');
            if (coord.size() < 3) continue;
            if ((coord[0] == "net.fabricmc" && coord[1] == "fabric-loader") || (coord[0] == "org.quiltmc" && coord[1] == "quilt-loader")) fabric = fabric.isEmpty() ? coord[2] : fabric;
            if (coord[0] == "net.minecraftforge" && (coord[1] == "forge" || coord[1] == "fmlloader" || coord[1] == "minecraftforge")) forge = forge.isEmpty() ? coord[2].section('-', -1) : forge;
            if (coord[0] == "net.neoforged" && (coord[1] == "neoforge" || coord[1] == "forge")) neo = neo.isEmpty() ? coord[2] : neo;
            if (coord[0].contains("optifine", Qt::CaseInsensitive)) optifine = coord[2];
            if (coord[1].contains("liteloader", Qt::CaseInsensitive)) lite = coord[2];
            if (coord[1] == "intermediary") intermediary = coord[2];
        }
        if (version.isEmpty()) version = intermediary;
        if (version.isEmpty()) version = merged.value("jar").toString();
        if (version.isEmpty()) {
            const QRegularExpression pattern(R"((?:\d{2}w\d{2}[a-z]|(?:1|[2-9][0-9])\.[0-9]+(?:\.[0-9]+)?(?:-(?:pre|rc|snapshot-?)[0-9]*)?))", QRegularExpression::CaseInsensitiveOption);
            for (const auto& candidate : {merged.value("id").toString(), name}) {
                if (fools.contains(candidate.toCaseFolded()) || QRegularExpression("^[abc]\\d", QRegularExpression::CaseInsensitiveOption).match(candidate).hasMatch()) { version = candidate; break; }
                version = pattern.match(candidate).captured();
                if (!version.isEmpty()) break;
            }
        }
        if (version.isEmpty()) {
            QZipReader jar(QDir(dir).filePath(name + ".jar"));
            for (const auto& entry : jar.fileInfoList()) if (entry.filePath == "version.json" && entry.size <= 1024 * 1024) {
                version = QJsonDocument::fromJson(jar.fileData(entry.filePath)).object().value("name").toString(); break;
            }
        }
        if (version.isEmpty() || version == "Unknown") throw std::runtime_error("无法识别 Minecraft 版本");
        if (!argument(merged, "--fml.forgeVersion").isEmpty()) forge = argument(merged, "--fml.forgeVersion");
        if (!argument(merged, "--fml.neoForgeVersion").isEmpty()) neo = argument(merged, "--fml.neoForgeVersion");
        const auto mainClass = merged.value("mainClass").toString();
        if (neo.isEmpty() && mainClass.contains("neoforged", Qt::CaseInsensitive)) neo = QStringLiteral("未知版本");
        if (forge.isEmpty() && mainClass.contains("minecraftforge", Qt::CaseInsensitive)) forge = QStringLiteral("未知版本");
        if (fabric.isEmpty() && manifest.value("loader").toString() == "Fabric") fabric = manifest.value("loaderVersion").toString();
        if (forge.isEmpty() && manifest.value("loader").toString() == "Forge") forge = manifest.value("loaderVersion").toString();
        if (neo.isEmpty() && manifest.value("loader").toString() == "NeoForge") neo = manifest.value("loaderVersion").toString();
        if (optifine.isEmpty()) optifine = manifest.value("optiFineVersion").toString();
        const auto release = QDateTime::fromString(chain.last().value("releaseTime").toString(), Qt::ISODate);
        const auto type = merged.value("type").toString();
        QString kind = "vanilla", loader;
        QString group = "regular";
        if (QRegularExpression("^[abc]\\d", QRegularExpression::CaseInsensitiveOption).match(version).hasMatch() || type.startsWith("old_") || version == "Old") { kind = "cobblestone"; group = "rare"; }
        else if (fools.contains(version.toCaseFolded()) || type == "fool" || (type == "snapshot" && release.addSecs(7200).date().month() == 4 && release.addSecs(7200).date().day() == 1)) { kind = "fools"; group = "rare"; }
        else if (type == "snapshot" || type == "pending" || QRegularExpression(R"(^\d{2}w|-(pre|rc)\d+$|-snapshot)", QRegularExpression::CaseInsensitiveOption).match(version).hasMatch()) { kind = "snapshot"; group = "rare"; }
        if (!optifine.isEmpty()) { kind = "grasspath"; loader = "OptiFine " + optifine; }
        if (!lite.isEmpty()) { kind = "egg"; loader = "LiteLoader " + lite; group = "api"; }
        if (!fabric.isEmpty()) { kind = "fabric"; loader = "Fabric " + fabric; group = "api"; }
        else if (!neo.isEmpty()) { kind = "neoforge"; loader = "NeoForge " + neo; group = "api"; }
        else if (!forge.isEmpty()) { kind = "forge"; loader = "Forge " + forge; group = "api"; }
        if (row.value("star").toBool()) group = "api";
        row["version"] = version; row["kind"] = kind; row["loader"] = loader;
        row["loaderVersion"] = !fabric.isEmpty() ? fabric : !neo.isEmpty() ? neo : !forge.isEmpty() ? forge : optifine;
        row["group"] = group; row["releaseTime"] = release.toSecsSinceEpoch();
        row["releaseType"] = type;
        row["loaderId"] = !fabric.isEmpty() ? "Fabric" : !neo.isEmpty() ? "NeoForge" : !forge.isEmpty() ? "Forge" : !lite.isEmpty() ? "LiteLoader" : !optifine.isEmpty() ? "OptiFine" : "";
        row["moddable"] = !fabric.isEmpty() || !forge.isEmpty() || !neo.isEmpty() || !lite.isEmpty();
        row["isolated"] = samePath(InstanceSettings::resolveDirectory(row, InstanceSettings::read(dir, InstanceSettings::defaults())), dir);
        row["lastPlayed"] = optionalObject(QDir(dir).filePath("97Launch/Usage.json")).value("LastPlayed").toString();
    } catch (const std::exception& error) {
        row["error"] = QString::fromUtf8(error.what()); row["group"] = "error"; row["kind"] = "redstone";
    }
    row["recovery"] = QFile::exists(QDir(dir).filePath("97Launch/Modification.json"));
    row["icon"] = "qrc:/art/blocks/" + row.value("kind").toString() + ".png";
    return row;
}
QVariantList scanRoot(const QString& root) {
    QVariantList rows;
    const QDir versions(QDir(root).filePath("versions"));
    for (const auto& dir : versions.entryInfoList(QDir::Dirs | QDir::NoDotAndDotDot | QDir::NoSymLinks, QDir::Name)) {
        const QDir directory(dir.absoluteFilePath());
        if (directory.exists(".97launch-installing") || directory.exists(".pclignore")) continue;
        if (directory.entryList(QDir::Files | QDir::Hidden | QDir::NoSymLinks).isEmpty()) continue;
        if ((dir.fileName() == "cache" || dir.fileName() == "PCL" || dir.fileName() == "BLClient") && !directory.exists(dir.fileName() + ".json")) continue;
        rows.append(readInstance(dir.absoluteFilePath(), root));
    }
    std::stable_sort(rows.begin(), rows.end(), [](const QVariant& a, const QVariant& b) {
        const auto x = a.toMap(), y = b.toMap();
        if (x.value("star") != y.value("star")) return x.value("star").toBool();
        if (x.value("releaseTime") != y.value("releaseTime")) return x.value("releaseTime").toLongLong() > y.value("releaseTime").toLongLong();
        return x.value("name").toString().compare(y.value("name").toString(), Qt::CaseInsensitive) > 0;
    });
    QSet<QString> represented;
    QSet<QString> optifineVersions;
    for (const auto& value : rows) {
        const auto row = value.toMap();
        if (row.value("kind") == "grasspath" && row.value("group") == "regular") optifineVersions.insert(row.value("version").toString());
    }
    bool newestSnapshot = true;
    for (auto& value : rows) {
        auto row = value.toMap();
        if (row.value("group") == "regular" && row.value("kind") == "vanilla" && optifineVersions.contains(row.value("version").toString())) row["group"] = "rare";
        if (row.value("group") == "regular") {
            const auto key = row.value("version").toString() + (row.value("kind") == "grasspath" ? ":opti" : ":vanilla");
            if (represented.contains(key)) row["group"] = "rare";
            else represented.insert(key);
        }
        if (row.value("group") != "api" && (row.value("kind") == "vanilla" || row.value("kind") == "snapshot")) {
            if (newestSnapshot && row.value("kind") == "snapshot" && row.value("group") == "rare") row["group"] = "regular";
            newestSnapshot = false;
        }
        value = row;
    }
    return rows;
}
QVariantMap folderRow(const QString& name, const QString& path, bool custom) {
    return {{"name", name}, {"path", pathOf(path)}, {"custom", custom}};
}
QString folderName(const QString& path) {
    QFileInfo info(path);
    const auto name = info.fileName() == ".minecraft" ? QFileInfo(info.absolutePath()).fileName() : info.fileName();
    return name.isEmpty() ? QStringLiteral("Minecraft") : name.left(40);
}
ModpackPreview inspectPack(const QString& path) {
    ModpackPreview result;
    QZipReader zip(path);
    if (!zip.isReadable()) { result.error = QStringLiteral("无法读取整合包，请选择有效的 .mrpack 或 .zip 文件。"); return result; }
    const auto entries = zip.fileInfoList();
    for (const auto& entry : entries) {
        if (entry.filePath != "modrinth.index.json" && entry.filePath != "manifest.json") continue;
        if (!entry.isFile || entry.isSymLink || entry.size < 0 || entry.size > 4 * 1024 * 1024) { result.error = QStringLiteral("整合包清单无效或过大。"); return result; }
        QJsonParseError error;
        const auto json = QJsonDocument::fromJson(zip.fileData(entry.filePath), &error).object();
        if (error.error != QJsonParseError::NoError || json.isEmpty()) { result.error = QStringLiteral("整合包清单格式错误。"); return result; }
        QString version, loader, format;
        if (entry.filePath == "modrinth.index.json") {
            if (json.value("game").toString() != "minecraft" || json.value("formatVersion").toInt() != 1) { result.error = QStringLiteral("不支持此整合包格式或游戏类型。"); return result; }
            const auto dependencies = json.value("dependencies").toObject();
            version = dependencies.value("minecraft").toString();
            for (const auto& key : {"fabric-loader", "quilt-loader", "neoforge", "forge"})
                if (dependencies.contains(key)) { loader = QString::fromLatin1(key) + " " + dependencies.value(key).toString(); break; }
            format = "Modrinth";
        } else {
            if (json.value("manifestType").toString() != "minecraftModpack") continue;
            const auto minecraft = json.value("minecraft").toObject();
            version = minecraft.value("version").toString();
            for (const auto& item : minecraft.value("modLoaders").toArray()) if (item.toObject().value("primary").toBool()) loader = item.toObject().value("id").toString();
            format = "CurseForge";
        }
        if (version.isEmpty()) { result.error = QStringLiteral("整合包中缺少 Minecraft 版本信息。"); return result; }
        result.info = {{"path", path}, {"name", json.value("name").toString(QFileInfo(path).completeBaseName())},
            {"version", version}, {"loader", loader}, {"format", format}, {"files", json.value("files").toArray().size()}};
        return result;
    }
    result.error = QStringLiteral("未找到 Modrinth 或 CurseForge 整合包清单，请选择 .mrpack 或包含标准清单的 .zip 文件。");
    return result;
}
}

Instances::Instances(QObject* parent) : QObject(parent) {
    m_directory = QDir(qEnvironmentVariable("LOCALAPPDATA", QDir::homePath() + "/AppData/Local")).filePath("97LaunchQt");
    m_file = m_directory + "/instances.json";
    m_builtin = pathOf(applicationRoot() + "/.minecraft");
    if (QFile::exists(m_file)) {
        try {
            const auto doc = readJson(m_file);
            if (!doc.isObject() || !doc.object().value("Folders").isArray()) throw std::runtime_error("文件夹配置格式错误");
            m_config = doc.object();
        } catch (...) { m_storeValid = false; m_error = QStringLiteral("实例配置无法读取，已保留原文件。请先备份并修复 instances.json。"); }
    } else {
        const auto old = QDir(qEnvironmentVariable("LOCALAPPDATA")).filePath("97Launch");
        QJsonArray folders;
        try { folders = readJson(old + "/gamefolders.json").array(); } catch (...) {}
        const auto settings = optionalObject(old + "/settings.json");
        const auto previousRoot = settings.value("SelectedGameFolderPath").toString();
        const auto legacyBuiltin = pathOf(applicationRoot() + "/../97Launch/.minecraft");
        if (QDir(legacyBuiltin).exists() && !samePath(legacyBuiltin, m_builtin)) {
            bool found = false;
            for (const auto& item : folders) if (samePath(item.toObject().value("Path").toString(), legacyBuiltin)) found = true;
            if (!found) folders.prepend(QJsonObject{{"Name", "97Launch"}, {"Path", legacyBuiltin}, {"IsUserAdded", true}});
        }
        m_config = {{"Folders", folders}, {"SelectedFolderPath", previousRoot}, {"SelectedInstancePath", settings.value("SelectedInstancePath")}};
        save(m_config);
    }
    if (!knownFolder(selectedFolder())) m_config["SelectedFolderPath"] = m_builtin;
    else m_config["SelectedFolderPath"] = pathOf(selectedFolder());
    const auto selected = m_config.value("SelectedInstancePath").toString();
    if (!selected.isEmpty()) m_config["SelectedInstancePath"] = pathOf(selected);
    connect(&m_scan, &QFutureWatcher<InstanceScan>::finished, this, [this] {
        m_loading = false;
        if (m_rescan) { m_rescan = false; refresh(); return; }
        InstanceScan result;
        try { result = m_scan.result(); }
        catch (...) { report(QStringLiteral("读取实例列表失败，请检查游戏目录后刷新。")); return; }
        m_rows = result.rows;
        if (!result.warnings.isEmpty()) m_error = result.warnings.join('\n');
        if (current().isEmpty()) {
            for (const auto& item : m_rows.value(pathKey(selectedFolder()))) {
                const auto row = item.toMap();
                if (row.value("error").toString().isEmpty()) { auto config = m_config; config["SelectedInstancePath"] = row.value("path").toString(); save(config); break; }
            }
        }
        emit entriesChanged(); emit changed();
    });
    connect(&m_pack, &QFutureWatcher<ModpackPreview>::finished, this, [this] {
        m_readingPack = false;
        ModpackPreview result;
        try { result = m_pack.result(); }
        catch (...) { report(QStringLiteral("读取整合包失败，请检查文件是否完整。")); return; }
        if (!result.error.isEmpty()) report(result.error);
        else { emit changed(); emit modpackReady(result.info); }
    });
    QTimer::singleShot(0, this, &Instances::refresh);
}
bool Instances::report(const QString& message) { m_error = message; emit changed(); return false; }
void Instances::clearError() { m_error.clear(); emit changed(); }
bool Instances::save(const QJsonObject& config) {
    if (!m_storeValid) return report(QStringLiteral("实例配置损坏，已阻止覆盖原文件。"));
    if (!QDir().mkpath(m_directory)) return report(QStringLiteral("无法创建实例配置目录。"));
    QSaveFile file(m_file);
    const auto data = QJsonDocument(config).toJson();
    if (!file.open(QIODevice::WriteOnly) || file.write(data) != data.size() || !file.commit()) return report(QStringLiteral("无法保存实例配置，原文件已保留。"));
    m_config = config; return true;
}
QVariantList Instances::available() const {
    QVariantList result;
    for (const auto& item : m_rows.value(pathKey(selectedFolder()))) if (item.toMap().value("error").toString().isEmpty()) result.append(item);
    return result;
}
QVariantList Instances::folders() const {
    QVariantList rows{folderRow(QStringLiteral("内置文件夹"), m_builtin, false)};
    QSet<QString> seen{pathKey(m_builtin)};
    for (const auto& item : m_config.value("Folders").toArray()) {
        const auto folder = item.toObject(); const auto path = folder.value("Path").toString();
        if (path.isEmpty() || seen.contains(pathKey(path))) continue;
        seen.insert(pathKey(path));
        rows.append(folderRow(folder.value("Name").toString(folderName(path)), path, true));
    }
    for (auto& item : rows) {
        auto row = item.toMap(); row["count"] = m_rows.value(pathKey(row.value("path").toString())).size();
        row["selected"] = samePath(row.value("path").toString(), selectedFolder());
        row["exists"] = QDir(row.value("path").toString()).exists(); item = row;
    }
    return rows;
}
bool Instances::knownFolder(const QString& path) const {
    if (samePath(path, m_builtin)) return true;
    for (const auto& item : m_config.value("Folders").toArray()) if (samePath(item.toObject().value("Path").toString(), path)) return true;
    return false;
}
QString Instances::selectedFolder() const { return m_config.value("SelectedFolderPath").toString(m_builtin); }
int Instances::count() const {
    int count = 0;
    for (const auto& item : m_rows.value(pathKey(selectedFolder()))) if (item.toMap().value("error").toString().isEmpty()) ++count;
    return count;
}
QVariantMap Instances::findInstance(const QString& path) const {
    if (path.isEmpty()) return {};
    const auto key = pathKey(path);
    for (const auto& rows : m_rows) for (const auto& item : rows) if (pathKey(item.toMap().value("path").toString()) == key) return item.toMap();
    return {};
}
QVariantMap Instances::current() const { return findInstance(m_config.value("SelectedInstancePath").toString()); }
QVariantMap Instances::details(const QString& path) const {
    const auto row = findInstance(path);
    if (row.isEmpty() || (!row.value("error").toString().isEmpty() && !row.value("recovery").toBool())) return {};
    const QFileInfo folder(path);
    if (!folder.isDir() || folder.isSymLink()
        || !samePath(folder.canonicalPath(), QDir(row.value("root").toString()).filePath("versions"))) return {};
    return row;
}
void Instances::instanceMoved(const QString& oldPath, const QString& newPath) {
    const auto old = findInstance(oldPath);
    if (old.isEmpty() || !samePath(QFileInfo(newPath).canonicalPath(), QDir(old.value("root").toString()).filePath("versions"))) return;
    if (samePath(m_config.value("SelectedInstancePath").toString(), oldPath)) {
        auto config = m_config; config["SelectedInstancePath"] = pathOf(newPath); save(config);
    }
}
void Instances::setQuery(const QString& query) { if (query == m_query) return; m_query = query; emit entriesChanged(); emit changed(); }
QVariantList Instances::entries() const {
    const QStringList keys{"api", "regular", "rare", "error"};
    const QStringList titles{QStringLiteral("可安装模组"), QStringLiteral("常规实例"), QStringLiteral("不常用实例"), QStringLiteral("错误实例")};
    QVariantList entries;
    const auto term = m_query.trimmed();
    for (int group = 0; group < keys.size(); ++group) {
        QVariantList matches;
        for (const auto& item : m_rows.value(pathKey(selectedFolder()))) {
            const auto row = item.toMap();
            if (row.value("group").toString() != keys[group]) continue;
            if (!term.isEmpty() && !(row.value("name").toString() + '\n' + row.value("version").toString() + '\n' + row.value("loader").toString()).contains(term, Qt::CaseInsensitive)) continue;
            matches.append(row);
        }
        if (matches.isEmpty()) continue;
        const bool expanded = !m_collapsed.contains(keys[group]) || !term.isEmpty();
        entries.append(QVariantMap{{"header", true}, {"key", keys[group]}, {"title", titles[group]}, {"count", matches.size()}, {"expanded", expanded}});
        for (int i = 0; i < matches.size(); ++i) {
            auto row = matches[i].toMap(); row["header"] = false; row["last"] = i == matches.size() - 1;
            row["offset"] = i * 54; entries.append(row);
        }
    }
    return entries;
}
void Instances::toggleGroup(const QString& key) { if (m_collapsed.contains(key)) m_collapsed.remove(key); else m_collapsed.insert(key); emit entriesChanged(); emit changed(); }
void Instances::refresh() {
    if (m_loading) { m_rescan = true; return; }
    m_loading = true;
    if (m_storeValid) m_error.clear();
    if (!QDir().mkpath(m_builtin + "/versions")) m_error = QStringLiteral("内置游戏目录无法创建，请在左侧添加可读写的文件夹。");
    const auto roots = folders();
    emit changed();
    m_scan.setFuture(QtConcurrent::run([roots] {
        InstanceScan result;
        for (const auto& item : roots) {
            const auto row = item.toMap(); const auto root = row.value("path").toString();
            if (!QDir(root).exists()) { result.rows.insert(pathKey(root), {}); continue; }
            result.rows.insert(pathKey(root), scanRoot(root));
        }
        return result;
    }));
}
void Instances::selectFolder(const QString& path) {
    if (!knownFolder(path) || samePath(path, selectedFolder())) return;
    auto config = m_config; config["SelectedFolderPath"] = pathOf(path);
    if (save(config)) { m_query.clear(); emit entriesChanged(); emit changed(); }
}
bool Instances::selectInstance(const QString& path) {
    const auto row = findInstance(path);
    if (row.isEmpty()) return report(QStringLiteral("实例已不存在，请刷新列表。"));
    if (!row.value("error").toString().isEmpty()) return report(row.value("error").toString());
    auto config = m_config; config["SelectedInstancePath"] = row.value("path").toString(); config["SelectedFolderPath"] = row.value("root").toString();
    if (!save(config)) return false;
    emit changed(); emit instanceSelected(); return true;
}
QVariantMap Instances::folderCandidate(const QUrl& url) {
    if (!url.isLocalFile()) { report(QStringLiteral("请选择本机游戏文件夹。")); return {}; }
    auto path = pathOf(url.toLocalFile());
    if (!QDir(path).exists()) { report(QStringLiteral("所选文件夹不存在。")); return {}; }
    if (QFileInfo(path).fileName() == "versions") path = QFileInfo(path).absolutePath();
    if (!QDir(QDir(path).filePath("versions")).exists()) {
        for (const auto& child : QDir(path).entryInfoList(QDir::Dirs | QDir::NoDotAndDotDot | QDir::Hidden | QDir::NoSymLinks))
            if (QDir(child.absoluteFilePath() + "/versions").exists()) { path = child.absoluteFilePath(); break; }
    }
    return folderRow(folderName(path), path, true);
}
bool Instances::addFolder(const QString& input, const QString& label) {
    const auto path = pathOf(input), name = label.trimmed();
    if (name.isEmpty() || name.size() > 40 || name.contains(QRegularExpression("[>|\\r\\n]"))) return report(QStringLiteral("显示名称应为 1–40 字，不能包含 > 或 |。"));
    if (path.contains('!') || path.contains(';') || !QDir(path).exists()) return report(QStringLiteral("请选择已有目录，游戏路径不能包含感叹号或分号。"));
    QTemporaryFile probe(QDir(path).filePath(".97launch-permission-XXXXXX"));
    if (!probe.open() || probe.write("1", 1) != 1 || !QDir().mkpath(QDir(path).filePath("versions"))) return report(QStringLiteral("没有访问该游戏目录的写入权限。"));
    if (samePath(path, m_builtin)) { selectFolder(path); return true; }
    auto config = m_config; auto list = config.value("Folders").toArray();
    bool found = false;
    for (qsizetype i = 0; i < list.size(); ++i) if (samePath(list[i].toObject().value("Path").toString(), path)) {
        auto row = list[i].toObject(); row["Name"] = name; list[i] = row; found = true; break;
    }
    if (!found) list.append(QJsonObject{{"Name", name}, {"Path", path}, {"IsUserAdded", true}});
    config["Folders"] = list; config["SelectedFolderPath"] = path;
    if (!save(config)) return false;
    refresh(); emit changed(); return true;
}
bool Instances::renameFolder(const QString& path, const QString& name) {
    if (!knownFolder(path) || samePath(path, m_builtin)) return report(QStringLiteral("内置文件夹不能重命名。"));
    const auto label = name.trimmed();
    if (label.isEmpty() || label.size() > 40 || label.contains(QRegularExpression("[>|\\r\\n]"))) return report(QStringLiteral("显示名称应为 1–40 字，不能包含 > 或 |。"));
    auto config = m_config; auto list = config.value("Folders").toArray();
    for (qsizetype i = 0; i < list.size(); ++i) if (samePath(list[i].toObject().value("Path").toString(), path)) {
        auto row = list[i].toObject(); row["Name"] = label; list[i] = row; break;
    }
    config["Folders"] = list;
    if (!save(config)) return false;
    emit changed(); return true;
}
bool Instances::removeFolder(const QString& path) {
    if (!knownFolder(path) || samePath(path, m_builtin)) return report(QStringLiteral("内置文件夹不能从列表移除。"));
    auto config = m_config; QJsonArray list;
    for (const auto& item : config.value("Folders").toArray()) if (!samePath(item.toObject().value("Path").toString(), path)) list.append(item);
    config["Folders"] = list;
    if (samePath(selectedFolder(), path)) config["SelectedFolderPath"] = m_builtin;
    if (samePath(current().value("root").toString(), path)) config["SelectedInstancePath"] = "";
    if (!save(config)) return false;
    m_rows.remove(pathKey(path));
    if (m_loading) m_rescan = true;
    emit entriesChanged(); emit changed(); return true;
}
bool Instances::openFolder(const QString& path) {
    if (!knownFolder(path) && findInstance(path).isEmpty()) return report(QStringLiteral("此目录不在实例列表中。"));
    if (!QDir(path).exists() || !QDesktopServices::openUrl(QUrl::fromLocalFile(path))) return report(QStringLiteral("无法打开该目录，请确认文件夹仍然存在。"));
    return true;
}
bool Instances::recycleInstance(const QString& path) {
    const auto row = findInstance(path);
    if (row.isEmpty()) return report(QStringLiteral("实例已不存在，请刷新列表。"));
    const auto versions = QDir(row.value("root").toString()).filePath("versions");
    if (QFileInfo(path).isSymLink() || !samePath(QFileInfo(pathOf(path)).absolutePath(), versions)
        || !QDir(path).exists()) return report(QStringLiteral("实例路径无效，已阻止删除。"));
    if (!QFile::moveToTrash(path)) return report(QStringLiteral("无法将实例移到回收站，请关闭正在使用该目录的程序后重试。"));
    if (samePath(m_config.value("SelectedInstancePath").toString(), path)) { auto config = m_config; config["SelectedInstancePath"] = ""; save(config); }
    refresh(); return true;
}
void Instances::inspectModpack(const QUrl& url) {
    if (m_readingPack) return;
    if (!url.isLocalFile() || !QFileInfo(url.toLocalFile()).isFile()) { report(QStringLiteral("请选择本机整合包文件。")); return; }
    if (!knownFolder(selectedFolder())) { report(QStringLiteral("请先选择游戏文件夹。")); return; }
    m_readingPack = true; m_error.clear(); emit changed();
    const auto path = url.toLocalFile();
    m_pack.setFuture(QtConcurrent::run([path] { return inspectPack(path); }));
}
