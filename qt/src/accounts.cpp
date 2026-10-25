#include "accounts.h"
#include "authbrowser.h"
#include <QBuffer>
#include <QCryptographicHash>
#include <QDateTime>
#include <QDesktopServices>
#include <QDir>
#include <QFile>
#include <QFileInfo>
#include <QGuiApplication>
#include <QImageReader>
#include <QJsonDocument>
#include <QNetworkProxyFactory>
#include <QNetworkReply>
#include <QPainter>
#include <QRegularExpression>
#include <QRandomGenerator>
#include <QSaveFile>
#include <QUrlQuery>
#include <QUuid>
#ifdef Q_OS_WIN
#include <windows.h>
#include <wincrypt.h>
#endif

namespace {
const QString defaultMicrosoftRedirect = QStringLiteral("https://login.microsoftonline.com/common/oauth2/nativeclient");
const QString microsoftTokenEndpoint = QStringLiteral("https://login.microsoftonline.com/consumers/oauth2/v2.0/token");
QString secureToken() {
    QByteArray bytes;
    bytes.reserve(32);
    for (int i = 0; i < 8; ++i) {
        const quint32 word = QRandomGenerator::system()->generate();
        bytes.append(reinterpret_cast<const char*>(&word), sizeof(word));
    }
    return QString::fromLatin1(bytes.toBase64(QByteArray::Base64UrlEncoding | QByteArray::OmitTrailingEquals));
}
QString freshId() { return QUuid::createUuid().toString(QUuid::Id128); }
QJsonObject object(const QByteArray& body) { return QJsonDocument::fromJson(body).object(); }
bool ok(int status) { return status >= 200 && status < 300; }
QString protectedValue(const QString& value, bool encrypt, bool* success = nullptr) {
    if (success) *success = true;
    if (value.isEmpty() || (!encrypt && !value.startsWith("enc:"))) return value;
#ifdef Q_OS_WIN
    auto bytes = encrypt ? value.toUtf8() : QByteArray::fromBase64(value.mid(4).toLatin1());
    DATA_BLOB input{DWORD(bytes.size()), reinterpret_cast<BYTE*>(bytes.data())}, output{};
    const bool worked = encrypt
        ? CryptProtectData(&input, nullptr, nullptr, nullptr, nullptr, CRYPTPROTECT_UI_FORBIDDEN, &output)
        : CryptUnprotectData(&input, nullptr, nullptr, nullptr, nullptr, CRYPTPROTECT_UI_FORBIDDEN, &output);
    if (worked) {
        const QByteArray result(reinterpret_cast<char*>(output.pbData), int(output.cbData));
        SecureZeroMemory(output.pbData, output.cbData);
        LocalFree(output.pbData);
        return encrypt ? "enc:" + QString::fromLatin1(result.toBase64()) : QString::fromUtf8(result);
    }
#endif
    if (success) *success = false;
    return {};
}
QString serviceError(int status, const QByteArray& body) {
    if (status == 0) return QStringLiteral("无法连接登录服务，请检查网络及代理设置后重试。");
    if (status == 429) return QStringLiteral("登录尝试过于频繁，请等待几分钟后再试。");
    const auto data = object(body);
    for (const auto* key : {"errorMessage", "error_message", "error_description", "error"}) {
        const auto text = data.value(key).toString();
        if (!text.isEmpty()) return QStringLiteral("登录失败：") + text.left(500);
    }
    if (status == 401 || status == 403) return QStringLiteral("登录失败，请检查账号、密码及服务器角色是否有效。");
    if (status == 404) return QStringLiteral("服务器地址无效，或该地址不是认证接口。");
    return QStringLiteral("登录服务返回 HTTP %1，请稍后重试。").arg(status);
}
QString form(const QList<QPair<QString, QString>>& values) {
    QUrlQuery query;
    for (const auto& entry : values) query.addQueryItem(entry.first, entry.second);
    return query.toString(QUrl::FullyEncoded).replace('+', "%2B");
}
QJsonObject profile(const QJsonObject& account) {
    return {{"name", account.value("Name")}, {"id", account.value("Uuid")}};
}
}

Accounts::Accounts(QObject* parent) : QObject(parent) {
    const auto local = qEnvironmentVariable("LOCALAPPDATA", QDir::homePath() + "/AppData/Local");
    m_directory = QDir(local).filePath("97LaunchQt");
    m_path = m_directory + "/accounts.json";
    m_authTimeout.setSingleShot(true);
    connect(&m_authTimeout, &QTimer::timeout, this, [this] {
        if (m_busy) fail(QStringLiteral("微软登录已超时，请重新登录。"));
    });
    const auto previous = QDir(local).filePath("97Launch/accounts.json");
    if (!QFile::exists(m_path) && QFile::exists(previous)) {
        QDir().mkpath(m_directory);
        if (!QFile::copy(previous, m_path)) {
            m_storeValid = false;
            m_error = QStringLiteral("无法导入旧版账号文件，原文件已保留。");
        }
    }
    if (m_storeValid) load();
    QTimer::singleShot(1500, this, [this] {
        const auto selected = find(m_selected);
        if (selected >= 0) fetchSkin(m_accounts[selected].toObject().value("Id").toString());
    });
}
Accounts::~Accounts() {
    ++m_generation;
    m_authTimeout.stop();
    m_browser.reset();
    if (m_network) {
        for (auto* reply : m_network->findChildren<QNetworkReply*>()) disconnect(reply, nullptr, this, nullptr);
        m_network.reset();
    }
}
void Accounts::load() {
    QFile file(m_path);
    if (!file.exists()) return;
    if (!file.open(QIODevice::ReadOnly)) { m_storeValid = false; m_error = QStringLiteral("无法读取账号文件。"); return; }
    QJsonParseError error;
    const auto doc = QJsonDocument::fromJson(file.readAll(), &error);
    if (error.error != QJsonParseError::NoError || !doc.isObject() || !doc.object().value("Accounts").isArray()) {
        m_storeValid = false;
        m_error = QStringLiteral("账号文件损坏，已保留原文件，请备份后修复。");
        return;
    }
    m_accounts = doc.object().value("Accounts").toArray();
    m_selected = doc.object().value("ActiveAccountId").toString();
    if (find(m_selected) < 0) m_selected = m_accounts.isEmpty() ? "" : m_accounts.first().toObject().value("Id").toString();
}
bool Accounts::commit(const QJsonArray& rows, const QString& selected) {
    if (!m_storeValid) {
        m_error = QStringLiteral("现有账号文件无法读取，已保留原文件。请先备份并修复 accounts.json。");
        emit loginChanged();
        return false;
    }
    QDir().mkpath(m_directory);
    QSaveFile file(m_path);
    const auto bytes = QJsonDocument(QJsonObject{{"ActiveAccountId", selected}, {"Accounts", rows}}).toJson();
    if (!file.open(QIODevice::WriteOnly) || file.write(bytes) != bytes.size() || !file.commit()) {
        m_error = QStringLiteral("账号保存失败，请检查数据目录权限或剩余空间。");
        emit loginChanged();
        return false;
    }
    m_accounts = rows;
    m_selected = selected;
    emit changed();
    return true;
}
int Accounts::find(const QString& id) const {
    for (int i = 0; i < m_accounts.size(); ++i)
        if (m_accounts[i].toObject().value("Id").toString() == id) return i;
    return -1;
}
bool Accounts::isSlim(const QString& uuid) {
    if (uuid.size() != 32) return false;
    int value = 0;
    for (int index : {7, 15, 23, 31}) value ^= QString(uuid[index]).toInt(nullptr, 16);
    return value % 2 != 0;
}
QString Accounts::offlineUuid(const QString& input) const {
    const auto name = input.trimmed();
    if (name.isEmpty()) return {};
    quint32 hash = 5381;
    for (auto ch : name) hash = (hash * 33) ^ ch.unicode();
    auto result = QString::number(name.size(), 16).rightJustified(16, '0') + QString::number(hash, 16).rightJustified(16, '0');
    result[12] = '3'; result[16] = '9';
    return result.toUpper();
}
QString Accounts::offlineAvatar(const QString& name) const {
    return isSlim(offlineUuid(name)) ? "qrc:/art/alex_head.png" : "qrc:/art/steve_head.png";
}
QString Accounts::skinPath(const QJsonObject& account) const {
    const auto key = QCryptographicHash::hash((account.value("Uuid").toString() + ':' + account.value("Server").toString()).toUtf8(), QCryptographicHash::Sha256).toHex();
    return m_directory + "/skins-qt/" + QString::fromLatin1(key) + ".png";
}
QVariantMap Accounts::publicAccount(const QJsonObject& account) const {
    const int type = account.value("Type").toInt();
    const QStringList names{QStringLiteral("离线模式"), QStringLiteral("微软正版"), "Authlib-Injector", QStringLiteral("统一通行证")};
    const QString fallback = isSlim(account.value("Uuid").toString()) ? "alex" : "steve";
    QString avatar = account.value("AvatarPath").toString();
    QString skin = skinPath(account);
    bool hasSkin = QFile::exists(skin);
    if (!hasSkin && (type == 0 || !QFile::exists(avatar))) { skin = "qrc:/art/" + fallback + ".png"; hasSkin = true; }
    else if (hasSkin) skin = QUrl::fromLocalFile(skin).toString();
    else skin.clear();
    if (QFile::exists(avatar)) avatar = QUrl::fromLocalFile(avatar).toString();
    else avatar = "qrc:/art/" + fallback + "_head.png";
    int scale = 1;
    if (hasSkin && skin.startsWith("file:")) { QImageReader reader(QUrl(skin).toLocalFile()); scale = qMax(1, reader.size().width() / 64); }
    const bool liveToken = account.value("ExpiresAt").toInteger() > QDateTime::currentSecsSinceEpoch()
        && !protectedValue(account.value("AccessToken").toString(), false).isEmpty();
    const bool usable = type == 0 ? !account.value("Name").toString().trimmed().isEmpty()
        : liveToken || !protectedValue(account.value(type == 1 ? "RefreshToken" : "Password").toString(), false).isEmpty();
    return {{"id", account.value("Id").toString()}, {"name", account.value("Name").toString()}, {"usable", usable},
            {"type", type}, {"typeDisplay", names.value(type, names[0])}, {"uuid", account.value("Uuid").toString()},
            {"server", account.value("Server").toString()}, {"loginName", account.value("LoginName").toString()},
            {"current", account.value("Id").toString() == m_selected}, {"avatar", avatar}, {"skin", skin}, {"skinScale", scale}};
}
QVariantList Accounts::accounts() const {
    QVariantList rows;
    for (const auto& value : m_accounts) rows.append(publicAccount(value.toObject()));
    return rows;
}
QVariantMap Accounts::current() const {
    const int index = find(m_selected);
    return index < 0 ? QVariantMap() : publicAccount(m_accounts[index].toObject());
}
QStringList Accounts::profiles() const {
    QStringList names;
    for (const auto& value : m_profiles) names.append(value.toObject().value("name").toString());
    return names;
}
void Accounts::clearError() { m_error.clear(); emit loginChanged(); }
void Accounts::progress(const QString& message) { m_status = message; emit loginChanged(); }
bool Accounts::begin(const QString& message) {
    if (m_busy) return false;
    ++m_generation;
    m_busy = true; m_error.clear(); m_status = message;
    m_profiles = {}; m_draft = {};
    emit loginChanged();
    return true;
}
void Accounts::cancelLogin() {
    ++m_generation;
    m_authTimeout.stop();
    m_browser.reset();
    const auto replies = m_authReplies;
    for (auto* reply : replies) reply->abort();
    m_busy = false;
    m_oauthState.clear(); m_pkceVerifier.clear(); m_authorizationCode.clear(); m_oauthClientId.clear(); m_oauthRedirect.clear();
    m_profiles = {}; m_draft = {}; m_password.clear(); m_status.clear();
    emit loginChanged();
}
void Accounts::fail(const QString& message) {
    auto safeMessage = message;
    for (const auto* field : {"Password", "AccessToken", "RefreshToken", "ClientToken"}) {
        const auto secret = m_draft.value(field).toString();
        if (secret.size() > 3) safeMessage.replace(secret, QStringLiteral("[已隐藏]"));
    }
    if (!m_password.isEmpty()) safeMessage.replace(m_password, QStringLiteral("[已隐藏]"));
    for (const auto& secret : {m_pkceVerifier, m_authorizationCode, m_oauthState})
        if (!secret.isEmpty()) safeMessage.replace(secret, QStringLiteral("[已隐藏]"));
    cancelLogin();
    m_error = safeMessage;
    emit loginChanged();
    qWarning("Account login did not complete");
}
bool Accounts::selectAccount(const QString& id) {
    if (m_busy || find(id) < 0) return false;
    return commit(m_accounts, id);
}
bool Accounts::acceptLaunchRefresh(const QVariantMap& patch) {
    const auto id = patch.value("id").toString();
    const int index = find(id);
    if (index < 0) return true; // Removing an account during launch must not restore it.
    auto row = m_accounts[index].toObject();
    if (row.value("Type").toInt() != 1 || row.value("Uuid").toString().compare(patch.value("uuid").toString(), Qt::CaseInsensitive) != 0) return false;
    for (const auto* key : {"accessToken", "refreshToken"})
        if (!patch.value(key).toString().startsWith("enc:")) return false;
    for (const auto& key : QStringList{"Name", "ExpiresAt", "ProfileJson", "AccessToken", "RefreshToken"}) {
        auto jsonKey = key; jsonKey[0] = jsonKey[0].toLower();
        row[key] = QJsonValue::fromVariant(patch.value(jsonKey));
    }
    auto rows = m_accounts; rows[index] = row;
    return commit(rows, m_selected);
}
bool Accounts::removeAccount(const QString& id) {
    if (m_busy) return false;
    const int index = find(id);
    if (index < 0) return false;
    auto rows = m_accounts; rows.removeAt(index);
    return commit(rows, id == m_selected ? (rows.isEmpty() ? "" : rows.first().toObject().value("Id").toString()) : m_selected);
}
void Accounts::addOffline(const QString& input) {
    if (m_busy) return;
    const auto name = input.trimmed();
    if (name.isEmpty()) { m_error = QStringLiteral("用户名不能为空。"); emit loginChanged(); return; }
    QJsonObject account{{"Type", 0}, {"Name", name}, {"Uuid", offlineUuid(name)}, {"LoginName", name}};
    finish(account);
}
void Accounts::request(const QUrl& url, const QByteArray& body, const QByteArray& contentType,
                       const QString& bearer, Reply callback, int generation, bool get) {
    if (!m_network) {
        QNetworkProxyFactory::setUseSystemConfiguration(true);
        m_network = std::make_unique<QNetworkAccessManager>(this);
    }
    QNetworkRequest request(url);
    request.setTransferTimeout(60000);
    request.setRawHeader("Accept-Language", "zh-CN");
    request.setRawHeader("User-Agent", "97Launch/0.1 Qt");
    request.setAttribute(QNetworkRequest::RedirectPolicyAttribute, generation < 0 ? QNetworkRequest::NoLessSafeRedirectPolicy : QNetworkRequest::ManualRedirectPolicy);
    if (!bearer.isEmpty()) request.setRawHeader("Authorization", ("Bearer " + bearer).toUtf8());
    if (!contentType.isEmpty()) request.setHeader(QNetworkRequest::ContentTypeHeader, contentType);
    auto* reply = get ? m_network->get(request) : m_network->post(request, body);
    if (generation >= 0) m_authReplies.insert(reply);
    auto data = std::make_shared<QByteArray>();
    connect(reply, &QIODevice::readyRead, this, [reply, data] {
        data->append(reply->readAll());
        if (data->size() > 2 * 1024 * 1024) reply->abort();
    });
    connect(reply, &QNetworkReply::finished, this, [this, reply, data, callback, generation] {
        m_authReplies.remove(reply);
        data->append(reply->readAll());
        const int code = reply->attribute(QNetworkRequest::HttpStatusCodeAttribute).toInt();
        const bool tooBig = data->size() > 2 * 1024 * 1024;
        reply->deleteLater();
        if (generation >= 0 && generation != m_generation) return;
        callback(tooBig ? 0 : code, tooBig ? QByteArray() : *data);
    });
}
void Accounts::json(const QString& url, const QJsonObject& body, Reply callback) {
    request(QUrl(url), QJsonDocument(body).toJson(QJsonDocument::Compact), "application/json", {}, callback, m_generation);
}
QString Accounts::clientId() {
    const auto configured = qEnvironmentVariable("PCL_MS_CLIENT_ID").trimmed();
    return configured.isEmpty() ? "28e30885-6643-4228-98bf-984eb6021d7d" : configured;
}
void Accounts::startMicrosoft() {
    if (!begin(QStringLiteral("请在微软登录窗口中完成授权。"))) return;
    m_oauthRedirect = qEnvironmentVariable("PCL_MS_REDIRECT_URI").trimmed();
    if (m_oauthRedirect.isEmpty()) m_oauthRedirect = defaultMicrosoftRedirect;
    const QUrl redirect(m_oauthRedirect, QUrl::StrictMode);
    const bool loopback = redirect.host() == "localhost" || redirect.host() == "127.0.0.1" || redirect.host() == "::1";
    if (!redirect.isValid() || redirect.host().isEmpty() || !redirect.userInfo().isEmpty()
        || redirect.hasQuery() || redirect.hasFragment()
        || (redirect.scheme() != "https" && !(redirect.scheme() == "http" && loopback))) {
        fail(QStringLiteral("登录回调地址配置无效，请设置 HTTPS 地址或本机 HTTP 回调地址。")); return;
    }
    m_oauthState = secureToken();
    m_pkceVerifier = secureToken();
    m_oauthClientId = clientId();
    const auto challenge = QCryptographicHash::hash(m_pkceVerifier.toLatin1(), QCryptographicHash::Sha256)
        .toBase64(QByteArray::Base64UrlEncoding | QByteArray::OmitTrailingEquals);
    QUrl authorize(QStringLiteral("https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize"));
    QUrlQuery query;
    query.addQueryItem("client_id", m_oauthClientId);
    query.addQueryItem("response_type", "code");
    query.addQueryItem("redirect_uri", m_oauthRedirect);
    query.addQueryItem("response_mode", "query");
    query.addQueryItem("scope", "XboxLive.signin offline_access");
    query.addQueryItem("state", m_oauthState);
    query.addQueryItem("code_challenge", QString::fromLatin1(challenge));
    query.addQueryItem("code_challenge_method", "S256");
    query.addQueryItem("prompt", "select_account");
    authorize.setQuery(query);
    const int generation = m_generation;
    m_browser = std::make_unique<MicrosoftAuthWindow>(QGuiApplication::focusWindow());
    connect(m_browser.get(), &MicrosoftAuthWindow::redirected, this, [this, generation](const QUrl& url) {
        if (generation == m_generation && m_busy && !m_oauthState.isEmpty()) acceptMicrosoftRedirect(url);
    }, Qt::QueuedConnection);
    connect(m_browser.get(), &MicrosoftAuthWindow::failed, this, [this, generation](const QString& message) {
        if (generation == m_generation && m_busy && !m_oauthState.isEmpty()) fail(message);
    }, Qt::QueuedConnection);
    connect(m_browser.get(), &MicrosoftAuthWindow::cancelled, this, [this, generation] {
        if (generation == m_generation && m_busy && !m_oauthState.isEmpty()) cancelLogin();
    }, Qt::QueuedConnection);
    m_authTimeout.start(10 * 60 * 1000);
    m_browser->open(authorize, redirect, m_directory + "/webview2-login");
}
void Accounts::acceptMicrosoftRedirect(const QUrl& url) {
    const QUrl expected(m_oauthRedirect);
    const int defaultPort = expected.scheme() == "https" ? 443 : 80;
    if (!url.isValid() || url.scheme() != expected.scheme() || url.host() != expected.host()
        || url.port(defaultPort) != expected.port(defaultPort) || url.path() != expected.path()
        || !url.userInfo().isEmpty() || url.hasFragment()) {
        fail(QStringLiteral("微软登录回调地址无效，请重新登录。")); return;
    }
    const QUrlQuery query(url);
    const auto fields = query.queryItems(QUrl::FullyDecoded);
    int stateCount = 0, codeCount = 0, errorCount = 0;
    for (const auto& field : fields) {
        if (field.first == "state") ++stateCount;
        if (field.first == "code") ++codeCount;
        if (field.first == "error") ++errorCount;
    }
    if (stateCount != 1 || query.queryItemValue("state", QUrl::FullyDecoded) != m_oauthState) {
        fail(QStringLiteral("微软登录会话校验失败，请重新登录。")); return;
    }
    if (errorCount > 0) {
        fail(query.queryItemValue("error") == "access_denied" ? QStringLiteral("你已取消微软授权。")
             : QStringLiteral("微软授权未完成，请确认应用 Client ID 已配置桌面应用回调地址后重试。"));
        return;
    }
    m_authorizationCode = query.queryItemValue("code", QUrl::FullyDecoded);
    if (codeCount != 1 || m_authorizationCode.isEmpty() || m_authorizationCode.size() > 16384 || m_pkceVerifier.isEmpty()) {
        fail(QStringLiteral("微软返回的授权码无效，请重新登录。")); return;
    }
    m_oauthState.clear();
    m_authTimeout.stop();
    m_browser.reset();
    progress(QStringLiteral("正在验证微软授权……"));
    const auto data = form({{"client_id", m_oauthClientId}, {"grant_type", "authorization_code"},
        {"code", m_authorizationCode}, {"redirect_uri", m_oauthRedirect}, {"code_verifier", m_pkceVerifier},
        {"scope", "XboxLive.signin offline_access"}}).toUtf8();
    request(QUrl(microsoftTokenEndpoint), data, "application/x-www-form-urlencoded", {}, [this](int code, const QByteArray& body) {
        const auto result = object(body);
        if (ok(code) && !result.value("access_token").toString().isEmpty()) {
            m_draft = {{"Type", 1}, {"RefreshToken", result.value("refresh_token")}, {"MicrosoftClientId", m_oauthClientId}};
            m_authorizationCode.clear(); m_pkceVerifier.clear();
            xboxLogin(result.value("access_token").toString()); return;
        }
        fail(serviceError(code, body));
    }, m_generation);
}
void Accounts::xboxLogin(const QString& oauthToken) {
    progress(QStringLiteral("正在登录……15%"));
    const QString ticket = oauthToken.startsWith("d=") ? oauthToken : "d=" + oauthToken;
    json("https://user.auth.xboxlive.com/user/authenticate", {{"Properties", QJsonObject{{"AuthMethod", "RPS"}, {"SiteName", "user.auth.xboxlive.com"}, {"RpsTicket", ticket}}}, {"RelyingParty", "http://auth.xboxlive.com"}, {"TokenType", "JWT"}}, [this](int code, const QByteArray& body) {
        const auto token = object(body).value("Token").toString();
        if (!ok(code) || token.isEmpty()) { fail(serviceError(code, body)); return; }
        xstsLogin(token);
    });
}
void Accounts::xstsLogin(const QString& xboxToken) {
    progress(QStringLiteral("正在登录……35%"));
    json("https://xsts.auth.xboxlive.com/xsts/authorize", {{"Properties", QJsonObject{{"SandboxId", "RETAIL"}, {"UserTokens", QJsonArray{xboxToken}}}}, {"RelyingParty", "rp://api.minecraftservices.com/"}, {"TokenType", "JWT"}}, [this](int code, const QByteArray& body) {
        const auto result = object(body);
        if (!ok(code)) {
            if (body.contains("2148916227")) fail(QStringLiteral("该微软账号已被封禁，无法登录。"));
            else if (body.contains("2148916233")) fail(QStringLiteral("尚未注册 Xbox 账户，请先在 signup.live.com 完成注册。"));
            else if (body.contains("2148916235")) fail(QStringLiteral("当前网络所在地区无法登录 Xbox，请检查网络设置。"));
            else if (body.contains("2148916238")) fail(QStringLiteral("该账号年龄不足，请先完善微软家庭和年龄设置。"));
            else fail(serviceError(code, body));
            return;
        }
        const auto token = result.value("Token").toString();
        const auto claims = result.value("DisplayClaims").toObject().value("xui").toArray();
        const auto uhs = claims.isEmpty() ? QString() : claims.first().toObject().value("uhs").toString();
        if (token.isEmpty() || uhs.isEmpty()) { fail(QStringLiteral("Xbox 返回的授权信息不完整。")); return; }
        minecraftLogin("XBL3.0 x=" + uhs + ';' + token);
    });
}
void Accounts::minecraftLogin(const QString& identity, int attempt) {
    progress(QStringLiteral("正在登录……55%"));
    json("https://api.minecraftservices.com/authentication/login_with_xbox", {{"identityToken", identity}}, [this, identity, attempt](int code, const QByteArray& body) {
        if (!ok(code)) {
            if (attempt < 2 && code != 429 && code != 503) {
                const int generation = m_generation;
                QTimer::singleShot(2000 * (attempt + 1), this, [this, identity, attempt, generation] { if (generation == m_generation) minecraftLogin(identity, attempt + 1); });
                return;
            }
            if (code == 403 && body.contains("Invalid app registration")) fail(QStringLiteral("此应用尚未获准访问 Minecraft 服务，需使用获批的微软应用 Client ID。"));
            else if (code == 403) fail(QStringLiteral("当前 IP 的登录尝试异常，请检查代理或更换网络后重试。"));
            else if (code == 503) fail(QStringLiteral("Minecraft 登录服务暂时不可用，请稍后重试。"));
            else fail(serviceError(code, body));
            return;
        }
        const auto result = object(body); const auto token = result.value("access_token").toString();
        if (token.isEmpty()) { fail(QStringLiteral("Minecraft 返回的令牌无效。")); return; }
        ownership(token, QDateTime::currentSecsSinceEpoch() + result.value("expires_in").toInt(86400) - 3600);
    });
}
void Accounts::ownership(const QString& token, qint64 expiresAt) {
    progress(QStringLiteral("正在检查游戏所有权……70%"));
    request(QUrl("https://api.minecraftservices.com/entitlements/mcstore"), {}, {}, token, [this, token, expiresAt](int code, const QByteArray& body) {
        if (!ok(code)) { fail(serviceError(code, body)); return; }
        if (object(body).value("items").toArray().isEmpty()) { fail(QStringLiteral("该账号尚未购买 Minecraft，或 Game Pass 已到期。")); return; }
        microsoftProfile(token, expiresAt);
    }, m_generation, true);
}
void Accounts::microsoftProfile(const QString& token, qint64 expiresAt) {
    progress(QStringLiteral("正在获取玩家档案……85%"));
    request(QUrl("https://api.minecraftservices.com/minecraft/profile"), {}, {}, token, [this, token, expiresAt](int code, const QByteArray& body) {
        if (code == 404) { fail(QStringLiteral("尚未创建 Minecraft 玩家档案，请先在 minecraft.net 创建。")); return; }
        if (!ok(code)) { fail(serviceError(code, body)); return; }
        const auto result = object(body);
        m_draft["Name"] = result.value("name"); m_draft["Uuid"] = result.value("id");
        m_draft["AccessToken"] = token; m_draft["ExpiresAt"] = expiresAt;
        m_draft["ProfileJson"] = QString::fromUtf8(body); m_draft["ClientToken"] = result.value("id");
        finish(m_draft);
    }, m_generation, true);
}
QString Accounts::normalizedServer(QString server) {
    server = server.trimmed(); while (server.endsWith('/')) server.chop(1);
    if (server.isEmpty()) return {};
    if (!server.contains('.') && !server.startsWith("http", Qt::CaseInsensitive)) return "https://auth.mc-user.com:233/" + server + "/authserver";
    if (!server.startsWith("http://", Qt::CaseInsensitive) && !server.startsWith("https://", Qt::CaseInsensitive)) server.prepend("https://");
    if (!server.endsWith("/authserver", Qt::CaseInsensitive)) server += "/authserver";
    const QUrl url(server);
    if (!url.isValid() || url.host().isEmpty() || (url.scheme() != "https" && url.scheme() != "http") || !url.userInfo().isEmpty() || url.hasQuery() || url.hasFragment()) return {};
    return server;
}
void Accounts::loginThirdParty(const QString& server, const QString& inputUser, const QString& password, bool remember) {
    if (m_busy) return;
    const auto base = normalizedServer(server); const auto user = inputUser.trimmed();
    if (base.isEmpty() || user.isEmpty()) { m_error = QStringLiteral("请输入有效的认证服务器和登录账号。"); emit loginChanged(); return; }
    if (!begin(QStringLiteral("正在登录认证服务器……"))) return;
    const int type = QUrl(base).host().compare("auth.mc-user.com", Qt::CaseInsensitive) == 0 ? 3 : 2;
    for (const auto& entry : m_accounts) {
        const auto account = entry.toObject();
        if (account.value("Type").toInt() == type && account.value("Server").toString().compare(base, Qt::CaseInsensitive) == 0 && account.value("LoginName").toString().compare(user, Qt::CaseInsensitive) == 0) m_draft = account;
    }
    for (const auto* field : {"AccessToken", "Password", "RefreshToken"}) m_draft[field] = protectedValue(m_draft.value(field).toString(), false);
    m_draft["Type"] = type; m_draft["Server"] = base; m_draft["LoginName"] = user;
    m_password = password.isEmpty() ? m_draft.value("Password").toString() : password;
    m_remember = remember;
    if (!m_draft.value("AccessToken").toString().isEmpty() && !m_draft.value("ClientToken").toString().isEmpty() && !m_draft.value("Uuid").toString().isEmpty()) {
        json(base + "/validate", {{"accessToken", m_draft.value("AccessToken")}, {"clientToken", m_draft.value("ClientToken")}, {"requestUser", true}}, [this](int code, const QByteArray&) {
            if (ok(code)) finishServer(); else refreshServer(true);
        });
    } else authenticateServer();
}
void Accounts::refreshServer(bool fallbackToPassword) {
    json(m_draft.value("Server").toString() + "/refresh", {{"selectedProfile", profile(m_draft)}, {"accessToken", m_draft.value("AccessToken")}, {"requestUser", true}}, [this, fallbackToPassword](int code, const QByteArray& body) {
        const auto result = object(body);
        const auto token = result.value("accessToken").toString();
        if (!ok(code) || token.isEmpty() || (fallbackToPassword && !result.value("selectedProfile").isObject())) {
            if (fallbackToPassword) authenticateServer(); else finishServer();
            return;
        }
        m_draft["AccessToken"] = token;
        if (result.contains("clientToken")) m_draft["ClientToken"] = result.value("clientToken");
        if (result.value("selectedProfile").isObject()) { const auto selected = result.value("selectedProfile").toObject(); m_draft["Name"] = selected.value("name"); m_draft["Uuid"] = selected.value("id"); }
        finishServer();
    });
}
void Accounts::authenticateServer() {
    if (m_password.isEmpty()) { fail(QStringLiteral("登录状态已失效，请输入密码重新登录。")); return; }
    json(m_draft.value("Server").toString() + "/authenticate", {{"agent", QJsonObject{{"name", "Minecraft"}, {"version", 1}}}, {"username", m_draft.value("LoginName")}, {"password", m_password}, {"requestUser", true}}, [this](int code, const QByteArray& body) {
        if (!ok(code)) { fail(serviceError(code, body)); return; }
        const auto result = object(body);
        m_profiles = result.value("availableProfiles").toArray();
        const auto token = result.value("accessToken").toString();
        if (m_profiles.isEmpty()) { fail(QStringLiteral("该账号尚未创建角色，请先到皮肤站创建角色。")); return; }
        if (token.isEmpty()) { fail(QStringLiteral("认证服务器返回的令牌无效。")); return; }
        m_draft["AccessToken"] = token; m_draft["ClientToken"] = result.value("clientToken");
        if (m_profiles.size() == 1 && result.value("selectedProfile").isObject()) {
            const auto selected = result.value("selectedProfile").toObject();
            m_draft["Name"] = selected.value("name"); m_draft["Uuid"] = selected.value("id");
            finishServer(); return;
        }
        for (int i = 0; i < m_profiles.size(); ++i) {
            if (!m_draft.value("Uuid").toString().isEmpty() && m_profiles[i].toObject().value("id").toString().compare(m_draft.value("Uuid").toString(), Qt::CaseInsensitive) == 0) { chooseProfile(i); return; }
        }
        progress(QStringLiteral("请选择要使用的角色。")); emit profilesRequired();
    });
}
void Accounts::chooseProfile(int index) {
    if (!m_busy || index < 0 || index >= m_profiles.size()) return;
    const auto selected = m_profiles[index].toObject();
    m_draft["Name"] = selected.value("name"); m_draft["Uuid"] = selected.value("id");
    m_profiles = {}; progress(QStringLiteral("正在保存角色选择……"));
    refreshServer(false);
}
void Accounts::finishServer() {
    m_draft["Password"] = m_remember ? m_password : QString();
    m_draft["ExpiresAt"] = QDateTime::currentSecsSinceEpoch() + 86400;
    finish(m_draft);
}
void Accounts::finish(QJsonObject account) {
    auto uuid = account.value("Uuid").toString();
    uuid.remove('-');
    account["Uuid"] = uuid;
    if (account.value("Name").toString().isEmpty() || !QRegularExpression("^[A-Fa-f0-9]{32}$").match(account.value("Uuid").toString()).hasMatch()) { fail(QStringLiteral("玩家档案的名称或 UUID 无效。")); return; }
    auto rows = m_accounts;
    int existing = -1;
    for (int i = 0; i < rows.size(); ++i) {
        const auto row = rows[i].toObject();
        if (row.value("Type") != account.value("Type")) continue;
        const bool same = account.value("Type").toInt() == 0
            ? row.value("Name").toString().compare(account.value("Name").toString(), Qt::CaseInsensitive) == 0
            : account.value("Type").toInt() == 1
                ? row.value("Uuid").toString().compare(account.value("Uuid").toString(), Qt::CaseInsensitive) == 0
                : row.value("Server").toString().compare(account.value("Server").toString(), Qt::CaseInsensitive) == 0 && row.value("LoginName").toString().compare(account.value("LoginName").toString(), Qt::CaseInsensitive) == 0;
        if (same) { existing = i; break; }
    }
    if (existing >= 0) {
        auto merged = rows[existing].toObject();
        for (auto it = account.begin(); it != account.end(); ++it) merged[it.key()] = it.value();
        account = merged;
    }
    if (account.value("Id").toString().isEmpty()) account["Id"] = freshId();
    if (!account.contains("CreatedAt")) account["CreatedAt"] = QDateTime::currentDateTimeUtc().toString(Qt::ISODateWithMs);
    for (const auto* field : {"Password", "AccessToken", "RefreshToken"}) {
        const auto value = account.value(field).toString();
        if (value.startsWith("enc:")) continue;
        bool success = true;
        account[field] = protectedValue(value, true, &success);
        if (!success) { fail(QStringLiteral("无法加密账号凭据，账号未保存。")); return; }
    }
    const auto id = account.value("Id").toString();
    if (existing >= 0) rows[existing] = account; else rows.prepend(account);
    if (!commit(rows, id)) { cancelLogin(); return; }
    cancelLogin();
    clearError(); emit loginSucceeded();
    qInfo("Account saved and selected");
    fetchSkin(id);
}
void Accounts::reloginAccount(const QString& id) {
    if (m_busy) return;
    const int index = find(id);
    if (index < 0) return;
    const auto account = m_accounts[index].toObject();
    const int type = account.value("Type").toInt();
    if (type == 0) { selectAccount(id); emit loginSucceeded(); return; }
    if (type != 1) {
        loginThirdParty(account.value("Server").toString(), account.value("LoginName").toString(), protectedValue(account.value("Password").toString(), false), !account.value("Password").toString().isEmpty());
        return;
    }
    const auto refresh = protectedValue(account.value("RefreshToken").toString(), false);
    if (refresh.isEmpty()) { startMicrosoft(); return; }
    if (!begin(QStringLiteral("正在刷新微软登录状态……"))) return;
    m_draft = account;
    for (const auto* field : {"Password", "AccessToken", "RefreshToken"}) m_draft[field] = protectedValue(account.value(field).toString(), false);
    const auto data = form({{"client_id", account.value("MicrosoftClientId").toString(clientId())}, {"refresh_token", refresh}, {"grant_type", "refresh_token"}, {"scope", "XboxLive.signin offline_access"}}).toUtf8();
    request(QUrl(microsoftTokenEndpoint), data, "application/x-www-form-urlencoded", {}, [this](int code, const QByteArray& body) {
        const auto result = object(body);
        if (!ok(code) || result.value("access_token").toString().isEmpty()) { fail(QStringLiteral("登录状态已失效，请通过正版登录重新授权。")); return; }
        m_draft["RefreshToken"] = result.value("refresh_token").toString(m_draft.value("RefreshToken").toString());
        xboxLogin(result.value("access_token").toString());
    }, m_generation);
}
void Accounts::openRegistration(const QString& server) {
    auto base = normalizedServer(server);
    if (base.isEmpty()) { m_error = QStringLiteral("请先填写有效的认证服务器地址。"); emit loginChanged(); return; }
    base.chop(QString("/authserver").size());
    QDesktopServices::openUrl(QUrl(base + "/auth/register"));
}
void Accounts::fetchSkin(const QString& id) {
    const int index = find(id);
    if (index < 0) return;
    const auto account = m_accounts[index].toObject();
    if (account.value("Type").toInt() == 0 || QFile::exists(skinPath(account))) return;
    QString root;
    if (account.value("Type").toInt() == 1) root = "https://sessionserver.mojang.com/session/minecraft/profile/";
    else {
        root = account.value("Server").toString();
        if (root.endsWith("/authserver", Qt::CaseInsensitive)) root.chop(11);
        root += "/sessionserver/session/minecraft/profile/";
    }
    request(QUrl(root + account.value("Uuid").toString()), {}, {}, {}, [this, id](int code, const QByteArray& body) {
        if (!ok(code) || find(id) < 0) return;
        for (const auto& value : object(body).value("properties").toArray()) {
            const auto property = value.toObject();
            if (property.value("name").toString() != "textures") continue;
            auto url = object(QByteArray::fromBase64(property.value("value").toString().toLatin1())).value("textures").toObject().value("SKIN").toObject().value("url").toString();
            if (url.startsWith("http://")) url.replace(0, 7, "https://");
            if (QUrl(url).scheme() == "https") downloadSkin(id, QUrl(url));
        }
    }, -1, true);
}
void Accounts::downloadSkin(const QString& id, const QUrl& url) {
    request(url, {}, {}, {}, [this, id](int code, const QByteArray& bytes) {
        const int index = find(id);
        if (!ok(code) || index < 0) return;
        QBuffer buffer; buffer.setData(bytes); buffer.open(QIODevice::ReadOnly);
        QImageReader reader(&buffer); const auto size = reader.size();
        if (size.width() < 64 || size.width() > 1024 || size.width() % 64 || (size.height() != size.width() && size.height() * 2 != size.width())) return;
        const auto skin = reader.read(); if (skin.isNull()) return;
        const auto account = m_accounts[index].toObject(); const auto path = skinPath(account);
        QDir().mkpath(QFileInfo(path).absolutePath());
        QSaveFile file(path);
        if (!file.open(QIODevice::WriteOnly) || !skin.save(&file, "PNG") || !file.commit()) return;
        const int scale = skin.width() / 64;
        QImage head = skin.copy(8 * scale, 8 * scale, 8 * scale, 8 * scale).scaled(48, 48, Qt::IgnoreAspectRatio, Qt::FastTransformation);
        const auto hat = skin.copy(40 * scale, 8 * scale, 8 * scale, 8 * scale).scaled(48, 48, Qt::IgnoreAspectRatio, Qt::FastTransformation);
        { QPainter painter(&head); painter.drawImage(0, 0, hat); }
        auto rows = m_accounts; auto updated = account;
        const auto headPath = path + ".head.png";
        if (head.save(headPath)) { updated["AvatarPath"] = headPath; rows[index] = updated; commit(rows, m_selected); }
        else emit changed();
    }, -1, true);
}
