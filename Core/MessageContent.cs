// Copyright (c) 2026 c933103 and contributors.
// SPDX-License-Identifier: LicenseRef-OREV-Community-1.1
// See LICENSE, including Section 10 and Schedule A.

using System.Text;
namespace ExportViewer;

// This is the presentation path used by the GUI, independently testable without WinForms.
public static class MessageContent
{
    public static IEnumerable<string> ReadableText(Archive archive, JNode data)
    {
        var emitted = new HashSet<long>();
        foreach (string name in new[] { "content", "summary" })
        {
            if (data[name] is not { } content) continue;
            if (content.Kind == '"') { if (emitted.Add(content.Start)) yield return archive.Text(content, 65536); continue; }
            foreach (var n in archive.Descendants(content))
            {
                if (n.Fields == null) continue;
                foreach (string key in new[] { "text", "refusal" })
                    if (n[key] is { Kind: '"' } text && emitted.Add(text.Start)) yield return archive.Text(text, 65536);
            }
        }
        foreach (string key in new[] { "text", "refusal" })
            if (data[key] is { Kind: '"' } text && emitted.Add(text.Start)) yield return archive.Text(text, 65536);
    }

    public static string ToolDetails(Archive archive, JNode data, int limit = 65536)
    {
        var output = new StringBuilder(); int entries = 0; bool shortened = false;
        void Append(string text)
        {
            int remaining = limit - output.Length;
            if (remaining <= 0) { shortened = true; return; }
            if (text.Length > remaining) { output.Append(text.AsSpan(0, remaining)); shortened = true; }
            else output.Append(text);
        }
        void Visit(JNode node, string label, int depth)
        {
            if (output.Length >= limit || entries++ >= 1000) { shortened = true; return; }
            string indent = new(' ', Math.Min(depth, 12) * 2);
            if (node.Fields != null)
            {
                if (label.Length > 0) Append(indent + label + ":\n");
                foreach (var pair in node.Fields)
                {
                    if (pair.Key is "encrypted_content" or "signature") { Append(indent + "  " + pair.Key + ": [opaque value retained in metadata]\n"); continue; }
                    Visit(pair.Value, pair.Key, depth + 1);
                }
            }
            else if (node.Values != null)
            {
                Append(indent + label + ":\n");
                for (int i = 0; i < node.Values.Count && output.Length < limit; i++) Visit(node.Values[i], $"[{i}]", depth + 1);
            }
            else
            {
                string value = archive.Text(node, Math.Min(16384, Math.Max(1, limit - output.Length)));
                if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) value = "[embedded attachment rendered below; original retained]";
                Append(indent + label + ": " + value + "\n");
            }
        }
        Visit(data, "", 0);
        if (shortened) output.Append("\n… [preview shortened; complete fields available in metadata and exports]");
        return output.ToString();
    }
}
