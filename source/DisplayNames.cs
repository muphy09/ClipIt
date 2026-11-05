using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text;

namespace ClipIt;

internal static class DisplayNames
{
    internal static string Get(string deviceName)
    {
        try
        {
            var monitor = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(deviceName, 0, ref monitor, 1)) return "";
            // The interface path identifies this exact monitor, including when identical models are attached.
            string[] parts = monitor.DeviceId.Split('#');
            if (parts.Length >= 3)
            {
                using var device = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\DISPLAY\" + parts[1] + @"\" + parts[2] + @"\Device Parameters");
                if (device?.GetValue("EDID") is byte[] edid && edid.Length >= 128)
                {
                    string model = "";
                    for (int offset = 54; offset + 18 <= 126; offset += 18)
                        if (edid[offset] == 0 && edid[offset + 1] == 0 && edid[offset + 3] == 0xFC)
                            model = Encoding.ASCII.GetString(edid, offset + 5, 13).Trim('\0', '\n', '\r', ' ');
                    if (model.Length > 0)
                        return parts[1].StartsWith("SAM", StringComparison.OrdinalIgnoreCase) && !model.Contains("Samsung", StringComparison.OrdinalIgnoreCase) ? "Samsung " + model : model;
                }
            }
            return monitor.DeviceString.Trim();
        }
        catch (Exception ex) { Log.Write("Monitor name lookup: " + ex.Message); return ""; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string deviceName, uint index, ref DisplayDevice device, uint flags);
}
