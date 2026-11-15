// Copyright (c) 2026 c933103 and contributors.
// SPDX-License-Identifier: LicenseRef-OREV-Community-1.1
// See LICENSE, including Section 10 and Schedule A.

using ExportViewer;
using SkiaSharp;
using System.Text.Json;
using var archive = Archive.Open(args.FirstOrDefault() ?? Path.Combine(AppContext.BaseDirectory, "fixtures", "synthetic-bulk.json"));
int count = 0;
foreach (var room in archive.Rooms)
foreach (var item in room.Node["items"]!.Children)
foreach (var attachment in archive.Attachments(item))
{
    byte[] png = ImagePreview.Decode(archive, attachment.Node);
    using var bitmap = SKBitmap.Decode(png);
    if (bitmap == null || bitmap.Width <= 0 || bitmap.Height <= 0 || bitmap.Width > 1200 || bitmap.Height > 800) throw new Exception("Invalid rendered preview");
    Console.WriteLine($"PASS: sample image {++count} renders to {bitmap.Width}x{bitmap.Height} preview");
}
// Verify modern image format rendering independently of platform-installed codecs.
string path = Path.Combine(Path.GetTempPath(), "or-webp-" + Guid.NewGuid().ToString("N") + ".json");
try
{
    using var surface = SKSurface.Create(new SKImageInfo(600, 400)); surface.Canvas.Clear(SKColors.CornflowerBlue);
    using var image = surface.Snapshot(); using var webp = image.Encode(SKEncodedImageFormat.Webp, 90);
    var data = new { version = "orpg.3.0", title = "WebP test", characters = new { }, messages = new { }, items = new { image = new { data = new { result = "data:image/webp;base64," + Convert.ToBase64String(webp.ToArray()) } } } };
    File.WriteAllText(path, JsonSerializer.Serialize(data));
    using var a = Archive.Open(path); var node = a.Rooms[0].Node["items"]!["image"]!["data"]!["result"]!;
    using var preview = SKBitmap.Decode(ImagePreview.Decode(a, node));
    if (preview.Width != 600 || preview.Height != 400 || preview.GetPixel(300,200).Blue < 150) throw new Exception("WebP preview differs");
    Console.WriteLine("PASS: base64 WebP renders with expected size and colour");
}
finally { File.Delete(path); }
Console.WriteLine($"SUCCESS: {count + 1} image rendering checks passed.");
