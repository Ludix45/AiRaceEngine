using System.Globalization;
using System.Text;

namespace AiRaceEngineer;

/// <summary>Answers telemetry fact queries directly, so a small language model cannot invent measurements.</summary>
public static class RaceEngineerFacts
{
    public static string? TryAnswer(string question, TelemetrySnapshot t)
    {
        var q = Normalize(question);
        if (ContainsAny(q, "mi senti", "mi ascolti", "riesci a sentirmi"))
            return "Sì, ti sento.";
        if (ContainsAny(q, "siamo pronti", "pronti per la gara", "possiamo partire", "si puo partire"))
            return t.Connected ? "Sì, telemetria collegata. Pronto per la gara." : "Sì. Avvia la sessione per collegare la telemetria.";
        if (ContainsAny(q, "danno", "danni", "dannegg", "damage", "rotto", "rotta"))
            return Damage(t);
        if (ContainsAny(q, "gomma", "gomme", "pneumatic", "usura", "temperatura gomme", "pressione gomme"))
            return Tyres(t);
        if (ContainsAny(q, "qualcuno e entrato", "chi e ai box", "chi e in pit", "in pit", "ai box") &&
            ContainsAny(q, "qualcuno", "chi", "entrato", "in pit", "ai box"))
            return PitStatus(t);
        if (ContainsAny(q, "davanti", "dietro", "rivale", "avversar", "raggiunger", "superar", "chi c e", "chi c'"))
            return Rivals(t, q);
        if (ContainsAny(q, "benzina", "carburante", "rifornimento", "pit stop", "sosta", "box"))
            return Fuel(t);
        if (ContainsAny(q, "ultimo giro", "ultima tornata", "come e andato il giro", "come e andata il giro", "ritmo", "passo gara", "pace"))
            return LastLap(t);
        if (ContainsAny(q, "dove perdo", "dove sto perdendo", "come posso migliorare", "come miglioro", "sono lento"))
            return Improvement(t);
        if (ContainsAny(q, "in generale", "riepilogo", "come va", "come sto", "come sono messo", "situazione gara", "quadro generale", "cosa devo sapere"))
            return Summary(t);
        return null;
    }

    private static string Damage(TelemetrySnapshot t)
    {
        var damage = (t.CarDamage ?? []).Take(4).Where(float.IsFinite).Select(v => Math.Max(0, v)).ToArray();
        if (damage.Length == 0) return "Non riesco a valutare i danni in questo momento.";

        // AC damage values are normalized on some versions/cars and expressed as percentages on others.
        var scale = damage.Any(v => v > 1.01f) ? 1f : 100f;
        var percent = damage.Select(v => v * scale).ToArray();
        var max = percent.Max();
        if (max < 5f) return "Nessun danno rilevante.";

        var severity = max < 15f ? "lieve" : max < 40f ? "moderato" : "importante";
        var areas = new[] { "anteriore", "posteriore", "lato sinistro", "lato destro" };
        var worst = Array.IndexOf(percent, max);
        var area = worst >= 0 && worst < areas.Length ? areas[worst] : "carrozzeria";
        var prefix = $"Danno {severity} {area}.";
        if (t.DamageReferenceLapSeconds <= 0)
            return $"{prefix} Perdita non stimabile: manca un giro pulito prima dell’urto.";
        if (t.DamageEstimateLapCount == 0)
            return $"{prefix} Perdita in calcolo: serve un giro completo dopo l’urto.";
        if (t.DamageEstimatedLossSeconds <= 0.05f)
            return $"{prefix} Nessuna perdita misurabile sui {t.DamageEstimateLapCount} giri dopo l’urto.";
        var laps = t.DamageEstimateLapCount == 1 ? "giro" : "giri";
        return $"{prefix} Perdita stimata: +{t.DamageEstimatedLossSeconds:0.0} s/giro ({t.DamageEstimateLapCount} {laps}).";
    }

    private static string Tyres(TelemetrySnapshot t)
    {
        if (t.TyreWearEstimateWheels is not { Length: > 0 }) return "Non riesco ancora a valutare l’usura delle gomme.";
        var values = t.TyreWearEstimateWheels.Take(4).Where(float.IsFinite).ToArray();
        if (values.Length == 0) return "Non riesco ancora a valutare l’usura delle gomme.";
        var average = values.Average();
        var highest = values.Max();
        return $"Gomme {t.TyreCompound}: usura {average:0}%, punto peggiore {highest:0}%.";
    }

    private static string Rivals(TelemetrySnapshot t, string? query = null)
    {
        var cars = t.FieldCars;
        if (cars is null || cars.Count == 0)
            return "Non vedo ancora i tuoi rivali. Riprova quando la classifica è aggiornata.";

        var player = cars.FirstOrDefault(x => x.Id == 0);
        if (player is null) return "Non riesco a leggere la tua posizione in classifica in questo momento.";
        var length = t.TrackLengthMeters;
        // Position is refreshed by AC only at timing lines. RealtimePosition is
        // the live ordering and changes immediately when cars swap places.
        var valid = cars.Where(x => x.Id != player.Id && (x.Flags & 1) != 0 && LiveRank(x) >= 0).ToArray();
        var playerRank = LiveRank(player);
        var ahead = valid.Where(x => LiveRank(x) < playerRank).OrderByDescending(LiveRank).FirstOrDefault();
        var behind = valid.Where(x => LiveRank(x) > playerRank).OrderBy(LiveRank).FirstOrDefault();
        var asksAhead = query?.Contains("davanti", StringComparison.Ordinal) == true;
        var asksBehind = query?.Contains("dietro", StringComparison.Ordinal) == true;
        var asksAllAhead = asksAhead && (query?.Contains("tutti", StringComparison.Ordinal) == true || query?.Contains("nomi", StringComparison.Ordinal) == true);
        var asksFirst = query?.Contains("primo", StringComparison.Ordinal) == true;
        if (asksAllAhead || asksFirst)
        {
            var names = valid.Where(x => LiveRank(x) < playerRank).OrderByDescending(LiveRank)
                .Select(x => string.IsNullOrWhiteSpace(x.Driver) ? x.Car : x.Driver).Take(12).ToArray();
            if (names.Length == 0) return "Sei già primo.";
            return asksFirst
                ? $"Per essere primo devi superare: {string.Join(", ", names)}."
                : $"Davanti hai: {string.Join(", ", names)}.";
        }
        if (asksAhead && ahead is not null) return Describe(ahead, ProgressDelta(ahead, player, length), "davanti", t);
        if (asksBehind && behind is not null) return Describe(behind, ProgressDelta(behind, player, length), "dietro", t);
        if (ahead is null && behind is null) return "Nessun rivale vicino.";
        if (ahead is not null) return Describe(ahead, ProgressDelta(ahead, player, length), "davanti", t);
        return Describe(behind!, ProgressDelta(behind!, player, length), "dietro", t);
    }

    private static string PitStatus(TelemetrySnapshot t)
    {
        var inPit = (t.FieldCars ?? []).Where(x => x.Id != 0 && ((x.Flags & 2) != 0 || (x.Flags & 4) != 0))
            .Select(x => string.IsNullOrWhiteSpace(x.Driver) ? x.Car : x.Driver).ToArray();
        return inPit.Length == 0 ? "Nessun avversario è ai box." : $"Ai box: {string.Join(", ", inPit)}.";
    }

    /// <summary>Short wording for automatic end-of-lap radio calls.</summary>
    public static string GetGapBrief(TelemetrySnapshot t)
    {
        return Rivals(t);
    }

    private static string Describe(FieldCar car, float delta, string direction, TelemetrySnapshot t)
    {
        var name = string.IsNullOrWhiteSpace(car.Driver) ? car.Car : car.Driver;
        var distance = Math.Abs(delta);
        var trackLength = t.TrackLengthMeters;
        var playerBestLapSeconds = t.BestLapTimeSeconds;
        var lapSeconds = ValidLapSeconds(car.BestLapMs) ?? ValidLapSeconds(car.LastLapMs)
            ?? (playerBestLapSeconds is >= 10 and <= 900 ? playerBestLapSeconds : null);
        var seconds = lapSeconds is { } paceSeconds && trackLength > 0
            ? distance / trackLength * paceSeconds
            : t.SpeedKmh > 15 ? distance / (t.SpeedKmh / 3.6f) : 0;
        return seconds > 0.01f
            ? $"{name} {direction}: {seconds:0.0} secondi."
            : $"{name} {direction}: {distance:0} metri.";
    }

    private static float? ValidLapSeconds(int ms) => ms is >= 10000 and <= 900000 ? ms / 1000f : null;

    private static float ProgressDelta(FieldCar car, FieldCar player, float length)
    {
        if (length <= 0) return LiveRank(car) < LiveRank(player) ? 1 : LiveRank(car) > LiveRank(player) ? -1 : 0;
        var delta = ((car.Laps - player.Laps) + (car.Spline - player.Spline)) * length;
        var carRank = LiveRank(car);
        var playerRank = LiveRank(player);
        if (carRank >= 0 && playerRank >= 0)
        {
            if (carRank < playerRank && delta <= 0) delta += length;
            if (carRank > playerRank && delta >= 0) delta -= length;
        }
        return delta;
    }

    private static int LiveRank(FieldCar car) => car.RealtimePosition >= 0 ? car.RealtimePosition : car.Position;

    private static string Fuel(TelemetrySnapshot t)
    {
        if (!t.Connected) return "La telemetria di Assetto Corsa non è collegata.";
        var lapsLeft = t.NumberOfLaps > 0 ? Math.Max(0, t.NumberOfLaps - t.CompletedLaps) : 0;
        var use = t.FuelPerLap;
        if (use <= 0) return "Ho bisogno di un giro completo per stimare l’autonomia del carburante.";
        var estimatedLaps = t.FuelLitres / use;
        var result = $"Carburante: {estimatedLaps:0.0} giri di autonomia stimata";
        if (lapsLeft > 0)
        {
            var margin = estimatedLaps - lapsLeft;
            result += $", te ne restano {lapsLeft}";
            result += margin < 0 ? $": mancano circa {Math.Abs(margin):0.0} giri di carburante. Valuta il box o riduci il consumo."
                : margin < 1 ? ": margine ridotto, risparmia carburante e pianifica il box."
                : $", margine di circa {margin:0.0} giri.";
        }
        if (t.PitWindowStart > 0 || t.PitWindowEnd > 0)
            result += $". Finestra pit: giri {t.PitWindowStart}–{t.PitWindowEnd}; sosta obbligatoria {(t.MandatoryPitDone != 0 ? "completata" : "ancora da fare")}";
        return result + ".";
    }

    private static string LastLap(TelemetrySnapshot t)
    {
        if (t.LastLapTimeSeconds is < 10 or > 900)
            return "Non ho ancora un giro completo valido da analizzare.";
        var last = FormatTime(t.LastLapTimeSeconds);
        if (t.BestLapTimeSeconds is < 10 or > 900)
            return $"Ultimo giro: {last}. È il primo riferimento valido della sessione.";
        var delta = t.LastLapTimeSeconds - t.BestLapTimeSeconds;
        if (delta <= 0.02f) return $"Ultimo giro: {last}, nuovo miglior giro. Mantieni questo ritmo.";
        if (delta < 0.35f) return $"Ultimo giro: {last}, solo {delta:0.000} s dal tuo migliore. Cerca un giro pulito: il passo c’è.";
        if (delta < 1.5f) return $"Ultimo giro: {last}, +{delta:0.000} s dal tuo migliore. Punta a pulire frenata e uscita dalle curve: non serve forzare.";
        return $"Ultimo giro: {last}, +{delta:0.000} s dal tuo migliore. Prima di spingere, controlla gomme e traffico: il calo è significativo.";
    }

    private static string Improvement(TelemetrySnapshot t)
    {
        if (t.LastLapTimeSeconds is < 10 or > 900 || t.BestLapTimeSeconds is < 10 or > 900)
            return "Completa almeno due giri puliti: confronterò l’ultimo con il tuo riferimento per darti un consiglio utile.";
        var delta = t.LastLapTimeSeconds - t.BestLapTimeSeconds;
        if (delta <= 0.15f)
            return "Sei già vicino al tuo riferimento. Non cercare un punto di frenata più tardi ovunque: concentra la guida su un’uscita pulita dalle curve lente.";
        if (t.TyreWearPercent >= 45)
            return $"Sei a +{delta:0.000} s dal tuo migliore e le gomme stanno calando. Gestisci le uscite e prepara la sosta: forzare ora ti farà perdere più tempo.";
        return $"Sei a +{delta:0.000} s dal tuo migliore. Fai un giro pulito seguendo i bip di frenata e riapertura: il coach usa il tuo miglior giro come riferimento.";
    }

    private static string Summary(TelemetrySnapshot t)
    {
        if (!t.Connected) return "Non ho ancora i dati della sessione. Avvia Assetto Corsa e torna in pista, poi chiedimi di nuovo il riepilogo.";
        var parts = new List<string>();
        if (t.Position > 0)
        {
            parts.Add($"Sei P{t.Position}. {Rivals(t)}");
        }

        if (t.TyreWearEstimateWheels is { Length: > 0 })
        {
            var wear = t.TyreWearEstimateWheels.Where(float.IsFinite).ToArray();
            if (wear.Length > 0)
            {
                var average = wear.Average();
                var tyreAdvice = average >= 70 ? "valuta il pit stop" : average >= 40 ? "inizia a pianificare il pit stop" : "gomme ancora gestibili";
                parts.Add($"Gomme: usura stimata {average:0}%, {tyreAdvice}.");
            }
        }

        if (t.FuelPerLap > 0)
        {
            var laps = t.FuelLitres / t.FuelPerLap;
            var remaining = t.NumberOfLaps > 0 ? Math.Max(0, t.NumberOfLaps - t.CompletedLaps) : 0;
            parts.Add(remaining > 0 && laps < remaining
                ? $"Carburante: autonomia per {laps:0.0} giri, inferiore ai {remaining} rimanenti; risparmia o rientra ai box."
                : $"Carburante: circa {laps:0.0} giri di autonomia.");
        }

        if (t.PenaltyTime > 0.1f) parts.Add($"Hai una penalità di {t.PenaltyTime:0.#} secondi: scontala secondo le regole della sessione.");
        if (!string.Equals(t.FlagText, "NESSUNA", StringComparison.OrdinalIgnoreCase)) parts.Add($"Attenzione: bandiera {t.FlagText.ToLowerInvariant()}.");
        if (t.LastLapTimeSeconds > 0 && t.BestLapTimeSeconds > 0)
        {
            var delta = t.LastLapTimeSeconds - t.BestLapTimeSeconds;
            if (delta > Math.Max(1.5f, t.BestLapTimeSeconds * 0.025f))
                parts.Add($"L’ultimo giro è {delta:0.0} s più lento del tuo migliore: controlla errori o usura gomme.");
        }
        if (parts.Count == 0) return "Sei in pista, ma per ora non ho abbastanza dati per un consiglio utile. Fai un giro e richiedi un nuovo riepilogo.";
        return string.Join(" ", parts);
    }

    private static string FormatTime(float seconds) => TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss\.fff" : @"m\:ss\.fff", CultureInfo.InvariantCulture);

    private static bool ContainsAny(string value, params string[] terms) => terms.Any(value.Contains);
    private static string Normalize(string value)
    {
        var decomposed = value.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        return new string(decomposed.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
    }
}
