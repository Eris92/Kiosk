using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kiosk.Client;

/// <summary>
/// Konfiguracja → Statystyki: how often people sign in and which options they open, to see whether the Kiosk helps.
/// No names: a person is a short code made from their account with a secret of this computer (the same person gives
/// the same code, so "unique people" can be counted, but the code cannot be turned back into a name).
/// The Kiosk Card service writes C:\ProgramData\Kiosk\Stats\yyyy-MM.jsonl (the Kiosk account may not write there) and,
/// with Konfiguracja z serwera, copies the months to Stats\ next to the server's client.json, one file per computer.
/// </summary>
internal static class Stats
{
    internal const string SignIn = "logowanie", Option = "opcja", News = "komunikat", App = "aplikacja";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Kiosk.Stats");
    internal static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Kiosk", "Stats");
    private static string SaltFile => Path.Combine(Folder, "Stats.key");
    private const int KeepMonths = 13;

    internal sealed record Entry
    {
        public DateTimeOffset Time { get; init; }
        public string Pc { get; init; } = "";
        public string Kind { get; init; } = "";
        public string Name { get; init; } = "";
        public string Category { get; init; } = "";
        /// <summary>The person's code (empty for a guest).</summary>
        public string Who { get; init; } = "";
    }

    /// <summary>Kiosk side: hands the event to the service in the background (never slows the screen).</summary>
    internal static void Record(string kind, string name, string category = "", string person = "")
    {
        var json = JsonSerializer.Serialize(new Entry { Kind = kind, Name = name.Trim(), Category = category.Trim(), Who = person.Trim() });
        _ = Task.Run(() =>
        {
            var answer = CardService.Call(new CardService.Request { Op = "stat", Source = json });
            if (answer.Code != 0) Audit.Write("stats_failed", answer.Message);
        });
    }

    /// <summary>Kiosk Card service, "stat": one line in this month's file; the person's account becomes their code here.</summary>
    internal static CardService.Response Append(string json)
    {
        try
        {
            var entry = JsonSerializer.Deserialize<Entry>(json) ?? throw new InvalidDataException("Puste zdarzenie.");
            if (entry.Kind.Length == 0 || entry.Kind.Length > 40 || entry.Name.Length > 300 || entry.Category.Length > 300)
                throw new InvalidDataException("Nieprawidłowe zdarzenie.");
            Directory.CreateDirectory(Folder);
            lock (Lock)
            {
                entry = entry with { Time = DateTimeOffset.Now, Pc = Environment.MachineName, Who = entry.Who.Length > 0 ? Code(entry.Who) : "" };
                File.AppendAllText(Path.Combine(Folder, entry.Time.ToString("yyyy-MM") + ".jsonl"), JsonSerializer.Serialize(entry) + Environment.NewLine);
                Prune();
            }
            return new CardService.Response();
        }
        catch (Exception ex) { return new CardService.Response { Code = -1, Message = ex.Message }; }
    }

    private static readonly object Lock = new();

    /// <summary>12 hex characters of an HMAC with this computer's secret: the same person, the same code; no way back to the name.</summary>
    private static string Code(string person)
    {
        byte[] key;
        if (File.Exists(SaltFile)) key = CentralConfig.ReadSecret(SaltFile, Entropy);
        else { key = RandomNumberGenerator.GetBytes(32); CentralConfig.WriteSecret(SaltFile, key, Entropy); }
        try { return Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(person.ToLowerInvariant())))[..12]; }
        finally { Array.Clear(key); }
    }

    private static void Prune()
    {
        var oldest = DateTime.Today.AddMonths(-KeepMonths).ToString("yyyy-MM");
        foreach (var file in Directory.GetFiles(Folder, "????-??.jsonl"))
            if (string.CompareOrdinal(Path.GetFileNameWithoutExtension(file), oldest) < 0) try { File.Delete(file); } catch (IOException) { }
    }

    /// <summary>The events of the months from <paramref name="from"/>, from a folder (this PC's, or the server's Stats folder with all PCs).</summary>
    internal static List<Entry> Read(string folder, DateTime from)
    {
        var list = new List<Entry>();
        if (!Directory.Exists(folder)) return list;
        var first = from.ToString("yyyy-MM");
        foreach (var file in Directory.GetFiles(folder, "*.jsonl", SearchOption.AllDirectories))
        {
            var month = Path.GetFileNameWithoutExtension(file);
            if (month.Length >= 7 && string.CompareOrdinal(month[^7..], first) < 0) continue;
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                while (reader.ReadLine() is { } line)
                {
                    if (line.Length == 0) continue;
                    try { if (JsonSerializer.Deserialize<Entry>(line) is { } e && e.Time.LocalDateTime >= from) list.Add(e); }
                    catch (JsonException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return list;
    }

    /// <summary>A table for Excel (semicolon, as Polish Excel expects).</summary>
    internal static string Csv(IEnumerable<Entry> entries)
    {
        static string Cell(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        var text = new StringBuilder("Czas;Komputer;Rodzaj;Nazwa;Kategoria;Osoba\r\n");
        foreach (var e in entries.OrderBy(e => e.Time))
            text.Append(Cell(e.Time.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"))).Append(';').Append(Cell(e.Pc)).Append(';').Append(Cell(e.Kind)).Append(';')
                .Append(Cell(e.Name)).Append(';').Append(Cell(e.Category)).Append(';').Append(Cell(e.Who)).Append("\r\n");
        return text.ToString();
    }
}
