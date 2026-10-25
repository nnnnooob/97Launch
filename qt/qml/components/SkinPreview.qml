import QtQuick
import LaunchPreview 1.0

SkinItem {
    id: preview

    property bool walking: true

    function resetPose() {
        yaw = -30;
    }

    Timer {
        interval: 33
        repeat: true
        running: preview.walking && preview.visible
        property real startPhase: 0
        property double startedAt: 0
        onRunningChanged: if (running) {
            startPhase = preview.phase;
            startedAt = Date.now();
        }
        onTriggered: preview.phase = (startPhase + (Date.now() - startedAt) * Math.PI * 2 / 1100) % (Math.PI * 2)
    }
    DragHandler {
        id: rotationDrag
        property real initialYaw: 0
        target: null
        yAxis.enabled: false
        onActiveChanged: if (active)
            initialYaw = preview.yaw
        onTranslationChanged: if (active)
            preview.yaw = initialYaw + activeTranslation.x * 0.65
    }
}
