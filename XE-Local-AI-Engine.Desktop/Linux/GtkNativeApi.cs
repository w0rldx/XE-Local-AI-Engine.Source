namespace XE_Local_AI_Engine.Desktop.Linux;

using System.Runtime.InteropServices;

internal sealed class GtkNativeApi : IDisposable
{
    private readonly nint _loader;
    private readonly CloseLibrary _close;
    private readonly List<nint> _libraries = [];
    private readonly nint _webkit;
    private readonly nint _objects;
    private readonly nint _soup;
    private readonly nint _gtk;
    private readonly nint _glib;
    private bool _disposed;

    private GtkNativeApi(nint loader, CloseLibrary close, nint webkit, nint objects, nint soup, nint gtk, nint glib)
    {
        _loader = loader;
        _close = close;
        _webkit = webkit;
        _objects = objects;
        _soup = soup;
        _gtk = gtk;
        _glib = glib;
        _libraries.AddRange([glib, gtk, soup, objects, webkit]);
    }

    internal static GtkNativeApi Create()
    {
        var loader = NativeLibrary.Load("libdl.so.2", typeof(GtkNativeApi).Assembly, DllImportSearchPath.SafeDirectories);
        var open = Export<OpenLibrary>(loader, "dlopen");
        var close = Export<CloseLibrary>(loader, "dlclose");
        var acquired = new List<nint>();
        try
        {
            foreach (var name in new[]
                     {
                         "libwebkit2gtk-4.1.so.0",
                         "libgobject-2.0.so.0",
                         "libsoup-3.0.so.0",
                         "libgtk-3.so.0",
                         "libglib-2.0.so.0"
                     })
            {
                var handle = open(name, 2 | 4); // RTLD_NOW | RTLD_NOLOAD: never load a second WebKit implementation.
                if (handle == nint.Zero)
                {
                    throw new PlatformNotSupportedException("The required GTK runtime is not already loaded.");
                }

                acquired.Add(handle);
            }

            return new GtkNativeApi(loader, close, acquired[0], acquired[1], acquired[2], acquired[3], acquired[4]);
        }
        catch
        {
            foreach (var handle in acquired) { _ = close(handle); }

            NativeLibrary.Free(loader);
            throw;
        }
    }

    internal T WebKit<T>(string name) where T : Delegate =>
        Export<T>(_webkit, name);

    internal T Gtk<T>(string name) where T : Delegate =>
        Export<T>(_gtk, name);

    internal T Glib<T>(string name) where T : Delegate =>
        Export<T>(_glib, name);

    internal T Object<T>(string name) where T : Delegate =>
        Export<T>(_objects, name);

    internal nint Ref(nint value) =>
        Object<Pointer>("g_object_ref")(value);

    internal void Unref(nint value) =>
        Object<Command>("g_object_unref")(value);

    internal void Disconnect(nint value, ulong signal) =>
        Object<DisconnectSignal>("g_signal_handler_disconnect")(value, signal);

    // No GDestroyNotify is passed, so the caller owns the delegate's lifetime: hold it in a field and Disconnect before releasing it.
    internal ulong Connect(nint value, string signal, Delegate callback)
    {
        var id = Object<ConnectSignal>("g_signal_connect_data")(value, signal, Marshal.GetFunctionPointerForDelegate(callback), nint.Zero, nint.Zero, 0);
        return id != 0 ? id : throw new InvalidOperationException("A GTK signal could not be attached.");
    }

    internal bool VerifyDocument(nint view, Uri origin)
    {
        var resource = WebKit<Pointer>("webkit_web_view_get_main_resource")(view);
        var response = resource == 0 ? 0 : WebKit<Pointer>("webkit_web_resource_get_response")(resource);
        if (response == 0) { return false; }

        var headers = WebKit<Pointer>("webkit_uri_response_get_http_headers")(response);
        if (headers == 0) { return false; }

        var csp = new List<string>();
        var permissions = new List<string>();
        var validHeaders = true;
        // The one callback not stored in a field: soup_message_headers_foreach invokes it synchronously and returns, so GC.KeepAlive below bounds its lifetime.
        HeaderCallback callback = (name, value, _) =>
        {
            try
            {
                var key = Marshal.PtrToStringUTF8(name);
                var text = Marshal.PtrToStringUTF8(value) ?? string.Empty;
                if (string.Equals(key, "Content-Security-Policy", StringComparison.OrdinalIgnoreCase)) { csp.Add(text); }

                if (string.Equals(key, "Permissions-Policy", StringComparison.OrdinalIgnoreCase)) { permissions.Add(text); }
            }
            catch (Exception) { validHeaders = false; }
        };
        Export<VisitHeaders>(_soup, "soup_message_headers_foreach")(headers, Marshal.GetFunctionPointerForDelegate(callback), nint.Zero);
        GC.KeepAlive(callback);
        return validHeaders && GtkDocumentPolicy.IsTrusted(origin,
            Text(WebKit<Pointer>("webkit_web_view_get_uri")(view)),
            Text(WebKit<Pointer>("webkit_uri_response_get_uri")(response)),
            WebKit<Unsigned>("webkit_uri_response_get_status_code")(response),
            Text(WebKit<Pointer>("webkit_uri_response_get_mime_type")(response)), csp, permissions);
    }

    internal static string? Text(nint pointer) =>
        pointer == 0 ? null : Marshal.PtrToStringUTF8(pointer);

    private static T Export<T>(nint library, string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

    public void Dispose()
    {
        if (_disposed) { return; }

        _disposed = true;
        foreach (var library in _libraries) { _ = _close(library); }

        NativeLibrary.Free(_loader);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate nint Pointer(nint instance);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void Command(nint instance);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int Predicate(nint instance);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate uint Unsigned(nint instance);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate ulong UnsignedLong(nint instance);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate nuint GetTypeId();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int IsType(nint instance, nuint type);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int PermissionCallback(nint view, nint request, nint data);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void EventCallback(nint instance, nint data);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void ObjectCallback(nint instance, nint other, nint data);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void LoadCallback(nint view, int state, nint data);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void DataCallback(nint download, ulong length, nint data);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int DecideCallback(nint download, nint suggested, nint data);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate nint StringPointer(nint instance, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void SetString(nint instance, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void SetBoolean(nint instance, int value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint OpenLibrary([MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CloseLibrary(nint handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ulong ConnectSignal(nint instance, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nint callback, nint data, nint destroy, int flags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DisconnectSignal(nint instance, ulong signal);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void HeaderCallback(nint name, nint value, nint data);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void VisitHeaders(nint headers, nint callback, nint data);
}
