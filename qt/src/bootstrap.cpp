#include <windows.h>
#include <shlobj.h>
#include <shellapi.h>
#include <bcrypt.h>
#include <fdi.h>
#include <filesystem>
#include <fstream>
#include <memory>
#include <stdexcept>
#include <string>
#include <vector>
#include <cstdlib>
#include <algorithm>
#include <cstring>
#include <cstdio>

namespace fs = std::filesystem;
namespace {
const unsigned char* payload;
size_t payloadSize;
fs::path extraction;
std::string extractionError;
struct Reader { long offset = 0; };
void* DIAMONDAPI allocate(ULONG size) { return std::malloc(size); }
void DIAMONDAPI release(void* memory) { std::free(memory); }
INT_PTR DIAMONDAPI openCab(char*, int, int) { return reinterpret_cast<INT_PTR>(new Reader); }
UINT DIAMONDAPI readCab(INT_PTR handle, void* buffer, UINT size) {
    auto* reader = reinterpret_cast<Reader*>(handle);
    size = static_cast<UINT>(std::min<size_t>(size, payloadSize - reader->offset));
    std::memcpy(buffer, payload + reader->offset, size); reader->offset += size; return size;
}
long DIAMONDAPI seekCab(INT_PTR handle, long offset, int origin) {
    auto* reader = reinterpret_cast<Reader*>(handle);
    const auto next = static_cast<long long>(offset) + (origin == SEEK_SET ? 0 : origin == SEEK_CUR ? reader->offset : payloadSize);
    if (next < 0 || next > static_cast<long long>(payloadSize)) return -1;
    reader->offset = static_cast<long>(next); return reader->offset;
}
int DIAMONDAPI closeCab(INT_PTR handle) { delete reinterpret_cast<Reader*>(handle); return 0; }
UINT DIAMONDAPI writeFile(INT_PTR handle, void* buffer, UINT size) {
    DWORD written = 0;
    return WriteFile(reinterpret_cast<HANDLE>(handle), buffer, size, &written, nullptr) ? written : static_cast<UINT>(-1);
}
std::wstring widen(const std::string& value) {
    const int size = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), nullptr, 0);
    if (!size) throw std::runtime_error("Invalid package file name");
    std::wstring result(size, L'\0');
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), result.data(), size);
    return result;
}
INT_PTR DIAMONDAPI notification(FDINOTIFICATIONTYPE type, PFDINOTIFICATION info) {
    try {
        if (type == fdintCOPY_FILE) {
            const fs::path relative(widen(info->psz1));
            if (relative.empty() || relative.is_absolute() || relative.has_root_name() || relative.wstring().find(L':') != std::wstring::npos)
                throw std::runtime_error("Invalid package path");
            for (const auto& part : relative) if (part == L".." || part == L".") throw std::runtime_error("Invalid package path");
            const auto destination = extraction / relative;
            fs::create_directories(destination.parent_path());
            const auto file = CreateFileW(destination.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (file == INVALID_HANDLE_VALUE) throw std::runtime_error("Cannot extract package file");
            return reinterpret_cast<INT_PTR>(file);
        }
        if (type == fdintCLOSE_FILE_INFO) return CloseHandle(reinterpret_cast<HANDLE>(info->hf)) ? TRUE : FALSE;
        if (type == fdintNEXT_CABINET) return -1;
        return 0;
    } catch (const std::exception& error) { extractionError = error.what(); return -1; }
}
std::wstring quote(const std::wstring& value) {
    std::wstring result = L"\""; size_t slashes = 0;
    for (const auto character : value) {
        if (character == L'\\') { ++slashes; continue; }
        result.append(slashes * (character == L'"' ? 2 : 1), L'\\'); slashes = 0;
        if (character == L'"') result += L'\\';
        result += character;
    }
    result.append(slashes * 2, L'\\'); return result + L'"';
}
std::wstring payloadHash() {
    BCRYPT_ALG_HANDLE algorithm = nullptr; unsigned char digest[32];
    if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) < 0) throw std::runtime_error("Cannot initialize package hash");
    const auto status = BCryptHash(algorithm, nullptr, 0, const_cast<PUCHAR>(payload), static_cast<ULONG>(payloadSize), digest, sizeof(digest));
    BCryptCloseAlgorithmProvider(algorithm, 0);
    if (status < 0) throw std::runtime_error("Cannot calculate package hash");
    std::wstring result; const wchar_t* hex = L"0123456789abcdef";
    for (const auto value : digest) { result += hex[value >> 4]; result += hex[value & 15]; }
    return result;
}
}

int WINAPI wWinMain(HINSTANCE module, HINSTANCE, PWSTR, int) {
    try {
        const auto resource = FindResourceW(module, MAKEINTRESOURCEW(99), RT_RCDATA);
        if (!resource) throw std::runtime_error("Missing application package");
        payloadSize = SizeofResource(module, resource);
        payload = static_cast<const unsigned char*>(LockResource(LoadResource(module, resource)));
        if (!payload || !payloadSize) throw std::runtime_error("Empty application package");
        const auto hash = payloadHash();
        PWSTR local = nullptr;
        if (FAILED(SHGetKnownFolderPath(FOLDERID_LocalAppData, KF_FLAG_CREATE, nullptr, &local))) throw std::runtime_error("Cannot locate local application data");
        const fs::path cache = fs::path(local) / L"97LaunchQt" / L"runtime";
        CoTaskMemFree(local);
        const auto runtime = cache / hash;
        const auto mutexName = L"Local\\97LaunchQt-unpack-" + hash;
        const auto mutex = CreateMutexW(nullptr, FALSE, mutexName.c_str());
        if (!mutex) throw std::runtime_error("Cannot lock application package");
        const auto wait = WaitForSingleObject(mutex, 120000);
        if (wait != WAIT_OBJECT_0 && wait != WAIT_ABANDONED) { CloseHandle(mutex); throw std::runtime_error("Another application is still extracting this version"); }
        try {
            if (!fs::is_regular_file(runtime / L".complete")) {
                fs::create_directories(cache);
                extraction = cache / (hash + L"." + std::to_wstring(GetCurrentProcessId()) + L".tmp");
                // A previous interrupted extraction is never reused or overwritten.
                if (!fs::create_directory(extraction)) throw std::runtime_error("Extraction directory already exists; please retry");
                ERF error{};
                const auto cabinet = FDICreate(allocate, release, openCab, readCab, writeFile, closeCab, seekCab, cpuUNKNOWN, &error);
                if (!cabinet) throw std::runtime_error("Cannot initialize package extraction");
                char cabinetName[] = "payload.cab", cabinetPath[] = "";
                const auto copied = FDICopy(cabinet, cabinetName, cabinetPath, 0, notification, nullptr, nullptr);
                FDIDestroy(cabinet);
                if (!copied) throw std::runtime_error(extractionError.empty() ? "Cannot extract application package" : extractionError);
                if (!fs::is_regular_file(extraction / L"97LaunchQt.exe") || !fs::is_regular_file(extraction / L"worker/97Launch.InstanceWorker.exe"))
                    throw std::runtime_error("Application package is incomplete");
                std::ofstream(extraction / L".complete") << "ready";
                fs::rename(extraction, runtime);
            }
        } catch (...) { ReleaseMutex(mutex); CloseHandle(mutex); throw; }
        ReleaseMutex(mutex); CloseHandle(mutex);
        std::vector<wchar_t> executable(32768);
        if (!GetModuleFileNameW(nullptr, executable.data(), static_cast<DWORD>(executable.size()))) throw std::runtime_error("Cannot locate launcher");
        const fs::path original(executable.data());
        if (!SetEnvironmentVariableW(L"LAUNCH_PORTABLE_EXE", original.c_str()) ||
            !SetEnvironmentVariableW(L"LAUNCH_PORTABLE_PID", std::to_wstring(GetCurrentProcessId()).c_str()))
            throw std::runtime_error("Cannot initialize portable launcher");
        int count = 0; auto arguments = CommandLineToArgvW(GetCommandLineW(), &count);
        if (!arguments) throw std::runtime_error("Cannot read launcher arguments");
        const auto gui = runtime / L"97LaunchQt.exe";
        std::wstring command = quote(gui.wstring());
        for (int i = 1; i < count; ++i) command += L" " + quote(arguments[i]);
        LocalFree(arguments);
        STARTUPINFOW startup{}; startup.cb = sizeof(startup); PROCESS_INFORMATION child{};
        if (!CreateProcessW(gui.c_str(), command.data(), nullptr, nullptr, FALSE, 0, nullptr, original.parent_path().c_str(), &startup, &child))
            throw std::runtime_error("Cannot start application (Windows error " + std::to_string(GetLastError()) + ")");
        CloseHandle(child.hThread); WaitForSingleObject(child.hProcess, INFINITE);
        DWORD exitCode = 1; GetExitCodeProcess(child.hProcess, &exitCode); CloseHandle(child.hProcess);
        return static_cast<int>(exitCode);
    } catch (const std::exception& error) {
        const auto message = L"97Launch 启动失败。请确认本机磁盘空间和文件权限。\n\n" + widen(error.what());
        MessageBoxW(nullptr, message.c_str(), L"97Launch", MB_ICONERROR | MB_OK); return 1;
    }
}
