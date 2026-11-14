using System.Runtime.InteropServices;
using System.Text;

namespace SpatialAudioServer.Media;

/// <summary>
/// Minimal raw GObject property access for objects the binding has no wrapper type for (libnice's GstWebRTCNice
/// and NiceAgent), and for reading any element's properties as text (the admin pipeline view). Reading such objects
/// through the binding's generic wrappers over-releases them and crashes (GST_IS_WEBRTC_ICE assertion), so this goes
/// straight to GLib with non-variadic calls and manages refs itself.
/// </summary>
internal static unsafe class NativeGObject
{
    private const nuint GTypeBoolean = 5 << 2;
    private const nuint GTypeObject = 20 << 2;
    private const uint ParamReadable = 1;
    private const uint ParamDeprecated = 1u << 31;

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

    /// <summary>
    /// Every readable, non-deprecated property of the object as (name, value as text), in the class's order. Values
    /// are GLib's own rendering (<c>g_strdup_value_contents</c>): strings quoted, enums by name, objects as
    /// "((Type*) address)". Long values are cut to <paramref name="maxLength"/> characters.
    /// </summary>
    public static IReadOnlyList<(string Name, string Value)> ReadProperties(nint obj, int maxLength = 400)
    {
        var result = new List<(string, string)>();
        if (Functions is not { } f || obj == 0)
        {
            return result;
        }
        uint count;
        var specs = f.ListProperties(*(nint*)obj, &count);
        try
        {
            for (var i = 0; i < count; i++)
            {
                var spec = ((nint*)specs)[i];
                var flags = *(uint*)(spec + 2 * sizeof(nint)); // GParamSpec: g_type_instance, name, flags, value_type…
                if ((flags & ParamReadable) == 0 || (flags & ParamDeprecated) != 0)
                {
                    continue;
                }
                var name = Marshal.PtrToStringUTF8(f.ParamSpecGetName(spec)) ?? "";
                var type = *(nuint*)(spec + 3 * sizeof(nint));
                result.Add((name, ValueText(f, obj, f.ParamSpecGetName(spec), type, maxLength)));
            }
        }
        finally
        {
            f.Free(specs);
        }
        return result;
    }

    /// <summary>One property as text (as <see cref="ReadProperties"/>), or null if the object has no such readable property.</summary>
    public static string? ReadProperty(nint obj, string name, int maxLength = 400)
    {
        if (Functions is not { } f || obj == 0)
        {
            return null;
        }
        fixed (byte* n = Utf8(name))
        {
            var spec = f.FindProperty(*(nint*)obj, (nint)n);
            if (spec == 0 || (*(uint*)(spec + 2 * sizeof(nint)) & ParamReadable) == 0)
            {
                return null;
            }
            return ValueText(f, obj, (nint)n, *(nuint*)(spec + 3 * sizeof(nint)), maxLength);
        }
    }

    /// <summary>The name of the GstObject held in an object-typed property (e.g. input-selector's active-pad), or null.</summary>
    public static string? ReadObjectName(nint obj, string property)
    {
        var held = GetObjectProperty(obj, property);
        if (held == 0)
        {
            return null;
        }
        try
        {
            return ReadProperty(held, "name")?.Trim('"');
        }
        finally
        {
            Unref(held);
        }
    }

    private static string ValueText(Api f, nint obj, nint name, nuint type, int maxLength)
    {
        GValue value = default;
        f.ValueInit(&value, type);
        f.GetProperty(obj, name, &value);
        var text = f.ValueContents(&value);
        f.ValueUnset(&value);
        var result = Marshal.PtrToStringUTF8(text) ?? "";
        f.Free(text);
        // Enums and flags come as "((TypeName) VALUE_NAME)": keep the value. Objects ("((Type*) 0x…)") stay as they are.
        if (result.StartsWith("((", StringComparison.Ordinal) && result.EndsWith(')') && result.IndexOf(") ", StringComparison.Ordinal) is > 2 and var close
            && result[close - 1] != '*')
        {
            result = result[(close + 2)..^1];
        }
        return result.Length > maxLength ? result[..maxLength] + "…" : result;
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
        public readonly delegate* unmanaged<nint, uint*, nint> ListProperties =
            (delegate* unmanaged<nint, uint*, nint>)NativeLibrary.GetExport(lib, "g_object_class_list_properties");
        public readonly delegate* unmanaged<nint, nint> ParamSpecGetName =
            (delegate* unmanaged<nint, nint>)NativeLibrary.GetExport(lib, "g_param_spec_get_name");
        public readonly delegate* unmanaged<GValue*, nint> ValueContents =
            (delegate* unmanaged<GValue*, nint>)NativeLibrary.GetExport(lib, "g_strdup_value_contents");
        public readonly delegate* unmanaged<nint, void> Free = (delegate* unmanaged<nint, void>)NativeLibrary.GetExport(
            NativeLibrary.TryLoad("libglib-2.0.so.0", out var glib) || NativeLibrary.TryLoad("glib-2.0-0.dll", out glib)
                || NativeLibrary.TryLoad("libglib-2.0.0.dylib", out glib) ? glib : lib, "g_free");
        public readonly delegate* unmanaged<nint, void> ObjectUnref =
            (delegate* unmanaged<nint, void>)NativeLibrary.GetExport(lib, "g_object_unref");
    }
}
