namespace Kiosk.Client;

/// <summary>Names of people as the desktop shows them.</summary>
internal static class People
{
    /// <summary>
    /// The directory's name when there is one, otherwise made from the login: "INVESTA\KrzysztofLechmyc",
    /// "krzysztof.lechmyc@firma.pl" and "AzureAD\KrzysztofLechmyc" all become "Krzysztof Lechmyc".
    /// </summary>
    internal static string FriendlyName(string directoryName, params string[] accounts)
    {
        if (!string.IsNullOrWhiteSpace(directoryName) && !directoryName.Contains('\\')) return directoryName.Trim();
        foreach (var account in accounts)
        {
            if (string.IsNullOrWhiteSpace(account)) continue;
            var name = account.Trim();
            if (!name.Contains('\\') && !name.Contains('@')) return name; // Already a name (e.g. from the card).
            int slash = name.LastIndexOf('\\');
            if (slash >= 0) name = name[(slash + 1)..];
            int at = name.IndexOf('@');
            if (at > 0) name = name[..at];
            var words = new List<string>();
            var word = new System.Text.StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool split = c is '.' or '_' or '-' or ' ';
                // KrzysztofLechmyc -> Krzysztof Lechmyc
                if (!split && char.IsUpper(c) && word.Length > 0 && char.IsLower(name[i - 1])) split = true;
                if (split && word.Length > 0) { words.Add(word.ToString()); word.Clear(); }
                if (c is not ('.' or '_' or '-' or ' ')) word.Append(c);
            }
            if (word.Length > 0) words.Add(word.ToString());
            if (words.Count == 0) continue;
            return string.Join(" ", words.Select(w => w.All(char.IsLower) ? char.ToUpper(w[0]) + w[1..] : w));
        }
        return accounts.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a)) ?? "";
    }

    /// <summary>Up to two letters for the round picture: "Krzysztof Lechmyc" -> "KL".</summary>
    internal static string Initials(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => char.IsLetterOrDigit(w[0])).ToArray();
        if (words.Length == 0) return "";
        return words.Length == 1 ? words[0][..1].ToUpper() : (words[0][..1] + words[^1][..1]).ToUpper();
    }
}
