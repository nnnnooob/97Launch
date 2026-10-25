#pragma once
#include <QObject>
#include <QJsonArray>
#include <QJsonObject>
#include <QNetworkAccessManager>
#include <QSet>
#include <QTimer>
#include <QVariantList>
#include <functional>
#include <memory>

class QNetworkReply;
class MicrosoftAuthWindow;
class WebViewRuntime;

class Accounts : public QObject {
    Q_OBJECT
    Q_PROPERTY(QVariantList accounts READ accounts NOTIFY changed)
    Q_PROPERTY(QVariantMap current READ current NOTIFY changed)
    Q_PROPERTY(int count READ count NOTIFY changed)
    Q_PROPERTY(bool busy READ busy NOTIFY loginChanged)
    Q_PROPERTY(QString error READ error NOTIFY loginChanged)
    Q_PROPERTY(QString status READ status NOTIFY loginChanged)
    Q_PROPERTY(QStringList profiles READ profiles NOTIFY loginChanged)
public:
    explicit Accounts(QObject* parent = nullptr);
    ~Accounts() override;
    QVariantList accounts() const;
    QVariantMap current() const;
    int count() const { return m_accounts.size(); }
    bool busy() const { return m_busy; }
    QString error() const { return m_error; }
    bool storageValid() const { return m_storeValid; }
    void attachWebViewRuntime(WebViewRuntime* runtime);
    QString status() const { return m_status; }
    QStringList profiles() const;
    bool acceptLaunchRefresh(const QVariantMap& encryptedPatch);
    Q_INVOKABLE QString offlineUuid(const QString& name) const;
    Q_INVOKABLE QString offlineAvatar(const QString& name) const;
    Q_INVOKABLE void addOffline(const QString& name);
    Q_INVOKABLE bool selectAccount(const QString& id);
    Q_INVOKABLE bool removeAccount(const QString& id);
    Q_INVOKABLE void startMicrosoft();
    Q_INVOKABLE void loginThirdParty(const QString& server, const QString& user, const QString& password, bool remember);
    Q_INVOKABLE void reloginAccount(const QString& id);
    Q_INVOKABLE void chooseProfile(int index);
    Q_INVOKABLE void cancelLogin();
    Q_INVOKABLE void clearError();
    Q_INVOKABLE void openRegistration(const QString& server);
signals:
    void changed();
    void loginChanged();
    void loginSucceeded();
    void profilesRequired();
private:
    using Reply = std::function<void(int, const QByteArray&)>;
    void load();
    bool commit(const QJsonArray& rows, const QString& selected);
    int find(const QString& id) const;
    QVariantMap publicAccount(const QJsonObject& account) const;
    QString skinPath(const QJsonObject& account) const;
    bool begin(const QString& status);
    void fail(const QString& message);
    void progress(const QString& message);
    void request(const QUrl& url, const QByteArray& body, const QByteArray& contentType,
                 const QString& bearer, Reply callback, int generation, bool get = false);
    void json(const QString& url, const QJsonObject& body, Reply callback);
    void acceptMicrosoftRedirect(const QUrl& url);
    void xboxLogin(const QString& oauthToken);
    void xstsLogin(const QString& xboxToken);
    void minecraftLogin(const QString& identity, int attempt = 0);
    void ownership(const QString& token, qint64 expiresAt);
    void microsoftProfile(const QString& token, qint64 expiresAt);
    void authenticateServer();
    void refreshServer(bool fallbackToPassword);
    void finishServer();
    void finish(QJsonObject account);
    void fetchSkin(const QString& id);
    void downloadSkin(const QString& id, const QUrl& url);
    static QString normalizedServer(QString server);
    static bool isSlim(const QString& uuid);
    static QString clientId();
    QJsonArray m_accounts;
    QString m_selected, m_directory, m_path;
    std::unique_ptr<QNetworkAccessManager> m_network;
    std::unique_ptr<MicrosoftAuthWindow> m_browser;
    WebViewRuntime* m_webViewRuntime=nullptr;
    bool m_waitingForWebView=false;
    QSet<QNetworkReply*> m_authReplies;
    QTimer m_authTimeout;
    int m_generation = 0;
    bool m_busy = false;
    bool m_storeValid = true;
    QString m_error, m_status;
    QString m_oauthState, m_pkceVerifier, m_authorizationCode, m_oauthClientId, m_oauthRedirect;
    QJsonObject m_draft;
    QJsonArray m_profiles;
    QString m_password;
    bool m_remember = false;
};
