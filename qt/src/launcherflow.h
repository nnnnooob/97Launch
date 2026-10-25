#pragma once
#include <QObject>
#include <QTimer>
#include <QVariantList>
#include <QElapsedTimer>
class GlobalSettings;
class Instances;
class Accounts;
class WebViewRuntime;

class LauncherFlow : public QObject {
    Q_OBJECT
    Q_PROPERTY(bool active READ active NOTIFY activeChanged)
    Q_PROPERTY(bool authorized READ authorized NOTIFY changed)
    Q_PROPERTY(bool initialized READ initialized NOTIFY changed)
    Q_PROPERTY(QString view READ view NOTIFY changed)
    Q_PROPERTY(QVariantList preparationSteps READ preparationSteps NOTIFY changed)
    Q_PROPERTY(QString preparationDetail READ preparationDetail NOTIFY changed)
    Q_PROPERTY(QString preparationError READ preparationError NOTIFY changed)
    Q_PROPERTY(bool preparationBusy READ preparationBusy NOTIFY changed)
    Q_PROPERTY(double preparationProgress READ preparationProgress NOTIFY changed)
    Q_PROPERTY(bool canSkipPreview READ canSkipPreview NOTIFY changed)
public:
    explicit LauncherFlow(GlobalSettings* settings,Instances* instances,Accounts* accounts,WebViewRuntime* runtime,QObject* parent=nullptr);
    bool active()const{return m_active;}
    bool authorized()const{return m_authorized;}
    bool initialized()const{return m_initialized;}
    QString view()const{return m_view;}
    QVariantList preparationSteps()const;
    QString preparationDetail()const{return m_preparationDetail;}
    QString preparationError()const{return m_preparationError;}
    bool preparationBusy()const{return m_preparationBusy;}
    double preparationProgress()const{return m_preparationCompleted/4.0;}
    bool canSkipPreview()const{return m_preparationStep==3&&!m_preparationBusy&&!m_preparationError.isEmpty();}
    Q_INVOKABLE void retryPreparation();
    Q_INVOKABLE void skipPreview();
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
    void enterMain();
    void runPreparationStep();
    void finishPreparationStep(const QString& detail);
    void failPreparation(const QString& error);
    void prepareAvatar();
    GlobalSettings* m_settings;
    Instances* m_instances;
    Accounts* m_accounts;
    WebViewRuntime* m_runtime;
    QElapsedTimer m_stepTime;
    QString m_preparationDetail,m_preparationError;
    int m_preparationStep=0,m_preparationCompleted=0;
    bool m_preparationBusy=false,m_preparationDone=false;
    QTimer m_verification;
    QString m_view="pairing";
    bool m_active=true,m_authorized=false,m_initialized=false,m_startup=true;
    bool m_updateStateReady=false,m_waitingStartupCheck=false,m_manualUpdate=false;
};
