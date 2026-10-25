#pragma once
#include <QObject>
#include <QVariantMap>
#include <QTimer>
#include <QUrl>
class Instances;
class InstanceJobs;
class GlobalSettings : public QObject {
    Q_OBJECT
    Q_PROPERTY(QVariantMap values READ values NOTIFY changed)
    Q_PROPERTY(QVariantMap memory READ memory NOTIFY memoryChanged)
    Q_PROPERTY(QString error READ error NOTIFY changed)
    Q_PROPERTY(QString dataDirectory READ dataDirectory CONSTANT)
    Q_PROPERTY(QString logDirectory READ logDirectory CONSTANT)
    Q_PROPERTY(QString version READ version CONSTANT)
    Q_PROPERTY(QVariantList javaRuntimes READ javaRuntimes NOTIFY changed)
    Q_PROPERTY(QVariantMap selectedJava READ selectedJava NOTIFY changed)
    Q_PROPERTY(bool javaBusy READ javaBusy NOTIFY changed)
    Q_PROPERTY(QString javaStatus READ javaStatus NOTIFY changed)
    Q_PROPERTY(QVariantMap authorization READ authorization NOTIFY changed)
    Q_PROPERTY(QVariantMap updates READ updates NOTIFY changed)
public:
    GlobalSettings(Instances* instances, QObject* parent=nullptr);
    QVariantMap values() const { return m_values; }
    QVariantMap memory() const { return m_memory; }
    QString error() const { return m_error; }
    bool storageValid() const { return m_valid; }
    QString dataDirectory() const;
    QString logDirectory() const;
    QString version() const;
    void attachJobs(InstanceJobs* reader,InstanceJobs* transfers,InstanceJobs* authorization);
    QVariantList javaRuntimes()const{return m_java;}
    QVariantMap selectedJava()const;
    bool javaBusy()const{return m_javaBusy;}
    QString javaStatus()const{return m_javaStatus;}
    QVariantMap authorization()const{return m_authorization;}
    QVariantMap updates()const{return m_updates;}
    void acceptAuthorization(const QVariantMap& state);
    Q_INVOKABLE void refreshJava();
    Q_INVOKABLE void addJava(const QUrl& file);
    Q_INVOKABLE bool removeJava(const QString& path = QString());
    Q_INVOKABLE void readPreset(const QString& instancePath);
    Q_INVOKABLE void authorizationAction(const QString& action);
    Q_INVOKABLE void pairDevice(const QString& token);
    Q_INVOKABLE void cancelAuthorization();
    Q_INVOKABLE void updateAction(const QString& action);
    Q_INVOKABLE void configureUpdates(const QString& channel,bool startupCheck);
    Q_INVOKABLE bool setOption(const QString& key,const QVariant& value);
    Q_INVOKABLE bool setPreset(const QString& key,const QVariant& value);
    Q_INVOKABLE bool applyPreset(const QVariantMap& values);
    Q_INVOKABLE bool resetPreset();
    Q_INVOKABLE void setActive(bool active);
    Q_INVOKABLE bool openDirectory(const QString& kind);
    Q_INVOKABLE bool openJava(const QString& path);
    Q_INVOKABLE void clearError();
    Q_INVOKABLE void clearUpdateError();
    Q_INVOKABLE void captureKey(bool active);
    Q_INVOKABLE QString gameKey(int virtualKey, int scanCode, int qtKey) const;
    Q_INVOKABLE QString keyLabel(const QString& key) const;
signals:
    void changed();
    void memoryChanged();
    void restartRequested();
    void transferRequested();
    void keyCaptured(const QString& key);
    void authorizationResolved(const QString& action);
    void updatesResolved(const QString& action);
    void javaResolved(bool success);
protected:
    bool eventFilter(QObject* watched,QEvent* event) override;
private:
    bool save(const QVariantMap& values);
    void updateMemory();
    QVariantMap m_values,m_memory;
    QString m_error;
    bool m_valid=true;
    bool m_captureKey=false;
    Instances* m_instances;
    QTimer m_timer;
    InstanceJobs *m_reader=nullptr,*m_transfers=nullptr,*m_authorizationJobs=nullptr;
    QVariantList m_java;
    QVariantMap m_authorization,m_updates;
    bool m_javaBusy=false;
    QString m_javaStatus="尚未搜索 Java";
};
