namespace XE_Local_AI_Engine.Desktop.Linux;

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.X11.Interop;

internal sealed class GtkMicrophoneConsent
{
    private readonly GtkNativeApi _api;
    private readonly nint _view;
    private readonly Uri _origin;
    private readonly GtkDocumentGate _gate;
    private readonly Window _owner;
    private readonly Func<Task<bool>> _verifyFrames;
    private readonly GtkNativeApi.PermissionCallback _callback;
    private readonly ulong _signal;
    private PendingConsent? _pending;
    private bool _closed;

    internal GtkMicrophoneConsent(GtkNativeApi api, nint view, Uri origin, GtkDocumentGate gate, Window owner, Func<Task<bool>> verifyFrames)
    {
        _api = api;
        _view = view;
        _origin = origin;
        _gate = gate;
        _owner = owner;
        _verifyFrames = verifyFrames;
        _ = api.WebKit<GtkNativeApi.GetTypeId>("webkit_user_media_permission_request_get_type");
        _ = api.WebKit<GtkNativeApi.Predicate>("webkit_user_media_permission_is_for_display_device");
        _ = api.WebKit<GtkNativeApi.Predicate>("webkit_user_media_permission_is_for_audio_device");
        _ = api.WebKit<GtkNativeApi.Predicate>("webkit_user_media_permission_is_for_video_device");
        _ = api.WebKit<GtkNativeApi.Command>("webkit_permission_request_allow");
        _ = api.WebKit<GtkNativeApi.Command>("webkit_permission_request_deny");
        _callback = OnPermission;
        _signal = api.Connect(view, "permission-request", _callback);
    }

    internal void CancelOnGlib()
    {
        var pending = _pending;
        _pending = null;
        if (pending is null) { return; }

        try
        {
#pragma warning disable MA0045 // The native callback must deny synchronously before navigation can grant another request.
            pending.Cancellation.Cancel();
#pragma warning restore MA0045
            _api.WebKit<GtkNativeApi.Command>("webkit_permission_request_deny")(pending.Request);
        }
        finally { _api.Unref(pending.Request); }
    }

    internal void CloseOnGlib()
    {
        _closed = true;
        try { CancelOnGlib(); }
        finally { _api.Disconnect(_view, _signal); }

        GC.KeepAlive(_callback);
    }

    private int OnPermission(nint view, nint request, nint data)
    {
        try
        {
            var userMediaType = _api.WebKit<GtkNativeApi.GetTypeId>("webkit_user_media_permission_request_get_type")();
            var media = _api.Object<GtkNativeApi.IsType>("g_type_check_instance_is_a")(request, userMediaType) != 0;
            var generation = _gate.Generation;
            var audioOnly = media && GtkDocumentPolicy.AudioOnly(_api.WebKit<GtkNativeApi.Predicate>("webkit_user_media_permission_is_for_audio_device")(request) != 0,
                _api.WebKit<GtkNativeApi.Predicate>("webkit_user_media_permission_is_for_video_device")(request) != 0,
                _api.WebKit<GtkNativeApi.Predicate>("webkit_user_media_permission_is_for_display_device")(request) != 0);
            if (_closed || view != _view || _pending is not null || !audioOnly || !_gate.IsCurrent(generation) || !_api.VerifyDocument(view, _origin))
            {
                _api.WebKit<GtkNativeApi.Command>("webkit_permission_request_deny")(request);
                return 1;
            }

            var retainedRequest = _api.Ref(request);
            CancellationTokenSource? cancellation = null;
            var transferred = false;
            try
            {
#pragma warning disable CA2000 // PendingConsent transfers ownership to PromptAsync's finally; failed transfer disposes below.
                cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
#pragma warning restore CA2000
                var pending = new PendingConsent
                {
                    Request = retainedRequest,
                    Generation = generation,
                    Cancellation = cancellation
                };
                _pending = pending;
                Dispatcher.UIThread.Post(() => _ = PromptAsync(pending));
                transferred = true;
            }
            finally
            {
                if (!transferred)
                {
                    _pending = null;
                    cancellation?.Dispose();
                    _api.Unref(retainedRequest);
                }
            }
        }
        catch (Exception)
        {
            try
            {
                if (_pending?.Request == request) { CancelOnGlib(); }
                else { _api.WebKit<GtkNativeApi.Command>("webkit_permission_request_deny")(request); }
            }
            catch (Exception)
            {
                /* Never unwind a native callback. */
            }
        }

        return 1;
    }

    private async Task PromptAsync(PendingConsent pending)
    {
        var allow = false;
        Window? dialog = null;
        try
        {
            pending.Cancellation.Token.ThrowIfCancellationRequested();
            if (!await _verifyFrames() || !_gate.IsCurrent(pending.Generation)) { return; }

            dialog = CreateDialog();
            await using var cancellation = pending.Cancellation.Token.Register(() => Dispatcher.UIThread.Post(() => dialog.Close(false)));
            pending.Cancellation.Token.ThrowIfCancellationRequested();
            allow = await dialog.ShowDialog<bool>(_owner);
            allow = allow && await _verifyFrames();
        }
        catch (Exception) { allow = false; }
        finally
        {
            try
            {
                dialog?.Close(false);
                await GtkInteropHelper.RunOnGlibThread(() =>
                {
                    if (!ReferenceEquals(_pending, pending)) { return false; }

                    _pending = null;
                    try
                    {
                        var permitted = !_closed && allow && !pending.Cancellation.IsCancellationRequested
                                        && _api.VerifyDocument(_view, _origin)
                                        && _gate.ExecuteIfCurrent(pending.Generation, () => _api.WebKit<GtkNativeApi.Command>("webkit_permission_request_allow")(pending.Request));
                        if (!permitted) { _api.WebKit<GtkNativeApi.Command>("webkit_permission_request_deny")(pending.Request); }
                    }
                    catch (Exception)
                    {
                        _api.WebKit<GtkNativeApi.Command>("webkit_permission_request_deny")(pending.Request);
                    }
                    finally { _api.Unref(pending.Request); }

                    return true;
                });
            }
            finally { pending.Cancellation.Dispose(); }
        }
    }

    private static Window CreateDialog()
    {
        var german = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de";
        var dialog = new Window
        {
            Title = german ? "Mikrofonzugriff" : "Microphone access",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var panel = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12
        };
        panel.Children.Add(new TextBlock
        {
            Text = german ? "XE den Zugriff auf das Mikrofon erlauben?" : "Allow XE to use the microphone?",
            TextWrapping = TextWrapping.Wrap
        });
        var deny = new Button
        {
            Content = german ? "Ablehnen" : "Deny",
            IsDefault = true,
            IsCancel = true
        };
        deny.Click += (_, _) => dialog.Close(false);
        var accept = new Button
        {
            Content = german ? "Erlauben" : "Allow"
        };
        accept.Click += (_, _) => dialog.Close(true);
        panel.Children.Add(deny);
        panel.Children.Add(accept);
        dialog.Content = panel;
        return dialog;
    }

    private sealed class PendingConsent
    {
        public required nint Request { get; init; }
        public required long Generation { get; init; }
        public required CancellationTokenSource Cancellation { get; init; }
    }
}
