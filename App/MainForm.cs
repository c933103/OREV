// Copyright (c) 2026 c933103 and contributors.
// SPDX-License-Identifier: LicenseRef-OREV-Community-1.1
// See LICENSE, including Section 10 and Schedule A.

using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text;


namespace ExportViewer;

public sealed class MainForm : Form
{
    const int PageSize = 25;
    readonly Color canvas = Color.FromArgb(245, 246, 249), ink = Color.FromArgb(30, 35, 48);
    readonly ToolStrip tools = new() { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(8), AutoSize = true };
    readonly ToolStripButton open = new("Open file…"), export = new("Export selected…"), all = new("Check all"), none = new("Uncheck all"), metadata = new("Metadata") { CheckOnClick = true }, cancelButton = new("Cancel") { Visible = false };
    readonly ListView rooms = new() { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, MultiSelect = true, FullRowSelect = true, HideSelection = false, BorderStyle = BorderStyle.None, ShowItemToolTips = true };
    readonly TextBox search = new() { Dock = DockStyle.Top, PlaceholderText = "Filter conversations by title…", BorderStyle = BorderStyle.FixedSingle };
    readonly SplitContainer layout = new() { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterWidth = 5 };
    readonly SplitContainer detail = new() { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel2, SplitterWidth = 5, Panel2Collapsed = true };
    readonly FlowLayoutPanel feed = new() { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(14), BackColor = Color.FromArgb(245, 246, 249) };
    readonly Label heading = new() { Dock = DockStyle.Top, Height = 62, Padding = new Padding(16, 10, 8, 4), AutoEllipsis = true };
    readonly Label pageLabel = new() { AutoSize = true, Margin = new Padding(16, 10, 16, 5) };
    readonly Button previous = new() { Text = "← Previous", AutoSize = true }, next = new() { Text = "Next →", AutoSize = true };
    readonly FlowLayoutPanel pager = new() { Dock = DockStyle.Bottom, Height = 43, Padding = new Padding(10, 3, 10, 3) };
    readonly TreeView metaTree = new() { Dock = DockStyle.Fill, HideSelection = false, BorderStyle = BorderStyle.None };
    readonly RichTextBox metaValue = new() { Dock = DockStyle.Bottom, Height = 240, ReadOnly = true, BorderStyle = BorderStyle.FixedSingle, WordWrap = false, DetectUrls = false };
    readonly Label metaTitle = new() { Dock = DockStyle.Top, Height = 44, Padding = new Padding(10), Text = "Conversation metadata", AutoEllipsis = true };
    readonly StatusStrip status = new();
    readonly ToolStripStatusLabel state = new("Choose an OpenRouter export to begin") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly ToolStripProgressBar progress = new() { Visible = false, Minimum = 0, Maximum = 100 };
    readonly HashSet<Room> checks = new();
    Archive? archive;
    Room? active;
    CancellationTokenSource? operation;
    bool busy, filtering;
    CancellationTokenSource? previewCancel;
    readonly List<(JNode Node, PictureBox Picture, Label Label, Archive Archive)> imageQueue = new();
    int page;
    readonly string? initialPath;
    public MainForm(string? path)
    {
        initialPath = path; Text = "OpenRouter Export Viewer";
        StartPosition = FormStartPosition.CenterScreen; Size = new(1320, 900); MinimumSize = new(920, 620);
        Font = new("Segoe UI", 10); ForeColor = ink; BackColor = canvas;
        tools.Items.AddRange(new ToolStripItem[] { open, new ToolStripSeparator(), all, none, export, new ToolStripSeparator(), metadata, cancelButton });
        status.Items.AddRange(new ToolStripItem[] { state, progress });
        rooms.Columns.Add("Conversation", 265); rooms.Columns.Add("Turns", 60);
        layout.Panel1.Controls.Add(rooms); layout.Panel1.Controls.Add(search);
        layout.Panel2.Controls.Add(detail);
        detail.Panel1.Controls.Add(feed); detail.Panel1.Controls.Add(heading); detail.Panel1.Controls.Add(pager);
        pager.Controls.AddRange(new Control[] { previous, pageLabel, next });
        detail.Panel2.Controls.Add(metaTree); detail.Panel2.Controls.Add(metaValue); detail.Panel2.Controls.Add(metaTitle);
        Controls.Add(layout); Controls.Add(tools); Controls.Add(status);
        tools.Dock = DockStyle.Top; status.Dock = DockStyle.Bottom;
        layout.SplitterDistance = 320;
        open.Click += async (_, _) => await PickFile();
        export.Click += async (_, _) => await ExportSelected();
        all.Click += (_, _) => SetChecks(true); none.Click += (_, _) => SetChecks(false);
        cancelButton.Click += (_, _) => operation?.Cancel();
        metadata.CheckedChanged += (_, _) => { detail.Panel2Collapsed = !metadata.Checked; if (metadata.Checked) detail.SplitterDistance = Math.Max(250, detail.Width - 330); };
        search.TextChanged += (_, _) => Filter();
        rooms.ItemChecked += (_, e) => { if (filtering || e.Item.Tag is not Room r) return; if (e.Item.Checked) checks.Add(r); else checks.Remove(r); UpdateExport(); };
        rooms.SelectedIndexChanged += (_, _) => { if (!busy && rooms.SelectedItems.Count > 0 && rooms.SelectedItems[0].Tag is Room r && active != r) { active = r; page = 0; RenderRoom(); } UpdateExport(); };
        rooms.KeyDown += (_, e) => { if (e.Control && e.KeyCode == Keys.A) { foreach (ListViewItem item in rooms.Items) item.Selected = true; e.SuppressKeyPress = true; } };
        previous.Click += (_, _) => { if (page > 0) { page--; RenderRoom(); } };
        next.Click += (_, _) => { if (active != null && (page + 1) * PageSize < active.Thread.Count) { page++; RenderRoom(); } };
        feed.SizeChanged += (_, _) => ResizeCards();
        heading.Click += (_, _) => { if (active != null) SetMetadata("Conversation metadata", active.Node); };
        metaTree.BeforeExpand += (_, e) => { if (e.Node != null) PopulateMetadata(e.Node); };
        metaTree.AfterSelect += (_, e) => { if (archive != null && e.Node?.Tag is JNode node) metaValue.Text = archive.Text(node, 65536); };
        Shown += async (_, _) => { if (initialPath != null) await LoadFile(initialPath); else await PickFile(); };
        FormClosing += (_, e) => { if (busy) { operation?.Cancel(); e.Cancel = true; state.Text = "Cancelling… close the window again when complete."; } };
        FormClosed += (_, _) => { previewCancel?.Cancel(); archive?.Dispose(); };
        AllowDrop = true;
        DragEnter += (_, e) => { if (!busy && e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
        DragDrop += async (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) await LoadFile(files[0]); };
        ShowWelcome(); UpdateExport();
    }
    void ShowWelcome()
    {
        ClearFeed(); heading.Text = "OpenRouter Export Viewer";
        var label = new Label { Text = "Open an OpenRouter JSON export\n\nBrowse conversations and export selected threads as individual JSON files.", AutoSize = true, MaximumSize = new(650, 0), Margin = new(25), Font = new(Font.FontFamily, 14) };
        var choose = new Button { Text = "Choose file…", AutoSize = true, Margin = new(25, 0, 0, 0) };
        choose.Click += async (_, _) => await PickFile(); feed.Controls.Add(label); feed.Controls.Add(choose);
    }
    async Task PickFile()
    {
        if (busy) return;
        using var picker = new OpenFileDialog { Filter = "OpenRouter export (*.json)|*.json|All files (*.*)|*.*", Title = "OpenRouter Export Viewer — choose an export", CheckFileExists = true };
        if (picker.ShowDialog(this) == DialogResult.OK) await LoadFile(picker.FileName);
    }
    void SetBusy(bool value, string? text)
    {
        busy = value; open.Enabled = all.Enabled = none.Enabled = search.Enabled = rooms.Enabled = !value;
        cancelButton.Visible = progress.Visible = value; previous.Enabled = !value && active != null && page > 0; next.Enabled = !value && active != null && (page + 1) * PageSize < active.Thread.Count;
        state.Text = text ?? ""; UpdateExport();
    }
    async Task LoadFile(string path)
    {
        if (busy) return;
        using var token = new CancellationTokenSource(); operation = token; progress.Value = 0;
        SetBusy(true, "Indexing file…");
        try
        {
            var reports = new Progress<int>(n => { progress.Value = n; state.Text = $"Indexing {System.IO.Path.GetFileName(path)} — {n}%"; });
            Archive loaded = await Task.Run(() => Archive.Open(path, token.Token, reports));
            previewCancel?.Cancel(); archive?.Dispose(); archive = loaded; checks.Clear(); active = null; page = 0; search.Clear(); Filter();
            active = archive.Rooms.FirstOrDefault(); RenderRoom();
            if (rooms.Items.Count > 0) rooms.Items[0].Selected = true;
            state.Text = $"{archive.Rooms.Count:N0} conversations • {archive.Size / 1048576.0:N1} MiB • {System.IO.Path.GetFileName(path)}";
        }
        catch (OperationCanceledException) { state.Text = "Loading cancelled. The previous file remains open."; }
        catch (Exception e) { Error("Could not open export", e); }
        finally { operation = null; SetBusy(false, state.Text); }
    }
    void Filter()
    {
        filtering = true; rooms.BeginUpdate(); rooms.Items.Clear();
        foreach (var r in archive?.Rooms ?? new())
        {
            if (!r.Title.Contains(search.Text, StringComparison.OrdinalIgnoreCase)) continue;
            var item = new ListViewItem(r.Title) { Tag = r, Checked = checks.Contains(r), ToolTipText = r.Title };
            item.SubItems.Add(r.Thread.Count.ToString()); rooms.Items.Add(item);
        }
        rooms.EndUpdate(); filtering = false; UpdateExport();
    }
    void SetChecks(bool value)
    {
        foreach (ListViewItem item in rooms.Items) item.Checked = value;
    }
    void UpdateExport()
    {
        int count = checks.Count > 0 ? checks.Count : rooms.SelectedItems.Count;
        export.Text = checks.Count > 0 ? $"Export checked ({count})…" : $"Export selected ({count})…";
        export.Enabled = !busy && archive != null && count > 0;
    }
    async Task ExportSelected()
    {
        if (archive == null || busy) return;
        var chosen = checks.Count > 0 ? checks.OrderBy(r => r.Number).ToArray() : rooms.SelectedItems.Cast<ListViewItem>().Select(i => (Room)i.Tag!).ToArray();
        if (chosen.Length == 0) return;
        using var folder = new FolderBrowserDialog { Description = "Choose a folder for the individual conversation JSON files", UseDescriptionForTitle = true };
        if (folder.ShowDialog(this) != DialogResult.OK) return;
        string destination = folder.SelectedPath;
        using var token = new CancellationTokenSource(); operation = token; SetBusy(true, "Exporting…"); progress.Value = 0;
        int completed = 0;
        try
        {
            var reports = new Progress<int>(n => { progress.Value = n * 100 / chosen.Length; state.Text = $"Exported {n} of {chosen.Length} conversations"; });
            var current = archive;
            await Task.Run(() =>
            {
                foreach (var room in chosen)
                {
                    token.Token.ThrowIfCancellationRequested();
                    string stem = System.IO.Path.GetFileNameWithoutExtension(Archive.FileName(room));
                    string path = System.IO.Path.Combine(destination, stem + ".json"); int suffix = 2;
                    while (File.Exists(path)) path = System.IO.Path.Combine(destination, $"{stem} ({suffix++}).json");
                    current.Export(room, path, token.Token); Interlocked.Increment(ref completed); ((IProgress<int>)reports).Report(completed);
                }
            });
            state.Text = $"Exported {completed} individual conversation files to {destination}";
            MessageBox.Show(this, $"Exported {completed} conversation(s).\n\nEach file contains one orpg.3.0 room, including its original metadata and attachments.\n\n{destination}", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException) { state.Text = $"Export cancelled. {completed} completed files retained."; }
        catch (Exception e) { Error($"Export stopped after {completed} completed files", e); }
        finally { operation = null; SetBusy(false, state.Text); }
    }
    void ClearFeed()
    {
        previewCancel?.Cancel(); imageQueue.Clear();
        // Controls.Clear does not dispose image handles or child controls.
        while (feed.Controls.Count > 0) { Control c = feed.Controls[0]; feed.Controls.RemoveAt(0); c.Dispose(); }
    }
    void RenderRoom()
    {
        ClearFeed();
        if (archive == null || active == null) { ShowWelcome(); previous.Enabled = next.Enabled = false; pageLabel.Text = ""; return; }
        heading.Text = active.Title + $"\n{active.Thread.Count:N0} messages • {active.Node.Length / 1048576.0:N2} MiB" + (active.BrokenLinks > 0 ? $" • {active.BrokenLinks} missing/cyclic parent links (all messages shown)" : "");
        heading.Font = new(Font.FontFamily, 11, FontStyle.Bold);
        pageLabel.Text = $"Page {page + 1} / {Math.Max(1, (active.Thread.Count + PageSize - 1) / PageSize)}";
        previous.Enabled = page > 0; next.Enabled = (page + 1) * PageSize < active.Thread.Count;
        SetMetadata("Conversation metadata", active.Node);
        feed.SuspendLayout();
        foreach (var entry in active.Thread.Skip(page * PageSize).Take(PageSize)) AddMessage(entry);
        if (active.Thread.Count == 0) feed.Controls.Add(new Label { AutoSize = true, Text = "This conversation has no messages. Its metadata and attachments are still exportable." });
        feed.ResumeLayout(); ResizeCards(); feed.AutoScrollPosition = Point.Empty; StartImagePreviews();
    }
    void ResizeCards()
    {
        int width = Math.Max(280, feed.ClientSize.Width - feed.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 8);
        foreach (Control c in feed.Controls)
        {
            if (c is MessageCard card)
            {
                card.Width = Math.Max(240, width - card.Margin.Horizontal);
                card.PerformLayout();
            }
        }
    }
    MessageCard Card(int depth) => new()
    {
        BackColor = Color.White, Padding = new Padding(12),
        Margin = new Padding(Math.Min(depth, 4) * 16, 0, 0, 14),
        BorderStyle = BorderStyle.FixedSingle, Width = Math.Max(280, feed.ClientSize.Width - 60)
    };
    void AddMessage(ThreadEntry entry)
    {
        if (archive == null || active == null) return;
        var a = archive; var room = active; var m = entry.Message;
        var card = Card(entry.Depth);
        string role = m.Role == "user" ? "You" : m.Model.Length > 0 ? m.Model : m.Role;
        var title = new Label { Text = role + (entry.Depth > 0 ? $"   ↳ reply · depth {entry.Depth}" : ""), AutoSize = true, Font = new(Font.FontFamily, 10, FontStyle.Bold), ForeColor = m.Role == "user" ? Color.FromArgb(70, 80, 175) : ink, Margin = new(0, 0, 0, 6) };
        title.Click += (_, _) => SetMetadata("Message metadata", m.Node); card.Controls.Add(title);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = true, Margin = new(0) };
        buttons.Controls.Add(new Label { Text = m.Date.Replace('T', ' '), AutoSize = true, ForeColor = Color.DimGray, Margin = new(0, 7, 10, 0) });
        var inspect = Link("Metadata", () => { metadata.Checked = true; SetMetadata("Message metadata", m.Node); }); buttons.Controls.Add(inspect);
        if (m.Parent.Length > 0) buttons.Controls.Add(Link("Go to parent", () => Jump(m.Parent)));
        card.Controls.Add(buttons);
        int itemNumber = 0;
        foreach (var item in a.Items(room, m))
        {
            itemNumber++; var data = item["data"] ?? item; string type = a.Text(data["type"], 160);
            var texts = MessageContent.ReadableText(a, data).ToArray();
            if (type == "reasoning")
            {
                AddExpandable(card, "Reasoning", () => string.Join("\n\n", texts.Length > 0 ? texts : new[] { "No readable reasoning text. Encrypted/signature fields remain in metadata and exports." }));
            }
            else if (type == "message")
            {
                foreach (string text in texts) card.Controls.Add(TextBox(text));
            }
            else
            {
                string label = string.IsNullOrEmpty(type) ? "Item" : type;
                var b = Link(label + " · details", () => { metadata.Checked = true; SetMetadata(label, item); }); card.Controls.Add(b);
                if (type is not ("image_generation_call" or "openrouter:image_generation"))
                    AddExpandable(card, "Show tool / item content", () => MessageContent.ToolDetails(a, data));
            }
            foreach (var attachment in a.Attachments(data)) AddAttachment(card, attachment.Node, attachment.Mime, itemNumber);
            // Remote references are links only; opening a file never makes a network request.
            foreach (var field in a.Descendants(data).Where(n => n.Fields != null))
            {
                foreach (var key in new[] { "imageUrl", "image_url", "url" })
                {
                    var value = field[key]; if (value?.Kind != '"') continue;
                    string url = a.Text(value, 4096);
                    if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                        card.Controls.Add(Link("Open external link", () => OpenLink(url)));
                }
            }
        }
        if (itemNumber == 0) card.Controls.Add(TextBox("No item content is attached to this message. See metadata for its saved fields."));
        feed.Controls.Add(card); card.PerformLayout();
    }
    RichTextBox TextBox(string text)
    {
        var box = new RichTextBox { Multiline = true, AutoSize = false, ReadOnly = true, DetectUrls = true, BorderStyle = BorderStyle.None, BackColor = Color.White, ForeColor = ink, Width = 600, Height = Math.Clamp((text.Count(c => c == '\n') + 2 + text.Length / 85) * 22, 55, 480), Font = Font, WordWrap = true, ScrollBars = RichTextBoxScrollBars.Vertical, Margin = new(0, 6, 0, 8) };
        NativeMarkdown.Render(box, text, Font);
        box.LinkClicked += (_, e) => { if (e.LinkText != null) OpenLink(e.LinkText); };
        box.Select(0, 0); return box;
    }
    void AddExpandable(MessageCard card, string title, Func<string> content)
    {
        var button = new Button { Text = "▾ " + title, AutoSize = true, FlatStyle = FlatStyle.Flat, Margin = new(0, 6, 0, 6) };
        var body = TextBox(content());
        card.Controls.Add(button); card.Controls.Add(body);
        button.Click += (_, _) =>
        {
            body.Visible = !body.Visible;
            button.Text = (body.Visible ? "▾ " : "▸ ") + title;
            card.PerformLayout(); ResizeCards();
        };
    }
    static LinkLabel Link(string text, Action action)
    {
        var link = new LinkLabel { Text = text, AutoSize = true, Margin = new(0, 7, 14, 5) };
        link.LinkClicked += (_, _) => action(); return link;
    }
    void Jump(string id)
    {
        if (active == null) return;
        int index = active.Thread.FindIndex(e => e.Message.Id == id);
        if (index < 0) { state.Text = "Parent message is absent from this export."; return; }
        page = index / PageSize; RenderRoom();
        if (index % PageSize < feed.Controls.Count) feed.ScrollControlIntoView(feed.Controls[index % PageSize]);
    }
    void OpenLink(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception e) { Error("Could not open link", e); }
    }
    void SetMetadata(string title, JNode node)
    {
        metaTitle.Text = title; metaTree.Nodes.Clear(); metaValue.Clear();
        var root = MetadataNode(title, node); metaTree.Nodes.Add(root); root.Expand();
    }
    TreeNode MetadataNode(string key, JNode node)
    {
        string value = node.Fields != null ? $"{{{node.Fields.Count}}}" : node.Values != null ? $"[{node.Values.Count}]" : node.Kind == '"' && node.Length > 256 ? $"string · {node.Length:N0} bytes" : archive?.Text(node, 150) ?? "";
        var tree = new TreeNode(key + " : " + value.Replace('\n', ' ').Replace('\r', ' ')) { Tag = node };
        if (node.Fields?.Count > 0 || node.Values?.Count > 0) tree.Nodes.Add("Loading…");
        return tree;
    }
    sealed record MetadataPage(JNode Node, int Offset);
    void PopulateMetadata(TreeNode tree)
    {
        if (tree.Nodes.Count != 1 || tree.Nodes[0].Tag != null) return;
        var page = tree.Tag switch { JNode node => new MetadataPage(node, 0), MetadataPage continuation => continuation, _ => null };
        if (page == null) return;
        tree.Nodes.Clear();
        var entries = page.Node.Fields?.Select(p => (p.Key, p.Value)).ToArray()
            ?? page.Node.Values?.Select((n, i) => (i.ToString(), n)).ToArray()
            ?? Array.Empty<(string, JNode)>();
        foreach (var (key, value) in entries.Skip(page.Offset).Take(100)) tree.Nodes.Add(MetadataNode(key, value));
        if (entries.Length > page.Offset + 100)
        {
            var more = new TreeNode($"More fields ({page.Offset + 101}–{Math.Min(entries.Length, page.Offset + 200)} of {entries.Length})") { Tag = new MetadataPage(page.Node, page.Offset + 100) };
            more.Nodes.Add("Loading…"); tree.Nodes.Add(more);
        }
    }
    void AddAttachment(MessageCard card, JNode node, string mime, int number)
    {
        var a = archive!;
        string ext = mime.ToLowerInvariant() switch { "image/png" => ".png", "image/jpeg" => ".jpg", "image/gif" => ".gif", "image/webp" => ".webp", "image/avif" => ".avif", "application/pdf" => ".pdf", "audio/mpeg" => ".mp3", "audio/wav" => ".wav", "video/mp4" => ".mp4", _ => ".bin" };
        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Margin = new(0, 8, 0, 4) };
        row.Controls.Add(new Label { Text = $"{mime} • ~{node.Length * 0.75 / 1048576:N2} MiB", AutoSize = true, Margin = new(0, 8, 10, 0) });
        var save = new Button { Text = "Save attachment…", AutoSize = true };
        save.Click += async (_, _) =>
        {
            if (busy) return;
            using var dialog = new SaveFileDialog { FileName = $"attachment-{number}{ext}", OverwritePrompt = true };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            if (string.Equals(System.IO.Path.GetFullPath(dialog.FileName), System.IO.Path.GetFullPath(a.Path), StringComparison.OrdinalIgnoreCase)) { MessageBox.Show(this, "Choose a different path from the source export.", Text); return; }
            string path = dialog.FileName, temp = path + "." + Guid.NewGuid().ToString("N") + ".partial";
            using var token = new CancellationTokenSource(); operation = token; SetBusy(true, "Saving attachment…"); progress.Style = ProgressBarStyle.Marquee;
            try
            {
                await Task.Run(() => { using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write)) a.DecodeAttachment(node, output, token.Token); token.Token.ThrowIfCancellationRequested(); File.Move(temp, path, true); });
                state.Text = "Saved attachment to " + path;
            }
            catch (OperationCanceledException) { state.Text = "Attachment save cancelled."; }
            catch (Exception e) { Error("Could not save attachment", e); }
            finally { if (File.Exists(temp)) File.Delete(temp); progress.Style = ProgressBarStyle.Blocks; operation = null; SetBusy(false, state.Text); }
        };
        row.Controls.Add(save);
        if (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && mime != "image/svg+xml")
        {
            var picture = new PictureBox { Width = 600, Height = 280, SizeMode = PictureBoxSizeMode.Zoom, Margin = new(0, 8, 0, 8), BackColor = Color.FromArgb(248, 249, 251) };
            var label = new Label { Text = "Loading embedded image…", AutoSize = true, ForeColor = Color.DimGray };
            card.Controls.Add(picture); card.Controls.Add(label);
            picture.Disposed += (_, _) => { var image = picture.Image; picture.Image = null; image?.Dispose(); };
            imageQueue.Add((node, picture, label, a));
        }
        card.Controls.Add(row);
    }
    async void StartImagePreviews()
    {
        previewCancel?.Cancel();
        var source = new CancellationTokenSource(); previewCancel = source; var token = source.Token;
        var queue = imageQueue.ToArray();
        try
        {
            foreach (var item in queue)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    byte[] png = await Task.Run(() => ImagePreview.Decode(item.Archive, item.Node, token), token);
                    token.ThrowIfCancellationRequested();
                    if (item.Picture.IsDisposed) continue;
                    using var bytes = new MemoryStream(png); using var decoded = Image.FromStream(bytes);
                    item.Picture.Image = new Bitmap(decoded);
                    item.Picture.Height = Math.Min(500, Math.Max(100, decoded.Height * Math.Max(240, item.Picture.Width) / Math.Max(1, decoded.Width)));
                    item.Label.Text = $"Embedded image · {decoded.Width} × {decoded.Height} preview · original retained for export";
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { if (!token.IsCancellationRequested && !item.Label.IsDisposed) { item.Picture.Visible = false; item.Label.Text = "Image preview unavailable: " + e.Message; } }
            }
        }
        catch (OperationCanceledException) { }
        finally { if (previewCancel == source) previewCancel = null; source.Dispose(); }
    }
    void Error(string title, Exception e) { state.Text = title + ": " + e.Message; MessageBox.Show(this, e.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error); }
}
