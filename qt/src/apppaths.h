#pragma once
#include <QCoreApplication>
#include <QFileInfo>
inline QString applicationRoot() {
    const auto portable = qEnvironmentVariable("LAUNCH_PORTABLE_EXE");
    return portable.isEmpty() ? QCoreApplication::applicationDirPath() : QFileInfo(portable).absolutePath();
}
