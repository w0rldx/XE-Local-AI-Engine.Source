namespace XE_Local_AI_Engine.Desktop.Linux;

using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.X11.Interop;

internal sealed class GtkDesktopBridge : IAsyncDisposable
{
    private readonly GtkNativeApi _api;
    private readonly nint _view;
    private readonly Uri _origin;
    private readonly NativeWebView _webView;
    private readonly Window _owner;
    private readonly GtkDocumentGate _gate = new();
    private readonly GtkNativeApi.LoadCallback _load;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Queue<string> _seen = new();
    private GtkMicrophoneConsent? _microphone;
    private GtkDownload? _download;
    private ulong _loadSignal;
    private string? _nonce;
    private string? _activeSaveId;
    private CancellationTokenSource? _saving;
    private Task _saveTask = Task.CompletedTask;
    private bool _disposed;
    private bool _notifying;
    private long _bridgeGeneration;

    private GtkDesktopBridge(GtkNativeApi api, nint view, Uri origin, NativeWebView webView, Window owner)
    {
        _api = api;
        _view = api.Ref(view);
        _origin = origin;
        _webView = webView;
        _owner = owner;
        _load = OnLoad;
    }

    internal static async Task<GtkDesktopBridge> CreateAsync(IPlatformHandle? handle, Uri origin, NativeWebView webView, Window owner)
    {
        if (handle is not IGtkWebViewPlatformHandle gtk || gtk.WebKitWebView == 0)
        {
            throw new PlatformNotSupportedException("Native Linux requires the GTK WebView adapter.");
        }

        return await GtkInteropHelper.RunOnGlibThread(() =>
        {
            var api = GtkNativeApi.Create();
            GtkDesktopBridge? bridge = null;
            try
            {
                bridge = new GtkDesktopBridge(api, gtk.WebKitWebView, origin, webView, owner);
                bridge._loadSignal = api.Connect(bridge._view, "load-changed", bridge._load);
                bridge._microphone = new GtkMicrophoneConsent(api, bridge._view, origin, bridge._gate, owner, bridge.VerifyFramesAsync);
                bridge._download = new GtkDownload(api, bridge._view, origin, bridge._gate, bridge.VerifyFramesAsync);
                return bridge;
            }
            catch
            {
                try { bridge?.CloseOnGlib(); }
                finally { bridge?._stopping.Dispose(); api.Dispose(); }
                throw;
            }
        });
    }

    internal async Task ArmAsync()
    {
        var generation = _gate.Generation;
        if (!await GtkInteropHelper.RunOnGlibThread(() => _api.VerifyDocument(_view, _origin)) || !await VerifyFramesAsync())
        {
            throw new InvalidOperationException("The Linux document lacks required restrictions.");
        }

        await using var stream = typeof(GtkDesktopBridge).Assembly.GetManifestResourceStream("DesktopDownloadBridge.js")
            ?? throw new InvalidOperationException("The native export bridge is missing.");
        using var reader = new StreamReader(stream);
        var script = await reader.ReadToEndAsync(_stopping.Token);
        var nonce = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var result = await _webView.InvokeScript(script.Replace("__XE_NONCE__", JsonSerializer.Serialize(nonce), StringComparison.Ordinal))
            .WaitAsync(TimeSpan.FromSeconds(5), _stopping.Token);
        if (result?.Trim('"') != "installed" || !_gate.Arm(generation))
        {
            throw new InvalidOperationException("The native export bridge could not be secured.");
        }

        _nonce = nonce;
        _bridgeGeneration = generation;
        _seen.Clear();
    }

    internal void Invalidate()
    {
        _gate.Invalidate();
        _ = CancelOperationsAsync();
    }

    internal async Task RevokeForBlockedNavigationAsync()
    {
        Invalidate();
        await CancelOperationsAsync();
        await _saveTask;
        await ArmAsync();
    }

    private async Task CancelOperationsAsync()
    {
        if (_saving is not null) { await _saving.CancelAsync(); }
        await GtkInteropHelper.RunOnGlibThread(() =>
        {
            if (!_disposed) { _microphone?.CancelOnGlib(); _download?.CancelOnGlib(); }
            return true;
        });
    }

    internal void ReceiveMessage(string? body)
    {
        if (_disposed || _nonce is null || !_gate.IsCurrent(_bridgeGeneration) || body is null || body.Length > 4096) { return; }
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.GetProperty("nonce").GetString() != _nonce) { return; }
            var kind = json.RootElement.GetProperty("kind").GetString();
            if (kind == "xe-frame-blocked")
            {
                Invalidate();
                _ = NotifyPolicyFailureAsync();
                return;
            }
            if (kind == "xe-save-cancel")
            {
                if (_saving is not null && json.RootElement.GetProperty("id").GetString() == _activeSaveId) { _ = _saving.CancelAsync(); }
                return;
            }
            if (kind == "xe-save-unavailable") { _ = NotifyFailureAsync(_stopping.Token); return; }
            var intent = GtkSaveIntent.Parse(body, _origin, _nonce);
            if (intent is null || _saving is not null || _seen.Contains(intent.Id, StringComparer.Ordinal)) { return; }
            if (_seen.Count == 128) { _seen.Dequeue(); }
            _seen.Enqueue(intent.Id);
            _activeSaveId = intent.Id;
            _saving = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            _saving.CancelAfter(TimeSpan.FromSeconds(90));
            _saveTask = SaveAsync(intent, _bridgeGeneration, _saving.Token);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException) { /* Reject malformed native-only messages. */ }
    }

    private async Task SaveAsync(GtkSaveIntent intent, long generation, CancellationToken cancellationToken)
    {
        try
        {
            if (!await VerifyFramesAsync() || !_gate.IsCurrent(generation)) { return; }
            var destination = await GtkSavePicker.PickAsync(_api, _view, intent.Filename, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (destination is null) { return; }
            var overwrite = File.Exists(destination);
            if (overwrite && !await ConfirmOverwriteAsync(cancellationToken)) { return; }
            cancellationToken.ThrowIfCancellationRequested();
            if (!await VerifyFramesAsync() || !_gate.IsCurrent(generation)) { return; }
            await _download!.SaveAsync(intent, destination, overwrite, generation, cancellationToken);
        }
        catch (OperationCanceledException) { /* Navigation, timeout and window close revoke the save. */ }
        catch (Exception)
        {
            if (!cancellationToken.IsCancellationRequested) { await NotifyFailureAsync(cancellationToken); }
        }
        finally
        {
            if (!_disposed && _nonce == intent.Nonce && !_stopping.IsCancellationRequested)
            {
                try
                {
                    await _webView.InvokeScript($"globalThis.__xeSaveBridge?.finish({JsonSerializer.Serialize(intent.Nonce)}, {JsonSerializer.Serialize(intent.Id)})")
                        .WaitAsync(TimeSpan.FromSeconds(5), _stopping.Token);
                }
                catch (Exception) { /* Navigation can remove the bridge before acknowledgment. */ }
            }

            _saving?.Dispose();
            _saving = null;
            _activeSaveId = null;
        }
    }

    private async Task<bool> VerifyFramesAsync()
    {
        if (_disposed || _stopping.IsCancellationRequested) { return false; }
        var value = await _webView.InvokeScript("window === window.top && !document.querySelector('iframe,frame,object,embed') ? 'clear' : 'blocked'")
            .WaitAsync(TimeSpan.FromSeconds(5), _stopping.Token);
        return value?.Trim('"') == "clear";
    }

    private void OnLoad(nint view, int state, nint data)
    {
        if (state != 0) { return; }
        try
        {
            _gate.Invalidate();
            _microphone?.CancelOnGlib();
            _download?.CancelOnGlib();
            Dispatcher.UIThread.Post(() => { _nonce = null; if (_saving is not null) { _ = _saving.CancelAsync(); } });
        }
        catch (Exception) { _gate.Close(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) { return; }
        _gate.Close();
        await _stopping.CancelAsync();
        await CancelOperationsAsync();
        await _saveTask;
        await GtkInteropHelper.RunOnGlibThread(() => { CloseOnGlib(); return true; });
        _stopping.Dispose();
    }

    private void CloseOnGlib()
    {
        if (_disposed) { return; }
        _disposed = true;
        try { _microphone?.CloseOnGlib(); }
        finally
        {
            try { _download?.CloseOnGlib(); }
            finally
            {
                if (_loadSignal != 0) { _api.Disconnect(_view, _loadSignal); }
                _api.Unref(_view);
                _api.Dispose();
            }
        }
        GC.KeepAlive(this);
    }

    internal static string FailureText => German
        ? "Die native Linux-Ansicht benötigt WebKitGTK 4.1 und eine aktuelle XE-Engine mit den erforderlichen Dokument-Sicherheitsregeln. WPE wird nicht unterstützt. Prüfen Sie die Installation oder starten Sie XE mit --browser."
        : "Native Linux requires WebKitGTK 4.1 and an up-to-date XE engine with the required document security policy. WPE is unsupported. Check the installation or start XE with --browser.";

    private static bool German => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de";
    private async Task NotifyPolicyFailureAsync()
    {
        if (_notifying) { return; }
        _notifying = true;
        try
        {
            _ = await DialogAsync(German
                ? "Eingebettete Frames sind in der nativen Linux-Ansicht nicht erlaubt. Mikrofon und Export wurden gesperrt. Bitte verwenden Sie den Browser."
                : "Embedded frames are not permitted in native Linux. Microphone and exports have been blocked. Please use browser mode.", false, _stopping.Token);
        }
        finally { _notifying = false; }
    }

    private async Task NotifyFailureAsync(CancellationToken cancellationToken = default)
    {
        if (_notifying) { return; }
        _notifying = true;
        try
        {
            _ = await DialogAsync(German
                ? "Der Export konnte nicht gespeichert werden. Unterstützt werden lokale Blob-Exporte bis 50 MiB. Bitte erneut versuchen oder den Browser verwenden."
                : "The export could not be saved. Local Blob exports up to 50 MiB are supported. Retry or use browser mode.", false, cancellationToken);
        }
        finally { _notifying = false; }
    }
    private Task<bool> ConfirmOverwriteAsync(CancellationToken cancellationToken) => DialogAsync(German ? "Die ausgewählte Datei wirklich ersetzen?" : "Replace the selected existing file?", true, cancellationToken);

    private async Task<bool> DialogAsync(string text, bool confirm, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token, cancellationToken);
        if (_disposed || linked.IsCancellationRequested) { return false; }
        var dialog = new Window { Title = "XE AI-Engine", Width = 460, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        var cancel = new Button { Content = confirm ? DesktopText.Cancel : "OK", IsDefault = true, IsCancel = true };
        cancel.Click += (_, _) => dialog.Close(false);
        panel.Children.Add(cancel);
        if (confirm)
        {
            var replace = new Button { Content = German ? "Ersetzen" : "Replace" };
            replace.Click += (_, _) => dialog.Close(true);
            panel.Children.Add(replace);
        }

        dialog.Content = panel;
        await using var cancellation = linked.Token.Register(() => Dispatcher.UIThread.Post(() => dialog.Close(false)));
        if (linked.IsCancellationRequested) { return false; }
        return await dialog.ShowDialog<bool>(_owner);
    }
}
