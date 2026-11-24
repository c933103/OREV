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
    readonly ToolStripButton open = new("Add exports…"), export = new("Export selected…"), all = new("Check all"), none = new("Uncheck all"), metadata = new("Metadata") { CheckOnClick = true }, cancelButton = new("Cancel") { Visible = false };
    readonly ToolStripDropDownButton sessionMenu = new("Session");
    readonly ListView rooms = new() { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, MultiSelect = true, FullRowSelect = true, HideSelection = false, BorderStyle = BorderStyle.None, ShowItemToolTips = true, ShowGroups = true };
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
    sealed record LoadedExport(Archive Archive, ListViewGroup Folder, int Order);
    sealed record Conversation(LoadedExport Source, Room Room);

    readonly List<LoadedExport> sources = new();
    readonly HashSet<Conversation> checks = new();
    Conversation? current;
    Room? active => current?.Room;
    CancellationTokenSource? operation;
    bool busy, filtering;
    CancellationTokenSource? previewCancel;
    readonly List<(JNode Node, PictureBox Picture, Label Label, Archive Archive)> imageQueue = new();
    readonly Func<IWin32Window, TaskDialogPage, TaskDialogButton> showRefreshWarning;
    readonly string sessionPath;
    readonly System.Windows.Forms.Timer autosave = new() { Interval = 600 };
    readonly List<SavedExport> unavailable = new();
    SavedPosition? pendingReadingPosition;
    bool sessionReady;
    bool sessionDirty;
    int page;
    public MainForm(string[] initialPaths) : this(initialPaths,
        (owner, dialog) => TaskDialog.ShowDialog(owner, dialog, TaskDialogStartupLocation.CenterOwner),
        ViewerSession.DefaultPath) { }

    MainForm(string[] initialPaths, Func<IWin32Window, TaskDialogPage, TaskDialogButton> showRefreshWarning, string sessionPath)
    {
        this.showRefreshWarning = showRefreshWarning;
        this.sessionPath = sessionPath;
        Text = "OpenRouter Export Viewer";
        StartPosition = FormStartPosition.CenterScreen; Size = new(1320, 900); MinimumSize = new(920, 620);
        Font = new("Segoe UI", 10); ForeColor = ink; BackColor = canvas;
        tools.Items.AddRange(new ToolStripItem[] { open, sessionMenu, new ToolStripSeparator(), all, none, export, new ToolStripSeparator(), metadata, cancelButton });
        sessionMenu.DropDownItems.Add("Start new session…", null, (_, _) => StartNewSession());
        autosave.Tick += (_, _) => { if (!busy) SaveSession(); };
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
        open.Click += async (_, _) => await PickFiles();
        export.Click += async (_, _) => await ExportSelected();
        all.Click += (_, _) => SetChecks(true); none.Click += (_, _) => SetChecks(false);
        cancelButton.Click += (_, _) => operation?.Cancel();
        metadata.CheckedChanged += (_, _) =>
        {
            detail.Panel2Collapsed = !metadata.Checked;
            if (metadata.Checked) detail.SplitterDistance = Math.Max(250, detail.Width - 330);
            QueueSessionSave();
        };
        search.TextChanged += (_, _) => { Filter(); QueueSessionSave(); };
        rooms.ItemChecked += (_, e) =>
        {
            if (filtering || e.Item.Tag is not Conversation conversation) return;
            if (e.Item.Checked) checks.Add(conversation); else checks.Remove(conversation);
            UpdateExport();
            QueueSessionSave();
        };
        rooms.SelectedIndexChanged += (_, _) =>
        {
            if (!busy && !filtering && rooms.SelectedItems.Count > 0 &&
                rooms.SelectedItems[0].Tag is Conversation conversation && current != conversation)
            {
                current = conversation;
                pendingReadingPosition = null;
                page = 0;
                RenderRoom();
            }
            UpdateExport();
            if (!busy && !filtering) QueueSessionSave();
        };
        rooms.KeyDown += (_, e) => { if (e.Control && e.KeyCode == Keys.A) { foreach (ListViewItem item in rooms.Items) item.Selected = true; e.SuppressKeyPress = true; } };
        previous.Click += (_, _) => { if (page > 0) { pendingReadingPosition = null; page--; RenderRoom(); } };
        next.Click += (_, _) => { if (active != null && (page + 1) * PageSize < active.Thread.Count) { pendingReadingPosition = null; page++; RenderRoom(); } };
        feed.SizeChanged += (_, _) => ResizeCards();
        heading.Click += (_, _) => { if (active != null) SetMetadata("Conversation metadata", active.Node); };
        metaTree.BeforeExpand += (_, e) => { if (e.Node != null) PopulateMetadata(e.Node); };
        metaTree.AfterSelect += (_, e) =>
        {
            if (current is { } conversation && e.Node?.Tag is JNode node)
                metaValue.Text = conversation.Source.Archive.Text(node, 65536);
        };
        Shown += async (_, _) => await RestoreSession(initialPaths);
        FormClosing += (_, e) =>
        {
            if (busy) { operation?.Cancel(); e.Cancel = true; state.Text = "Cancelling… close the window again when complete."; }
            else SaveSession();
        };
        FormClosed += (_, _) =>
        {
            autosave.Dispose();
            previewCancel?.Cancel();
            foreach (var source in sources) source.Archive.Dispose();
        };
        AllowDrop = true;
        DragEnter += (_, e) => { if (!busy && e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
        DragDrop += async (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] files) await LoadFiles(files); };
        ShowWelcome(); UpdateExport();
    }
    async Task RestoreSession(string[] initialPaths)
    {
        ViewerSession saved;
        try { saved = ViewerSession.Read(sessionPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            saved = ViewerSession.Empty;
            MessageBox.Show(this, e.Message, "Could not restore the saved session", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        foreach (var source in saved.Exports.DistinctBy(source => source.Path))
            unavailable.Add(saved.Position?.Path == source.Path ? source with { Position = saved.Position } : source);
        if (unavailable.Any(source => source.Path == saved.Position?.Path)) pendingReadingPosition = saved.Position;
        search.Text = saved.Filter;
        metadata.Checked = saved.MetadataOpen;
        if (unavailable.Count > 0)
            await LoadFilesCore(unavailable.Select(source => source.Path).ToArray(), saved.Position);
        sessionReady = true;
        if (saved.Exports.Length > 0) QueueSessionSave();
        if (initialPaths.Length > 0) await LoadFiles(initialPaths);
        else if (saved.Exports.Length == 0) await PickFiles();
    }

    void QueueSessionSave()
    {
        if (!sessionReady) return;
        sessionDirty = true;
        autosave.Stop();
        autosave.Start();
    }

    void SaveSession()
    {
        autosave.Stop();
        if (!sessionDirty) return;
        try
        {
            var checkedRooms = checks.ToLookup(conversation => conversation.Source, conversation => conversation.Room.Number);
            var selectedRooms = rooms.SelectedItems.Cast<ListViewItem>().Select(item => (Conversation)item.Tag!)
                .ToLookup(conversation => conversation.Source, conversation => conversation.Room.Number);
            var exports = sources.Select(source => new SavedExport(source.Archive.Path, source.Archive.SnapshotDigest!,
                checkedRooms[source].Order().ToArray(), selectedRooms[source].Order().ToArray())).Concat(unavailable).ToArray();
            var position = pendingReadingPosition ??
                (current == null ? null : new SavedPosition(current.Source.Archive.Path, current.Room.Number, page));
            (ViewerSession.Empty with { Exports = exports, Position = position, Filter = search.Text, MetadataOpen = metadata.Checked })
                .Write(sessionPath);
            sessionDirty = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            state.Text = "Could not autosave this session: " + e.Message;
        }
    }

    void StartNewSession()
    {
        if (busy || MessageBox.Show(this, "Close the open export folders and clear the saved session?",
            "Start new session", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK)
            return;

        try { ViewerSession.Empty.Write(sessionPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, e.Message, "Could not clear the saved session", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        sessionReady = false;
        autosave.Stop();
        sessionDirty = false;
        current = null;
        pendingReadingPosition = null;
        page = 0;
        checks.Clear();
        unavailable.Clear();
        ClearFeed();
        metaTree.Nodes.Clear();
        metaValue.Clear();
        foreach (var source in sources) source.Archive.Dispose();
        sources.Clear();
        rooms.Items.Clear();
        rooms.Groups.Clear();
        search.Clear();
        metadata.Checked = false;
        RenderRoom();
        state.Text = "Choose an OpenRouter export to begin";
        UpdateExport();
        sessionReady = true;
    }
    void ShowWelcome()
    {
        ClearFeed(); heading.Text = "OpenRouter Export Viewer";
        var label = new Label { Text = "Open OpenRouter JSON exports\n\nBrowse conversations by source file and export selected threads as individual JSON files.", AutoSize = true, MaximumSize = new(650, 0), Margin = new(25), Font = new(Font.FontFamily, 14) };
        var choose = new Button { Text = "Choose exports…", AutoSize = true, Margin = new(25, 0, 0, 0) };
        choose.Click += async (_, _) => await PickFiles(); feed.Controls.Add(label); feed.Controls.Add(choose);
    }
    async Task PickFiles()
    {
        if (busy) return;
        using var picker = new OpenFileDialog { Filter = "OpenRouter export (*.json)|*.json|All files (*.*)|*.*", Title = "OpenRouter Export Viewer — choose exports", CheckFileExists = true, Multiselect = true };
        if (picker.ShowDialog(this) == DialogResult.OK) await LoadFiles(picker.FileNames);
    }
    void SetBusy(bool value, string? text)
    {
        busy = value; open.Enabled = sessionMenu.Enabled = all.Enabled = none.Enabled = search.Enabled = rooms.Enabled = !value;
        cancelButton.Visible = progress.Visible = value; previous.Enabled = !value && active != null && page > 0; next.Enabled = !value && active != null && (page + 1) * PageSize < active.Thread.Count;
        state.Text = text ?? ""; UpdateExport();
    }
    Task LoadFiles(string[] paths) => LoadFilesCore(paths);

    async Task LoadFilesCore(string[] paths, SavedPosition? savedPosition = null)
    {
        if (busy || paths.Length == 0) return;
        using var token = new CancellationTokenSource();
        operation = token;
        progress.Value = 0;
        SetBusy(true, "Indexing exports…");
        var failures = new List<string>();
        int duplicates = 0;
        int refreshed = 0;
        int kept = 0;
        var restoredSelection = new HashSet<Conversation>();
        bool restoreReading = false;
        ListViewGroup? preferredFolder = null;
        bool cancelled = false;
        try
        {
            for (int i = 0; i < paths.Length; i++)
            {
                token.Token.ThrowIfCancellationRequested();
                int index = i;
                string input = paths[index];
                try
                {
                    string path = Path.GetFullPath(input);
                    string name = Path.GetFileName(path);
                    var reports = new Progress<int>(n =>
                    {
                        if (operation != token) return;
                        progress.Value = (int)((index * 100L + n) / paths.Length);
                        state.Text = $"Loading {name} ({index + 1:N0}/{paths.Length:N0}) — {n}%";
                    });
                    var (existing, remembered, loaded) = await Task.Run(() =>
                    {
                        var match = sources.Find(source => source.Archive.Path == path);
                        // Windows storage can be case-sensitive, so casing alone cannot prove a duplicate.
                        match ??= sources.Find(source =>
                            string.Equals(source.Archive.Path, path, StringComparison.OrdinalIgnoreCase) &&
                            WindowsFileIdentity.SameFile(source.Archive.Path, path));
                        var saved = unavailable.Find(source => source.Path == path);
                        saved ??= unavailable.Find(source =>
                            string.Equals(source.Path, path, StringComparison.OrdinalIgnoreCase) &&
                            WindowsFileIdentity.SameFile(source.Path, path));
                        return (match, saved, Archive.OpenSnapshot(path, match?.Archive.SnapshotDigest, token.Token, reports));
                    });
                    if (loaded == null) { duplicates++; continue; }
                    if (existing != null)
                    {
                        bool approved = false;
                        try
                        {
                            int checkedCount = checks.Count(conversation => conversation.Source == existing);
                            bool confirmed = ConfirmRefresh(loaded.Path, checkedCount);
                            token.Token.ThrowIfCancellationRequested();
                            approved = confirmed;
                        }
                        finally { if (!approved) loaded.Dispose(); }
                        if (!approved) { kept++; continue; }

                        sources[existing.Order] = existing with { Archive = loaded };
                        checks.RemoveWhere(conversation => conversation.Source == existing);
                        if (current?.Source == existing)
                        {
                            preferredFolder = existing.Folder;
                            ClearFeed();
                            metaTree.Nodes.Clear();
                            metaValue.Clear();
                            current = null;
                            page = 0;
                        }
                        existing.Archive.Dispose();
                        refreshed++;
                        continue;
                    }
                    bool matchesSession = remembered != null && remembered.Digest == loaded.SnapshotDigest;
                    if (remembered != null && !matchesSession)
                    {
                        bool approved = false;
                        try
                        {
                            bool confirmed = ConfirmRefresh(loaded.Path, remembered.Checked.Length, restoring: true);
                            token.Token.ThrowIfCancellationRequested();
                            approved = confirmed;
                        }
                        finally { if (!approved) loaded.Dispose(); }
                        if (!approved) { kept++; continue; }
                    }
                    var folder = new ListViewGroup(name) { CollapsedState = ListViewGroupCollapsedState.Expanded };
                    var source = new LoadedExport(loaded, folder, sources.Count);
                    sources.Add(source);
                    rooms.Groups.Add(folder);
                    if (remembered != null)
                    {
                        unavailable.Remove(remembered);
                        if (pendingReadingPosition?.Path == remembered.Path) pendingReadingPosition = null;
                        if (matchesSession)
                        {
                            foreach (var number in remembered.Checked.Where(number => (uint)number < (uint)loaded.Rooms.Count))
                                checks.Add(new(source, loaded.Rooms[number]));
                            foreach (var number in remembered.Selected.Where(number => (uint)number < (uint)loaded.Rooms.Count))
                                restoredSelection.Add(new(source, loaded.Rooms[number]));
                            if (remembered.Position is { } position && position.Path == remembered.Path &&
                                (uint)position.Room < (uint)loaded.Rooms.Count &&
                                (!restoreReading || position == savedPosition))
                            {
                                current = new(source, loaded.Rooms[position.Room]);
                                page = Math.Clamp(position.Page, 0, Math.Max(0, (current.Room.Thread.Count - 1) / PageSize));
                                restoreReading = true;
                            }
                        }
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or ArgumentException or NotSupportedException or System.Text.Json.JsonException or System.Security.SecurityException)
                {
                    failures.Add($"{input}\n{e.Message}");
                }
            }
        }
        catch (OperationCanceledException) { cancelled = true; }
        finally
        {
            foreach (var group in sources.GroupBy(source => Path.GetFileName(source.Archive.Path), StringComparer.OrdinalIgnoreCase))
            {
                int number = 0;
                bool sharedName = group.Count() > 1;
                foreach (var source in group)
                {
                    string header = sharedName
                        ? $"[{++number}] {Path.GetFileName(source.Archive.Path)} — {Path.GetDirectoryName(source.Archive.Path)}"
                        : Path.GetFileName(source.Archive.Path);
                    if (source.Folder.Header != header) source.Folder.Header = header;
                }
            }
            Filter(restoredSelection);
            if (current == null)
            {
                var items = rooms.Items.Cast<ListViewItem>();
                var item = items.FirstOrDefault(row => row.Group == preferredFolder)
                    ?? rooms.SelectedItems.Cast<ListViewItem>().FirstOrDefault() ?? items.FirstOrDefault();
                current = item?.Tag as Conversation;
                if (item != null) item.Selected = true;
                RenderRoom();
            }
            else if (restoreReading) RenderRoom();
            string summary = $"{sources.Sum(s => (long)s.Archive.Rooms.Count):N0} conversations in {sources.Count:N0} exports";
            if (duplicates > 0) summary += $" • {duplicates:N0} unchanged";
            if (refreshed > 0) summary += $" • {refreshed:N0} refreshed (checks reset in those folders)";
            if (kept > 0) summary += $" • {kept:N0} updates skipped";
            if (unavailable.Count > 0) summary += $" • {unavailable.Count:N0} saved exports pending";
            if (failures.Count > 0) summary += $" • {failures.Count:N0} failed";
            if (cancelled) summary += " • Loading cancelled";
            operation = null;
            SetBusy(false, summary);
            QueueSessionSave();
        }
        if (failures.Count > 0)
        {
            string details = string.Join("\n\n", failures.Take(5));
            if (failures.Count > 5) details += $"\n\nAnd {failures.Count - 5:N0} other files.";
            string recovery = sessionReady ? "Any open folders are still available."
                : "Unavailable exports stay in the saved session. Use Add exports to retry.";
            MessageBox.Show(this, details + "\n\n" + recovery,
                "Could not open some exports", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
    bool ConfirmRefresh(string path, int checkedCount, bool restoring = false)
    {
        string message = restoring
            ? $"This file changed since the saved session. Open the updated file?\n\n{path}"
            : $"Refresh this folder with the updated file?\n\n{path}";
        if (checkedCount == 1)
            message += "\n\nRefreshing clears 1 checked conversation in this folder.";
        else if (checkedCount > 1)
            message += $"\n\nRefreshing clears {checkedCount:N0} checked conversations in this folder.";

        var keep = new TaskDialogButton(restoring ? "Skip file" : "Keep current version");
        var refresh = new TaskDialogButton(restoring ? "Open updated file" : "Refresh folder");
        var dialog = new TaskDialogPage
        {
            Caption = "Confirm export refresh",
            Heading = "This export has changed",
            Text = message,
            Icon = TaskDialogIcon.Warning,
            AllowCancel = true,
            Buttons = { keep, refresh },
            DefaultButton = keep
        };
        return showRefreshWarning(this, dialog) == refresh;
    }

    void Filter(IEnumerable<Conversation>? restoredSelection = null)
    {
        var selected = rooms.SelectedItems.Cast<ListViewItem>().Select(item => (Conversation)item.Tag!).ToHashSet();
        if (restoredSelection != null) selected.UnionWith(restoredSelection);
        filtering = true;
        rooms.BeginUpdate();
        try
        {
            rooms.Items.Clear();
            foreach (var source in sources)
            foreach (var room in source.Archive.Rooms)
            {
                if (!room.Title.Contains(search.Text, StringComparison.OrdinalIgnoreCase)) continue;
                var conversation = new Conversation(source, room);
                var item = new ListViewItem(room.Title, source.Folder)
                {
                    Tag = conversation,
                    Checked = checks.Contains(conversation),
                    ToolTipText = $"{room.Title}\n{source.Archive.Path}"
                };
                item.SubItems.Add(room.Thread.Count.ToString());
                rooms.Items.Add(item);
                item.Selected = selected.Contains(conversation);
            }
        }
        finally { rooms.EndUpdate(); filtering = false; }
        UpdateExport();
    }
    void SetChecks(bool value)
    {
        foreach (ListViewItem item in rooms.Items) item.Checked = value;
    }
    void UpdateExport()
    {
        int count = checks.Count > 0 ? checks.Count : rooms.SelectedItems.Count;
        export.Text = checks.Count > 0 ? $"Export checked ({count})…" : $"Export selected ({count})…";
        export.Enabled = !busy && count > 0;
    }
    Conversation[] ChosenConversations() => checks.Count > 0
        ? checks.OrderBy(c => c.Source.Order).ThenBy(c => c.Room.Number).ToArray()
        : rooms.SelectedItems.Cast<ListViewItem>().Select(i => (Conversation)i.Tag!).ToArray();

    static void ExportConversation(Conversation conversation, string destination, CancellationToken token)
    {
        string stem = Path.GetFileNameWithoutExtension(Archive.FileName(conversation.Room));
        string path = Path.Combine(destination, stem + ".json");
        int suffix = 2;
        while (File.Exists(path))
        {
            token.ThrowIfCancellationRequested();
            path = Path.Combine(destination, $"{stem} ({suffix++}).json");
        }
        conversation.Source.Archive.Export(conversation.Room, path, token);
    }

    async Task ExportSelected()
    {
        if (busy) return;
        var chosen = ChosenConversations();
        if (chosen.Length == 0) return;
        using var folder = new FolderBrowserDialog { Description = "Choose a folder for the individual conversation JSON files", UseDescriptionForTitle = true };
        if (folder.ShowDialog(this) != DialogResult.OK) return;
        string destination = folder.SelectedPath;
        using var token = new CancellationTokenSource(); operation = token; SetBusy(true, "Exporting…"); progress.Value = 0;
        int completed = 0;
        try
        {
            var reports = new Progress<int>(n => { progress.Value = n * 100 / chosen.Length; state.Text = $"Exported {n} of {chosen.Length} conversations"; });
            await Task.Run(() =>
            {
                foreach (var conversation in chosen)
                {
                    token.Token.ThrowIfCancellationRequested();
                    ExportConversation(conversation, destination, token.Token);
                    Interlocked.Increment(ref completed);
                    ((IProgress<int>)reports).Report(completed);
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
        QueueSessionSave();
        ClearFeed();
        if (current == null || active == null) { ShowWelcome(); previous.Enabled = next.Enabled = false; pageLabel.Text = ""; return; }
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
        if (current is not { } conversation) return;
        var a = conversation.Source.Archive; var room = conversation.Room; var m = entry.Message;
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
            foreach (var attachment in a.Attachments(data)) AddAttachment(card, a, attachment.Node, attachment.Mime, itemNumber);
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
        pendingReadingPosition = null;
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
        string value = node.Fields != null ? $"{{{node.Fields.Count}}}" : node.Values != null ? $"[{node.Values.Count}]" : node.Kind == '"' && node.Length > 256 ? $"string · {node.Length:N0} bytes" : current?.Source.Archive.Text(node, 150) ?? "";
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
    void AddAttachment(MessageCard card, Archive a, JNode node, string mime, int number)
    {
        string ext = mime.ToLowerInvariant() switch { "image/png" => ".png", "image/jpeg" => ".jpg", "image/gif" => ".gif", "image/webp" => ".webp", "image/avif" => ".avif", "application/pdf" => ".pdf", "audio/mpeg" => ".mp3", "audio/wav" => ".wav", "video/mp4" => ".mp4", _ => ".bin" };
        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Margin = new(0, 8, 0, 4) };
        row.Controls.Add(new Label { Text = $"{mime} • ~{node.Length * 0.75 / 1048576:N2} MiB", AutoSize = true, Margin = new(0, 8, 10, 0) });
        var save = new Button { Text = "Save attachment…", AutoSize = true };
        save.Click += async (_, _) =>
        {
            if (busy) return;
            using var dialog = new SaveFileDialog { FileName = $"attachment-{number}{ext}", OverwritePrompt = true };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            string path = Path.GetFullPath(dialog.FileName);
            if (sources.Any(source => string.Equals(path, source.Archive.Path, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show(this, "Choose a path other than an open export.", Text);
                return;
            }
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".partial";
            string[] sourcePaths = sources.Select(source => source.Archive.Path).ToArray();
            using var token = new CancellationTokenSource(); operation = token; SetBusy(true, "Saving attachment…"); progress.Style = ProgressBarStyle.Marquee;
            try
            {
                await Task.Run(() =>
                {
                    using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
                        a.DecodeAttachment(node, output, token.Token);
                    token.Token.ThrowIfCancellationRequested();
                    // The originals are no longer locked after import. Protect aliases as well as exact paths.
                    if (File.Exists(path) && sourcePaths.Any(source => WindowsFileIdentity.SameFile(path, source)))
                        throw new IOException("Choose a path other than an open export.");
                    File.Move(temp, path, true);
                });
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
