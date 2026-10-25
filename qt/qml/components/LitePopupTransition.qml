import QtQuick

Transition {
    id: motion
    property bool leaving: false
    property bool compact: false

    ParallelAnimation {
        PropertyAnimation {
            property: "opacity"
            from: motion.leaving ? undefined : 0
            to: motion.leaving ? 0 : 1
            duration: motion.compact ? (motion.leaving ? 80 : 100) : 130
            easing.type: Easing.OutCubic
        }
        SequentialAnimation {
            PropertyAnimation {
                property: "scale"
                from: motion.leaving ? undefined : (motion.compact ? 0.975 : 0.94)
                to: motion.compact ? (motion.leaving ? 0.98 : 1) : (motion.leaving ? 1.006 : 1.012)
                duration: motion.compact ? (motion.leaving ? 80 : 110) : (motion.leaving ? 35 : 135)
                easing.type: Easing.OutCubic
            }
            NumberAnimation {
                property: "scale"
                to: motion.leaving ? (motion.compact ? 0.98 : 0.965) : 1
                duration: motion.compact ? 0 : (motion.leaving ? 95 : 100)
                easing.type: motion.leaving ? Easing.InCubic : Easing.OutCubic
            }
        }
    }
}
