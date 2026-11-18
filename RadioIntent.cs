using System.Globalization;
using System.Text;

namespace AiRaceEngineer;

/// <summary>
/// Repairs the most common Whisper near-misses before the language model sees
/// them. This is intentionally a helper, not a command whitelist: anything it
/// cannot repair is still sent to the local model as a normal radio request.
/// </summary>
public static class RadioIntent
{
    public static string? NormalizeQuestion(string phrase)
    {
        var q = Normalize(phrase);
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return null;

        if (ContainsPhrase(q, "mi senti", "mi ascolti", "riesci a sentirmi"))
            return "mi senti";
        if (ContainsPhrase(q, "siamo pronti", "pronti per la gara", "possiamo partire", "si puo partire"))
            return "siamo pronti per la gara";

        if (words.Any(IsDamageWord) && words.Any(w => IsCarWord(w) || w == "alla" || w == "la"))
            return "che danni ha la macchina";

        var mentionsFront = words.Any(w => w.Contains("vant", StringComparison.Ordinal) || Distance(w, "davanti") <= 2);
        var asksHowFar = words.Any(w => w.StartsWith("quant", StringComparison.Ordinal) || w.StartsWith("cont", StringComparison.Ordinal) || Distance(w, "quanto") <= 2 || w.Contains("dist", StringComparison.Ordinal));
        var asksWho = words.Any(w => w.StartsWith("chi", StringComparison.Ordinal) || Distance(w, "ciao") <= 1 || Distance(w, "chiho") <= 2);
        if (mentionsFront && (asksHowFar || asksWho || words.Length <= 3))
            return "chi ho davanti e quanto dista";
        if (asksHowFar && words.Any(w => w.StartsWith("pross", StringComparison.Ordinal) || Distance(w, "prossimo") <= 2))
            return "chi ho davanti e quanto dista";

        if (words.Any(w => w.Contains("dentr", StringComparison.Ordinal) || w.Contains("dietr", StringComparison.Ordinal)))
            return "chi ho dietro e quanto dista";
        if (words.Any(w => w.StartsWith("entr", StringComparison.Ordinal) || Distance(w, "gentrato") <= 1) &&
            words.Any(w => w.StartsWith("pit", StringComparison.Ordinal) || w.StartsWith("box", StringComparison.Ordinal)))
            return "qualcuno è entrato ai box";
        // "gomme" is especially often transcribed as "gomes", "gome" or
        // two short words such as "go me". Look at individual words and the
        // final two words so a preceding sentence does not hide the request.
        var lastTwo = words.Length >= 2 ? string.Concat(words.TakeLast(2)) : string.Empty;
        var compact = string.Concat(words);
        if (words.Any(IsTyreWord) || Distance(lastTwo, "gomme") <= 2 ||
            (words.Length <= 3 && Distance(compact, "gomme") <= 2))
            return "come sono le gomme";
        if (words.Any(w => w.StartsWith("carbur", StringComparison.Ordinal) || w.StartsWith("benzin", StringComparison.Ordinal))) return "quanto carburante ho";
        if (words.Any(w => w.StartsWith("ultim", StringComparison.Ordinal)) && words.Any(w => w.StartsWith("gir", StringComparison.Ordinal))) return "come è andato l ultimo giro";
        if (words.Any(w => w.Contains("spost", StringComparison.Ordinal) || w.Contains("rispost", StringComparison.Ordinal)) && words.Any(w => w.StartsWith("brev", StringComparison.Ordinal))) return "risposte brevi";
        return null;
    }

    private static bool IsDamageWord(string value) => value.Contains("dann", StringComparison.Ordinal) || value.StartsWith("dan", StringComparison.Ordinal) || Distance(value, "danni") <= 2;
    private static bool IsCarWord(string value) => value.StartsWith("macchin", StringComparison.Ordinal) || Distance(value, "macchina") <= 2;
    private static bool IsTyreWord(string value) => value.StartsWith("gomm", StringComparison.Ordinal) || Distance(value, "gomme") <= 2;

    private static bool ContainsPhrase(string value, params string[] phrases) => phrases.Any(value.Contains);

    private static int Distance(string a, string b)
    {
        if (a == b) return 0;
        if (Math.Abs(a.Length - b.Length) > 2) return 3;
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1]; current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            previous = current;
        }
        return previous[^1];
    }

    private static string Normalize(string value)
    {
        var decomposed = value.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var plain = new string(decomposed.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
        return new string(plain.Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray());
    }
}
