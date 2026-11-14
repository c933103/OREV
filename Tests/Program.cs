using ExportViewer;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

int checks = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); checks++; Console.WriteLine("PASS: " + name); }
string folder = Path.Combine(Path.GetTempPath(), "or-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
try
{
    string sample = args.FirstOrDefault(a => !a.StartsWith("--")) ?? Path.Combine(AppContext.BaseDirectory, "fixtures", "synthetic-bulk.json");
    using (var archive = Archive.Open(sample))
    {
        var original = JsonNode.Parse(File.ReadAllText(sample))!;
        bool expectedBulk = original["rooms"] is JsonArray;
        var expectedRooms = expectedBulk ? ((JsonArray)original["rooms"]!).ToArray() : new[] { original };
        int[] expectedMessages = expectedRooms.Select(r => ((JsonObject)r!["messages"]!).Count).ToArray();
        Check(archive.Bulk == expectedBulk && archive.Rooms.Count == expectedRooms.Length, $"all {expectedRooms.Length} conversations indexed");
        Check(archive.Rooms.Select(r => r.Thread.Count).SequenceEqual(expectedMessages), $"all {expectedMessages.Sum()} messages indexed");
        int images = 0;
        foreach (var room in archive.Rooms)
        {
            Check(room.Thread.Select(e => e.Message.Id).Distinct().Count() == room.Thread.Count, $"room {room.Number + 1}: branches contain every message exactly once");
            var expectedRoom = expectedRooms[room.Number]!;
            var expectedMap = (JsonObject)expectedRoom["messages"]!;
            Check(room.Thread.Select(e => e.Message.Id).ToHashSet().SetEquals(expectedMap.Select(p => p.Key)), $"room {room.Number + 1}: indexed message IDs exactly match source");
            var positions = room.Thread.Select((e, index) => (e.Message.Id, index)).ToDictionary(x => x.Id, x => x.index);
            Check(room.Thread.All(e =>
            {
                var saved = expectedMap[e.Message.Id]!;
                string parent = saved["parentMessageId"]?.GetValue<string>() ?? "";
                string character = saved["characterId"]?.GetValue<string>() ?? "";
                string model = expectedRoom["characters"]?[character]?["model"]?.GetValue<string>() ?? "";
                return e.Message.Parent == parent && e.Message.Model == model &&
                    (parent.Length == 0 || !positions.TryGetValue(parent, out int parentAt) || parentAt < positions[e.Message.Id]);
            }), $"room {room.Number + 1}: parent links, reply order and model labels match source");
            int emptyMessages = 0, longestText = 0;
            foreach (var entry in room.Thread)
            {
                var message = expectedMap[entry.Message.Id]!;
                var expectedItems = new List<string>();
                foreach (var reference in message["items"]?.AsArray() ?? new JsonArray())
                {
                    string id = reference is JsonValue ? reference.GetValue<string>() : reference?["id"]?.GetValue<string>() ?? "";
                    if (expectedRoom["items"]?[id] != null && !expectedItems.Contains(id)) expectedItems.Add(id);
                }
                foreach (var pair in (JsonObject)expectedRoom["items"]!)
                    if (pair.Value?["messageId"]?.GetValue<string>() == entry.Message.Id && !expectedItems.Contains(pair.Key)) expectedItems.Add(pair.Key);
                var actualItems = archive.Items(room, entry.Message).ToArray();
                Check(actualItems.Select(item => archive.Text(item["id"], 1024)).SequenceEqual(expectedItems), $"room {room.Number + 1}: message content references preserve item order");
                foreach (var item in actualItems)
                {
                    string id = archive.Text(item["id"], 1024);
                    var expectedData = expectedRoom["items"]![id]?["data"] ?? expectedRoom["items"]![id]!;
                    var expectedText = new List<string>();
                    void Collect(JsonNode? n)
                    {
                        if (n is JsonObject obj)
                        {
                            foreach (string key in new[] { "text", "refusal" }) if (obj[key] is JsonValue v && v.TryGetValue<string>(out string? value)) expectedText.Add(value!);
                            foreach (var pair in obj) if (pair.Key is not ("text" or "refusal")) Collect(pair.Value);
                        }
                        else if (n is JsonArray array) foreach (var child in array) Collect(child);
                    }
                    foreach (string field in new[] { "content", "summary" })
                    {
                        if (expectedData[field] is JsonValue str && str.TryGetValue<string>(out var value)) expectedText.Add(value!);
                        else Collect(expectedData[field]);
                    }
                    foreach (string key in new[] { "text", "refusal" }) if (expectedData[key] is JsonValue value && value.TryGetValue<string>(out var text)) expectedText.Add(text!);
                    Check(MessageContent.ReadableText(archive, item["data"] ?? item).SequenceEqual(expectedText), $"room {room.Number + 1}: GUI presentation receives all text/reasoning values");
                }
                if (actualItems.Length == 0) emptyMessages++;
                foreach (var item in actualItems)
                foreach (var node in archive.Descendants(item))
                {
                    if (node.Fields == null) continue;
                    foreach (string key in new[] { "text", "refusal" })
                    {
                        if (node[key] is not { Kind: '"' } text) continue;
                        var displayed = archive.Text(text, 65536);
                        longestText = Math.Max(longestText, displayed.Length);
                        if (text.Length < 65536)
                        {
                            using var raw = archive.Raw(text);
                            string expectedText = System.Text.Json.JsonSerializer.Deserialize<string>(raw)!;
                            Check(displayed == expectedText, $"room {room.Number + 1}: readable text matches the original Unicode value");
                        }
                    }
                }
            }
            Console.WriteLine($"INFO: room {room.Number + 1}: {room.Thread.Count} messages; {emptyMessages} empty messages retained; longest readable value={longestText:N0} characters.");
            string output = Path.Combine(folder, Archive.FileName(room)); archive.Export(room, output);
            var actual = JsonNode.Parse(File.ReadAllText(output))!;
            var expected = expectedRooms[room.Number]!.DeepClone(); expected["version"] = "orpg.3.0";
            Check(JsonNode.DeepEquals(expected, actual), $"room {room.Number + 1}: complete metadata/content/base64 round-trip");
            using var single = Archive.Open(output);
            Check(!single.Bulk && single.Rooms.Count == 1, $"room {room.Number + 1}: individual file loads as single export");
            string recopy = output + ".copy.json"; single.Export(single.Rooms[0], recopy);
            Check(SHA256.HashData(File.ReadAllBytes(output)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(recopy))), $"room {room.Number + 1}: single export is byte-for-byte unchanged");
            bool rejected = false; try { archive.Export(room, output); } catch (IOException) { rejected = true; }
            Check(rejected, $"room {room.Number + 1}: existing output never overwritten");
            foreach (var item in room.Node["items"]!.Children)
            foreach (var (node, mime) in archive.Attachments(item))
            {
                using var decoded = new MemoryStream(); archive.DecodeAttachment(node, decoded);
                var data = archive.Text(node, 20_000_000); var expectedBytes = Convert.FromBase64String(data[(data.IndexOf(',') + 1)..]);
                Check(decoded.ToArray().SequenceEqual(expectedBytes), $"embedded attachment {++images}: decoded bytes match source");
            }
        }
        Console.WriteLine($"INFO: {images} embedded attachments decoded and checked.");
    }
    using (var presentation = Archive.Open(Path.Combine(AppContext.BaseDirectory, "fixtures", "presentation.json")))
    {
        var items = presentation.Rooms[0].Node["items"]!;
        Check(MessageContent.ReadableText(presentation, items["user-text"]!["data"]!).Single().Contains("Visible user paragraph"), "presentation: user paragraph reaches the GUI");
        Check(MessageContent.ReadableText(presentation, items["answer"]!["data"]!).Single().Contains("Visible assistant answer"), "presentation: assistant answer reaches the GUI");
        Check(MessageContent.ReadableText(presentation, items["reasoning"]!["data"]!).Single() == "Visible reasoning body.", "presentation: reasoning body reaches the GUI");
        string tool = MessageContent.ToolDetails(presentation, items["tool"]!["data"]!);
        Check(tool.Contains("Visible tool search query.") && tool.Contains("Visible tool source.") && tool.Contains("https://example.com/"), "presentation: nested tool query, source title and URL are readable");
    }
    string edge = Path.Combine(folder, "edge.json");
    string RoomBody(string messages = "{}", string items = "{}") => "{\"title\":\"Test \\uD83D\\uDE80 \\n \\\" \\\\\",\"characters\":{},\"messages\":" + messages + ",\"items\":" + items + ",\"artifacts\":{\"future\":{\"unknown\":[true,false,null,1.25e-3]}}}";
    string Wrap(string room) => "{\"version\":\"orpg.3.0\",\"rooms\":[" + room + "]}";
    File.WriteAllText(edge, Wrap(RoomBody()));
    using (var a = Archive.Open(edge)) { Check(a.Rooms.Count == 1 && a.Rooms[0].Thread.Count == 0, "empty conversation and Unicode escapes"); }
    var bad = new[] { "{", Wrap(RoomBody()) + "x", "{\"version\":\"other\"}", "{\"version\":\"orpg.3.0\",\"version\":\"orpg.3.0\"}", Wrap(RoomBody()).Replace("1.25e-3", "01"), Wrap(RoomBody()).Replace("1.25e-3", "1."), Wrap(RoomBody()).Replace("1.25e-3", "1e+") };
    foreach (string value in bad)
    {
        File.WriteAllText(edge, value); bool rejected = false;
        try { using var ignored = Archive.Open(edge); } catch (FormatException) { rejected = true; }
        Check(rejected, "malformed/unsupported JSON rejected");
    }
    File.WriteAllText(edge, Wrap(RoomBody("{\"a\":{\"parentMessageId\":\"b\",\"type\":\"user\"},\"b\":{\"parentMessageId\":\"a\",\"type\":\"assistant\"},\"c\":{\"parentMessageId\":\"missing\"}}")));
    using (var a = Archive.Open(edge)) Check(a.Rooms[0].Thread.Count == 3 && a.Rooms[0].BrokenLinks > 0, "cyclic/missing parents cannot hide messages or hang");
    using (var cancelled = new CancellationTokenSource())
    {
        cancelled.Cancel(); bool rejected = false;
        try { using var ignored = Archive.Open(sample, cancelled.Token); } catch (OperationCanceledException) { rejected = true; }
        Check(rejected, "loading cancellation");
    }
    // String spanning scanner boundaries; escaped slash/newline inside base64.
    string longString = new('z', 1048576 - 39);
    File.WriteAllText(edge, Wrap(RoomBody(items: "{\"big\":{\"data\":{\"padding\":\"" + longString + "\",\"result\":\"data:application/octet-stream;base64,QUJD\\nRA==\"}}}")));
    using (var a = Archive.Open(edge))
    {
        var item = a.Rooms[0].Node["items"]!["big"]!;
        Check(a.Text(item["data"]!["padding"], longString.Length + 1) == longString, "large string across buffer boundary");
        using var decoded = new MemoryStream(); a.DecodeAttachment(a.Attachments(item).Single().Node, decoded);
        Check(Encoding.ASCII.GetString(decoded.ToArray()) == "ABCD", "escaped whitespace in base64 decoded");
    }
    if (args.Contains("--large"))
    {
        Console.WriteLine("Generating 1 GiB base64 test archive…");
        string huge = Path.Combine(folder, "1GiB.json"), output = Path.Combine(folder, "1GiB-single.json");
        using (var f = File.Create(huge))
        {
            f.Write(Encoding.UTF8.GetBytes("{\"version\":\"orpg.3.0\",\"rooms\":[{\"title\":\"Large\",\"characters\":{},\"messages\":{},\"items\":{\"blob\":{\"data\":{\"result\":\"data:application/octet-stream;base64,"));
            byte[] chunk = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("QUJD", 262144)));
            for (int i = 0; i < 1024; i++) f.Write(chunk);
            f.Write(Encoding.UTF8.GetBytes("\"}}}}]}"));
        }
        GC.Collect(); var watch = Stopwatch.StartNew();
        using var a = Archive.Open(huge); double indexed = watch.Elapsed.TotalSeconds;
        long managed = GC.GetTotalMemory(true); long peak = Process.GetCurrentProcess().PeakWorkingSet64;
        Check(a.Rooms.Count == 1 && a.Size > 1073741824L, "1 GiB archive indexed");
        Check(managed < 100 * 1048576L, "managed index remains below 100 MiB on 1 GiB input");
        a.Export(a.Rooms[0], output);
        using var split = Archive.Open(output);
        var blob = a.Rooms[0].Node["items"]!["blob"]!["data"]!["result"]!;
        var splitBlob = split.Rooms[0].Node["items"]!["blob"]!["data"]!["result"]!;
        using var first = a.Raw(blob); using var second = split.Raw(splitBlob);
        Check(SHA256.HashData(first).SequenceEqual(SHA256.HashData(second)), "1 GiB base64 payload preserved byte-for-byte in individual export");
        Console.WriteLine($"BENCHMARK: index={indexed:F2}s; index/export/reindex/hash={watch.Elapsed.TotalSeconds:F2}s; managed={managed / 1048576.0:F1}MiB; peak working set at index={peak / 1048576.0:F1}MiB");
    }
    if (args.Contains("--compat"))
    {
        using var synthetic = Archive.Open("/workspace/shared/or-viewer-compat-bulk.json");
        synthetic.Export(synthetic.Rooms[0], "/workspace/shared/or-viewer-compat-single.json");
        Console.WriteLine("Created synthetic importer fixture using the production exporter.");
    }
    Console.WriteLine($"SUCCESS: {checks} checks passed.");
}
finally { Directory.Delete(folder, true); }
