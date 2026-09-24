namespace XE_Local_AI_Engine.Desktop.Linux;

using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia.X11.Interop;

internal sealed class GtkSavePicker
{
    private readonly GtkNativeApi _api;
    private readonly ResponseCallback _response;
    private readonly TaskCompletionSource<string?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private nint _dialog;
    private ulong _signal;

    private GtkSavePicker(GtkNativeApi api)
    {
        _api = api;
        _response = OnResponse;
    }

    internal static async Task<string?> PickAsync(GtkNativeApi api, nint view, string filename, CancellationToken cancellationToken)
    {
        var picker = new GtkSavePicker(api);
        try
        {
            await GtkInteropHelper.RunOnGlibThread(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                picker.Show(view, filename);
                return true;
            });
            return await picker._completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            await GtkInteropHelper.RunOnGlibThread(() =>
            {
                picker.Close();
                return true;
            });
        }
    }

    private void Show(nint view, string filename)
    {
        var german = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de";
        var parent = _api.Gtk<GtkNativeApi.Pointer>("gtk_widget_get_toplevel")(view);
        _dialog = _api.Gtk<CreateChooser>("gtk_file_chooser_dialog_new")(german ? "XE-Export speichern (maximal 50 MiB)" : "Save XE export (maximum 50 MiB)", parent, 1, nint.Zero);
        if (_dialog == 0) { throw new InvalidOperationException("The Save dialog could not open."); }

        _api.Gtk<AddButton>("gtk_dialog_add_button")(_dialog, german ? "Abbrechen" : "Cancel", -6);
        _api.Gtk<AddButton>("gtk_dialog_add_button")(_dialog, german ? "Speichern" : "Save", -3);
        _api.Gtk<GtkNativeApi.SetBoolean>("gtk_dialog_set_default_response")(_dialog, -6);
        _api.Gtk<GtkNativeApi.SetBoolean>("gtk_window_set_modal")(_dialog, 1);
        _api.Gtk<GtkNativeApi.SetString>("gtk_file_chooser_set_current_name")(_dialog, filename);
        _signal = _api.Connect(_dialog, "response", _response);
        _api.Gtk<GtkNativeApi.Command>("gtk_widget_show_all")(_dialog);
    }

    private void OnResponse(nint dialog, int response, nint data)
    {
        try
        {
            if (dialog != _dialog || response != -3)
            {
                _completion.TrySetResult(null);
                return;
            }

            var pointer = _api.Gtk<GtkNativeApi.Pointer>("gtk_file_chooser_get_filename")(dialog);
            try { _completion.TrySetResult(GtkNativeApi.Text(pointer)); }
            finally
            {
                if (pointer != 0) { _api.Glib<GtkNativeApi.Command>("g_free")(pointer); }
            }
        }
        catch (Exception) { _completion.TrySetResult(null); }
    }

    private void Close()
    {
        if (_dialog == 0) { return; }

        if (_signal != 0) { _api.Disconnect(_dialog, _signal); }

        _api.Gtk<GtkNativeApi.Command>("gtk_widget_destroy")(_dialog);
        _dialog = 0;
        GC.KeepAlive(_response);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint CreateChooser([MarshalAs(UnmanagedType.LPUTF8Str)] string title, nint parent, int action, nint firstButton);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint AddButton(nint dialog, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, int response);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ResponseCallback(nint dialog, int response, nint data);
}
