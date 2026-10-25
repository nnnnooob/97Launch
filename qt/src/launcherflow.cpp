#include "launcherflow.h"
#include "globalsettings.h"

LauncherFlow::LauncherFlow(GlobalSettings* settings,QObject* parent):QObject(parent),m_settings(settings){
    m_verification.setInterval(15*60*1000);
    connect(&m_verification,&QTimer::timeout,this,[this]{m_settings->authorizationAction("verify");});
    connect(settings,&GlobalSettings::authorizationResolved,this,[this](const QString&){
        const auto status=m_settings->authorization().value("status").toString();
        const bool wasAuthorized=m_authorized;
        m_initialized=true;m_authorized=status=="Authorized"||status=="Offline";
        if(m_authorized){
            if(!wasAuthorized)emit homeRequested();
            m_verification.start();
            if(m_view=="pairing"){
                if(m_startup)prepareStartup();else setActive(false);
            }
        }else{
            m_verification.stop();
            if(!m_active||m_view!="update"){setView("pairing");setActive(true);}
        }
        emit changed();
    });
    connect(settings,&GlobalSettings::updatesResolved,this,[this](const QString& action){
        if(action=="settingsUpdateState"){
            m_updateStateReady=true;
            if(m_startup&&m_authorized&&m_view=="pairing")prepareStartup();
        }
        if(action=="settingsUpdateCheck"&&m_waitingStartupCheck){
            m_waitingStartupCheck=false;
            const auto state=m_settings->updates();
            if(state.value("error").toString().isEmpty()&&state.value("latest").toMap().isEmpty()&&!state.value("ready").toBool()){
                QTimer::singleShot(350,this,[this]{if(!m_manualUpdate&&m_active&&m_view=="update"&&m_authorized)continueFromUpdates();});
            }
        }
    });
    QTimer::singleShot(0,this,[this]{m_settings->authorizationAction("verify");});
}
void LauncherFlow::setActive(bool active){if(m_active==active)return;m_active=active;emit activeChanged();}
void LauncherFlow::setView(const QString& view){if(m_view==view)return;m_view=view;emit changed();}
void LauncherFlow::prepareStartup(){
    if(!m_updateStateReady)return;
    const auto state=m_settings->updates();
    if(state.value("startupCheck",true).toBool()){
        m_manualUpdate=false;m_waitingStartupCheck=true;setView("update");m_settings->updateAction("startup");
    }else if(state.value("ready").toBool())setView("update");
    else {m_startup=false;setActive(false);}
}
void LauncherFlow::openUpdates(bool check){
    m_manualUpdate=true;m_waitingStartupCheck=false;setView("update");setActive(true);
    if(check)m_settings->updateAction("check");
}
void LauncherFlow::openPairing(){setView("pairing");setActive(true);}
void LauncherFlow::continueFromUpdates(){
    if(m_settings->updates().value("busy").toBool())return;
    m_waitingStartupCheck=false;
    if(m_authorized){m_startup=false;setActive(false);}
    else setView("pairing");
}
