// Copyright (c) 2026 c933103 and contributors.
// SPDX-License-Identifier: LicenseRef-OREV-Community-1.1
// See LICENSE, including Section 10 and Schedule A.

using SkiaSharp;
namespace ExportViewer;
public static class ImagePreview
{
    public static byte[] Decode(Archive archive, JNode node, CancellationToken cancel = default)
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "or-viewer-" + Guid.NewGuid().ToString("N"));
        using var temp = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.DeleteOnClose);
        // Decode the base64 to disk, so an unusually large image does not require a giant string/byte[].
        archive.DecodeAttachment(node, temp, cancel); cancel.ThrowIfCancellationRequested(); temp.Position = 0;
        using var codec = SKCodec.Create(temp) ?? throw new FormatException("This image format is not supported by the image decoder.");
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0) throw new FormatException("Image has no valid dimensions.");
        float scale = Math.Min(1f, Math.Min(1200f / info.Width, 800f / info.Height));
        var dimensions = codec.GetScaledDimensions(scale);
        if ((long)dimensions.Width * dimensions.Height > 64_000_000) throw new IOException("Image is too large for an inline preview. The original can still be saved or exported.");
        var decodeInfo = new SKImageInfo(dimensions.Width, dimensions.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = SKBitmap.Decode(codec, decodeInfo) ?? throw new FormatException("The embedded image could not be decoded.");
        cancel.ThrowIfCancellationRequested();
        int w = Math.Max(1, (int)(info.Width * scale)), h = Math.Max(1, (int)(info.Height * scale));
        using var surface = SKSurface.Create(new SKImageInfo(w, h));
        using var image = SKImage.FromBitmap(bitmap);
        surface.Canvas.Clear(SKColors.Transparent); surface.Canvas.DrawImage(image, new SKRect(0, 0, w, h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        using var result = surface.Snapshot(); using var png = result.Encode(SKEncodedImageFormat.Png, 90);
        return png.ToArray();
    }
}
