using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MajinDesktop;

// Busca la última release de GitHub. Si la app está instalada, descarga el instalador y lo lanza con /S:
// el instalador cierra la app, la reemplaza y la vuelve a abrir. La versión portable solo avisa.
sealed class Updater : IDisposable
{
    const string Api = "https://api.github.com/repos/MajinSander/MajinDesktop/releases/latest";
    public const string ReleasesUrl = "https://github.com/MajinSander/MajinDesktop/releases/latest";

    readonly System.Windows.Forms.Timer timer = new() { Interval = 6 * 60 * 60 * 1000 };
    readonly Action<string, string> notify;
    readonly SynchronizationContext ui;
    int busy;

    public string? Available { get; private set; } // versión nueva encontrada (portable: para el menú)

    public Updater(Action<string, string> notify)
    {
        this.notify = notify;
        ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        timer.Tick += (_, _) => Check(false);
    }

    public static bool Disabled => Environment.GetEnvironmentVariable("MAJINDESKTOP_NO_UPDATE") == "1";

    public static bool Installed => Application.ExecutablePath.StartsWith(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
        StringComparison.OrdinalIgnoreCase);

    public void Start()
    {
        if (Disabled) return;
        var first = new System.Windows.Forms.Timer { Interval = 15000 }; // deja arrancar Windows tranquilo
        first.Tick += (_, _) => { first.Dispose(); Check(false); };
        first.Start();
        timer.Start();
    }

    // manual = el usuario lo pidió desde el menú: entonces también se avisa si no hay nada nuevo.
    public void Check(bool manual)
    {
        if (Interlocked.Exchange(ref busy, 1) == 1) return;
        Task.Run(() =>
        {
            try
            {
                var (version, setupUrl) = Latest();
                var current = Version.Parse(Application.ProductVersion.Split('-', '+')[0]);
                if (version <= current)
                {
                    if (manual) Post("Estás al día", $"MajinDesktop {Application.ProductVersion} es la última versión");
                    return;
                }
                Available = version.ToString(3);
                if (!Installed || setupUrl == null)
                {
                    Post($"MajinDesktop {Available} disponible", "Descárgala desde el menú de la bandeja");
                    return;
                }
                Log.Info($"Actualizando a {Available}");
                Post($"Actualizando a {Available}", "MajinDesktop se reiniciará en unos segundos");
                string file = Path.Combine(Path.GetTempPath(), $"MajinDesktop-Setup-{Available}.exe");
                using (var web = Client()) web.DownloadFile(setupUrl, file);
                Process.Start(new ProcessStartInfo(file, "/S") { UseShellExecute = false });
            }
            catch (Exception ex) when (ex is WebException or IOException or FormatException or System.ComponentModel.Win32Exception)
            {
                Log.Error("Actualización", ex);
                if (manual) Post("No se pudo buscar actualizaciones", ex.Message);
            }
            finally { Interlocked.Exchange(ref busy, 0); }
        });
    }

    static (Version, string?) Latest()
    {
        using var web = Client();
        web.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
        string json = web.DownloadString(Api);
        var tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"v?([0-9.]+)");
        if (!tag.Success) throw new FormatException("Respuesta de GitHub inesperada");
        var asset = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]*MajinDesktop-Setup-[^\"]*\\.exe)\"");
        return (Version.Parse(tag.Groups[1].Value), asset.Success ? asset.Groups[1].Value : null);
    }

    static WebClient Client()
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        var web = new WebClient();
        web.Headers[HttpRequestHeader.UserAgent] = "MajinDesktop/" + Application.ProductVersion;
        return web;
    }

    void Post(string title, string detail) => ui.Post(_ => notify(title, detail), null);

    public void Dispose() => timer.Dispose();
}
