#include "previewgraphics.h"

#include <QImage>
#include <QMatrix4x4>
#include <QQmlEngine>
#include <QQuickImageProvider>
#include <QQuickWindow>
#include <QSGGeometryNode>
#include <QSGTextureMaterial>
#include <QVector3D>
#include <algorithm>
#include <array>
#include <cmath>

namespace {
QImage makeShadow(int side, int extent, int radius)
{
    QImage image(side, side, QImage::Format_ARGB32_Premultiplied);
    const float half = side * 0.5f - extent - radius;
    const float sigma = extent / 3.0f;
    for (int y = 0; y < side; ++y) {
        auto* line = reinterpret_cast<QRgb*>(image.scanLine(y));
        for (int x = 0; x < side; ++x) {
            const float dx = std::abs(x + 0.5f - side * 0.5f) - half;
            const float dy = std::abs(y + 0.5f - side * 0.5f) - half;
            const float distance = std::max(0.0f, std::hypot(std::max(dx, 0.0f), std::max(dy, 0.0f))
                + std::min(std::max(dx, dy), 0.0f) - radius);
            const int alpha = qRound(255 * std::exp(-distance * distance / (2 * sigma * sigma)));
            line[x] = qRgba(0, 0, 0, alpha);
        }
    }
    return image;
}

class ShadowImages : public QQuickImageProvider
{
public:
    ShadowImages() : QQuickImageProvider(Image), card(makeShadow(64, 12, 8)), navigation(makeShadow(96, 22, 10)) {}
    QImage requestImage(const QString& id, QSize* size, const QSize&) override
    {
        const QImage image = id == "card" ? card : id == "navigation" ? navigation : QImage();
        if (size) *size = image.size();
        return image;
    }
private:
    const QImage card;
    const QImage navigation;
};

// Six small brightness variants share one atlas and one scene graph material.
constexpr int shadeCount = 6;
QImage skinAtlas()
{
    const QImage source = QImage(":/art/steve.png").convertToFormat(QImage::Format_ARGB32);
    if (source.isNull()) return {};
    QImage atlas(source.width() * shadeCount, source.height(), QImage::Format_ARGB32_Premultiplied);
    for (int shade = 0; shade < shadeCount; ++shade) {
        const float brightness = 0.55f + shade * 0.09f;
        for (int y = 0; y < source.height(); ++y) {
            auto* target = reinterpret_cast<QRgb*>(atlas.scanLine(y)) + shade * source.width();
            const auto* pixels = reinterpret_cast<const QRgb*>(source.constScanLine(y));
            for (int x = 0; x < source.width(); ++x) {
                const auto pixel = pixels[x];
                target[x] = qPremultiply(qRgba(qRound(qRed(pixel) * brightness), qRound(qGreen(pixel) * brightness),
                    qRound(qBlue(pixel) * brightness), qAlpha(pixel)));
            }
        }
    }
    return atlas;
}

struct SkinNode : QSGGeometryNode
{
    explicit SkinNode(QSGTexture* texture) : geometry(QSGGeometry::defaultAttributes_TexturedPoint2D(), 0)
    {
        geometry.setDrawingMode(QSGGeometry::DrawTriangles);
        geometry.setVertexDataPattern(QSGGeometry::DynamicPattern);
        setGeometry(&geometry);
        material.setTexture(texture);
        material.setFiltering(QSGTexture::Nearest);
        material.setFlag(QSGMaterial::Blending, true);
        setMaterial(&material);
    }
    ~SkinNode() override { delete material.texture(); }
    QSGGeometry geometry;
    QSGTextureMaterial material;
};

struct Part {
    QVector3D size;
    QVector3D pivot;
    float centerY;
    float swing;
    std::array<QRectF, 6> uv;
};

const std::array<Part, 6> parts = {{
    {{8,8,8}, {0,28,0}, 0, 0, {{{8,8,8,8},{24,8,8,8},{0,8,8,8},{16,8,8,8},{8,0,8,8},{16,0,8,8}}}},
    {{8,12,4}, {0,18,0}, 0, 0, {{{20,20,8,12},{32,20,8,12},{16,20,4,12},{28,20,4,12},{20,16,8,4},{28,16,8,4}}}},
    {{4,12,4}, {-6,24,0}, -6, -1, {{{44,20,4,12},{52,20,4,12},{40,20,4,12},{48,20,4,12},{44,16,4,4},{48,16,4,4}}}},
    {{4,12,4}, {6,24,0}, -6, 1, {{{36,52,4,12},{44,52,4,12},{32,52,4,12},{40,52,4,12},{36,48,4,4},{40,48,4,4}}}},
    {{4,12,4}, {-2,12,0}, -6, 1, {{{4,20,4,12},{12,20,4,12},{0,20,4,12},{8,20,4,12},{4,16,4,4},{8,16,4,4}}}},
    {{4,12,4}, {2,12,0}, -6, -1, {{{20,52,4,12},{28,52,4,12},{16,52,4,12},{24,52,4,12},{20,48,4,4},{24,48,4,4}}}}
}};

struct Face {
    std::array<QVector3D, 4> vertices;
    QRectF uv;
    float depth;
    int shade;
};
}

SkinItem::SkinItem(QQuickItem* parent) : QQuickItem(parent)
{
    setFlag(ItemHasContents, true);
}

void SkinItem::setYaw(qreal value)
{
    if (!std::isfinite(value) || qFuzzyCompare(m_yaw, value)) return;
    m_yaw = value;
    emit yawChanged();
    update();
}

void SkinItem::setPhase(qreal value)
{
    if (!std::isfinite(value) || qFuzzyCompare(m_phase, value)) return;
    m_phase = value;
    emit phaseChanged();
    update();
}

QSGNode* SkinItem::updatePaintNode(QSGNode* oldNode, UpdatePaintNodeData*)
{
    auto* node = static_cast<SkinNode*>(oldNode);
    if (width() <= 0 || height() <= 0) { delete node; return nullptr; }
    if (!node) {
        static const QImage atlas = skinAtlas();
        auto* texture = window()->createTextureFromImage(atlas);
        if (!texture) return nullptr;
        node = new SkinNode(texture);
    }
    QMatrix4x4 camera;
    camera.rotate(-5, 1, 0, 0);
    camera.rotate(m_yaw, 0, 1, 0);
    camera.translate(0, -16, 0);
    const float stride = std::sin(m_phase) * 26;
    const QVector3D light = QVector3D(-0.4f, 0.6f, 1).normalized();
    std::array<Face, 36> faces;
    int faceCount = 0;
    for (const auto& part : parts) {
        auto transform = camera;
        transform.translate(part.pivot);
        transform.rotate(stride * part.swing, 1, 0, 0);
        transform.translate(0, part.centerY, 0);
        const float x = part.size.x() / 2, y = part.size.y() / 2, z = part.size.z() / 2;
        const std::array<std::array<QVector3D, 4>, 6> corners = {{
            {{{-x,-y,z},{x,-y,z},{x,y,z},{-x,y,z}}},
            {{{x,-y,-z},{-x,-y,-z},{-x,y,-z},{x,y,-z}}},
            {{{-x,-y,-z},{-x,-y,z},{-x,y,z},{-x,y,-z}}},
            {{{x,-y,z},{x,-y,-z},{x,y,-z},{x,y,z}}},
            {{{-x,y,z},{x,y,z},{x,y,-z},{-x,y,-z}}},
            {{{-x,-y,-z},{x,-y,-z},{x,-y,z},{-x,-y,z}}}
        }};
        for (int f = 0; f < 6; ++f) {
            Face face{corners[f], part.uv[f], 0, 0};
            for (auto& vertex : face.vertices) { vertex = transform.map(vertex); face.depth += vertex.z() / 4; }
            const auto normal = QVector3D::crossProduct(face.vertices[1] - face.vertices[0], face.vertices[2] - face.vertices[0]).normalized();
            if (normal.z() <= 0) continue;
            face.shade = std::clamp(qRound(std::max(0.0f, QVector3D::dotProduct(normal, light)) * (shadeCount - 1)), 0, shadeCount - 1);
            faces[faceCount++] = face;
        }
    }
    std::sort(faces.begin(), faces.begin() + faceCount, [](const Face& a, const Face& b) { return a.depth < b.depth; });
    node->geometry.allocate(faceCount * 6);
    auto* vertices = node->geometry.vertexDataAsTexturedPoint2D();
    const float scale = static_cast<float>(std::max(0.0, std::min(width() / 25, height() / 38)));
    for (int f = 0; f < faceCount; ++f) {
        const auto& face = faces[f];
        const std::array<QPointF, 4> uv = {{face.uv.bottomLeft(), face.uv.bottomRight(), face.uv.topRight(), face.uv.topLeft()}};
        for (int i : {0, 1, 2, 0, 2, 3}) {
            const auto& p = face.vertices[i];
            (vertices++)->set(width() / 2 + p.x() * scale, height() / 2 - p.y() * scale,
                (uv[i].x() / 64 + face.shade) / shadeCount, uv[i].y() / 64);
        }
    }
    node->geometry.markVertexDataDirty();
    node->markDirty(QSGNode::DirtyGeometry);
    return node;
}

void registerPreviewGraphics(QQmlEngine& engine)
{
    qmlRegisterType<SkinItem>("LaunchPreview", 1, 0, "SkinItem");
    engine.addImageProvider("shadows", new ShadowImages);
}
