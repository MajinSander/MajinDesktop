using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MajinDesktop;

// Instalador por usuario (sin admin): lleva MajinDesktop.exe dentro como recurso.
//   MajinDesktop-Setup.exe            instala con ventana
//   MajinDesktop-Setup.exe /S         instala en silencio (para actualizaciones)
//   uninstall.exe --uninstall         desinstala (lo lanza "Aplicaciones instaladas")
static class Setup
{
    public const string App = "MajinDesktop";
    public static readonly string InstallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", App);
    public static string AppExe => Path.Combine(InstallDir, App + ".exe");
    static string UninstallExe => Path.Combine(InstallDir, "uninstall.exe");
    static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), App + ".lnk");
    static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), App + ".lnk");
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + App;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static string Version => Application.ProductVersion;

    [STAThread]
    static int Main(string[] args)
    {
        Native.SetProcessDPIAware();
        bool uninstall = Array.Exists(args, a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase));
        bool silent = Array.Exists(args, a => a.Equals("/S", StringComparison.OrdinalIgnoreCase));
        if (silent && uninstall)
        {
            Uninstall(removeData: false);
            return 0;
        }
        if (silent)
        {
            try { Install(autoStart: Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue(App) != null, desktopLink: false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 1; }
            Process.Start(AppExe);
            return 0;
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new SetupForm(uninstall));
        return 0;
    }

    public static void CloseRunningApp()
    {
        foreach (var p in Process.GetProcessesByName(App))
        {
            try { p.Kill(); p.WaitForExit(3000); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            finally { p.Dispose(); }
        }
    }

    public static void Install(bool autoStart, bool desktopLink)
    {
        CloseRunningApp();
        Directory.CreateDirectory(InstallDir);
        using (var res = typeof(Setup).Assembly.GetManifestResourceStream(App + ".exe")!)
        using (var file = File.Create(AppExe))
            res.CopyTo(file);
        string self = Process.GetCurrentProcess().MainModule!.FileName;
        if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(UninstallExe), StringComparison.OrdinalIgnoreCase))
            File.Copy(self, UninstallExe, true);

        CreateLink(StartMenuLink, AppExe);
        if (desktopLink) CreateLink(DesktopLink, AppExe);

        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (autoStart) run.SetValue(App, $"\"{AppExe}\"");
            else run.DeleteValue(App, false);
        }

        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", App);
        key.SetValue("DisplayVersion", Version);
        key.SetValue("Publisher", "Majin");
        key.SetValue("DisplayIcon", AppExe);
        key.SetValue("InstallLocation", InstallDir);
        key.SetValue("UninstallString", $"\"{UninstallExe}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{UninstallExe}\" --uninstall /S");
        key.SetValue("URLInfoAbout", "https://github.com/MajinSander/MajinDesktop");
        key.SetValue("EstimatedSize", (int)((new FileInfo(AppExe).Length + new FileInfo(UninstallExe).Length) / 1024), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    public static void Uninstall(bool removeData)
    {
        CloseRunningApp();
        using (var run = Registry.CurrentUser.OpenSubKey(RunKey, true)) run?.DeleteValue(App, false);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
        foreach (var link in new[] { StartMenuLink, DesktopLink })
            if (File.Exists(link)) File.Delete(link);
        if (removeData)
        {
            string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), App);
            if (Directory.Exists(data)) Directory.Delete(data, true);
        }
        // uninstall.exe está dentro de la carpeta que hay que borrar: se borra cuando este proceso termine.
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{InstallDir}\"")
        {
            CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath(),
        });
    }

    static void CreateLink(string linkPath, string target) =>
        Shortcuts.CreateLnk(linkPath, target, "Estados del escritorio: pantallas, iconos y accesos directos");
}

sealed class SetupForm : Form
{
    readonly bool uninstall;
    readonly Toggle optA, optB;
    readonly PillButton go = new("", PillButton.Kind.Primary), cancel = new("Cancelar", PillButton.Kind.Secondary);
    readonly Label status = new() { AutoSize = false, ForeColor = Theme.Muted };

    public SetupForm(bool uninstall)
    {
        this.uninstall = uninstall;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = uninstall ? "Desinstalar MajinDesktop" : "Instalar MajinDesktop";
        Icon = Theme.AppIcon;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(480, 270);

        Controls.Add(new Label { Text = "MajinDesktop", Font = Theme.Title, ForeColor = Theme.Bright, AutoSize = true, Location = new Point(84, 22) });
        Controls.Add(new Label { Text = "Versión " + Setup.Version, ForeColor = Theme.Muted, AutoSize = true, Location = new Point(86, 54) });
        var info = new Label
        {
            AutoSize = false, ForeColor = Theme.Text, Location = new Point(24, 96), Size = new Size(432, 40),
            Text = uninstall
                ? "Se quitará el programa, su acceso del menú Inicio y el inicio automático."
                : "Se instalará en " + Setup.InstallDir.Replace(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%") + " (sin permisos de administrador).",
        };
        Controls.Add(info);

        optA = new Toggle(uninstall ? "Borrar también mis estados guardados" : "Iniciar con Windows") { Checked = !uninstall };
        optA.SetBounds(24, 140, 432, 30);
        optA.Toggled += () => optA.Checked = !optA.Checked;
        Controls.Add(optA);
        optB = new Toggle("Acceso directo en el escritorio");
        optB.SetBounds(24, 174, 432, 30);
        optB.Toggled += () => optB.Checked = !optB.Checked;
        optB.Visible = !uninstall;
        Controls.Add(optB);

        status.SetBounds(24, 226, 220, 32);
        status.TextAlign = ContentAlignment.MiddleLeft;
        Controls.Add(status);
        cancel.SetBounds(256, 222, 96, 36);
        cancel.Click += (_, _) => Close();
        Controls.Add(cancel);
        go.Text = uninstall ? "Desinstalar" : "Instalar";
        go.SetBounds(360, 222, 96, 36);
        go.Click += (_, _) => Run();
        Controls.Add(go);
        ResumeLayout(false);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.DarkTitleBar(Handle);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        float s = DeviceDpi / 96f;
        using var icon = Theme.IconBitmap((int)(48 * s));
        e.Graphics.DrawImage(icon, 24 * s, 22 * s);
    }

    void Run()
    {
        go.Enabled = cancel.Enabled = false;
        status.Text = uninstall ? "Desinstalando…" : "Instalando…";
        status.Refresh();
        try
        {
            if (uninstall)
            {
                Setup.Uninstall(optA.Checked);
                Close();
                return;
            }
            Setup.Install(optA.Checked, optB.Checked);
            Process.Start(Setup.AppExe);
            Close();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
        {
            status.ForeColor = Theme.Red;
            status.Text = "Error: " + ex.Message;
            go.Enabled = cancel.Enabled = true;
        }
    }
}
