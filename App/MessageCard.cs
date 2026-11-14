// Copyright (c) 2026 c933103 and contributors.
// SPDX-License-Identifier: LicenseRef-OREV-Community-1.0
// See LICENSE, including Section 10 and Schedule A.

namespace ExportViewer;

// A fixed-width vertical card. Its height is the sum of its visible children;
// the outer scroller never has to infer a nested FlowLayoutPanel's preferred size.
internal sealed class MessageCard : Panel
{
    bool arranging;
    public MessageCard()
    {
        AutoSize = false;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
    }
    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        e.Control!.SizeChanged += ChildChanged;
        e.Control.VisibleChanged += ChildChanged;
        PerformLayout();
    }
    protected override void OnControlRemoved(ControlEventArgs e)
    {
        e.Control!.SizeChanged -= ChildChanged;
        e.Control.VisibleChanged -= ChildChanged;
        base.OnControlRemoved(e); PerformLayout();
    }
    void ChildChanged(object? sender, EventArgs e) { if (!arranging && !IsDisposed) PerformLayout(); }
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (Visible) PerformLayout(); }
    protected override void OnLayout(LayoutEventArgs e)
    {
        if (arranging || IsDisposed) return;
        arranging = true;
        try
        {
            int y = Padding.Top, available = Math.Max(150, ClientSize.Width - Padding.Horizontal);
            foreach (Control child in Controls)
            {
                if (!child.Visible) continue;
                int width = Math.Max(100, available - child.Margin.Horizontal);
                int height = Math.Max(1, child.Height);
                if (child is Label label)
                {
                    label.MaximumSize = new(width, 0);
                    height = Math.Max(label.Font.Height + 2, label.GetPreferredSize(new(width, 0)).Height);
                }
                else if (child is FlowLayoutPanel row)
                {
                    row.AutoSize = false;
                    row.Width = width;
                    height = Math.Max(30, row.GetPreferredSize(new(width, 0)).Height);
                }
                else if (child is Button button)
                {
                    var preferred = button.GetPreferredSize(new(width, 0));
                    width = Math.Min(width, Math.Max(120, preferred.Width)); height = Math.Max(30, preferred.Height);
                }
                y += child.Margin.Top;
                child.SetBounds(Padding.Left + child.Margin.Left, y, width, height);
                y += height + child.Margin.Bottom;
            }
            int wanted = y + Padding.Bottom + (BorderStyle == BorderStyle.None ? 0 : 2);
            if (Height != wanted) Height = Math.Max(40, wanted);
        }
        finally { arranging = false; }
        base.OnLayout(e);
    }
}
