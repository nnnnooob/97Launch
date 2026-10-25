#pragma once
#include <QAbstractListModel>
#include <QTimer>
#include <QVariantMap>
class InstanceJobs;
class Instances;
class GlobalSettings;
class LauncherFlow;

class ServerCatalog : public QAbstractListModel {
    Q_OBJECT
    Q_PROPERTY(int count READ count NOTIFY changed)
    Q_PROPERTY(bool busy READ busy NOTIFY changed)
    Q_PROPERTY(bool stale READ stale NOTIFY changed)
    Q_PROPERTY(QString error READ error NOTIFY changed)
    Q_PROPERTY(QString notice READ notice NOTIFY changed)
    Q_PROPERTY(QString preparingId READ preparingId NOTIFY changed)
    Q_PROPERTY(QVariantMap preparedPack READ preparedPack NOTIFY changed)
public:
    ServerCatalog(Instances* instances,GlobalSettings* settings,LauncherFlow* flow,InstanceJobs* reader,InstanceJobs* transfers,QObject* parent=nullptr);
    int rowCount(const QModelIndex& parent=QModelIndex())const override{return parent.isValid()?0:m_rows.size();}
    QVariant data(const QModelIndex& index,int role)const override;
    QHash<int,QByteArray> roleNames()const override{return {{Qt::UserRole+1,"record"}};}
    int count()const{return m_rows.size();}
    bool busy()const{return m_busy;}
    bool stale()const{return m_stale;}
    QString error()const{return m_error;}
    QString notice()const{return m_notice;}
    QString preparingId()const{return m_preparing;}
    QVariantMap preparedPack()const;
    Q_INVOKABLE void setActive(bool active);
    Q_INVOKABLE void refresh();
    Q_INVOKABLE void copyAddress(const QString& id);
    Q_INVOKABLE void requestInstall(const QString& id);
    Q_INVOKABLE QString suggestedName(const QString& root)const;
    Q_INVOKABLE bool installRelated(const QString& root,const QString& name);
    Q_INVOKABLE void discardPrepared();
signals:
    void changed();
    void installDialogRequested();
    void queued();
private:
    void syncAccess();
    void merge(const QVariantList& rows);
    void invalidateRows();
    int find(const QString& id)const;
    Instances* m_instances;
    GlobalSettings* m_settings;
    LauncherFlow* m_flow;
    InstanceJobs *m_reader,*m_transfers;
    QVariantList m_rows;
    QVariantMap m_pack;
    QTimer m_timer,m_noticeTimer;
    QString m_device,m_error,m_notice,m_preparing,m_packServer;
    bool m_active=false,m_allowed=false,m_busy=false,m_stale=false;
    int m_epoch=0;
};
