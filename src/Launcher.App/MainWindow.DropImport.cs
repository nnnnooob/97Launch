using System.Numerics;
using System.Text.RegularExpressions;
using Launcher.App.Services;
using Launcher.App.ViewModels;
using Launcher.Core.Instances;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI.ViewManagement;

namespace Launcher.App;

public sealed partial class MainWindow
{
    private bool _importingDrop;
    private bool _dropHiding;
    private int _dropVisualVersion;
    private SpriteVisual? _dropPointerGlow;
    private SpriteVisual? _dropCardGlow;
    private CompositionRoundedRectangleGeometry? _dropCardClip;
    private readonly UISettings _dropUiSettings = new();
    private readonly AccessibilitySettings _dropAccessibility = new();
    private readonly CancellationTokenSource _dropCancellation = new();
    private readonly DispatcherTimer _dropLeaveTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    private void InitializeDropImport()
    {
        DropHost.AddHandler(UIElement.DragEnterEvent, new DragEventHandler(DropHost_DragOver), true);
        DropHost.AddHandler(UIElement.DragOverEvent, new DragEventHandler(DropHost_DragOver), true);
        DropHost.AddHandler(UIElement.DragLeaveEvent, new DragEventHandler(DropHost_DragLeave), true);
        DropHost.AddHandler(UIElement.DropEvent, new DragEventHandler(DropHost_Drop), true);
        ElementCompositionPreview.SetIsTranslationEnabled(DropCard, true);
        DropCard.Translation = new Vector3(0, 0, 24);
        DropCardShine.SizeChanged += (_, _) => ResizeDropEffects();
        DropPointerShine.SizeChanged += (_, _) => ResizeDropEffects();
        _dropLeaveTimer.Tick += (_, _) => { _dropLeaveTimer.Stop(); if (!_importingDrop) HideDropOverlay(); };
        Closed += (_, _) => { _dropLeaveTimer.Stop(); _dropCancellation.Cancel(); };
    }

    private InstanceItem? DropTargetInstance()
    {
        var instances = App.Services.GetRequiredService<InstancesViewModel>();
        var selected = App.Services.GetRequiredService<LaunchViewModel>().SelectedInstanceId;
        return selected is not null ? instances.FindInstance(selected) : instances.SelectedInstance;
    }

    private void DropHost_DragOver(object sender, DragEventArgs e)
    {
        if (!App.Services.GetRequiredService<LauncherAuthorizationService>().IsAuthorized) { e.AcceptedOperation = DataPackageOperation.None; return; }
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        _dropLeaveTimer.Stop();
        e.Handled = true;
        e.AcceptedOperation = _importingDrop ? DataPackageOperation.None : DataPackageOperation.Copy;
        e.DragUIOverride.IsCaptionVisible = false;
        if (_importingDrop) return;
        DropHeading.Text = "松开以导入";
        DropTarget.Text = DropTargetInstance() is { } instance
            ? $"Mod → {instance.Name}" : "Mod → 请先选择目标实例";
        DropPackHint.Text = "整合包 → 创建新实例";
        DropBusy.IsActive = false;
        DropBusy.Visibility = Visibility.Collapsed;
        DropIcon.Visibility = Visibility.Visible;
        ShowDropOverlay();
        UpdateDropGlow(e);
    }

    private void ShowDropOverlay()
    {
        if (DropOverlay.Visibility == Visibility.Visible && !_dropHiding) return;
        var entering = DropOverlay.Visibility != Visibility.Visible;
        _dropVisualVersion++;
        _dropHiding = false;
        DropOverlay.IsHitTestVisible = true;
        DropOverlay.Visibility = Visibility.Visible;
        EnsureDropEffects();
        var visual = ElementCompositionPreview.GetElementVisual(DropOverlay);
        var card = ElementCompositionPreview.GetElementVisual(DropCard);
        visual.StopAnimation("Opacity");
        card.StopAnimation("Translation");
        if (!_dropUiSettings.AnimationsEnabled)
        {
            visual.Opacity = 1;
            card.Properties.InsertVector3("Translation", new Vector3(0, 0, 24));
            return;
        }
        var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
        if (entering) fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1);
        fade.Duration = TimeSpan.FromMilliseconds(160);
        visual.StartAnimation("Opacity", fade);
        var slide = visual.Compositor.CreateVector3KeyFrameAnimation();
        if (entering) slide.InsertKeyFrame(0, new Vector3(0, 8, 24));
        slide.InsertKeyFrame(1, new Vector3(0, 0, 24), visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(.2f, .8f), new Vector2(.2f, 1)));
        slide.Duration = fade.Duration;
        card.StartAnimation("Translation", slide);
    }

    private void HideDropOverlay()
    {
        _dropLeaveTimer.Stop();
        if (DropOverlay.Visibility != Visibility.Visible || _dropHiding) return;
        _dropHiding = true;
        DropOverlay.IsHitTestVisible = false;
        DropBusy.IsActive = false;
        var generation = ++_dropVisualVersion;
        var visual = ElementCompositionPreview.GetElementVisual(DropOverlay);
        visual.StopAnimation("Opacity");
        if (!_dropUiSettings.AnimationsEnabled || _dropCancellation.IsCancellationRequested)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            _dropHiding = false;
            return;
        }
        var batch = visual.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        batch.Completed += (_, _) =>
        {
            if (generation == _dropVisualVersion)
            {
                DropOverlay.Visibility = Visibility.Collapsed;
                _dropHiding = false;
            }
            batch.Dispose();
        };
        var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, 0);
        fade.Duration = TimeSpan.FromMilliseconds(120);
        visual.StartAnimation("Opacity", fade);
        var card = ElementCompositionPreview.GetElementVisual(DropCard);
        var slide = visual.Compositor.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(1, new Vector3(0, 6, 24));
        slide.Duration = fade.Duration;
        card.StartAnimation("Translation", slide);
        batch.End();
    }

    private void EnsureDropEffects()
    {
        DropTint.Visibility = _dropAccessibility.HighContrast ? Visibility.Collapsed : Visibility.Visible;
        if (_dropPointerGlow is not null) return;
        _dropPointerGlow = CreateDropGlow(DropPointerShine, new Vector2(360, 280), 48);
        _dropCardGlow = CreateDropGlow(DropCardShine, new Vector2(280, 150), 30);
        var compositor = _dropCardGlow.Compositor;
        _dropCardClip = compositor.CreateRoundedRectangleGeometry();
        _dropCardClip.CornerRadius = new Vector2(15);
        ElementCompositionPreview.GetElementVisual(DropCardShine).Clip = compositor.CreateGeometricClip(_dropCardClip);
        ResizeDropEffects();
    }

    private static SpriteVisual CreateDropGlow(UIElement host, Vector2 size, byte alpha)
    {
        var compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
        var brush = compositor.CreateRadialGradientBrush();
        brush.ColorStops.Add(compositor.CreateColorGradientStop(0, Windows.UI.Color.FromArgb(alpha, 74, 141, 255)));
        brush.ColorStops.Add(compositor.CreateColorGradientStop(.45f, Windows.UI.Color.FromArgb((byte)(alpha / 3), 74, 141, 255)));
        brush.ColorStops.Add(compositor.CreateColorGradientStop(1, Windows.UI.Color.FromArgb(0, 74, 141, 255)));
        var glow = compositor.CreateSpriteVisual();
        glow.Brush = brush;
        glow.Size = size;
        glow.Opacity = 0;
        ElementCompositionPreview.SetElementChildVisual(host, glow);
        return glow;
    }

    private void ResizeDropEffects()
    {
        if (_dropCardClip is not null) _dropCardClip.Size = new Vector2((float)DropCardShine.ActualWidth, (float)DropCardShine.ActualHeight);
        var visual = ElementCompositionPreview.GetElementVisual(DropPointerShine);
        visual.Clip ??= visual.Compositor.CreateInsetClip();
    }

    private void UpdateDropGlow(DragEventArgs e)
    {
        if (_dropPointerGlow is null || _dropCardGlow is null) return;
        var active = _dropUiSettings.AnimationsEnabled && !_dropAccessibility.HighContrast;
        _dropPointerGlow.Opacity = _dropCardGlow.Opacity = active ? 1 : 0;
        if (!active) return;
        var point = e.GetPosition(DropPointerShine);
        _dropPointerGlow.Offset = new Vector3((float)point.X - _dropPointerGlow.Size.X / 2, (float)point.Y - _dropPointerGlow.Size.Y / 2, 0);
        var x = Math.Clamp(point.X / Math.Max(1, DropPointerShine.ActualWidth), 0, 1);
        var y = Math.Clamp(point.Y / Math.Max(1, DropPointerShine.ActualHeight), 0, 1);
        _dropCardGlow.Offset = new Vector3((float)(x * DropCardShine.ActualWidth) - _dropCardGlow.Size.X / 2,
            (float)(y * DropCardShine.ActualHeight) - _dropCardGlow.Size.Y / 2, 0);
    }

    private void DropHost_DragLeave(object sender, DragEventArgs e)
    {
        if (_importingDrop) return;
        var point = e.GetPosition(DropHost);
        if (point.X <= 0 || point.Y <= 0 || point.X >= DropHost.ActualWidth || point.Y >= DropHost.ActualHeight)
            HideDropOverlay();
        else _dropLeaveTimer.Start();
    }

    private async void DropHost_Drop(object sender, DragEventArgs e)
    {
        if (!App.Services.GetRequiredService<LauncherAuthorizationService>().IsAuthorized) { e.AcceptedOperation = DataPackageOperation.None; return; }
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.Handled = true;
        if (_importingDrop) return;
        _importingDrop = true;
        var instance = DropTargetInstance();
        var instances = App.Services.GetRequiredService<InstancesViewModel>();
        var root = instances.FolderSelectionPath ?? instances.Folders.FirstOrDefault()?.Path;
        var content = App.Services.GetRequiredService<InstanceContentService>();
        InstanceContext? context = null;
        string? contextError = null;
        try { if (instance is not null) context = content.Context(instance); }
        catch (Exception ex) { contextError = ex.Message; }
        var deferral = e.GetDeferral();
        try
        {
            string[] paths;
            try
            {
                var items = await e.DataView.GetStorageItemsAsync();
                paths = items.Select(item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
            finally { deferral.Complete(); }
            DropHeading.Text = "正在识别文件…";
            DropTarget.Text = $"正在检查 {paths.Length} 个文件";
            DropPackHint.Text = "识别后自动导入或加入安装队列";
            DropIcon.Visibility = Visibility.Collapsed;
            DropBusy.Visibility = Visibility.Visible;
            DropBusy.IsActive = true;
            if (_dropPointerGlow is not null) _dropPointerGlow.Opacity = 0;
            if (_dropCardGlow is not null) _dropCardGlow.Opacity = 0;
            ShowDropOverlay();
            await ImportDroppedFilesAsync(paths, context, contextError, root, _dropCancellation.Token);
        }
        catch (OperationCanceledException) when (_dropCancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "拖拽导入失败");
            App.Services.GetRequiredService<ToastService>().ShowError("无法导入文件", ex.Message);
        }
        finally { _importingDrop = false; HideDropOverlay(); }
    }

    private async Task ImportDroppedFilesAsync(string[] paths, InstanceContext? context, string? contextError,
        string? root, CancellationToken token)
    {
        var errors = new List<string>();
        var iconCache = Path.Combine(Path.GetTempPath(), "97Launch", "drop-icons");
        var files = await Task.Run(() =>
        {
            var result = new List<DroppedFile>();
            foreach (var path in paths)
            {
                token.ThrowIfCancellationRequested();
                try { result.Add(DroppedFileReader.Read(path, iconCache)); }
                catch (Exception ex) { errors.Add($"{Path.GetFileName(path)}：{ex.Message}"); }
            }
            return result;
        }, token);
        HideDropOverlay();
        var content = App.Services.GetRequiredService<InstanceContentService>();
        var transfers = App.Services.GetRequiredService<TransferViewModel>();
        var modCount = 0;
        var packCount = 0;
        var skipped = 0;
        bool? replaceRemaining = null;
        var packTargets = transfers.Jobs.Where(j => j.State is TransferJobState.Active or TransferJobState.Queued)
            .Select(j => Path.Combine(j.GameDirectory, "versions", j.InstanceName)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index];
            token.ThrowIfCancellationRequested();
            try
            {
                if (file.IsModpack)
                {
                    if (string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException("请先选择游戏文件夹。");
                    var name = AvailablePackName(file.Name, root, packTargets);
                    transfers.EnqueueOperation("导入整合包 · " + name, name, root,
                        ["读取整合包", "安装游戏与资源", "整理实例"], (progress, cancellation) =>
                            App.Services.GetRequiredService<LocalModpackService>().InstallAsync(file.Path, root, name, progress, cancellation),
                        refreshInstances: true, returnToLaunchOnCompletion: true);
                    packTargets.Add(Path.Combine(root, "versions", name));
                    packCount++;
                    continue;
                }
                if (context is null) throw new InvalidOperationException(contextError ?? "请先选择目标实例。");
                if (!context.Instance.IsModdable) throw new InvalidOperationException("目标实例没有 Mod 加载器，请先安装兼容的加载器。");
                var mod = file.Mod!;
                if (mod.ModLoaders is { Count: > 0 } loaders && !loaders.Contains(context.Instance.Loader, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException("加载器不兼容，需要 " + string.Join(" / ", loaders));
                if (mod.GameRequirements is { Count: > 0 } requirements
                    && requirements.All(r => ModDiagnostics.Matches(context.Instance.MinecraftVersionId, r) == false))
                    throw new InvalidOperationException("游戏版本不兼容，需要 " + string.Join(" 或 ", requirements));
                content.EnsureWritable(context);
                var target = InstanceContentService.SafeChild(context.DirectoryFor(ContentKind.Mods),
                    LocalContentReader.EnabledName(Path.GetFileName(file.Path)));
                var overwrite = false;
                if (File.Exists(target))
                {
                    if (GameFolderService.PathsEqual(file.Path, target)
                        || new FileInfo(file.Path).Length == new FileInfo(target).Length
                        && await InstanceModificationService.HashAsync(file.Path, token) == await InstanceModificationService.HashAsync(target, token))
                    { skipped++; continue; }
                    if (replaceRemaining is null)
                    {
                        var applyAll = new CheckBox { Content = "对本批其他同名文件应用此选择" };
                        var panel = new StackPanel { Spacing = 12 };
                        panel.Children.Add(new TextBlock { Text = $"「{context.Instance.Name}」已存在 {Path.GetFileName(target)}。替换前会将旧文件移到回收站。", TextWrapping = TextWrapping.Wrap });
                        panel.Children.Add(applyAll);
                        var choice = await ContentUi.ShowAsync(new ContentDialog
                        {
                            Title = "存在同名 Mod", Content = panel, PrimaryButtonText = "替换",
                            SecondaryButtonText = "跳过", CloseButtonText = "取消剩余导入", DefaultButton = ContentDialogButton.Close,
                        }, DropHost.XamlRoot);
                        if (choice == ContentDialogResult.None) { skipped += files.Count - index; break; }
                        overwrite = choice == ContentDialogResult.Primary;
                        if (applyAll.IsChecked == true) replaceRemaining = overwrite;
                    }
                    else overwrite = replaceRemaining.Value;
                    if (!overwrite) { skipped++; continue; }
                }
                await content.ImportAsync(context, ContentKind.Mods, [file.Path], overwrite, token);
                modCount++;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { errors.Add($"{Path.GetFileName(file.Path)}：{ex.Message}"); }
        }
        if (packCount > 0)
        {
            HideModal();
            _navigation.Navigate(AppPage.Transfer);
        }
        var detail = new List<string>();
        if (modCount > 0) detail.Add($"已将 {modCount} 个 Mod 添加到「{context!.Instance.Name}」" + (context.Shared ? "的共享 Mod 目录" : ""));
        if (packCount > 0) detail.Add($"{packCount} 个整合包已加入安装队列");
        if (skipped > 0) detail.Add($"跳过 {skipped} 个文件");
        if (errors.Count > 0)
        {
            detail.AddRange(errors.Take(3));
            if (errors.Count > 3) detail.Add($"另有 {errors.Count - 3} 个文件未导入，详情见运行日志");
            foreach (var error in errors) App.Services.GetRequiredService<LogBuffer>().Append("拖拽导入：" + error, LogEntryKind.Warning);
        }
        var toasts = App.Services.GetRequiredService<ToastService>();
        var message = string.Join("\n", detail);
        if (errors.Count > 0) toasts.ShowError("部分文件未导入", message);
        else toasts.Show(packCount > 0 ? "开始安装整合包" : "Mod 导入完成", message);
    }

    private static string AvailablePackName(string requested, string root, HashSet<string> reserved)
    {
        var name = string.Concat(requested.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.', ' ');
        if (name.Length > 80) name = name[..80].TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(name)) name = "导入的整合包";
        if (Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase)) name = "_" + name;
        var result = name;
        for (var suffix = 2; Directory.Exists(Path.Combine(root, "versions", result))
            || File.Exists(Path.Combine(root, "versions", result)) || reserved.Contains(Path.Combine(root, "versions", result)); suffix++)
            result = $"{name} ({suffix})";
        ModrinthModpackInstaller.ValidateName(result);
        return result;
    }
}
