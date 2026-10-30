using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
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
        Keys.Home => "Inicio", Keys.End => "Fin", Keys.Space => "Espacio", Keys.Back => "Retroceso",
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

[DataContract]
sealed class Slot
{
    [DataMember(Name = "name", Order = 1)] public string Name = "";
    public Hotkey Hotkey = new();
    [DataMember(Name = "monitors", Order = 3)] public List<MonitorState> Monitors = new();
    [DataMember(Name = "icons", Order = 4)] public List<IconPos> Icons = new();
    // Qué partes se aplican (se guardan siempre las dos).
    [DataMember(Name = "useDisplays", Order = 5)] public bool UseDisplays = true;
    [DataMember(Name = "useIcons", Order = 6)] public bool UseIcons = true;
    public bool IsEmpty => Monitors.Count == 0 && Icons.Count == 0;

    [DataMember(Name = "hotkey", Order = 2)]
    string HotkeyText { get => Hotkey.Serialize(); set => Hotkey = Hotkey.Parse(value); }

    // El serializador no ejecuta constructores ni inicializadores: se rellenan aquí los valores por defecto.
    [OnDeserializing]
    void Defaults(StreamingContext _) { Name = ""; Hotkey = new(); Monitors = new(); Icons = new(); UseDisplays = UseIcons = true; }
}

[DataContract]
sealed class Config
{
    public const int SlotCount = 9;
    // MAJINDESKTOP_DATA permite usar otra carpeta (para pruebas sin tocar la configuración real).
    public static readonly string Dir = Environment.GetEnvironmentVariable("MAJINDESKTOP_DATA") is { Length: > 0 } custom
        ? custom
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MajinDesktop");
    public static string FilePath => Path.Combine(Dir, "config.json");
    static string LegacyPath => Path.Combine(Dir, "config.ini");

    [DataMember(Name = "version", Order = 1)] public int Version = 2;
    [DataMember(Name = "slots", Order = 6)] public Slot[] Slots = NewSlots();
    public Hotkey Cycle = new();
    public Hotkey Undo = new();
    [DataMember(Name = "last", Order = 4)] public int Last = -1;   // último slot aplicado (para el atajo de ciclo)
    // Cómo estaba todo antes del último cambio (para deshacer).
    [DataMember(Name = "previous", Order = 5)] public List<MonitorState>? Previous = null;
    [DataMember(Name = "previousIcons", Order = 5)] public List<IconPos>? PreviousIcons = null;

    [DataMember(Name = "cycle", Order = 2)]
    string CycleText { get => Cycle.Serialize(); set => Cycle = Hotkey.Parse(value); }

    [DataMember(Name = "undo", Order = 3)]
    string UndoText { get => Undo.Serialize(); set => Undo = Hotkey.Parse(value); }

    [OnDeserializing]
    void Defaults(StreamingContext _) { Version = 2; Slots = NewSlots(); Cycle = new(); Undo = new(); Last = -1; }

    static Slot[] NewSlots() => Enumerable.Range(0, SlotCount).Select(_ => new Slot()).ToArray();

    static Hotkey Combo(Keys key) => new() { Ctrl = true, Alt = true, Shift = true, Key = key };

    public static Config Load()
    {
        if (File.Exists(FilePath))
        {
            Config c;
            try
            {
                using var stream = File.OpenRead(FilePath);
                c = (Config)new DataContractJsonSerializer(typeof(Config)).ReadObject(stream);
            }
            catch (SerializationException ex)
            {
                // Archivo roto (por ejemplo, editado a mano): se aparta y se empieza de cero.
                string broken = Path.Combine(Dir, $"config.broken-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                File.Move(FilePath, broken);
                Log.Error("config.json no se pudo leer, apartado como " + Path.GetFileName(broken), ex);
                return Load();
            }
            // Siempre 9 slots, aunque el archivo traiga otros (editado a mano o de otra versión).
            if (c.Slots.Length != SlotCount)
                c.Slots = c.Slots.Concat(NewSlots()).Take(SlotCount).Select(sl => sl ?? new Slot()).ToArray();
            return c;
        }
        if (File.Exists(LegacyPath))
        {
            var c = LoadLegacy();
            c.Save();
            File.Move(LegacyPath, LegacyPath + ".bak");
            return c;
        }
        // Primera vez: Ctrl+Alt+Shift+1..9 para cada slot, +0 para ir al siguiente y +Retroceso para deshacer.
        // (Ctrl+Alt solo choca con AltGr en teclado español: AltGr+2 = @, AltGr+3 = #.)
        var fresh = new Config { Cycle = Combo(Keys.D0), Undo = Combo(Keys.Back) };
        for (int i = 0; i < SlotCount; i++) fresh.Slots[i].Hotkey = Combo(Keys.D1 + i);
        return fresh;
    }

    // config.ini de la v0.1.
    static Config LoadLegacy()
    {
        var c = new Config { Undo = Combo(Keys.Back) };
        Slot? slot = null;
        foreach (var raw in File.ReadAllLines(LegacyPath, Encoding.UTF8))
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
        Directory.CreateDirectory(Dir);
        string tmp = FilePath + ".tmp";
        using (var stream = File.Create(tmp))
        using (var writer = JsonReaderWriterFactory.CreateJsonWriter(stream, new UTF8Encoding(false), false, true, "  "))
        {
            new DataContractJsonSerializer(typeof(Config)).WriteObject(writer, this);
            writer.Flush();
        }
        if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
        else File.Move(tmp, FilePath);
    }

    public string SlotTitle(int i) =>
        Slots[i].Name.Length > 0 ? Slots[i].Name : $"Estado {i + 1}";

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
