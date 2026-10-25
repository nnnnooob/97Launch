# Instance task worker

This console worker contains a source snapshot of the original WinUI launcher's
pure C# instance services and Core modules. It has no WinUI or Windows App SDK
dependency. `Adapters.cs` supplies Qt folder/context integration. The Qt process
starts it for an explicit task and it exits after one result; it is not resident.

Protocol: one UTF-8 JSON request on stdin, JSON progress/result/error lines on
stdout. A subsequent `cancel` line requests cooperative cancellation. No account
credentials are read or sent. The original services retain their path validation,
download verification, modification journals, backups and rollback logic.

Build: `dotnet publish InstanceWorker.csproj -c Release -o ../worker`.
Source reference: `../../97Launch/src/Launcher.Core` and
`../../97Launch/src/Launcher.App/Services`, snapshot 2026-10-25.

Download and transfer actions are dispatched by `DownloadActions.cs`. They share
the original Minecraft installer, community resource resolver, Mod dependency
installer and online/local modpack installers. `SettingsStore.RequestDefaults`
merges Qt's effective launch options into the saved download preferences.

Progress also includes `stage`, `fraction`, `preserve` and `speed`; the Qt queue
uses the WinUI stage weights and preserves parallel stage progress. A `question`
message pauses an operation for confirmation (Mod replacement or missing
CurseForge pack files). Reply on stdin with `{"id":1,"answer":true}` or `false`.
Cancellation also releases pending questions.

Catalog reads and installed-Mod identification run in separate transient workers
from the FIFO mutation queue. Network searches can publish `partial` results
before the other platform finishes. No worker loads WinUI or retains passwords.
