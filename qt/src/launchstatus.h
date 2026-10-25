#pragma once
#include <QObject>
#include <QVariantMap>
#include <QFileSystemWatcher>
#include <QTimer>
class Accounts;
class Instances;
class InstanceSettings;
class InstanceJobs;
class GlobalSettings;

class LaunchStatus : public QObject {
    Q_OBJECT
    Q_PROPERTY(QVariantMap values READ values NOTIFY changed)
public:
    LaunchStatus(Instances* instances,Accounts* accounts,InstanceSettings* settings,GlobalSettings* globals,InstanceJobs* reader,QObject* parent=nullptr);
    QVariantMap values()const{return m_values;}
    Q_INVOKABLE void setActive(bool active);
    void setWatchingPaused(bool paused);
signals:
    void changed();
private:
    void refresh();
    void summarize();
    void watchFiles();
    QString fingerprint()const;
    Instances* m_instances;
    Accounts* m_accounts;
    InstanceJobs* m_reader;
    GlobalSettings* m_globals;
    QVariantMap m_values,m_java;
    QString m_key,m_checkedPath;
    QStringList m_files;
    QTimer m_debounce,m_age;
    QFileSystemWatcher m_watcher;
    int m_generation=0;
    bool m_active=false,m_checking=false,m_failed=false,m_watchingPaused=false;
    QString m_failure;
};
