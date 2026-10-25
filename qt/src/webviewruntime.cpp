#include "webviewruntime.h"
#include "instancejobs.h"
#include <QTimer>
#ifdef Q_OS_WIN
#include <windows.h>
#include <wrl.h>
#include <WebView2.h>
#endif
WebViewRuntime::WebViewRuntime(InstanceJobs* jobs,QObject* parent):QObject(parent),m_jobs(jobs){
    connect(jobs,&InstanceJobs::changed,this,[this]{if(m_busy){m_detail=m_jobs->detail();emit changed();}});
    connect(jobs,&InstanceJobs::completed,this,[this](const QString& action,const QVariantMap&,const QVariantMap&,const QString&){
        if(action!="settingsInstallWebView")return;
        m_busy=false;
        const auto ready=available();
        m_detail=ready?"WebView2 Runtime 已就绪":"WebView2 安装后仍不可用";
        m_error=ready?QString():"WebView2 安装后仍无法加载，请重试或使用兼容头像继续。";
        emit changed();emit finished(ready);
    });
    connect(jobs,&InstanceJobs::failed,this,[this](const QString& action,const QString& error,const QString&){
        if(action!="settingsInstallWebView")return;
        m_busy=false;m_error=error;m_detail=error;emit changed();emit finished(false);
    });
}
bool WebViewRuntime::available()const{
#ifdef Q_OS_WIN
    LPWSTR version=nullptr;
    const auto result=GetAvailableCoreWebView2BrowserVersionString(nullptr,&version);
    const bool ready=SUCCEEDED(result)&&version&&*version;
    CoTaskMemFree(version);return ready;
#else
    return false;
#endif
}
void WebViewRuntime::ensure(){
    if(m_busy)return;
    m_error.clear();
    if(available()){m_detail="WebView2 Runtime 已就绪";emit changed();QTimer::singleShot(0,this,[this]{emit finished(true);});return;}
    m_busy=true;m_detail="缺少 WebView2，正在准备自动安装…";emit changed();
    if(!m_jobs->settings("settingsInstallWebView")){m_busy=false;m_error=m_jobs->error();emit changed();emit finished(false);}
}
