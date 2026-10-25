#pragma once
#include <QObject>
#include <QProcess>
#include <QQueue>
#include <QVariantMap>
#include <QFileSystemWatcher>
#include <QTimer>
#include <QUrl>
class Instances;
class InstanceSettings;
class InstanceJobs : public QObject {
    Q_OBJECT
    Q_PROPERTY(bool busy READ busy NOTIFY changed)
    Q_PROPERTY(bool mutating READ mutating NOTIFY changed)
    Q_PROPERTY(QString detail READ detail NOTIFY changed)
    Q_PROPERTY(QString error READ error NOTIFY changed)
    Q_PROPERTY(double progress READ progress NOTIFY changed)
    Q_PROPERTY(QVariantList tasks READ tasks NOTIFY changed)
    Q_PROPERTY(QVariantMap prompt READ prompt NOTIFY changed)
public:
    InstanceJobs(Instances* instances, InstanceSettings* settings, QObject* parent = nullptr);
    ~InstanceJobs();
    bool busy() const { return m_process.state() != QProcess::NotRunning || !m_queue.isEmpty(); }
    bool mutating() const;
    QString detail() const { return m_detail; }
    QString error() const { return m_error; }
    double progress() const { return m_progress; }
    QVariantList tasks() const { return m_tasks; }
    bool hasPendingTarget(const QString& root,const QString& name) const;
    QVariantMap prompt() const { return m_prompt; }
    Q_INVOKABLE bool submit(const QString& action, const QVariantMap& args = {});
    Q_INVOKABLE bool importDrop(const QList<QUrl>& urls);
    Q_INVOKABLE QString availableDropName(const QString& root, const QString& requested) const;
    Q_INVOKABLE void answerDrop(const QString& choice, bool applyAll);
    Q_INVOKABLE bool download(const QString& action, const QVariantMap& args = {}, const QString& instancePath = "");
    Q_INVOKABLE bool settings(const QString& action, const QVariantMap& args = {}, const QString& instancePath = "");
    Q_INVOKABLE void taskAction(int id);
    Q_INVOKABLE void retry(int id);
    Q_INVOKABLE void clearFinished();
    Q_INVOKABLE void answer(bool accepted);
    Q_INVOKABLE void copyText(const QString& text);
    Q_INVOKABLE bool revealOutput(const QString& path);
    Q_INVOKABLE QUrl saveSuggestion(const QString& name) const;
    Q_INVOKABLE QVariantMap downloadPreferences() const;
    Q_INVOKABLE void cancel();
    Q_INVOKABLE void cancelRead(const QString& action);
    Q_INVOKABLE void clearError();
    Q_INVOKABLE void watchDirectory(const QString& kind);
    Q_INVOKABLE void watchDownloadTarget(const QString& instancePath, const QString& kind = "mods");
    void setWatchingPaused(bool paused, const QString& oldPath, const QString& newPath);
    Q_INVOKABLE bool openPath(const QString& path, bool select = false);
    Q_INVOKABLE bool copyImage(const QString& path);
    Q_INVOKABLE QVariantMap readConfig(const QUrl& file);
    Q_INVOKABLE bool writeConfig(const QUrl& file, const QVariantMap& config);
    Q_INVOKABLE QString localPath(const QUrl& file) const { return file.toLocalFile(); }
signals:
    void changed();
    void completed(const QString& action, const QVariantMap& result, const QVariantMap& args, const QString& path);
    void partial(const QString& action, const QVariantMap& result, const QVariantMap& args, const QString& path);
    void failed(const QString& action, const QString& error, const QString& path);
    void failedDetailed(const QString& action, const QString& error, const QVariantMap& args, const QString& path);
    void instanceMoved(const QString& oldPath, const QString& newPath);
    void resourcesChanged();
    void downloadQueued(const QString& title, bool showTransfer);
    void returnToLaunch();
    void modificationStateChanged(bool running, const QString& oldPath, const QString& newPath);
private:
    struct Job;
    void startNext();
    void readOutput();
    void finish(int exitCode);
    bool fail(const QString& error);
    void updateTask(const QString& state);
    void applyProgress(const QVariantMap& update);
    void enqueueTask(const Job& job);
    bool knownPath(const QString& path) const;
    struct Job { QString action; QVariantMap args, instance, global; int id; };
    QProcess m_process;
    QFileSystemWatcher m_watch;
    QTimer m_watchDebounce;
    QTimer m_progressNotify;
    QQueue<Job> m_queue;
    Job m_current;
    Instances* m_instances;
    InstanceSettings* m_settings;
    QByteArray m_output, m_stderr;
    QString m_detail, m_error, m_watchKind, m_downloadWatchPath, m_downloadWatchKind;
    QVariantMap m_result;
    QVariantList m_tasks;
    QVariantMap m_prompt;
    QHash<int, Job> m_retries;
    double m_progress = 0;
    int m_nextId = 0;
    bool m_gotResult = false, m_canceled = false;
    bool m_silentCancel = false;
    bool m_watchingPaused = false;
};
