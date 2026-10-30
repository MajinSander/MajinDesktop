using System;
using System.IO;
using System.Text;

namespace MajinDesktop;

// Registro en %APPDATA%\MajinDesktop\log.txt. Al pasar de 256 KB se guarda como log.old.txt y empieza otro.
static class Log
{
    static readonly object gate = new();
    static string FilePath => Path.Combine(Config.Dir, "log.txt");

    public static void Info(string message) => Write("INFO ", message);
    public static void Error(string message) => Write("ERROR", message);
    public static void Error(string context, Exception ex) => Write("ERROR", $"{context}: {ex}");

    static void Write(string level, string message)
    {
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(Config.Dir);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > 256 * 1024)
                {
                    string old = Path.Combine(Config.Dir, "log.old.txt");
                    if (File.Exists(old)) File.Delete(old);
                    file.MoveTo(old);
                }
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}", new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // el registro nunca debe tumbar la app
        }
    }
}
