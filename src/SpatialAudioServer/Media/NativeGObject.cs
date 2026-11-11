using System.Runtime.InteropServices;
using System.Text;

namespace SpatialAudioServer.Media;

/// <summary>
/// Minimal raw GObject property access for objects the binding has no wrapper type for (libnice's GstWebRTCNice
/// and NiceAgent). Reading those through the binding's generic wrappers over-releases them and crashes
/// (GST_IS_WEBRTC_ICE assertion), so this goes straight to GLib with non-variadic calls and manages refs itself.
/// </summary>
internal static unsafe class NativeGObject
{
    private const nuint GTypeBoolean = 5 << 2;
    private const nuint GTypeObject = 20 << 2;

    private static readonly Api? Functions = Load();

    public static bool Available => Functions is not null;

    /// <summary>Returns a new reference to the object held in an object-typed property (or 0). Release it with <see cref="Unref"/>.</summary>
    public static nint GetObjectProperty(nint obj, string name)
    {
        if (Functions is not { } f || obj == 0 || !HasProperty(obj, name))
        {
            return 0;
        }
        GValue value = default;
        f.ValueInit(&value, GTypeObject);
        fixed (byte* n = Utf8(name))
        {
            f.GetProperty(obj, (nint)n, &value);
        }
        var result = f.ValueDupObject(&value);
        f.ValueUnset(&value);
        return result;
    }

    /// <summary>Sets a boolean property if the object has it. Returns whether it was set.</summary>
    public static bool SetBooleanProperty(nint obj, string name, bool on)
    {
        if (Functions is not { } f || obj == 0 || !HasProperty(obj, name))
        {
            return false;
        }
        GValue value = default;
        f.ValueInit(&value, GTypeBoolean);
        f.ValueSetBoolean(&value, on ? 1 : 0);
        fixed (byte* n = Utf8(name))
        {
            f.SetProperty(obj, (nint)n, &value);
        }
        f.ValueUnset(&value);
        return true;
    }

    public static void Unref(nint obj)
    {
        if (Functions is { } f && obj != 0)
        {
            f.ObjectUnref(obj);
        }
    }

    private static bool HasProperty(nint obj, string name)
    {
        var klass = *(nint*)obj; // GTypeInstance.g_class is the first field of every GObject
        fixed (byte* n = Utf8(name))
        {
            return Functions!.FindProperty(klass, (nint)n) != 0;
        }
    }

    private static byte[] Utf8(string name) => Encoding.UTF8.GetBytes(name + "\0");

    private static Api? Load()
    {
        foreach (var library in new[] { "libgobject-2.0.so.0", "gobject-2.0-0.dll", "libgobject-2.0.0.dylib" })
        {
            if (NativeLibrary.TryLoad(library, out var handle))
            {
                return new Api(handle);
            }
        }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GValue
    {
        public nuint GType;
        public ulong Data0;
        public ulong Data1;
    }

    private sealed class Api(nint lib)
    {
        public readonly delegate* unmanaged<GValue*, nuint, GValue*> ValueInit =
            (delegate* unmanaged<GValue*, nuint, GValue*>)NativeLibrary.GetExport(lib, "g_value_init");
        public readonly delegate* unmanaged<GValue*, void> ValueUnset =
            (delegate* unmanaged<GValue*, void>)NativeLibrary.GetExport(lib, "g_value_unset");
        public readonly delegate* unmanaged<GValue*, nint> ValueDupObject =
            (delegate* unmanaged<GValue*, nint>)NativeLibrary.GetExport(lib, "g_value_dup_object");
        public readonly delegate* unmanaged<GValue*, int, void> ValueSetBoolean =
            (delegate* unmanaged<GValue*, int, void>)NativeLibrary.GetExport(lib, "g_value_set_boolean");
        public readonly delegate* unmanaged<nint, nint, GValue*, void> GetProperty =
            (delegate* unmanaged<nint, nint, GValue*, void>)NativeLibrary.GetExport(lib, "g_object_get_property");
        public readonly delegate* unmanaged<nint, nint, GValue*, void> SetProperty =
            (delegate* unmanaged<nint, nint, GValue*, void>)NativeLibrary.GetExport(lib, "g_object_set_property");
        public readonly delegate* unmanaged<nint, nint, nint> FindProperty =
            (delegate* unmanaged<nint, nint, nint>)NativeLibrary.GetExport(lib, "g_object_class_find_property");
        public readonly delegate* unmanaged<nint, void> ObjectUnref =
            (delegate* unmanaged<nint, void>)NativeLibrary.GetExport(lib, "g_object_unref");
    }
}
