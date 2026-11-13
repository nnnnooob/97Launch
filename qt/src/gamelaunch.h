#pragma once
#include <QObject>
#include <QLocalSocket>
#include <QTimer>
#include <QVariantList>
class Instances;
class Accounts;
class GlobalSettings;
class LauncherFlow;
class LaunchStatus;

class GameLaunch : public QObject {
    Q_OBJECT
    Q_PROPERTY(bool starting READ starting NOTIFY changed)
    Q_PROPERTY(bool canLaunch READ canLaunch NOTIFY changed)
    Q_PROPERTY(QVariantList sessions READ sessions NOTIFY changed)
    Q_PROPERTY(QString error READ error NOTIFY changed)
    Q_PROPERTY(QString detail READ detail NOTIFY changed)
    Q_PROPERTY(QVariantMap prompt READ prompt NOTIFY changed)
    Q_PROPERTY(QVariantMap crash READ crash NOTIFY changed)
    Q_PROPERTY(QVariantMap preparation READ preparation NOTIFY changed)
public:
    GameLaunch(Instances*,Accounts*,GlobalSettings*,LauncherFlow*,LaunchStatus*,QObject* parent=nullptr);
    ~GameLaunch();
    bool starting()const{return m_starting;}
    bool canLaunch()const;
    QVariantList sessions()const{return m_sessions;}
    QString error()const{return m_error;}
    QString detail()const{return m_detail;}
    QVariantMap prompt()const{return m_prompt;}
    QVariantMap crash()const{return m_crash;}
    QVariantMap preparation()const;
    Q_INVOKABLE bool start();
    Q_INVOKABLE void cancel();
    Q_INVOKABLE void stop(const QString& id);
    Q_INVOKABLE void stopAll();
    Q_INVOKABLE void answer(bool accepted);
    Q_INVOKABLE void clearError();
    Q_INVOKABLE void dismissCrash();
    Q_INVOKABLE void openLogs(const QString& path=QString());
    Q_INVOKABLE void detach();
signals:
    void changed();
    void gameStarted();
    void crashReported();
private:
    void connectHost();
    void read();
    void send(const QVariantMap&);
    void fail(const QString&);
    Instances* m_instances;
    Accounts* m_accounts;
    GlobalSettings* m_settings;
    LauncherFlow* m_flow;
    LaunchStatus* m_status;
    QLocalSocket m_socket;
    QTimer m_connect;
    QByteArray m_input;
    QVariantList m_sessions;
    QVariantMap m_pending,m_prompt,m_crash,m_preparation;
    QString m_pipe,m_error,m_detail,m_startingId;
    bool m_starting=false,m_spawned=false,m_detaching=false;
    int m_attempts=0;
};
