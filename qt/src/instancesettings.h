#pragma once
#include <QObject>
#include <QVariantMap>
#include <QVariantList>
#include <QTimer>
#include <QUrl>
class Instances;
class InstanceJobs;

class InstanceSettings : public QObject {
    Q_OBJECT
    Q_PROPERTY(QVariantMap instance READ instance NOTIFY changed)
    Q_PROPERTY(QVariantMap values READ values NOTIFY changed)
    Q_PROPERTY(QVariantMap memory READ memory NOTIFY memoryChanged)
    Q_PROPERTY(QVariantList javaRuntimes READ javaRuntimes NOTIFY javaChanged)
    Q_PROPERTY(QString gameDirectory READ gameDirectory NOTIFY changed)
    Q_PROPERTY(QString error READ error NOTIFY changed)
public:
    explicit InstanceSettings(Instances* instances, QObject* parent = nullptr);
    void attachJavaJobs(InstanceJobs* jobs);
    QVariantMap instance() const { return m_instance; }
    QVariantMap values() const { return m_values; }
    QVariantMap memory() const { return m_memory; }
    QVariantList javaRuntimes() const { return m_java; }
    QString error() const { return m_error; }
    QString gameDirectory() const;
    Q_INVOKABLE bool open(const QString& path);
    Q_INVOKABLE void close();
    Q_INVOKABLE bool setOption(const QString& key, const QVariant& value);
    Q_INVOKABLE bool addJava(const QUrl& file);
    Q_INVOKABLE void discoverJava();
    Q_INVOKABLE void setMemoryPreviewActive(bool active);
    Q_INVOKABLE bool openDirectory(const QString& kind = QString());
    Q_INVOKABLE void clearError();
    static QVariantMap defaults();
    static QVariantMap memoryPlan(const QVariantMap& instance, const QVariantMap& values);
    static QVariantMap read(const QString& instance, const QVariantMap& global);
    static QString resolveDirectory(const QVariantMap& instance, const QVariantMap& options);
signals:
    void changed();
    void memoryChanged();
    void javaChanged();
    void settingsSaved();
private:
    bool fail(const QString& error);
    bool save(const QVariantMap& values);
    void updateMemory();
    Instances* m_instances;
    QVariantMap m_instance, m_values, m_defaults, m_memory;
    QVariantList m_java;
    QString m_error;
    bool m_dirty = false;
    QTimer m_timer;
    InstanceJobs* m_javaJobs = nullptr;
    int m_javaEpoch = 0;
};
