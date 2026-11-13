#pragma once
#include <QObject>
#include <QVariantList>
#include <QElapsedTimer>
class GlobalSettings;
class Instances;
class Accounts;
class WebViewRuntime;

class LauncherFlow : public QObject {
    Q_OBJECT
    Q_PROPERTY(bool active READ active NOTIFY activeChanged)
    Q_PROPERTY(QVariantList preparationSteps READ preparationSteps NOTIFY changed)
    Q_PROPERTY(QString preparationDetail READ preparationDetail NOTIFY changed)
    Q_PROPERTY(QString preparationError READ preparationError NOTIFY changed)
    Q_PROPERTY(bool preparationBusy READ preparationBusy NOTIFY changed)
    Q_PROPERTY(double preparationProgress READ preparationProgress NOTIFY changed)
    Q_PROPERTY(bool canSkipPreview READ canSkipPreview NOTIFY changed)
public:
    explicit LauncherFlow(GlobalSettings* settings,Instances* instances,Accounts* accounts,WebViewRuntime* runtime,QObject* parent=nullptr);
    bool active()const{return m_active;}
    QVariantList preparationSteps()const;
    QString preparationDetail()const{return m_preparationDetail;}
    QString preparationError()const{return m_preparationError;}
    bool preparationBusy()const{return m_preparationBusy;}
    double preparationProgress()const{return m_preparationCompleted/4.0;}
    bool canSkipPreview()const{return m_preparationStep==3&&!m_preparationBusy&&!m_preparationError.isEmpty();}
    Q_INVOKABLE void retryPreparation();
    Q_INVOKABLE void skipPreview();
signals:
    void changed();
    void activeChanged();
private:
    void setActive(bool active);
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
    bool m_active=true;
};
