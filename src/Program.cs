using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace MajinDesktop;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        Native.SetProcessDPIAware();
        if (args.Length > 0) return Cli(args);

        using var mutex = new Mutex(true, @"Local\MajinDesktop", out bool firstInstance);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\MajinDesktop.Show");
        if (!firstInstance)
        {
            showSignal.Set(); // ya está abierto: que muestre su ventana
            return 0;
        }

        bool firstRun = !File.Exists(Path.Combine(Config.Dir, "config.ini"));
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        ToolStripManager.Renderer = new DarkMenuRenderer();

        var app = new TrayApp(firstRun);
        ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => app.PostShow(), null, -1, false);
        if (firstRun) app.TrySave();
        Application.Run(app);
        return 0;
    }

    // MajinDesktop.exe --list | --apply N | --save N | --validate N | --make-icon ruta.ico
    static int Cli(string[] args)
    {
        Native.AttachConsole(-1);
        var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(output);
        try
        {
            string cmd = args[0].TrimStart('-', '/').ToLowerInvariant();
            int slot = args.Length > 1 && int.TryParse(args[1], out int n) ? n - 1 : -1;
            var cfg = Config.Load();

            switch (cmd)
            {
                case "list":
                    foreach (var m in Displays.Snapshot())
                        Console.WriteLine($"{m.Name,-20} {m.Width}x{m.Height} en ({m.X},{m.Y}) {m.RotationText}{(m.Primary ? " [principal]" : "")}\n    {m.Path}");
                    for (int i = 0; i < Config.SlotCount; i++)
                        if (!cfg.Slots[i].IsEmpty)
                            Console.WriteLine($"slot {i + 1}: {cfg.SlotTitle(i)} ({cfg.Slots[i].Monitors.Count} pantallas, atajo {cfg.Slots[i].Hotkey})");
                    return 0;

                case "version":
                    Console.WriteLine(Application.ProductVersion);
                    return 0;

                case "make-icon" when args.Length > 1:
                    Theme.WriteIco(args[1]);
                    return 0;

                case "save" when slot >= 0 && slot < Config.SlotCount:
                    cfg.Slots[slot].Monitors = Displays.Snapshot();
                    cfg.Save();
                    Console.WriteLine($"Guardado en el slot {slot + 1}.");
                    return 0;

                case "apply" or "validate" when slot >= 0 && slot < Config.SlotCount:
                    if (cfg.Slots[slot].IsEmpty) { Console.WriteLine($"El slot {slot + 1} está vacío."); return 2; }
                    var status = Displays.Apply(cfg.Slots[slot].Monitors, cmd == "validate", out string error);
                    Console.WriteLine(status switch
                    {
                        ApplyStatus.Applied => cmd == "validate" ? "Válido: Windows aceptaría esta disposición." : "Aplicado.",
                        ApplyStatus.AlreadyActive => "Ya estaba activo.",
                        ApplyStatus.NoMonitorsFound => "Ninguna pantalla del slot está conectada.",
                        _ => "Error: " + error,
                    });
                    return status is ApplyStatus.Applied or ApplyStatus.AlreadyActive ? 0 : 1;
            }
            Console.WriteLine("Uso: MajinDesktop [--list | --save N | --apply N | --validate N]");
            return 2;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            Console.WriteLine("Error: " + ex.Message);
            return 1;
        }
    }
}
