// Copyright (c) 2026 c933103 and contributors.
// SPDX-License-Identifier: LicenseRef-OREV-Community-1.1
// See LICENSE, including Section 10 and Schedule A.

using ExportViewer;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        string scratch = Path.Combine(Path.GetTempPath(), "orev-folders-" + Guid.NewGuid().ToString("N"));
        string first = Path.Combine(Directory.CreateDirectory(Path.Combine(scratch, "first")).FullName, "export.json");
        string second = Path.Combine(Directory.CreateDirectory(Path.Combine(scratch, "second")).FullName, "export.json");
        string third = Path.Combine(scratch, "bulk.json");
        File.Copy(args[0], first);
        var secondRoom = JsonNode.Parse(File.ReadAllText(first))!.AsObject();
        var userContent = secondRoom["items"]!["user-text"]!["data"]!["content"]![0]!;
        userContent["text"] = "Message from the second export";
        secondRoom["sourceMarker"] = "second file metadata";
        File.WriteAllText(second, secondRoom.ToJsonString());
        var thirdRoom = secondRoom.DeepClone();
        thirdRoom["title"] = "Bulk folder conversation";
        File.WriteAllText(third, new JsonObject { ["version"] = "orpg.3.0", ["rooms"] = new JsonArray(thirdRoom) }.ToJsonString());
        using var form = new MainForm(new[] { first });
        form.Shown += async (_, _) =>
        {
            try
            {
                for (int i = 0; i < 1000; i++)
                {
                    await Task.Delay(20);
                    if (Get(form, "current") != null && !(bool)Get(form, "busy")!) break;
                }
                var feed = (FlowLayoutPanel)Get(form, "feed")!;
                var before = Dump(feed); Console.WriteLine(JsonSerializer.Serialize(before));
                Console.WriteLine("BEFORE " + string.Join("; ", Descendants(feed).OfType<RichTextBox>().Select(c => c.Text.Length + " chars " + c.Bounds + " parent " + c.Parent!.ClientRectangle)));
                foreach (var button in Descendants(feed).OfType<Button>().Where(b => b.Text.Contains("Reasoning") || b.Text.Contains("Show tool")).ToArray())
                {
                    int expanded = button.Parent!.Height;
                    button.PerformClick(); Application.DoEvents();
                    if (button.Parent.Height >= expanded) throw new Exception("Collapsing a section did not reduce its card height");
                    button.PerformClick(); Application.DoEvents();
                    if (button.Parent.Height < expanded) throw new Exception("Expanding a section clipped its body");
                }
                await Task.Delay(100);
                var after = Dump(feed);
                string prefix = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "OpenRouter-Viewer-GUI-test");
                File.WriteAllText(prefix + ".json", JsonSerializer.Serialize(new { before, after }, new JsonSerializerOptions { WriteIndented = true }));
                using (var screenshot = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(screenshot, new Rectangle(Point.Empty, form.Size)); screenshot.Save(prefix + ".png"); }
                var boxes = Descendants(feed).OfType<RichTextBox>().ToArray();
                if (boxes.Length == 0) throw new Exception("No native text controls were created");
                foreach (var box in boxes)
                {
                    if (box.TextLength == 0) throw new Exception("Message native text control is empty");
                    if (!box.Visible || box.Width < 100 || box.Height < 25 || !box.Parent!.ClientRectangle.Contains(box.Bounds)) throw new Exception("Native text control is hidden or clipped: " + box.Bounds + " within " + box.Parent!.ClientRectangle);
                }
                Console.WriteLine("PASS: " + boxes.Length + " nonempty native text controls visible without parent clipping");
                await CheckSourceFolders(form, first, second, third, scratch);
                File.WriteAllText(prefix + ".result", "PASS");
            }
            catch (Exception e) { Console.WriteLine(e.ToString()); Environment.ExitCode = 1; }
            finally { form.Close(); }
        };
        Application.Run(form);
        Directory.Delete(scratch, true);
    }
    static async Task CheckSourceFolders(MainForm form, string first, string second, string third, string scratch)
    {
        var rooms = (ListView)Get(form, "rooms")!;
        rooms.Items[0].Checked = true;
        string caseVariant = Path.Combine(Path.GetDirectoryName(first)!, "EXPORT.JSON");
        string[] inputs = File.Exists(caseVariant) ? new[] { second, third, first, caseVariant } : new[] { second, third, first };
        await (Task)Method("LoadFiles").Invoke(form, new object[] { inputs })!;
        Expect(rooms.Groups.Count == 3 && rooms.Items.Count == 3, "Later imports must accumulate and skip the same path");
        Expect(rooms.Items[0].Checked && rooms.Items[0].Selected, "Adding exports must preserve checks and selection");
        Expect(rooms.Groups[0].Header != rooms.Groups[1].Header &&
            rooms.Groups[0].Header.Contains("first", StringComparison.Ordinal) &&
            rooms.Groups[1].Header.Contains("second", StringComparison.Ordinal), "Matching filenames need distinct folder labels");
        Expect(rooms.Items[0].Group == rooms.Groups[0] && rooms.Items[1].Group == rooms.Groups[1] &&
            rooms.Items[2].Group == rooms.Groups[2], "Each row must stay in its source folder");
        Expect(rooms.Items[1].ToolTipText.EndsWith(second, StringComparison.Ordinal), "Row tooltip must identify the original path");

        rooms.Items[0].Selected = false;
        rooms.Items[1].Selected = true;
        var feed = (FlowLayoutPanel)Get(form, "feed")!;
        Expect(Descendants(feed).OfType<RichTextBox>().Any(box => box.Text.Trim() == "Message from the second export"),
            "Selecting the other same-named JSON must render its own message");
        ((ToolStripButton)Get(form, "metadata")!).Checked = true;
        var tree = (TreeView)Get(form, "metaTree")!;
        tree.SelectedNode = tree.Nodes[0].Nodes.Cast<TreeNode>().Single(node => node.Text.StartsWith("sourceMarker :", StringComparison.Ordinal));
        Expect(((RichTextBox)Get(form, "metaValue")!).Text == "second file metadata", "Metadata must read the selected source");

        rooms.Items[1].Checked = true;
        rooms.Items[2].Checked = true;
        rooms.Groups[0].CollapsedState = ListViewGroupCollapsedState.Collapsed;
        ((TextBox)Get(form, "search")!).Text = "Bulk folder";
        Expect(rooms.Items.Count == 1 && rooms.Items[0].Checked, "Filtering must preserve checks and match across folders");
        var chosen = (Array)Method("ChosenConversations").Invoke(form, null)!;
        Expect(chosen.Length == 3, "Checked rows from other folders must remain exportable");
        string destination = Directory.CreateDirectory(Path.Combine(scratch, "output")).FullName;
        foreach (var conversation in chosen)
            Method("ExportConversation").Invoke(null, new[] { conversation, destination, CancellationToken.None });
        string[] outputs = Directory.GetFiles(destination, "*.json");
        Expect(outputs.Length == 3 && outputs.Any(path => path.EndsWith(" (2).json", StringComparison.Ordinal)),
            "Same-titled conversations must export to distinct filenames");
        var body = outputs.Select(path => JsonNode.Parse(File.ReadAllText(path))).ToArray();
        var expected = new[] { JsonNode.Parse(File.ReadAllText(first)), JsonNode.Parse(File.ReadAllText(second)),
            JsonNode.Parse(File.ReadAllText(third))!["rooms"]![0] };
        foreach (var room in expected)
            Expect(body.Any(output => JsonNode.DeepEquals(room, output)), "Export must preserve the correct source's room fields");
        Console.WriteLine("PASS: accumulated export folders, duplicate filenames, source-specific display and cross-folder exports");
    }
    static void Expect(bool condition, string message) { if (!condition) throw new Exception(message); }
    static MethodInfo Method(string name) => typeof(MainForm).GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic)!;
    static object? Get(object o,string field) => o.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o);
    static IEnumerable<Control> Descendants(Control c) { foreach (Control child in c.Controls) { yield return child; foreach (var n in Descendants(child)) yield return n; } }
    static object[] Dump(Control c) => c.Controls.Cast<Control>().Select(child => (object)new { type = child.GetType().Name, bounds = child.Bounds.ToString(), visible = child.Visible, textLength = child.Text.Length, children = Dump(child) }).ToArray();
}
