using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;

namespace ClipIt;

internal enum BufferDriveKind { Unknown, SolidState, HardDisk }

internal static class BufferDrive
{
    internal static string VolumeRoot(string path)
    {
        var root = new StringBuilder(1024);
        return GetVolumePathName(Path.GetFullPath(path), root, root.Capacity) ? root.ToString() : Path.GetPathRoot(path)!;
    }

    internal static BufferDriveKind Detect(string path)
    {
        try
        {
            string root = VolumeRoot(path);
            var volume = new StringBuilder(1024);
            if (!GetVolumeNameForVolumeMountPoint(root, volume, volume.Capacity)) return BufferDriveKind.Unknown;
            using var handle = CreateFile(volume.ToString().TrimEnd('\\'), 0, 7, 0, 3, 0, 0);
            if (handle.IsInvalid) return BufferDriveKind.Unknown;
            byte[] query = new byte[12]; query[0] = 7; // StorageDeviceSeekPenaltyProperty, PropertyStandardQuery.
            byte[] result = new byte[12];
            if (!DeviceIoControl(handle, 0x002D1400, query, query.Length, result, result.Length, out int returned, 0) || returned < 9)
                return BufferDriveKind.Unknown;
            return result[8] != 0 ? BufferDriveKind.HardDisk : BufferDriveKind.SolidState;
        }
        catch { return BufferDriveKind.Unknown; }
    }

    internal static string Warning(BufferDriveKind kind) => kind switch
    {
        BufferDriveKind.HardDisk => "Traditional HDD detected on your save drive. Continuous buffering may compete with games for disk access. We recommend turning on Save replay in RAM, or choosing a save folder on an SSD.",
        BufferDriveKind.Unknown => "Windows could not identify this drive type. Disk buffering writes continuously; use Save replay in RAM if this is a traditional HDD or recording affects disk performance.",
        _ => ""
    };

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumePathName(string file, StringBuilder volume, int length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string mount, StringBuilder volume, int length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint sharing, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputLength, byte[] output, int outputLength, out int returned, nint overlapped);
}
