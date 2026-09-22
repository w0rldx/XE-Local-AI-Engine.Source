namespace XE_Local_AI_Engine.Desktop.Linux;

using System.Security.Cryptography;
using Avalonia.X11.Interop;

internal sealed class GtkDownload
{
    private readonly GtkNativeApi _api;
    private readonly nint _view;
    private readonly nint _context;
    private readonly Uri _origin;
    private readonly GtkDocumentGate _gate;
    private readonly Func<Task<bool>> _verifyFrames;
    private readonly GtkNativeApi.ObjectCallback _started;
    private readonly GtkNativeApi.DecideCallback _decide;
    private readonly GtkNativeApi.EventCallback _finished;
    private readonly GtkNativeApi.ObjectCallback _failed;
    private readonly GtkNativeApi.DataCallback _received;
    private readonly ulong _startedSignal;
    private readonly List<ulong> _signals = [];
    private nint _download;
    private GtkSaveIntent? _intent;
    private string? _temporaryPath;
    private long _generation;
    private TaskCompletionSource<bool>? _completion;
    private bool _closed;

    internal GtkDownload(GtkNativeApi api, nint view, Uri origin, GtkDocumentGate gate, Func<Task<bool>> verifyFrames)
    {
        _api = api;
        _view = view;
        _origin = origin;
        _gate = gate;
        _verifyFrames = verifyFrames;
        _context = api.Ref(api.WebKit<GtkNativeApi.Pointer>("webkit_web_view_get_context")(view));
        _started = OnStarted;
        _decide = OnDecide;
        _finished = OnFinished;
        _failed = (_, _, _) => { _completion?.TrySetResult(false); };
        _received = OnReceived;
        try { _startedSignal = api.Connect(_context, "download-started", _started); }
        catch { api.Unref(_context); throw; }
    }

    internal async Task SaveAsync(GtkSaveIntent intent, string destination, bool overwrite, long generation, CancellationToken cancellationToken)
    {
        var temporary = TemporaryPath(destination);
        try
        {
            var completion = await GtkInteropHelper.RunOnGlibThread(() => StartOnGlib(intent, temporary, generation));
            if (!await completion.WaitAsync(TimeSpan.FromSeconds(75), cancellationToken))
            {
                throw new IOException("The native download was canceled.");
            }

            await using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            {
                if (stream.Length != intent.Size || stream.Length > GtkSaveIntent.MaximumBytes
                    || !string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)), intent.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("The export failed integrity validation.");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!await _verifyFrames() || !await GtkInteropHelper.RunOnGlibThread(() => !_closed && _api.VerifyDocument(_view, _origin))
                || !Commit(_gate, generation, temporary, destination, overwrite, cancellationToken))
            {
                throw new InvalidOperationException("The export document changed.");
            }
        }
        finally
        {
            await GtkInteropHelper.RunOnGlibThread(() => { ClearOnGlib(); return true; });
            File.Delete(temporary);
        }
    }

    internal static bool Commit(GtkDocumentGate gate, long generation, string temporary, string destination, bool overwrite, CancellationToken cancellationToken) =>
        gate.ExecuteIfCurrent(generation, () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite);
        });

    internal static string TemporaryPath(string destination)
    {
        if (!Path.IsPathFullyQualified(destination) || destination.Any(char.IsControl)
            || string.IsNullOrEmpty(Path.GetFileName(destination)))
        {
            throw new ArgumentException("A local file destination is required.", nameof(destination));
        }

        return Path.Combine(Path.GetDirectoryName(destination)!, ".xe-download-" + Guid.NewGuid().ToString("N") + ".tmp");
    }

    private Task<bool> StartOnGlib(GtkSaveIntent intent, string temporary, long generation)
    {
        if (_closed || _download != 0 || !_gate.IsCurrent(generation) || !_api.VerifyDocument(_view, _origin))
        {
            throw new InvalidOperationException("The native download is unavailable.");
        }

        _completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _intent = intent;
        _temporaryPath = temporary;
        _generation = generation;
        _download = _api.WebKit<GtkNativeApi.StringPointer>("webkit_web_view_download_uri")(_view, intent.Url);
        if (_download == 0) { throw new InvalidOperationException("The native download could not start."); }
        _api.WebKit<GtkNativeApi.SetBoolean>("webkit_download_set_allow_overwrite")(_download, 0);
        _signals.Add(_api.Connect(_download, "decide-destination", _decide));
        _signals.Add(_api.Connect(_download, "finished", _finished));
        _signals.Add(_api.Connect(_download, "failed", _failed));
        _signals.Add(_api.Connect(_download, "received-data", _received));
        return _completion.Task;
    }

    private void OnStarted(nint context, nint download, nint data)
    {
        try
        {
            if (_api.WebKit<GtkNativeApi.Pointer>("webkit_download_get_web_view")(download) != _view) { return; }
            var request = _api.WebKit<GtkNativeApi.Pointer>("webkit_download_get_request")(download);
            var url = GtkNativeApi.Text(_api.WebKit<GtkNativeApi.Pointer>("webkit_uri_request_get_uri")(request));
            if (_closed || download != _download || _intent?.Url != url || !_gate.IsCurrent(_generation))
            {
                _api.WebKit<GtkNativeApi.Command>("webkit_download_cancel")(download);
            }
        }
        catch (Exception)
        {
            try { _api.WebKit<GtkNativeApi.Command>("webkit_download_cancel")(download); }
            catch (Exception) { /* Never unwind a native callback. */ }
        }
    }

    private int OnDecide(nint download, nint suggested, nint data)
    {
        try
        {
            if (download != _download || _temporaryPath is null || !_gate.IsCurrent(_generation) || !_api.VerifyDocument(_view, _origin))
            {
                CancelOnGlib();
                return 1;
            }

            _api.WebKit<GtkNativeApi.SetString>("webkit_download_set_destination")(download, new Uri(_temporaryPath, UriKind.Absolute).AbsoluteUri);
        }
        catch (Exception) { CancelOnGlib(); }
        return 1;
    }

    private void OnReceived(nint download, ulong length, nint data)
    {
        try
        {
            if (download != _download || _intent is null || !_gate.IsCurrent(_generation)
                || _api.WebKit<GtkNativeApi.UnsignedLong>("webkit_download_get_received_data_length")(download) > (ulong)_intent.Size)
            {
                CancelOnGlib();
            }
        }
        catch (Exception) { CancelOnGlib(); }
    }

    private void OnFinished(nint download, nint data)
    {
        try { _completion?.TrySetResult(download == _download && _gate.IsCurrent(_generation)); }
        catch (Exception) { /* Never unwind a native callback. */ }
    }

    internal void CancelOnGlib()
    {
        _completion?.TrySetResult(false);
        try { if (_download != 0) { _api.WebKit<GtkNativeApi.Command>("webkit_download_cancel")(_download); } }
        catch (Exception) { /* Cancellation must not unwind a native callback. */ }
    }

    private void ClearOnGlib()
    {
        CancelOnGlib();
        if (_download != 0)
        {
            foreach (var signal in _signals) { _api.Disconnect(_download, signal); }
            _signals.Clear();
            _api.Unref(_download);
            _download = 0;
        }

        _intent = null;
        _temporaryPath = null;
    }

    internal void CloseOnGlib()
    {
        _closed = true;
        ClearOnGlib();
        _api.Disconnect(_context, _startedSignal);
        _api.Unref(_context);
        GC.KeepAlive(_started);
        GC.KeepAlive(this);
    }
}
