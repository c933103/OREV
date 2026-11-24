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
        for (int i = 0; i < 25; i++)
            thirdRoom["messages"]![$"extra-{i}"] = new JsonObject { ["type"] = "user" };
        File.WriteAllText(third, new JsonObject { ["version"] = "orpg.3.0", ["rooms"] = new JsonArray(thirdRoom) }.ToJsonString());
        string sessionPath = Path.Combine(scratch, "session.json");
        var decisions = new Queue<(string Path, bool Refresh)>();
        TaskDialogButton ShowWarning(IWin32Window owner, TaskDialogPage dialog)
        {
            Expect(decisions.TryDequeue(out var decision), "Unexpected warning for an unchanged or new export");
            string text = dialog.Text ?? "";
            Expect(dialog.Heading == "This export has changed" && dialog.Icon == TaskDialogIcon.Warning &&
                text.Contains(decision.Path, StringComparison.Ordinal) &&
                text.Contains("1 checked conversation", StringComparison.Ordinal),
                "Warning must identify the changed path and the number of checks that would be cleared");
            var keep = dialog.Buttons.Single(button => button.Text is "Keep current version" or "Skip file");
            var refresh = dialog.Buttons.Single(button => button.Text is "Refresh folder" or "Open updated file");
            Expect(dialog.DefaultButton == keep && dialog.AllowCancel, "Keeping the current version must be the default");
            dialog.Created += (_, _) => ((Control)owner).BeginInvoke((Action)(() =>
                (decision.Refresh ? refresh : keep).PerformClick()));
            return TaskDialog.ShowDialog(owner, dialog);
        }
        using var form = CreateForm([first], ShowWarning, sessionPath);
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
                await CheckSamePathRefresh(form, first, second, scratch, decisions);
                Expect(decisions.Count == 0, "Every changed export must show its warning");
                await CheckSessionRestore(form, sessionPath, second, third, decisions, ShowWarning);
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
    static async Task CheckSamePathRefresh(MainForm form, string first, string second, string scratch,
        Queue<(string Path, bool Refresh)> decisions)
    {
        var rooms = (ListView)Get(form, "rooms")!;
        var feed = (FlowLayoutPanel)Get(form, "feed")!;
        ((TextBox)Get(form, "search")!).Clear();
        rooms.Items[1].Selected = true;
        ListViewGroup folder = rooms.Groups[1];
        string original = File.ReadAllText(second);
        string edited = original.Replace("Message from the second export", "Changed from the second export");
        long size = new FileInfo(second).Length;
        DateTime stamp = File.GetLastWriteTimeUtc(second);
        File.WriteAllText(second, edited);
        File.SetLastWriteTimeUtc(second, stamp);
        Expect(new FileInfo(second).Length == size && File.GetLastWriteTimeUtc(second) == stamp,
            "The in-place edit must preserve length and timestamp to test content-based refresh");
        decisions.Enqueue((second, false));
        await (Task)Method("LoadFiles").Invoke(form, new object[] { new[] { second } })!;
        Expect(rooms.Groups.Count == 3 && rooms.Groups[1] == folder && rooms.Items[1].Selected && rooms.Items[1].Checked,
            "Declining the warning must retain the folder, current selection and check");
        Expect(Descendants(feed).OfType<RichTextBox>().Any(box => box.Text.Trim() == "Message from the second export"),
            "Declining the warning must keep displaying the previous snapshot");

        decisions.Enqueue((second, true));
        await (Task)Method("LoadFiles").Invoke(form, new object[] { new[] { second } })!;
        Expect(rooms.Groups.Count == 3 && rooms.Groups[1] == folder && rooms.Items[1].Selected,
            "Refreshing the displayed source must reuse its folder and keep displaying refreshed content");
        Expect(Descendants(feed).OfType<RichTextBox>().Any(box => box.Text.Trim() == "Changed from the second export"),
            "The same-length, same-timestamp edit must update the visible message");
        Expect(!rooms.Items[1].Checked && rooms.Items[0].Checked && rooms.Items[2].Checked,
            "Only checks from the refreshed source must reset");

        rooms.Items[1].Checked = true;
        string replacement = Path.Combine(scratch, "replacement.json");
        File.Copy(second, replacement);
        File.Move(replacement, second, overwrite: true);
        await (Task)Method("LoadFiles").Invoke(form, new object[] { new[] { second } })!;
        Expect(rooms.Items[1].Checked && rooms.Groups.Count == 3, "An identical file replacement must keep existing checks");

        string newFirst = File.ReadAllText(first).Replace("Visible content regression", "Replacement export");
        File.WriteAllText(replacement, newFirst);
        File.Move(replacement, first, overwrite: true);
        decisions.Enqueue((first, true));
        await (Task)Method("LoadFiles").Invoke(form, new object[] { new[] { first } })!;
        Expect(rooms.Groups.Count == 3 && rooms.Items[0].Text == "Replacement export" && rooms.Items[1].Selected,
            "A changed replacement must refresh its source folder without switching away from another open conversation");
        string destination = Directory.CreateDirectory(Path.Combine(scratch, "refreshed")).FullName;
        Method("ExportConversation").Invoke(null, new[] { rooms.Items[0].Tag, destination, CancellationToken.None });
        Expect(File.ReadAllText(Directory.GetFiles(destination, "*.json").Single()) == newFirst,
            "Export must read the refreshed bytes from an atomically replaced file");
        Console.WriteLine("PASS: same-path refresh after in-place edits, identical and changed replacements, and updated exports");
    }
    static async Task CheckSessionRestore(MainForm form, string sessionPath, string second, string third,
        Queue<(string Path, bool Refresh)> decisions, Func<IWin32Window, TaskDialogPage, TaskDialogButton> showWarning)
    {
        ((TextBox)Get(form, "search")!).Text = "Bulk folder";
        ((ListView)Get(form, "rooms")!).Items[0].Selected = true;
        ((Button)Get(form, "next")!).PerformClick();
        for (int i = 0; i < 200 && (bool)Get(form, "sessionDirty")!; i++) await Task.Delay(20);
        Expect(!(bool)Get(form, "sessionDirty")!, "Session must autosave while the original window is open");
        string saved = File.ReadAllText(sessionPath);
        var json = JsonNode.Parse(saved)!;
        Expect(json["Position"]!["Path"]!.GetValue<string>() == third &&
            json["Position"]!["Page"]!.GetValue<int>() == 1 &&
            !saved.Contains("Changed from the second export", StringComparison.Ordinal),
            "Autosave must capture the active page without saving message bodies");

        File.WriteAllText(second, File.ReadAllText(second).Replace("Changed from", "Updated from"));
        decisions.Enqueue((second, false));
        using var restored = CreateForm([], showWarning, sessionPath);
        Exception? failure = null;
        restored.Shown += async (_, _) =>
        {
            try
            {
                for (int i = 0; i < 500 && !(bool)Get(restored, "sessionReady")!; i++) await Task.Delay(20);
                Expect((bool)Get(restored, "sessionReady")!, "Saved session must finish reopening");
                var rooms = (ListView)Get(restored, "rooms")!;
                Expect(rooms.Groups.Count == 2 && rooms.Items.Count == 1 &&
                    rooms.Items[0].Checked && rooms.Items[0].Selected &&
                    (int)Get(restored, "page")! == 1 && ((ToolStripButton)Get(restored, "metadata")!).Checked,
                    "Restore must recover available folders, filter, current page, selection, checks and metadata visibility");
                Expect(((Array)Method("ChosenConversations").Invoke(restored, null)!).Length == 1,
                    "Declined changed exports must stay out of the current checked selection");

                decisions.Enqueue((second, true));
                await (Task)Method("LoadFiles").Invoke(restored, new object[] { new[] { second } })!;
                ((TextBox)Get(restored, "search")!).Clear();
                Expect(rooms.Groups.Count == 3 && !rooms.Items.Cast<ListViewItem>().Single(item =>
                    item.ToolTipText.EndsWith(second, StringComparison.Ordinal)).Checked,
                    "Reopening a skipped changed export must ask again and never apply the old checks");
                Expect(decisions.Count == 0, "All startup and re-add warnings must have been shown");
            }
            catch (Exception e) { failure = e; }
            finally { restored.Close(); }
        };
        restored.ShowDialog(form);
        if (failure != null) throw failure;
        Console.WriteLine("PASS: session autosave, startup restore, and confirmation for changed exports");
    }
    static MainForm CreateForm(string[] paths, Func<IWin32Window, TaskDialogPage, TaskDialogButton> showWarning, string sessionPath) =>
        (MainForm)Activator.CreateInstance(typeof(MainForm), BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: new object[] { paths, showWarning, sessionPath }, culture: null)!;
    static void Expect(bool condition, string message) { if (!condition) throw new Exception(message); }
    static MethodInfo Method(string name) => typeof(MainForm).GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic)!;
    static object? Get(object o,string field) => o.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o);
    static IEnumerable<Control> Descendants(Control c) { foreach (Control child in c.Controls) { yield return child; foreach (var n in Descendants(child)) yield return n; } }
    static object[] Dump(Control c) => c.Controls.Cast<Control>().Select(child => (object)new { type = child.GetType().Name, bounds = child.Bounds.ToString(), visible = child.Visible, textLength = child.Text.Length, children = Dump(child) }).ToArray();
}
