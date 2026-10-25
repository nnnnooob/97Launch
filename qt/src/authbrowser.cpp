#include "authbrowser.h"
#include <QDir>
#include <QEvent>
#include <QGuiApplication>
#include <QPointer>
#include <QScreen>
#include <QTimer>
#ifdef Q_OS_WIN
#include <windows.h>
#include <wrl.h>
#include <WebView2.h>
using Microsoft::WRL::Callback;
using Microsoft::WRL::ComPtr;
#endif

struct MicrosoftAuthWindow::Browser {
    bool closing = false;
    bool received = false;
    QUrl redirect;
#ifdef Q_OS_WIN
    bool comInitialized = false;
    ComPtr<ICoreWebView2Environment> environment;
    ComPtr<ICoreWebView2Controller> controller;
    ComPtr<ICoreWebView2> view;
#endif
};

MicrosoftAuthWindow::MicrosoftAuthWindow(QWindow* owner) : m_browser(std::make_unique<Browser>()) {
    setTitle(QStringLiteral("97Launch · 微软登录"));
    setIcon(QGuiApplication::windowIcon());
    setFlags(Qt::Dialog | Qt::WindowTitleHint | Qt::WindowSystemMenuHint | Qt::WindowCloseButtonHint);
    setTransientParent(owner);
    setMinimumSize({360, 480});
    const auto available = (owner ? owner->screen() : QGuiApplication::primaryScreen())->availableGeometry();
    resize(qMin(520, available.width() - 32), qMin(680, available.height() - 48));
    const auto center = owner ? owner->geometry().center() : available.center();
    setPosition(qBound(available.left(), center.x() - width() / 2, available.right() - width()),
                qBound(available.top(), center.y() - height() / 2, available.bottom() - height()));
}

MicrosoftAuthWindow::~MicrosoftAuthWindow() {
    m_browser->closing = true;
#ifdef Q_OS_WIN
    if (m_browser->controller) m_browser->controller->Close();
    m_browser->view.Reset();
    m_browser->controller.Reset();
    m_browser->environment.Reset();
    if (m_browser->comInitialized) CoUninitialize();
#endif
}

void MicrosoftAuthWindow::resizeBrowser() {
#ifdef Q_OS_WIN
    if (!m_browser->controller) return;
    RECT bounds{};
    GetClientRect(reinterpret_cast<HWND>(winId()), &bounds);
    m_browser->controller->put_Bounds(bounds);
    m_browser->controller->NotifyParentWindowPositionChanged();
#endif
}

bool MicrosoftAuthWindow::event(QEvent* event) {
    if (event->type() == QEvent::Close && !m_browser->closing) {
        m_browser->closing = true;
        emit cancelled();
    }
    if (event->type() == QEvent::Resize || event->type() == QEvent::Move) resizeBrowser();
#ifdef Q_OS_WIN
    if (event->type() == QEvent::FocusIn && m_browser->controller)
        m_browser->controller->MoveFocus(COREWEBVIEW2_MOVE_FOCUS_REASON_PROGRAMMATIC);
#endif
    return QWindow::event(event);
}

void MicrosoftAuthWindow::open(const QUrl& authorization, const QUrl& redirect, const QString& dataDirectory) {
    m_browser->redirect = redirect;
#ifdef Q_OS_WIN
    const HRESULT com = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(com)) { emit failed(QStringLiteral("无法初始化微软登录窗口。")); return; }
    m_browser->comInitialized = true;
    if (!QDir().mkpath(dataDirectory)) { emit failed(QStringLiteral("无法创建登录浏览器缓存目录。")); return; }
    show();
    requestActivate();
    const QPointer<MicrosoftAuthWindow> guard(this);
    const auto directory = QDir::toNativeSeparators(dataDirectory).toStdWString();
    const auto result = CreateCoreWebView2EnvironmentWithOptions(nullptr, directory.c_str(), nullptr,
        Callback<ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler>(
        [guard, authorization](HRESULT result, ICoreWebView2Environment* environment) -> HRESULT {
            if (!guard || guard->m_browser->closing) return S_OK;
            if (FAILED(result) || !environment) {
                emit guard->failed(QStringLiteral("无法加载 WebView2，请安装或修复 Microsoft Edge WebView2 Runtime 后重试。"));
                return S_OK;
            }
            guard->m_browser->environment = environment;
            const auto controllerResult = environment->CreateCoreWebView2Controller(reinterpret_cast<HWND>(guard->winId()),
                Callback<ICoreWebView2CreateCoreWebView2ControllerCompletedHandler>(
                [guard, authorization](HRESULT result, ICoreWebView2Controller* controller) -> HRESULT {
                    if (!guard || guard->m_browser->closing) { if (controller) controller->Close(); return S_OK; }
                    if (FAILED(result) || !controller) { emit guard->failed(QStringLiteral("无法创建微软登录浏览器。")); return S_OK; }
                    auto& browser = *guard->m_browser;
                    browser.controller = controller;
                    if (FAILED(controller->get_CoreWebView2(&browser.view)) || !browser.view) {
                        emit guard->failed(QStringLiteral("无法初始化微软登录网页。")); return S_OK;
                    }
                    ComPtr<ICoreWebView2Settings> settings;
                    if (SUCCEEDED(browser.view->get_Settings(&settings))) {
                        settings->put_AreDevToolsEnabled(FALSE);
                        settings->put_AreDefaultContextMenusEnabled(FALSE);
                        settings->put_IsStatusBarEnabled(FALSE);
                        settings->put_IsZoomControlEnabled(FALSE);
                        ComPtr<ICoreWebView2Settings4> autofill;
                        if (SUCCEEDED(settings.As(&autofill))) {
                            autofill->put_IsPasswordAutosaveEnabled(FALSE);
                            autofill->put_IsGeneralAutofillEnabled(FALSE);
                        }
                    }
                    EventRegistrationToken token{};
                    HRESULT registered = browser.view->add_NavigationStarting(Callback<ICoreWebView2NavigationStartingEventHandler>(
                        [guard](ICoreWebView2*, ICoreWebView2NavigationStartingEventArgs* args) -> HRESULT {
                            if (!guard || guard->m_browser->closing) { args->put_Cancel(TRUE); return S_OK; }
                            LPWSTR raw = nullptr;
                            if (FAILED(args->get_Uri(&raw))) return S_OK;
                            const QUrl url(QString::fromWCharArray(raw));
                            CoTaskMemFree(raw);
                            auto& browser = *guard->m_browser;
                            const auto& redirect = browser.redirect;
                            const int defaultPort = redirect.scheme() == "https" ? 443 : 80;
                            if (url.scheme() == redirect.scheme() && url.host() == redirect.host()
                                && url.port(defaultPort) == redirect.port(defaultPort) && url.path() == redirect.path()
                                && url.userInfo().isEmpty()) {
                                args->put_Cancel(TRUE);
                                if (!browser.received) { browser.received = true; emit guard->redirected(url); }
                            } else if (url.scheme() != "https" || !url.userInfo().isEmpty()) {
                                args->put_Cancel(TRUE);
                            } else {
                                guard->setTitle(QStringLiteral("97Launch · 微软登录 — ") + url.host());
                            }
                            return S_OK;
                        }).Get(), &token);
                    if (FAILED(registered)) { emit guard->failed(QStringLiteral("无法接收微软登录回调。")); return S_OK; }
                    browser.view->add_NewWindowRequested(Callback<ICoreWebView2NewWindowRequestedEventHandler>(
                        [guard](ICoreWebView2*, ICoreWebView2NewWindowRequestedEventArgs* args) -> HRESULT {
                            args->put_Handled(TRUE);
                            if (!guard || guard->m_browser->closing) return S_OK;
                            LPWSTR raw = nullptr;
                            if (SUCCEEDED(args->get_Uri(&raw))) {
                                const QUrl url(QString::fromWCharArray(raw));
                                CoTaskMemFree(raw);
                                if (url.scheme() == "https" && url.userInfo().isEmpty()) {
                                    QTimer::singleShot(0, guard, [guard, url] {
                                        if (guard && !guard->m_browser->closing && guard->m_browser->view)
                                            guard->m_browser->view->Navigate(url.toString(QUrl::FullyEncoded).toStdWString().c_str());
                                    });
                                }
                            }
                            return S_OK;
                        }).Get(), &token);
                    browser.view->add_NavigationCompleted(Callback<ICoreWebView2NavigationCompletedEventHandler>(
                        [guard](ICoreWebView2*, ICoreWebView2NavigationCompletedEventArgs* args) -> HRESULT {
                            if (!guard || guard->m_browser->closing || guard->m_browser->received) return S_OK;
                            BOOL success = FALSE;
                            COREWEBVIEW2_WEB_ERROR_STATUS status{};
                            args->get_IsSuccess(&success);
                            args->get_WebErrorStatus(&status);
                            if (!success && status != COREWEBVIEW2_WEB_ERROR_STATUS_OPERATION_CANCELED)
                                emit guard->failed(QStringLiteral("微软登录网页加载失败，请检查网络及代理设置后重试。"));
                            return S_OK;
                        }).Get(), &token);
                    browser.view->add_ProcessFailed(Callback<ICoreWebView2ProcessFailedEventHandler>(
                        [guard](ICoreWebView2*, ICoreWebView2ProcessFailedEventArgs*) -> HRESULT {
                            if (guard && !guard->m_browser->closing) emit guard->failed(QStringLiteral("登录浏览器意外退出，请重新登录。"));
                            return S_OK;
                        }).Get(), &token);
                    guard->resizeBrowser();
                    controller->put_IsVisible(TRUE);
                    controller->MoveFocus(COREWEBVIEW2_MOVE_FOCUS_REASON_PROGRAMMATIC);
                    if (FAILED(browser.view->Navigate(authorization.toString(QUrl::FullyEncoded).toStdWString().c_str())))
                        emit guard->failed(QStringLiteral("无法打开微软授权页面。"));
                    return S_OK;
                }).Get());
            if (FAILED(controllerResult)) emit guard->failed(QStringLiteral("无法创建微软登录窗口。"));
            return S_OK;
        }).Get());
    if (FAILED(result)) emit failed(QStringLiteral("无法启动 WebView2，请安装或修复 Microsoft Edge WebView2 Runtime 后重试。"));
#else
    Q_UNUSED(authorization);
    Q_UNUSED(dataDirectory);
    emit failed(QStringLiteral("当前内嵌微软登录需要 Windows WebView2。"));
#endif
}
