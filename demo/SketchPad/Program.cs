// SketchPad: a stand-in "drawing app" for the ResMan demo. Deliberately misbehaves on command.
//   SketchPad hang [seconds]   opens a window that freezes (stops responding) for a while
//   SketchPad crash            crashes on startup (Windows records an Application Error)

var mode = args.ElementAtOrDefault(0) ?? "hang";
if (mode == "crash")
{
    Thread.Sleep(800);
    throw new InvalidOperationException("SketchPad failed to load its brush library (demo crash).");
}

var seconds = int.TryParse(args.ElementAtOrDefault(1), out var s) ? s : 20;
Application.EnableVisualStyles();
var form = new Form { Text = "SketchPad - Untitled", Width = 640, Height = 420, StartPosition = FormStartPosition.CenterScreen, TopMost = true };
form.Controls.Add(new Label
{
    Text = "SketchPad is loading a large canvas...\n\n(ResMan demo: this window will stop responding.)",
    Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 14),
});
form.Shown += async (_, _) =>
{
    form.Activate();
    await Task.Delay(1500);
    Thread.Sleep(seconds * 1000); // freeze the UI thread on purpose
    await Task.Delay(1500);
    form.Close();
};
Application.Run(form);
