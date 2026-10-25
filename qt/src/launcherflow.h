#pragma once
#include <QObject>
#include <QTimer>
class GlobalSettings;

class LauncherFlow : public QObject {
    Q_OBJECT
    Q_PROPERTY(bool active READ active NOTIFY activeChanged)
    Q_PROPERTY(bool authorized READ authorized NOTIFY changed)
    Q_PROPERTY(bool initialized READ initialized NOTIFY changed)
    Q_PROPERTY(QString view READ view NOTIFY changed)
public:
    explicit LauncherFlow(GlobalSettings* settings,QObject* parent=nullptr);
    bool active()const{return m_active;}
    bool authorized()const{return m_authorized;}
    bool initialized()const{return m_initialized;}
    QString view()const{return m_view;}
    Q_INVOKABLE void openUpdates(bool check=true);
    Q_INVOKABLE void openPairing();
    Q_INVOKABLE void continueFromUpdates();
signals:
    void changed();
    void activeChanged();
    void homeRequested();
private:
    void setActive(bool active);
    void setView(const QString& view);
    void prepareStartup();
    GlobalSettings* m_settings;
    QTimer m_verification;
    QString m_view="pairing";
    bool m_active=true,m_authorized=false,m_initialized=false,m_startup=true;
    bool m_updateStateReady=false,m_waitingStartupCheck=false,m_manualUpdate=false;
};
