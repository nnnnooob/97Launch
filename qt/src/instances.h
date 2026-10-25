#pragma once
#include <QObject>
#include <QFutureWatcher>
#include <QHash>
#include <QJsonObject>
#include <QSet>
#include <QUrl>
#include <QVariantList>

struct InstanceScan {
    QHash<QString, QVariantList> rows;
    QStringList warnings;
};

struct ModpackPreview {
    QVariantMap info;
    QString error;
};

class Instances : public QObject {
    Q_OBJECT
    Q_PROPERTY(QVariantList folders READ folders NOTIFY changed)
    Q_PROPERTY(QVariantList available READ available NOTIFY changed)
    Q_PROPERTY(QVariantList entries READ entries NOTIFY entriesChanged)
    Q_PROPERTY(QVariantMap current READ current NOTIFY changed)
    Q_PROPERTY(QString selectedFolder READ selectedFolder NOTIFY changed)
    Q_PROPERTY(QString query READ query WRITE setQuery NOTIFY changed)
    Q_PROPERTY(int count READ count NOTIFY changed)
    Q_PROPERTY(bool loading READ loading NOTIFY changed)
    Q_PROPERTY(bool readingPack READ readingPack NOTIFY changed)
    Q_PROPERTY(QString error READ error NOTIFY changed)
public:
    explicit Instances(QObject* parent = nullptr);
    QVariantList folders() const;
    QVariantList available() const;
    QVariantList entries() const;
    QVariantMap current() const;
    QVariantMap details(const QString& path) const;
    void instanceMoved(const QString& oldPath, const QString& newPath);
    QString selectedFolder() const;
    QString query() const { return m_query; }
    int count() const;
    bool loading() const { return m_loading; }
    bool readingPack() const { return m_readingPack; }
    QString error() const { return m_error; }
    void setQuery(const QString& query);
    Q_INVOKABLE void refresh();
    Q_INVOKABLE void selectFolder(const QString& path);
    Q_INVOKABLE bool selectInstance(const QString& path);
    Q_INVOKABLE void toggleGroup(const QString& key);
    Q_INVOKABLE QVariantMap folderCandidate(const QUrl& url);
    Q_INVOKABLE bool addFolder(const QString& path, const QString& name);
    Q_INVOKABLE bool renameFolder(const QString& path, const QString& name);
    Q_INVOKABLE bool removeFolder(const QString& path);
    Q_INVOKABLE bool openFolder(const QString& path);
    Q_INVOKABLE bool recycleInstance(const QString& path);
    Q_INVOKABLE void inspectModpack(const QUrl& url);
    Q_INVOKABLE void clearError();
signals:
    void changed();
    void entriesChanged();
    void instanceSelected();
    void modpackReady(const QVariantMap& info);
private:
    bool save(const QJsonObject& config);
    bool report(const QString& message);
    QVariantMap findInstance(const QString& path) const;
    bool knownFolder(const QString& path) const;
    QJsonObject m_config;
    QString m_directory, m_file, m_builtin, m_query, m_error;
    bool m_storeValid = true, m_loading = false, m_rescan = false, m_readingPack = false;
    QSet<QString> m_collapsed{QStringLiteral("rare"), QStringLiteral("error")};
    QHash<QString, QVariantList> m_rows;
    QFutureWatcher<InstanceScan> m_scan;
    QFutureWatcher<ModpackPreview> m_pack;
};
