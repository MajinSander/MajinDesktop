using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
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

        bool firstRun = !File.Exists(Config.FilePath) && !File.Exists(Path.Combine(Config.Dir, "config.ini"));
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        ToolStripManager.Renderer = new DarkMenuRenderer();

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("Error no controlado", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Error fatal: " + e.ExceptionObject);
        Log.Info("Inicio " + Application.ProductVersion);

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
                            Console.WriteLine($"estado {i + 1}: {cfg.SlotTitle(i)} ({cfg.Slots[i].Monitors.Count} pantallas, {cfg.Slots[i].Icons.Count} iconos, atajo {cfg.Slots[i].Hotkey})");
                    return 0;

                case "icons":
                    var mons = Displays.Snapshot();
                    var icons = Icons.Snapshot(mons);
                    foreach (var ic in icons)
                        Console.WriteLine($"{ic.Name,-40} {mons.FirstOrDefault(m => m.Path == ic.Monitor)?.Name,-14} +{ic.Dx},+{ic.Dy}");
                    Console.WriteLine($"{icons.Count} iconos");
                    if (args.Length > 1 && args[1] is "--reapply" or "--dry")
                    {
                        var r = Icons.Apply(icons, mons, args[1] == "--dry");
                        Console.WriteLine($"Reaplicado: {r.Moved} movidos, {r.Missing} no encontrados, {r.NoMonitor} sin pantalla");
                    }
                    return 0;

                case "version":
                    Console.WriteLine(Application.ProductVersion);
                    return 0;

                case "make-icon" when args.Length > 1:
                    Theme.WriteIco(args[1]);
                    return 0;

                case "save" when slot >= 0 && slot < Config.SlotCount:
                    cfg.Slots[slot].Monitors = Displays.Snapshot();
                    cfg.Slots[slot].Icons = Icons.Snapshot(cfg.Slots[slot].Monitors);
                    cfg.Save();
                    Console.WriteLine($"Guardado en el estado {slot + 1}: {cfg.Slots[slot].Monitors.Count} pantallas y {cfg.Slots[slot].Icons.Count} iconos.");
                    return 0;

                case "apply" or "validate" when slot >= 0 && slot < Config.SlotCount:
                {
                    var st = cfg.Slots[slot];
                    if (st.IsEmpty) { Console.WriteLine($"El estado {slot + 1} está vacío."); return 2; }
                    var status = ApplyStatus.AlreadyActive;
                    if (st.UseDisplays && st.Monitors.Count > 0)
                    {
                        status = Displays.Apply(st.Monitors, cmd == "validate", out string error);
                        Console.WriteLine("Pantallas: " + status switch
                        {
                            ApplyStatus.Applied => cmd == "validate" ? "Windows aceptaría esta disposición." : "aplicadas.",
                            ApplyStatus.AlreadyActive => "ya estaban así.",
                            ApplyStatus.NoMonitorsFound => "ninguna del estado está conectada.",
                            _ => "error: " + error,
                        });
                        if (status == ApplyStatus.Failed) return 1;
                    }
                    if (st.UseIcons && st.Icons.Count > 0)
                    {
                        // Tras cambiar las pantallas, Explorer recoloca los iconos a su manera: se espera un poco.
                        if (status == ApplyStatus.Applied && cmd == "apply") Thread.Sleep(1500);
                        var r = Icons.Apply(st.Icons, Displays.Snapshot(), cmd == "validate");
                        Console.WriteLine($"Iconos: {r.Moved} {(cmd == "validate" ? "se moverían" : "recolocados")}, {r.Missing} ya no están, {r.NoMonitor} sin su pantalla.");
                    }
                    return 0;
                }
            }
            Console.WriteLine("Uso: MajinDesktop [--list | --icons | --save N | --apply N | --validate N | --version]");
            return 2;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            Console.WriteLine("Error: " + ex.Message);
            return 1;
        }
    }
}
