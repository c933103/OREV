// Copyright (c) 2026 c933103 and contributors.
// SPDX-License-Identifier: LicenseRef-OREV-Community-1.1
// See LICENSE, including Section 10 and Schedule A.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ExportViewer;

public sealed record Message(string Id, JNode Node, string Parent, string Role, string Model, string Date);
public sealed record ThreadEntry(Message Message, int Depth);
public sealed class Room
{
    public required JNode Node { get; init; }
    public required string Title { get; init; }
    public required List<ThreadEntry> Thread { get; init; }
    public int Number { get; init; }
    public int BrokenLinks { get; init; }
    public Dictionary<string, List<JNode>> ItemsByMessage { get; init; } = new();
    public override string ToString() => $"{Number + 1}. {Title}  ({Thread.Count})";
}

public sealed class Archive : IDisposable
{
    readonly FileStream file;
    public string Path { get; }
    public long Size => file.Length;
    public JNode Root { get; private set; } = null!;
    public string Version { get; private set; } = "";
    public List<Room> Rooms { get; } = new();
    public bool Bulk { get; private set; }
    Archive(string path) { Path = path; file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.RandomAccess); }
    public static Archive Open(string path, CancellationToken token = default, IProgress<int>? progress = null)
    {
        var a = new Archive(path);
        try { a.Load(token, progress); return a; } catch { a.Dispose(); throw; }
    }
    void Load(CancellationToken token, IProgress<int>? progress)
    {
        Root = new JsonIndex(file, token, progress).Parse();
        Version = Text(Root["version"], 64);
        if (Version != "orpg.3.0") throw new FormatException($"Unsupported export version: {Version}. Expected orpg.3.0.");
        Bulk = Root["rooms"] != null;
        var rooms = Bulk ? Root["rooms"]?.Values ?? throw new FormatException("rooms must be an array") : new List<JNode> { Root };
        foreach (var node in rooms)
        {
            token.ThrowIfCancellationRequested();
            if (node.Kind != '{' || node["messages"]?.Fields == null || node["items"]?.Fields == null || node["characters"]?.Fields == null)
                throw new FormatException($"Room {Rooms.Count + 1} lacks the expected messages/items/characters objects.");
            var messages = new List<Message>();
            foreach (var pair in node["messages"]!.Fields!)
            {
                var m = pair.Value; string character = Text(m["characterId"], 1024);
                string role = Text(m["type"], 128); if (role.Length == 0) role = character == "USER" ? "user" : "assistant";
                string model = Text(node["characters"]?[character]?["model"], 1024);
                messages.Add(new(pair.Key, m, Text(m["parentMessageId"], 1024), role, model, Text(m["createdAt"], 128)));
            }
            // Stable timestamp order, preserving export order for ties and missing dates.
            messages = messages.OrderBy(m => m.Date, StringComparer.Ordinal).ToList();
            var byId = messages.ToDictionary(m => m.Id);
            var children = messages.Where(m => byId.ContainsKey(m.Parent) && m.Parent != m.Id)
                .GroupBy(m => m.Parent).ToDictionary(g => g.Key, g => g.ToList());
            var entries = new List<ThreadEntry>(); var seen = new HashSet<string>();
            int broken = messages.Count(m => m.Parent.Length > 0 && !byId.ContainsKey(m.Parent));
            void Add(Message root)
            {
                var stack = new Stack<(Message, int)>(); stack.Push((root, 0));
                while (stack.Count > 0)
                {
                    token.ThrowIfCancellationRequested(); var (m, depth) = stack.Pop();
                    if (!seen.Add(m.Id)) continue;
                    entries.Add(new(m, depth));
                    if (children.TryGetValue(m.Id, out var replies)) for (int i = replies.Count - 1; i >= 0; i--) stack.Push((replies[i], depth + 1));
                }
            }
            foreach (var m in messages.Where(m => !byId.ContainsKey(m.Parent))) Add(m);
            foreach (var m in messages.Where(m => !seen.Contains(m.Id)).ToArray()) { if (seen.Contains(m.Id)) continue; broken++; Add(m); }
            var itemsByMessage = new Dictionary<string, List<JNode>>(StringComparer.Ordinal);
            foreach (var item in node["items"]!.Fields!.Values)
            {
                string owner = Text(item["messageId"], 1024); if (owner.Length == 0) continue;
                if (!itemsByMessage.TryGetValue(owner, out var list)) itemsByMessage[owner] = list = new(); list.Add(item);
            }
            string title = Text(node["title"], 500);
            Rooms.Add(new() { Node = node, Title = title.Length > 0 ? title : "Untitled conversation", Number = Rooms.Count, Thread = entries, BrokenLinks = broken, ItemsByMessage = itemsByMessage });
        }
    }
    public SliceStream Raw(JNode n) => new(file, n.Start, n.Length);
    public JsonStringReader StringReader(JNode n)
    {
        if (n.Kind != '"') throw new ArgumentException("Not a JSON string");
        return new(new SliceStream(file, n.Start + 1, n.Length - 2));
    }
    public string Text(JNode? n, int limit = 32768)
    {
        if (n == null) return "";
        if (n.Kind != '"')
        {
            if (n.Fields != null) return $"{{{n.Fields.Count} fields}}";
            if (n.Values != null) return $"[{n.Values.Count} entries]";
            using var r = new StreamReader(Raw(n), Encoding.UTF8); return r.ReadToEnd();
        }
        using var reader = StringReader(n); var s = new StringBuilder();
        for (int i = 0; i < limit; i++) { int c = reader.Read(); if (c < 0) return s.ToString(); s.Append((char)c); }
        if (reader.Read() >= 0) s.Append("\n… [preview shortened; export retains the full value]");
        return s.ToString();
    }
    public IEnumerable<JNode> Items(Room room, Message message)
    {
        var used = new HashSet<long>();
        foreach (var reference in message.Node["items"]?.Values ?? new())
        {
            string id = reference.Kind == '"' ? Text(reference, 1024) : Text(reference["id"], 1024);
            if (room.Node["items"]?[id] is { } item && used.Add(item.Start)) yield return item;
        }
        // Include orphan item references; do not silently hide data from a partially saved export.
        foreach (var item in room.ItemsByMessage.GetValueOrDefault(message.Id) ?? Enumerable.Empty<JNode>())
            if (!used.Contains(item.Start)) { used.Add(item.Start); yield return item; }
    }
    public IEnumerable<JNode> Descendants(JNode node)
    {
        var stack = new Stack<JNode>(); stack.Push(node);
        while (stack.Count > 0) { var n = stack.Pop(); yield return n; foreach (var child in n.Children.Reverse()) stack.Push(child); }
    }
    public IEnumerable<(JNode Node, string Mime)> Attachments(JNode item)
    {
        var seen = new List<JNode>();
        foreach (var node in Descendants(item))
        {
            if (node.Kind != '"' || node.Length < 10) continue;
            string prefix = Text(node, 160).Split('\n')[0];
            if (!prefix.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            int semi = prefix.IndexOf(';'); int comma = prefix.IndexOf(',');
            if (semi > 5 && comma > semi && prefix[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                {
                if (seen.Any(previous => EqualRaw(previous, node))) continue;
                seen.Add(node); yield return (node, prefix[5..semi]);
            }
        }
    }
    bool EqualRaw(JNode left, JNode right)
    {
        if (left.Length != right.Length) return false;
        using var a = Raw(left); using var b = Raw(right);
        var first = new byte[65536]; var second = new byte[65536]; int read;
        while ((read = a.Read(first)) > 0)
        {
            int got = 0; while (got < read) { int n = b.Read(second, got, read - got); if (n == 0) return false; got += n; }
            if (!first.AsSpan(0, read).SequenceEqual(second.AsSpan(0, read))) return false;
        }
        return true;
    }
    public void DecodeAttachment(JNode node, Stream output, CancellationToken cancel = default, long limit = long.MaxValue)
    {
        using var reader = StringReader(node);
        int c; int header = 0;
        do { c = reader.Read(); if (c < 0 || ++header > 4096) throw new FormatException("Invalid data URL"); } while (c != ',');
        var chars = new char[32768]; var bytes = new byte[24576]; int used = 0; long total = 0;
        while ((c = reader.Read()) >= 0)
        {
            if (char.IsWhiteSpace((char)c)) continue;
            chars[used++] = (char)c;
            if (used != chars.Length) continue;
            cancel.ThrowIfCancellationRequested();
            if (!Convert.TryFromBase64Chars(chars.AsSpan(0, used), bytes, out int got)) throw new FormatException("Invalid base64 attachment");
            total += got; if (total > limit) throw new IOException("Attachment exceeds preview limit; use Save attachment instead.");
            output.Write(bytes, 0, got); used = 0;
        }
        cancel.ThrowIfCancellationRequested();
        if (used > 0)
        {
            if (!Convert.TryFromBase64Chars(chars.AsSpan(0, used), bytes, out int got)) throw new FormatException("Invalid base64 attachment");
            total += got; if (total > limit) throw new IOException("Attachment exceeds preview limit; use Save attachment instead."); output.Write(bytes, 0, got);
        }
    }
    public void CopyRaw(JNode n, Stream destination, CancellationToken cancel = default, IProgress<long>? progress = null)
    {
        using var source = Raw(n); var buffer = new byte[1024 * 1024]; long copied = 0; int read;
        while ((read = source.Read(buffer)) > 0) { cancel.ThrowIfCancellationRequested(); destination.Write(buffer, 0, read); copied += read; progress?.Report(copied); }
    }
    public void Export(Room room, string destination, CancellationToken cancel = default)
    {
        if ((uint)room.Number >= (uint)Rooms.Count || !ReferenceEquals(Rooms[room.Number], room))
            throw new ArgumentException("The conversation belongs to a different export.", nameof(room));
        // CreateNew + atomic rename: never replace an existing file or leave a half-written JSON.
        string temp = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            {
                if (!Bulk) CopyRaw(Root, output, cancel);
                else
                {
                    var prefix = Encoding.UTF8.GetBytes("{\"version\":" + JsonSerializer.Serialize(Version)); output.Write(prefix);
                    foreach (var pair in room.Node.Fields!)
                    {
                        if (pair.Key == "version") continue;
                        cancel.ThrowIfCancellationRequested(); output.Write(Encoding.UTF8.GetBytes("," + JsonSerializer.Serialize(pair.Key) + ":"));
                        CopyRaw(pair.Value, output, cancel);
                    }
                    output.WriteByte((byte)'}');
                }
                output.Flush(true);
            }
            cancel.ThrowIfCancellationRequested(); File.Move(temp, destination, false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static string FileName(Room room)
    {
        string title = new(room.Title.Select(c => c < 32 || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray());
        title = title.Trim().TrimEnd('.'); if (title.Length > 90) title = title[..90];
        return $"{room.Number + 1:D4}-{title}.json";
    }
    public void Dispose() => file.Dispose();
}
