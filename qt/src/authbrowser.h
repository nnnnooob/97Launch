#pragma once
#include <QWindow>
#include <QUrl>
#include <memory>

class MicrosoftAuthWindow : public QWindow {
    Q_OBJECT
public:
    explicit MicrosoftAuthWindow(QWindow* owner);
    ~MicrosoftAuthWindow() override;
    void open(const QUrl& authorization, const QUrl& redirect, const QString& dataDirectory);
signals:
    void redirected(const QUrl& url);
    void failed(const QString& message);
    void cancelled();
protected:
    bool event(QEvent* event) override;
private:
    struct Browser;
    std::unique_ptr<Browser> m_browser;
    void resizeBrowser();
};
