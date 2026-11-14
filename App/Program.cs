// Copyright (c) 2026 c933103 and contributors.
// SPDX-License-Identifier: LicenseRef-OREV-Community-1.0
// See LICENSE, including Section 10 and Schedule A.

namespace ExportViewer;
internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => MessageBox.Show(e.Exception.Message, "OpenRouter Export Viewer", MessageBoxButtons.OK, MessageBoxIcon.Error);
        Application.Run(new MainForm(args.FirstOrDefault()));
    }
}
