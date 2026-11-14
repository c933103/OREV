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
