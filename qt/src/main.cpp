#include <QGuiApplication>
#include <QQmlApplicationEngine>
#include <QQmlContext>
#include <QFile>
#include <QDir>
#include <QIcon>
#include <QFont>
#include <QDateTime>
#include <mutex>
#include <QWindow>
#include <QScreen>
#include <QStyleHints>
#include <QLockFile>
#include <QLocalServer>
#include <QLocalSocket>
#include <QCryptographicHash>
#include <QThread>
#include <QAbstractNativeEventFilter>
#include "accounts.h"
#include "instances.h"
#include "instancesettings.h"
#include "instancejobs.h"
#include "globalsettings.h"
#include "launcherflow.h"
#include "webviewruntime.h"
#include "launchstatus.h"
#include "gamelaunch.h"

#ifdef Q_OS_WIN
#include <windows.h>
#include <dwmapi.h>
#endif

class WindowActions : public QObject, public QAbstractNativeEventFilter
{
    Q_OBJECT
public:
    Q_INVOKABLE bool animationsEnabled() const {
#ifdef Q_OS_WIN
        BOOL enabled = TRUE;
        SystemParametersInfoW(SPI_GETCLIENTAREAANIMATION, 0, &enabled, 0);
        return enabled;
#else
        return true;
#endif
    }
    Q_INVOKABLE bool highContrast() const {
#ifdef Q_OS_WIN
        HIGHCONTRASTW state{sizeof(HIGHCONTRASTW)};
        return SystemParametersInfoW(SPI_GETHIGHCONTRAST, sizeof(state), &state, 0) && (state.dwFlags & HCF_HIGHCONTRASTON);
#else
        return false;
#endif
    }
    Q_INVOKABLE bool startMove(QWindow* window) { return window && window->startSystemMove(); }
    Q_INVOKABLE void minimize(QWindow* window)
    {
        if (!window) return;
#ifdef Q_OS_WIN
        if (PostMessageW(reinterpret_cast<HWND>(window->winId()), WM_SYSCOMMAND, SC_MINIMIZE, 0)) return;
#endif
        window->showMinimized();
    }

    void attachWindow(QWindow* window)
    {
#ifdef Q_OS_WIN
        m_window = reinterpret_cast<HWND>(window->winId());
        QCoreApplication::instance()->installNativeEventFilter(this);
        // Keep DWM's caption/minimize styles while drawing the whole client area ourselves.
        const auto style = GetWindowLongPtrW(m_window, GWL_STYLE);
        SetWindowLongPtrW(m_window, GWL_STYLE,
            (style | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX | WS_THICKFRAME) & ~LONG_PTR(WS_MAXIMIZEBOX));
        SetWindowPos(m_window, nullptr, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        const DWMNCRENDERINGPOLICY renderingPolicy = DWMNCRP_ENABLED;
        DwmSetWindowAttribute(m_window, DWMWA_NCRENDERING_POLICY, &renderingPolicy, sizeof(renderingPolicy));
        const MARGINS frameMargins = {1, 1, 1, 1};
        DwmExtendFrameIntoClientArea(m_window, &frameMargins);
        const DWORD roundCorners = 2;
        DwmSetWindowAttribute(m_window, 33, &roundCorners, sizeof(roundCorners));
        const COLORREF borderColor = RGB(217, 221, 227);
        DwmSetWindowAttribute(m_window, 34, &borderColor, sizeof(borderColor));
#else
        Q_UNUSED(window);
#endif
    }

    ~WindowActions() override { QCoreApplication::instance()->removeNativeEventFilter(this); }

    bool nativeEventFilter(const QByteArray&, void* message, qintptr* result) override
    {
#ifdef Q_OS_WIN
        const auto* event = static_cast<MSG*>(message);
        if (event->hwnd != m_window) return false;
        if (event->message == WM_NCCALCSIZE && event->wParam) {
            *result = 0;
            return true;
        }
        if (event->message == WM_NCHITTEST) {
            *result = HTCLIENT;
            return true;
        }
        if (event->message == WM_SYSCOMMAND
            && ((event->wParam & 0xfff0) == SC_SIZE || (event->wParam & 0xfff0) == SC_MAXIMIZE)) {
            *result = 0;
            return true;
        }
#endif
        return false;
    }
private:
#ifdef Q_OS_WIN
    HWND m_window = nullptr;
#endif
};

int main(int argc, char** argv)
{
    QGuiApplication app(argc, argv);
    // Qt Quick uses 24 pixels per line: five lines match PCL's 120-pixel wheel step.
    app.styleHints()->setWheelScrollLines(5);
    app.setApplicationName("97Launch Lite Preview");
    app.setApplicationVersion(QStringLiteral(LAUNCH_PREVIEW_VERSION));
    app.setOrganizationName("97Launch");
    app.setWindowIcon(QIcon(":/art/app-icon.ico"));
    app.setFont(QFont("Microsoft YaHei UI", 10));
    qInstallMessageHandler([](QtMsgType, const QMessageLogContext& context, const QString& message) {
        static std::mutex logMutex;
        const std::lock_guard<std::mutex> lock(logMutex);
        static QFile log([]{
            const auto directory=qEnvironmentVariable("LOCALAPPDATA")+"/97LaunchQt/logs";
            QDir().mkpath(directory);
            return directory+"/qt.log";
        }());
        if (!log.isOpen() && !log.open(QIODevice::Append | QIODevice::Text)) return;
        log.write(QDateTime::currentDateTime().toString(Qt::ISODateWithMs).toUtf8() + ' '
            + (context.category ? context.category : "default") + ' ' + message.toUtf8() + '\n');
        log.flush();
    });
    const auto startupError=[](const QString& message){
        qCritical().noquote()<<message;
#ifdef Q_OS_WIN
        MessageBoxW(nullptr,message.toStdWString().c_str(),L"97Launch",MB_OK|MB_ICONERROR);
#endif
        return 1;
    };
    const auto dataDirectory=qEnvironmentVariable("LOCALAPPDATA",QDir::homePath()+"/AppData/Local")+"/97LaunchQt";
    if(!QDir().mkpath(dataDirectory))return startupError("无法创建本地数据目录，请检查目录权限或磁盘空间。");
    QLockFile instanceLock(dataDirectory+"/instance.lock");
    instanceLock.setStaleLockTime(0);
    const auto serverName="97LaunchQt-ui-"+QString::fromLatin1(QCryptographicHash::hash(QDir::cleanPath(dataDirectory).toCaseFolded().toUtf8(),QCryptographicHash::Sha256).toHex().left(24));
    if(!instanceLock.tryLock(0)){
        if(instanceLock.error()!=QLockFile::LockFailedError)return startupError("无法锁定启动器数据目录，请检查目录权限。");
#ifdef Q_OS_WIN
        qint64 pid=0;QString host,name;
        if(instanceLock.getLockInfo(&pid,&host,&name)&&pid>0&&pid<=MAXDWORD)AllowSetForegroundWindow(static_cast<DWORD>(pid));
#endif
        for(int attempt=0;attempt<4;++attempt){
            QLocalSocket activation;
            activation.connectToServer(serverName);
            if(activation.waitForConnected(500)){activation.disconnectFromServer();return 0;}
            if(attempt<3)QThread::msleep(100);
        }
        qInfo()<<"启动器已运行，未创建第二个实例";return 0;
    }
    QLocalServer activationServer;
    activationServer.setSocketOptions(QLocalServer::UserAccessOption);
    QLocalServer::removeServer(serverName);
    if(!activationServer.listen(serverName))return startupError("无法创建启动器窗口激活通道："+activationServer.errorString());
    WindowActions windowActions;
    Accounts accounts;
    Instances instances;
    InstanceSettings instanceSettings(&instances);
    InstanceJobs instanceJobs(&instances, &instanceSettings);
    InstanceJobs dropJobs(&instances, &instanceSettings);
    QObject::connect(&dropJobs, &InstanceJobs::completed, &instanceJobs,
        [&instanceJobs](const QString&, const QVariantMap&, const QVariantMap&, const QString&) { emit instanceJobs.resourcesChanged(); });
    InstanceJobs catalogJobs(&instances, &instanceSettings);
    InstanceJobs resourceStateJobs(&instances, &instanceSettings);
    InstanceJobs settingsJobs(&instances, &instanceSettings);
    InstanceJobs updateJobs(&instances, &instanceSettings);
    GlobalSettings globalSettings(&instances);
    globalSettings.attachJobs(&settingsJobs, &instanceJobs, &updateJobs);
    InstanceJobs runtimeJobs(&instances, &instanceSettings);
    WebViewRuntime webViewRuntime(&runtimeJobs);
    accounts.attachWebViewRuntime(&webViewRuntime);
    LauncherFlow launcherFlow(&globalSettings, &instances, &accounts, &webViewRuntime);
    InstanceJobs launchStatusJobs(&instances, &instanceSettings);
    InstanceJobs instanceJavaJobs(&instances, &instanceSettings);
    instanceSettings.attachJavaJobs(&instanceJavaJobs);
    LaunchStatus launchStatus(&instances, &accounts, &instanceSettings, &globalSettings, &launchStatusJobs);
    QObject::connect(&instanceJobs, &InstanceJobs::modificationStateChanged, &instanceJobs, &InstanceJobs::setWatchingPaused);
    QObject::connect(&instanceJobs, &InstanceJobs::modificationStateChanged, &resourceStateJobs, &InstanceJobs::setWatchingPaused);
    QObject::connect(&instanceJobs, &InstanceJobs::modificationStateChanged, &launchStatus, &LaunchStatus::setWatchingPaused);
    GameLaunch gameLaunch(&instances, &accounts, &globalSettings, &launcherFlow, &launchStatus);
    QQmlApplicationEngine engine;
    engine.rootContext()->setContextProperty("windowActions", &windowActions);
    engine.rootContext()->setContextProperty("accountService", &accounts);
    engine.rootContext()->setContextProperty("instanceService", &instances);
    engine.rootContext()->setContextProperty("instanceSettings", &instanceSettings);
    engine.rootContext()->setContextProperty("instanceJobs", &instanceJobs);
    engine.rootContext()->setContextProperty("dropJobs", &dropJobs);
    engine.rootContext()->setContextProperty("catalogJobs", &catalogJobs);
    engine.rootContext()->setContextProperty("resourceStateJobs", &resourceStateJobs);
    engine.rootContext()->setContextProperty("settingsJobs", &settingsJobs);
    engine.rootContext()->setContextProperty("updateJobs", &updateJobs);
    engine.rootContext()->setContextProperty("globalSettings", &globalSettings);
    engine.rootContext()->setContextProperty("launcherFlow", &launcherFlow);
    engine.rootContext()->setContextProperty("launchStatusJobs", &launchStatusJobs);
    engine.rootContext()->setContextProperty("instanceJavaJobs", &instanceJavaJobs);
    engine.rootContext()->setContextProperty("launchStatus", &launchStatus);
    engine.rootContext()->setContextProperty("gameLaunch", &gameLaunch);
    const QUrl url(QStringLiteral("qrc:/qml/Main.qml"));
    QObject::connect(&engine, &QQmlApplicationEngine::objectCreated, &app,
        [url](QObject* object, const QUrl& objectUrl) {
            if (!object && objectUrl == url) QCoreApplication::exit(1);
        }, Qt::QueuedConnection);
    engine.load(url);
    if (engine.rootObjects().isEmpty()) return 1;
    auto* window = qobject_cast<QWindow*>(engine.rootObjects().first());
    if (!window) return 1;
    QObject::connect(&activationServer,&QLocalServer::newConnection,window,[&activationServer,window]{
        while(auto* socket=activationServer.nextPendingConnection()){socket->close();socket->deleteLater();}
        if(window->windowState()==Qt::WindowMinimized)window->showNormal();else window->show();
        window->raise();window->requestActivate();
#ifdef Q_OS_WIN
        SetForegroundWindow(reinterpret_cast<HWND>(window->winId()));
#endif
    });
    windowActions.attachWindow(window);
    const auto available = window->screen()->availableGeometry();
    window->setPosition(available.center() - QPoint(window->width() / 2, window->height() / 2));
    window->show();
    const auto ticket=app.arguments().indexOf("--update-ticket");
    if(ticket>=0&&ticket+1<app.arguments().size())
        updateJobs.settings("settingsAcknowledge",{{"ticket",app.arguments()[ticket+1]}});
    updateJobs.settings("settingsUpdateState",{{"startupTicket",ticket>=0}});
    return app.exec();
}

#include "main.moc"

