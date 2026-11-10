using System.Runtime.InteropServices;

namespace ClipIt;

internal static class Obs
{
    private const string Dll = "obs.dll";
    private const CallingConvention C = CallingConvention.Cdecl;
    // OBS 33.0.1 public ABI. C bool is one byte; enum is four bytes.
    [StructLayout(LayoutKind.Sequential)] internal struct VideoInfo
    {
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string Graphics;
        public uint FpsNum, FpsDen, BaseWidth, BaseHeight, Width, Height;
        public int Format;
        public uint Adapter;
        [MarshalAs(UnmanagedType.I1)] public bool Gpu;
        public int ColorSpace, Range, Scale;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct AudioInfo { public uint Rate; public int Speakers; }
    [StructLayout(LayoutKind.Sequential)] internal struct CallData
    {
        public nint Stack;
        public nuint Size, Capacity;
        [MarshalAs(UnmanagedType.I1)] public bool Fixed;
    }
    [UnmanagedFunctionPointer(C)] internal delegate void Signal(nint data, nint callData);

    [DllImport(Dll, CallingConvention=C)] [return:MarshalAs(UnmanagedType.I1)] internal static extern bool obs_startup([MarshalAs(UnmanagedType.LPUTF8Str)] string locale, [MarshalAs(UnmanagedType.LPUTF8Str)] string config, nint profiler);
    [DllImport(Dll, CallingConvention=C)] internal static extern void obs_shutdown();
    [DllImport(Dll, CallingConvention=C)] internal static extern nint obs_get_version_string();
    [DllImport(Dll, CallingConvention=C)] internal static extern void obs_add_data_path([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport(Dll, CallingConvention=C)] internal static extern int obs_reset_video(ref VideoInfo info);
    [DllImport(Dll, CallingConvention=C)] [return:MarshalAs(UnmanagedType.I1)] internal static extern bool obs_reset_audio(ref AudioInfo info);
    [DllImport(Dll, CallingConvention=C)] internal static extern int obs_open_module(out nint module, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string data);
    [DllImport(Dll, CallingConvention=C)] [return:MarshalAs(UnmanagedType.I1)] internal static extern bool obs_init_module(nint module);
    [DllImport(Dll, CallingConvention=C)] internal static extern void obs_post_load_modules();
    [DllImport(Dll, CallingConvention=C)] internal static extern nint obs_data_create();
    [DllImport(Dll, CallingConvention=C)] internal static extern void obs_data_release(nint data);
    [DllImport(Dll, CallingConvention=C)] internal static extern void obs_data_set_string(nint data, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(Dll, CallingConvention=C)] internal static extern void obs_data_set_int(nint data, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, long value);
    [DllImport(Dll, CallingConvention=C)] internal static extern void obs_data_set_bool(nint data, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.I1)] bool value);
    [DllImport(Dll, CallingConvention=C)] internal static extern nint obs_source_create([MarshalAs(UnmanagedType.LPUTF8Str)] string id, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nint settings, nint hotkeys);
    [DllImport(Dll, CallingConvention=C)] internal static extern nint obs_get_source_properties([MarshalAs(UnmanagedType.LPUTF8Str)] string id);
    [DllImport(Dll, CallingConvention=C)] internal static extern nint obs_properties_get(nint properties, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Dll, CallingConvention=C)] internal static extern nuint obs_property_list_item_count(nint property);
    [DllImport(Dll, CallingConvention=C)] internal static extern nint obs_property_list_item_name(nint property, nuint index);
    [DllImport(Dll, CallingConvention=C)] internal static extern nint obs_property_list_item_string(nint property, nuint index);
    [DllImport(Dll, CallingConvention=C)] internal static extern void obs_properties_destroy(nint properties);
    [DllImport(Dll, CallingConvention=C)] internal static extern void obs_source_release(nint source);
    [DllImport(Dll, CallingConvention=C)] internal static extern void obs_source_update(nint source, nint settings);
    [DllImport(Dll, CallingConvention=C)] internal static extern void obs_source_set_audio_mixers(nint source, uint mixers);
    [DllImport(Dll, CallingConvention=C)] internal static extern void obs_output_update(nint output, nint settings);
    [DllImport(Dll, CallingConvention=C)] internal static extern void obs_source_inc_showing(nint source);
    [DllImport(Dll, CallingConvention=C)] internal static extern void obs_source_dec_showing(nint source);
    [DllImport(Dll, CallingConvention=C)] internal static extern uint obs_source_get_width(nint source);
    [DllImport(Dll, CallingConvention=C)] internal static extern uint obs_source_get_height(nint source);
    [DllImport(Dll, CallingConvention=C)] internal static extern void obs_set_output_source(uint channel, nint source);
    [DllImport(Dll, CallingConvention=C)] internal static extern nint obs_video_encoder_create([MarshalAs(UnmanagedType.LPUTF8Str)] string id, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nint settings, nint hotkeys);
    [DllImport(Dll, CallingConvention=C)] internal static extern nint obs_audio_encoder_create([MarshalAs(UnmanagedType.LPUTF8Str)] string id, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nint settings, nuint mixer, nint hotkeys);
    [DllImport(Dll, CallingConvention=C)] internal static extern void obs_encoder_release(nint encoder);
    [DllImport(Dll, CallingConvention=C)] internal static extern void obs_encoder_set_video(nint encoder, nint video);
    [DllImport(Dll, CallingConvention=C)] internal static extern void obs_encoder_set_audio(nint encoder, nint audio);
    [DllImport(Dll, CallingConvention=C)] internal static extern nint obs_get_video();
    [DllImport(Dll, CallingConvention=C)] internal static extern nint obs_get_audio();
    [DllImport(Dll, CallingConvention=C)] [return:MarshalAs(UnmanagedType.I1)] internal static extern bool obs_enum_encoder_types(nuint index, out nint id);
    [DllImport(Dll, CallingConvention=C)] internal static extern nint obs_output_create([MarshalAs(UnmanagedType.LPUTF8Str)] string id, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nint settings, nint hotkeys);
    [DllImport(Dll, CallingConvention=C)] internal static extern void obs_output_release(nint output);
    [DllImport(Dll, CallingConvention=C)] internal static extern void obs_output_set_video_encoder(nint output, nint encoder);
    [DllImport(Dll, CallingConvention=C)] internal static extern void obs_output_set_audio_encoder(nint output, nint encoder, nuint index);
    [DllImport(Dll, CallingConvention=C)] [return:MarshalAs(UnmanagedType.I1)] internal static extern bool obs_output_start(nint output);
    [DllImport(Dll, CallingConvention=C)] internal static extern void obs_output_stop(nint output);
    [DllImport(Dll, CallingConvention=C)] [return:MarshalAs(UnmanagedType.I1)] internal static extern bool obs_output_active(nint output);
    [DllImport(Dll, CallingConvention=C)] internal static extern int obs_output_get_total_frames(nint output);
    [DllImport(Dll, CallingConvention=C)] internal static extern nint obs_output_get_last_error(nint output);
    [DllImport(Dll, CallingConvention=C)] internal static extern nint obs_output_get_proc_handler(nint output);
    [DllImport(Dll, CallingConvention=C)] internal static extern nint obs_output_get_signal_handler(nint output);
    [DllImport(Dll, CallingConvention=C)] internal static extern void signal_handler_connect(nint handler, [MarshalAs(UnmanagedType.LPUTF8Str)] string signal, Signal callback, nint data);
    [DllImport(Dll, CallingConvention=C)] internal static extern void signal_handler_disconnect(nint handler, [MarshalAs(UnmanagedType.LPUTF8Str)] string signal, Signal callback, nint data);
    [DllImport(Dll, CallingConvention=C)] [return:MarshalAs(UnmanagedType.I1)] internal static extern bool proc_handler_call(nint handler, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, ref CallData data);
    [DllImport(Dll, CallingConvention=C)] [return:MarshalAs(UnmanagedType.I1)] internal static extern bool calldata_get_string(ref CallData data, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, out nint value);
    [DllImport(Dll, CallingConvention=C)] internal static extern void bfree(nint memory);
    internal static string Text(nint text) => Marshal.PtrToStringUTF8(text) ?? "";
}

internal static class Win32
{
    private const int GetMinMaxInfoMessage = 0x0024;

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public int ReservedX, ReservedY;
        public int MaxWidth, MaxHeight;
        public int MaxX, MaxY;
        public int MinTrackWidth, MinTrackHeight;
        public int MaxTrackWidth, MaxTrackHeight;
    }

    internal static nint FitMaximizedWindow(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != GetMinMaxInfoMessage) return 0;
        var screen = Screen.FromHandle(window);
        // Match the visible client frame to the work area Windows reports for this monitor.
        var workArea = screen.WorkingArea;
        var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        limits.MaxX = workArea.Left - screen.Bounds.Left;
        limits.MaxY = workArea.Top - screen.Bounds.Top;
        limits.MaxWidth = workArea.Width;
        limits.MaxHeight = workArea.Height;
        Marshal.StructureToPtr(limits, lParam, false);
        handled = true;
        return 0;
    }

    [DllImport("kernel32", CharSet=CharSet.Unicode, SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool SetDllDirectory(string path);
    [DllImport("user32", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32")] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool UnregisterHotKey(nint window, int id);
}
