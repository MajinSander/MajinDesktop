using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MajinDesktop;

sealed class Hotkey
{
    public Keys Key;
    public bool Ctrl, Alt, Shift, Win;

    public bool IsEmpty => Key == Keys.None;

    public uint Modifiers =>
        (Ctrl ? Native.MOD_CONTROL : 0) | (Alt ? Native.MOD_ALT : 0) |
        (Shift ? Native.MOD_SHIFT : 0) | (Win ? Native.MOD_WIN : 0);

    IEnumerable<string> Mods()
    {
        if (Ctrl) yield return "Ctrl";
        if (Alt) yield return "Alt";
        if (Shift) yield return "Shift";
        if (Win) yield return "Win";
    }

    // Formato del archivo: nombres del enum Keys ("Ctrl+Alt+D1").
    public string Serialize() => IsEmpty ? "" : string.Join("+", Mods().Append(Key.ToString()));

    // Formato para mostrar: "Ctrl+Alt+1".
    public override string ToString() => IsEmpty ? "" : string.Join("+", Mods().Append(KeyText(Key)));

    public static string KeyText(Keys k) => k switch
    {
        >= Keys.D0 and <= Keys.D9 => ((char)('0' + (k - Keys.D0))).ToString(),
        >= Keys.NumPad0 and <= Keys.NumPad9 => "Num" + (k - Keys.NumPad0),
        Keys.Oemplus => "+", Keys.OemMinus => "-", Keys.Oemcomma => ",", Keys.OemPeriod => ".",
        Keys.Next => "AvPág", Keys.Prior => "RePág", Keys.Insert => "Insert", Keys.Delete => "Supr",
        Keys.Home => "Inicio", Keys.End => "Fin", Keys.Space => "Espacio",
        Keys.Left => "←", Keys.Right => "→", Keys.Up => "↑", Keys.Down => "↓",
        _ => k.ToString(),
    };

    public static Hotkey Parse(string s)
    {
        var h = new Hotkey();
        foreach (var part in (s ?? "").Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.Trim().ToLowerInvariant())
            {
                case "ctrl": h.Ctrl = true; break;
                case "alt": h.Alt = true; break;
                case "shift": h.Shift = true; break;
                case "win": h.Win = true; break;
                default:
                    if (Enum.TryParse(part.Trim(), true, out Keys k)) h.Key = k;
                    break;
            }
        }
        return h;
    }

    public bool SameAs(Hotkey o) => !IsEmpty && Key == o.Key && Modifiers == o.Modifiers;
}

sealed class Slot
{
    public string Name = "";
    public Hotkey Hotkey = new();
    public List<MonitorState> Monitors = new();
    public bool IsEmpty => Monitors.Count == 0;
}

sealed class Config
{
    public const int SlotCount = 9;
    // MAJINDESKTOP_DATA permite usar otra carpeta (para pruebas sin tocar la configuración real).
    public static readonly string Dir = Environment.GetEnvironmentVariable("MAJINDESKTOP_DATA") is { Length: > 0 } custom
        ? custom
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MajinDesktop");
    static readonly string FilePath = Path.Combine(Dir, "config.ini");

    public Slot[] Slots = Enumerable.Range(0, SlotCount).Select(_ => new Slot()).ToArray();
    public Hotkey Cycle = new();
    public int Last = -1;   // último slot aplicado (para el atajo de ciclo)

    public static Config Load()
    {
        var c = new Config();
        if (!File.Exists(FilePath))
        {
            // Primera vez: Ctrl+Alt+Shift+1..9 para cada slot y Ctrl+Alt+Shift+0 para ir al siguiente.
            // (Ctrl+Alt solo choca con AltGr en teclado español: AltGr+2 = @, AltGr+3 = #.)
            for (int i = 0; i < SlotCount; i++)
                c.Slots[i].Hotkey = new Hotkey { Ctrl = true, Alt = true, Shift = true, Key = Keys.D1 + i };
            c.Cycle = new Hotkey { Ctrl = true, Alt = true, Shift = true, Key = Keys.D0 };
            return c;
        }

        Slot? slot = null;
        foreach (var raw in File.ReadAllLines(FilePath, Encoding.UTF8))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line.StartsWith("[slot") && line.EndsWith("]") &&
                int.TryParse(line.Substring(5, line.Length - 6), out int n) && n >= 1 && n <= SlotCount)
            {
                slot = c.Slots[n - 1];
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            string key = line.Substring(0, eq).Trim(), val = line.Substring(eq + 1).Trim();
            if (slot == null)
            {
                if (key == "cycle") c.Cycle = Hotkey.Parse(val);
                else if (key == "last" && int.TryParse(val, out int last)) c.Last = last;
            }
            else if (key == "name") slot.Name = val;
            else if (key == "hotkey") slot.Hotkey = Hotkey.Parse(val);
            else if (key == "monitor" && MonitorState.Parse(val) is { } m) slot.Monitors.Add(m);
        }
        return c;
    }

    public void Save()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# MajinDesktop: monitor = rotación|x|y|ancho|alto|edid|nombre|ruta");
        sb.AppendLine("cycle=" + Cycle.Serialize());
        sb.AppendLine("last=" + Last);
        for (int i = 0; i < SlotCount; i++)
        {
            var s = Slots[i];
            sb.AppendLine().AppendLine($"[slot{i + 1}]");
            sb.AppendLine("name=" + s.Name);
            sb.AppendLine("hotkey=" + s.Hotkey.Serialize());
            foreach (var m in s.Monitors) sb.AppendLine("monitor=" + m.Serialize());
        }
        Directory.CreateDirectory(Dir);
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
        if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
        else File.Move(tmp, FilePath);
    }

    public string SlotTitle(int i) =>
        Slots[i].Name.Length > 0 ? Slots[i].Name : $"Slot {i + 1}";

    // --- Arranque con Windows (HKCU\...\Run) ---
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool AutoStart
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue("MajinDesktop") is string;
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue("MajinDesktop", $"\"{Application.ExecutablePath}\"");
            else k.DeleteValue("MajinDesktop", false);
        }
    }
}
