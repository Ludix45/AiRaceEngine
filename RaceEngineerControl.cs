using System.Globalization;
using System.Text;

namespace AiRaceEngineer;

/// <summary>Live radio preferences which can be changed by typing or push-to-talk.</summary>
public static class RaceEngineerControl
{
    public static string? TryApply(string message, AppSettings settings, VoiceService voice)
    {
        var q = Normalize(message);
        if (Contains(q, "risposte brevi", "risposta breve", "parla poco", "meno dettagli"))
            return "Ricevuto. Ti darò solo il dato richiesto.";
        if (Contains(q, "piu veloce", "parla veloce", "aumenta velocita", "velocita maggiore"))
        {
            settings.SpeechRatePercent = Math.Min(135, settings.SpeechRatePercent + 10);
            voice.ConfigureOutput(settings.OutputVoice, settings.SpeechRatePercent, settings.UseNaturalVoice);
            settings.Save();
            return $"Ricevuto. Parlerò al {settings.SpeechRatePercent}% della velocità normale.";
        }
        if (Contains(q, "piu piano", "parla piano", "rallenta", "velocita minore"))
        {
            settings.SpeechRatePercent = Math.Max(75, settings.SpeechRatePercent - 10);
            voice.ConfigureOutput(settings.OutputVoice, settings.SpeechRatePercent, settings.UseNaturalVoice);
            settings.Save();
            return $"Ricevuto. Parlerò al {settings.SpeechRatePercent}% della velocità normale.";
        }
        if (Contains(q, "non dirmi i distacchi", "non dire i distacchi", "niente distacchi"))
        {
            settings.AnnounceGapsEachLap = false;
            settings.Save();
            return "Ricevuto. Non chiamerò più i distacchi automaticamente; potrai chiedermeli quando vuoi.";
        }
        if (Contains(q, "dimmi i distacchi", "riattiva distacchi", "voglio i distacchi"))
        {
            settings.AnnounceGapsEachLap = true;
            settings.Save();
            return "Ricevuto. Torno a chiamare i distacchi a fine giro quando sono disponibili.";
        }
        if (Contains(q, "ogni giro", "a ogni giro") && Contains(q, "carburante", "benzina"))
        {
            settings.AnnounceFuelEachLap = true;
            settings.Save();
            return "Ricevuto. A ogni giro ti aggiornerò sul carburante e sull’autonomia.";
        }
        if (Contains(q, "non parlare", "stai zitto", "silenzio") && Contains(q, "prossimo giro", "un giro"))
        {
            settings.SilentLaps = 1;
            settings.Save();
            return "Ricevuto. Rimango in silenzio per il prossimo giro, salvo avvisi importanti.";
        }
        if (Contains(q, "ingegnere off", "spegni ingegnere", "disattiva ingegnere"))
        {
            settings.RaceEngineerEnabled = false;
            settings.Save();
            return "Ingegnere di gara disattivato. Restano attivi soltanto gli avvisi importanti del gioco.";
        }
        if (Contains(q, "ingegnere on", "attiva ingegnere", "riattiva ingegnere"))
        {
            settings.RaceEngineerEnabled = true;
            settings.Save();
            return "Ingegnere di gara attivato.";
        }
        var match = System.Text.RegularExpressions.Regex.Match(q, @"(?:box|pit)[^0-9]{0,16}(?:giro )?(\d{1,3})");
        if (match.Success && int.TryParse(match.Groups[1].Value, out var lap) && lap > 0)
        {
            settings.PlannedPitLap = lap;
            settings.Save();
            return $"Piano aggiornato: punto al box al termine del giro {lap}, salvo carburante, gomme o danni richiedano una scelta diversa.";
        }
        if (Contains(q, "annulla piano box", "cancella piano box", "torna al tuo piano"))
        {
            settings.PlannedPitLap = 0;
            settings.Save();
            return "Piano box manuale cancellato. Ti consiglierò la sosta in base a carburante e gomme.";
        }
        return null;
    }

    public static string BuildLapCall(TelemetrySnapshot t, AppSettings settings)
    {
        var parts = new List<string>();
        if (t.LastLapTimeSeconds is >= 10 and <= 900)
        {
            var lap = FormatTime(t.LastLapTimeSeconds);
            if (t.BestLapTimeSeconds is >= 10 and <= 900)
            {
                var delta = t.LastLapTimeSeconds - t.BestLapTimeSeconds;
                parts.Add(delta <= 0.02f ? $"Giro {t.CompletedLaps}: {lap}, nuovo miglior giro." :
                    $"Giro {t.CompletedLaps}: {lap}, {FormatDelta(delta)} dal tuo migliore.");
            }
            else parts.Add($"Giro {t.CompletedLaps}: {lap}.");
        }

        if (settings.AnnounceFuelEachLap && t.FuelPerLap > 0)
        {
            var range = t.FuelLitres / t.FuelPerLap;
            parts.Add($"Carburante per circa {range:0.0} giri.");
        }

        if (t.TyreWearEstimateWheels is { Length: > 0 })
        {
            var wear = t.TyreWearEstimateWheels.Take(4).Where(float.IsFinite).DefaultIfEmpty().Average();
            if (wear >= 70) parts.Add("Gomme al limite: prepara la sosta.");
            else if (wear >= 45) parts.Add("Gomme in calo: pianifica il box.");
        }

        if (settings.PlannedPitLap > 0)
        {
            var toPit = settings.PlannedPitLap - t.CompletedLaps;
            if (toPit is >= 0 and <= 2) parts.Add(toPit == 0 ? "Box questo giro." : $"Box tra {toPit} giri.");
        }
        if (settings.AnnounceGapsEachLap && t.FieldCars is { Count: > 0 })
            parts.Add(RaceEngineerFacts.GetGapBrief(t));

        return parts.Count == 0 ? $"Giro {t.CompletedLaps} completato. Continua così." : string.Join(" ", parts);
    }

    private static string FormatTime(float seconds) => TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss\.fff" : @"m\:ss\.fff", CultureInfo.InvariantCulture);
    private static string FormatDelta(float seconds) => $"+{seconds:0.000} s";
    private static bool Contains(string q, params string[] terms) => terms.Any(q.Contains);
    private static string Normalize(string value)
    {
        var decomposed = value.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        return new string(decomposed.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
    }
}
