// Copyright (c) 2026 c933103 and contributors.
// SPDX-License-Identifier: LicenseRef-OREV-Community-1.1
// See LICENSE, including Section 10 and Schedule A.

using System.Text.Json;

namespace ExportViewer;

sealed record SavedExport(string Path, string Digest, int[] Checked, int[] Selected);
sealed record SavedPosition(string Path, int Room, int Page);

sealed record ViewerSession(int Version, SavedExport[] Exports, SavedPosition? Position, string Filter, bool MetadataOpen)
{
    const int CurrentVersion = 1;
    const int MaxBytes = 16 * 1024 * 1024;
    public static ViewerSession Empty { get; } = new(CurrentVersion, [], null, "", false);
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OREV", "session.json");

    public static ViewerSession Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > MaxBytes) throw new InvalidDataException("The session file is too large.");
            var session = JsonSerializer.Deserialize<ViewerSession>(stream);
            if (session == null || session.Version != CurrentVersion || session.Exports == null ||
                session.Filter == null || session.Exports.Any(source => source == null ||
                    string.IsNullOrWhiteSpace(source.Path) || !Path.IsPathFullyQualified(source.Path) ||
                    source.Digest?.Length != 64 || source.Checked == null || source.Selected == null))
                throw new InvalidDataException("The session file has an unsupported format.");
            return session;
        }
        catch (FileNotFoundException) { return Empty; }
        catch (DirectoryNotFoundException) { return Empty; }
    }

    public void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, this);
                if (stream.Length > MaxBytes) throw new InvalidDataException("The session file is too large.");
                stream.Flush(flushToDisk: true);
            }
            File.Move(staging, path, overwrite: true);
        }
        finally
        {
            File.Delete(staging);
        }
    }
}
