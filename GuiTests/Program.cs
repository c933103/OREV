using ExportViewer;
using System.Reflection;
using System.Text.Json;
internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        using var form = new MainForm(args[0]);
        form.Shown += async (_, _) =>
        {
            try
            {
                for (int i = 0; i < 1000; i++)
                {
                    await Task.Delay(20);
                    if (Get(form,"archive") != null && !(bool)Get(form,"busy")!) break;
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
                File.WriteAllText(prefix + ".result", "PASS");
            }
            catch (Exception e) { Console.WriteLine(e.ToString()); Environment.ExitCode = 1; }
            finally { form.Close(); }
        };
        Application.Run(form);
    }
    static object? Get(object o,string field) => o.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o);
    static IEnumerable<Control> Descendants(Control c) { foreach (Control child in c.Controls) { yield return child; foreach (var n in Descendants(child)) yield return n; } }
    static object[] Dump(Control c) => c.Controls.Cast<Control>().Select(child => (object)new { type = child.GetType().Name, bounds = child.Bounds.ToString(), visible = child.Visible, textLength = child.Text.Length, children = Dump(child) }).ToArray();
}
