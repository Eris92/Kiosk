using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

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
    private const int MaxKindLength = 40, MaxNameLength = 300, MaxCategoryLength = 300, MaxPersonLength = 2048;
    private static readonly StatisticsQueue Sender = new(
        json => CardService.Call(new CardService.Request { Op = "stat", Source = json }), Audit.Write, 128);

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

    /// <summary>Kiosk side: queues the event for one background sender without blocking the screen.</summary>
    internal static void Record(string kind, string name, string category = "", string person = "")
    {
        var json = JsonSerializer.Serialize(Prepare(kind, name, category, person));
        Sender.Enqueue(json);
    }

    private static Entry Prepare(string kind, string name, string category, string person)
    {
        static string Limit(string value, int length)
        {
            value = value.Trim();
            return value.Length <= length ? value : value[..length];
        }
        return new Entry
        {
            Kind = Limit(kind, MaxKindLength), Name = Limit(name, MaxNameLength),
            Category = Limit(category, MaxCategoryLength), Who = Limit(person, MaxPersonLength)
        };
    }

#if KIOSK_SELF_TEST
    internal static void SelfTestQueue()
    {
        var bounded = Prepare(new string('k', 100), new string('n', 500), new string('c', 500), new string('p', 3000));
        if (bounded.Kind.Length != MaxKindLength || bounded.Name.Length != MaxNameLength ||
            bounded.Category.Length != MaxCategoryLength || bounded.Who.Length != MaxPersonLength)
            throw new InvalidOperationException("Statistics event exceeded its queue size limits.");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var sent = new List<string>();
        var reports = new List<(string Action, string Detail)>();
        var queue = new StatisticsQueue(json =>
        {
            if (json == "first") { entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); }
            sent.Add(json);
            return new CardService.Response();
        }, (action, detail) => reports.Add((action, detail)), 3);
        queue.Enqueue("first");
        if (!entered.Wait(TimeSpan.FromSeconds(5)))
        {
            release.Set();
            throw new InvalidOperationException("Statistics sender did not start.");
        }
        for (var i = 0; i < 5; i++) queue.Enqueue(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        release.Set();
        if (!queue.CompleteForTest().Wait(TimeSpan.FromSeconds(5)) ||
            !sent.SequenceEqual(["first", "0", "1", "2"]) ||
            !reports.Contains(("stats_dropped", "2")))
            throw new InvalidOperationException("Statistics queue lost ordering or exceeded its capacity.");
    }
#endif

    /// <summary>Kiosk Card service, "stat": one line in this month's file; the person's account becomes their code here.</summary>
    internal static CardService.Response Append(string json)
    {
        try
        {
            var entry = JsonSerializer.Deserialize<Entry>(json) ?? throw new InvalidDataException("Puste zdarzenie.");
            if (entry.Kind.Length == 0 || entry.Kind.Length > MaxKindLength || entry.Name.Length > MaxNameLength ||
                entry.Category.Length > MaxCategoryLength || entry.Who.Length > MaxPersonLength)
                throw new InvalidDataException("Nieprawidłowe zdarzenie.");
            Directory.CreateDirectory(Folder);
            lock (Lock)
            {
                entry = entry with { Time = DateTimeOffset.Now, Pc = Environment.MachineName, Who = entry.Who.Length > 0 ? Code(entry.Who) : "" };
                File.AppendAllText(Path.Combine(Folder, entry.Time.ToString("yyyy-MM") + ".jsonl"), JsonSerializer.Serialize(entry) + Environment.NewLine);
                PruneIfDue(entry.Time.LocalDateTime);
            }
            return new CardService.Response();
        }
        catch (Exception ex) { return new CardService.Response { Code = -1, Message = ex.Message }; }
    }

    private static readonly object Lock = new();
    private static DateOnly lastPrunedOn;

    /// <summary>12 hex characters of an HMAC with this computer's secret: the same person, the same code; no way back to the name.</summary>
    private static string Code(string person)
    {
        var exists = File.Exists(SaltFile);
        var key = exists ? CentralConfig.ReadSecret(SaltFile, Entropy) : RandomNumberGenerator.GetBytes(32);
        try
        {
            if (!exists) CentralConfig.WriteSecret(SaltFile, key, Entropy);
            return Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(person.ToLowerInvariant())))[..12];
        }
        finally { Array.Clear(key); }
    }

    private static void PruneIfDue(DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        if (lastPrunedOn == today) return;
        lastPrunedOn = today;
        try
        {
            var oldest = today.AddMonths(-KeepMonths).ToString("yyyy-MM");
            foreach (var file in Directory.EnumerateFiles(Folder, "????-??.jsonl"))
            {
                if (string.CompareOrdinal(Path.GetFileNameWithoutExtension(file), oldest) >= 0) continue;
                try { File.Delete(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Audit.Write("stats_prune_failed", ex.Message); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Audit.Write("stats_prune_failed", ex.Message); }
    }

    /// <summary>The events of the months from <paramref name="from"/>, from this PC's folder or the server's one-level per-PC folders.</summary>
    internal static List<Entry> Read(string folder, DateTime from, CancellationToken cancellation = default)
    {
        var list = new List<Entry>();
        cancellation.ThrowIfCancellationRequested();
        if (!Directory.Exists(folder)) return list;
        var first = from.ToString("yyyy-MM");
        var options = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        void ReadFolder(string directory)
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.jsonl", options))
            {
                cancellation.ThrowIfCancellationRequested();
                var month = Path.GetFileNameWithoutExtension(file);
                if (month.Length >= 7 && string.CompareOrdinal(month[^7..], first) < 0) continue;
                list.AddRange(ReadFile(file, cancellation).Where(e => e.Time.LocalDateTime >= from));
            }
        }
        ReadFolder(folder);
        foreach (var computer in Directory.EnumerateDirectories(folder, "*", options))
        {
            cancellation.ThrowIfCancellationRequested();
            ReadFolder(computer);
        }
        return list;
    }

    /// <summary>One month's file (unreadable lines and files are skipped).</summary>
    internal static List<Entry> ReadFile(string file, CancellationToken cancellation = default)
        => FileEntries(file, cancellation).ToList();

    private static IEnumerable<Entry> FileEntries(string file, CancellationToken cancellation, bool tolerateReadErrors = true)
    {
        cancellation.ThrowIfCancellationRequested();
        FileStream? stream = null;
        try { stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); }
        catch (IOException) { if (!tolerateReadErrors) throw; }
        catch (UnauthorizedAccessException) { if (!tolerateReadErrors) throw; }
        if (stream == null) yield break;
        using (stream)
        using (var reader = new StreamReader(stream))
        {
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                string? line = null;
                bool failed = false;
                try { line = reader.ReadLine(); }
                catch (IOException) { if (!tolerateReadErrors) throw; failed = true; }
                catch (UnauthorizedAccessException) { if (!tolerateReadErrors) throw; failed = true; }
                if (failed || line == null) break;
                if (line.Length == 0) continue;
                Entry? entry = null;
                try { entry = JsonSerializer.Deserialize<Entry>(line); }
                catch (JsonException) { }
                if (entry != null) yield return entry;
            }
        }
    }

    // ------------------------------------------------------------------ the export folder's account

    private static readonly byte[] AccountEntropy = Encoding.UTF8.GetBytes("Kiosk.StatsAccount");
    internal static string AccountFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Kiosk", "StatsAccount.cred");
    internal static bool AccountSaved => File.Exists(AccountFile);

    /// <summary>Saves (or with an empty login removes) the account for the export folder; needs an administrator.</summary>
    internal static void WriteAccount(CentralConfig.AccountChange change)
    {
        if (change.User.Trim().Length == 0) { if (File.Exists(AccountFile)) File.Delete(AccountFile); Audit.Write("stats_account_removed"); return; }
        var plain = JsonSerializer.SerializeToUtf8Bytes(new SharedAccounts.Account(change.User.Trim(), change.Password));
        try { CentralConfig.WriteSecret(AccountFile, plain, AccountEntropy); }
        finally { Array.Clear(plain); }
        Audit.Write("stats_account_saved", change.User.Trim());
    }

    /// <summary>Konto Kiosku replaces this older separate account.</summary>
    internal static void RemoveAccount()
    {
        if (!AccountSaved) return;
        File.Delete(AccountFile);
        Audit.Write("stats_account_removed", "zastąpione Kontem Kiosku");
    }

    /// <summary>Only the service (SYSTEM) and administrators can read it; null = none saved.</summary>
    internal static SharedAccounts.Account? ReadAccount()
    {
        if (!AccountSaved) return null;
        var plain = CentralConfig.ReadSecret(AccountFile, AccountEntropy);
        try { return JsonSerializer.Deserialize<SharedAccounts.Account>(plain); }
        finally { Array.Clear(plain); }
    }

    private const string CsvHeader = "Czas;Komputer;Rodzaj;Nazwa;Kategoria;Osoba\r\n";

    private static string CsvCell(string s)
    {
        // Quotes delimit CSV fields, but Excel still evaluates a field whose first visible character is a formula marker.
        // A leading tab inside the quoted field keeps it as text when the CSV is opened in Excel.
        var first = 0;
        while (first < s.Length && (char.IsWhiteSpace(s[first]) || char.IsControl(s[first]) || s[first] == '\uFEFF')) first++;
        if ((s.Length > 0 && s[0] is '\t' or '\r' or '\n' or '\0') ||
            (first < s.Length && s[first] is '=' or '+' or '-' or '@' or '＝' or '＋' or '－' or '＠')) s = "\t" + s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    private static void WriteCsvRow(TextWriter output, Entry e)
    {
        output.Write(CsvCell(e.Time.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss")));
        output.Write(';'); output.Write(CsvCell(e.Pc));
        output.Write(';'); output.Write(CsvCell(e.Kind));
        output.Write(';'); output.Write(CsvCell(e.Name));
        output.Write(';'); output.Write(CsvCell(e.Category));
        output.Write(';'); output.Write(CsvCell(e.Who));
        output.Write("\r\n");
    }

    /// <summary>A table for Excel (semicolon, as Polish Excel expects).</summary>
    internal static string Csv(IEnumerable<Entry> entries)
    {
        using var output = new StringWriter();
        output.Write(CsvHeader);
        foreach (var entry in entries.OrderBy(e => e.Time)) WriteCsvRow(output, entry);
        return output.ToString();
    }

    /// <summary>Export one service log in append order, without retaining the month's entries or CSV in memory.</summary>
    internal static void WriteCsvFile(string file, TextWriter output)
    {
        output.Write(CsvHeader);
        foreach (var entry in FileEntries(file, CancellationToken.None, tolerateReadErrors: false)) WriteCsvRow(output, entry);
    }
}

/// <summary>Best-effort statistics sender. A failed service cannot accumulate unbounded client tasks.</summary>
internal sealed class StatisticsQueue
{
    private readonly Channel<string> pending;
    private readonly Func<string, CardService.Response> send;
    private readonly Action<string, string> log;
    private readonly Lazy<Task> worker;
    private long dropped;

    internal StatisticsQueue(Func<string, CardService.Response> send, Action<string, string> log, int capacity)
    {
        this.send = send;
        this.log = log;
        pending = Channel.CreateBounded<string>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait
        });
        worker = new Lazy<Task>(() => Task.Run(DrainAsync));
    }

    internal void Enqueue(string json)
    {
        if (!pending.Writer.TryWrite(json)) { Interlocked.Increment(ref dropped); return; }
        _ = worker.Value;
    }

    private async Task DrainAsync()
    {
        await foreach (var json in pending.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                var answer = send(json);
                if (answer.Code != 0) SafeLog("stats_failed", answer.Message);
            }
            catch (Exception ex) { SafeLog("stats_failed", ex.Message); }
            var lost = Interlocked.Exchange(ref dropped, 0);
            if (lost > 0) SafeLog("stats_dropped", lost.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private void SafeLog(string action, string detail)
    {
        try { log(action, detail); }
        catch (Exception) { } // Statistics must never stop the kiosk or its background sender.
    }

#if KIOSK_SELF_TEST
    internal Task CompleteForTest()
    {
        pending.Writer.TryComplete();
        return worker.IsValueCreated ? worker.Value : Task.CompletedTask;
    }
#endif
}
