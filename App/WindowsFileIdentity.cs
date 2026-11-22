// Copyright (c) 2026 c933103 and contributors.
// SPDX-License-Identifier: LicenseRef-OREV-Community-1.1
// See LICENSE, including Section 10 and Schedule A.

using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace ExportViewer;

internal static class WindowsFileIdentity
{
    const int FileIdInfo = 18;

    [StructLayout(LayoutKind.Sequential)]
    struct FileIdentity
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass,
        out FileIdentity information, uint size);

    public static bool SameFile(string firstPath, string secondPath)
    {
        try
        {
            using var first = File.OpenHandle(firstPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var second = File.OpenHandle(secondPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            uint size = (uint)Marshal.SizeOf<FileIdentity>();
            return GetFileInformationByHandleEx(first, FileIdInfo, out var left, size) &&
                GetFileInformationByHandleEx(second, FileIdInfo, out var right, size) &&
                left.VolumeSerialNumber == right.VolumeSerialNumber &&
                left.FileIdLow == right.FileIdLow && left.FileIdHigh == right.FileIdHigh;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Let the importer open the requested file normally if identity cannot be checked.
            return false;
        }
    }
}
