#pragma once

#include <QQuickItem>

class QQmlEngine;

class SkinItem : public QQuickItem
{
    Q_OBJECT
    Q_PROPERTY(qreal yaw READ yaw WRITE setYaw NOTIFY yawChanged)
    Q_PROPERTY(qreal phase READ phase WRITE setPhase NOTIFY phaseChanged)
public:
    explicit SkinItem(QQuickItem* parent = nullptr);
    qreal yaw() const { return m_yaw; }
    qreal phase() const { return m_phase; }
    void setYaw(qreal value);
    void setPhase(qreal value);
signals:
    void yawChanged();
    void phaseChanged();
protected:
    QSGNode* updatePaintNode(QSGNode* oldNode, UpdatePaintNodeData*) override;
private:
    qreal m_yaw = -30;
    qreal m_phase = 0;
};

void registerPreviewGraphics(QQmlEngine& engine);
