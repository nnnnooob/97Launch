#pragma once
#include <QObject>
#include <QString>
class InstanceJobs;
class WebViewRuntime : public QObject {
    Q_OBJECT
    Q_PROPERTY(bool available READ available NOTIFY changed)
    Q_PROPERTY(bool busy READ busy NOTIFY changed)
    Q_PROPERTY(QString detail READ detail NOTIFY changed)
    Q_PROPERTY(QString error READ error NOTIFY changed)
public:
    explicit WebViewRuntime(InstanceJobs* jobs, QObject* parent=nullptr);
    bool available() const;
    bool busy() const { return m_busy; }
    QString detail() const { return m_detail; }
    QString error() const { return m_error; }
    Q_INVOKABLE void ensure();
signals:
    void changed();
    void finished(bool success);
private:
    InstanceJobs* m_jobs;
    bool m_busy=false;
    QString m_detail,m_error;
};
