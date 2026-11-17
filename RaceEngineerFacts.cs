using System.Globalization;
using System.Text;

namespace AiRaceEngineer;

/// <summary>Answers telemetry fact queries directly, so a small language model cannot invent measurements.</summary>
public static class RaceEngineerFacts
{
    public static string? TryAnswer(string question, TelemetrySnapshot t)
    {
        var q = Normalize(question);
        if (ContainsAny(q, "danno", "danni", "dannegg", "damage", "rotto", "rotta"))
            return Damage(t);
        if (ContainsAny(q, "gomma", "gomme", "pneumatic", "usura", "temperatura gomme", "pressione gomme"))
            return Tyres(t);
        if (ContainsAny(q, "davanti", "dietro", "rivale", "avversar", "raggiunger", "superar", "chi c e", "chi c'"))
            return Rivals(t);
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
        var affected = percent.Count(v => v >= 5f);
        var max = percent.Max();
        if (max < 5f) return "Non risultano danni rilevanti. Puoi continuare; se senti l’auto diversa, ricontrolliamo dopo un altro giro.";

        var severity = max < 15f ? "lievi" : max < 40f ? "moderati" : "importanti";
        var extent = affected == 1 ? "un’area" : $"{affected} aree";
        var advice = max < 15f
            ? "Puoi continuare, ma fai attenzione se l’auto inizia a tirare o vibrare."
            : max < 40f
                ? "Continua con prudenza; se l’auto tira, vibra o perde stabilità, rientra ai box per controllarla."
                : "Rientra ai box appena puoi: il danno può compromettere stabilità e prestazioni.";
        return $"Danni {severity}: coinvolta {extent}. {advice}";
    }

    private static string Tyres(TelemetrySnapshot t)
    {
        if (t.TyreWearEstimateWheels is not { Length: > 0 }) return "Non riesco ancora a valutare l’usura delle gomme.";
        var values = t.TyreWearEstimateWheels.Take(4).Where(float.IsFinite).ToArray();
        if (values.Length == 0) return "Non riesco ancora a valutare l’usura delle gomme.";
        var average = values.Average();
        var highest = values.Max();
        var advice = highest >= 70 ? "Le gomme sono molto consumate: programma una sosta appena possibile."
            : highest >= 40 ? "L’usura comincia a essere significativa: tieni d’occhio il grip e pianifica la sosta."
            : "Per ora puoi continuare; se perdi aderenza o i tempi peggiorano, valuta una sosta.";
        return $"Gomme {t.TyreCompound}: usura stimata media {average:0}%, massimo {highest:0}%. {advice}";
    }

    private static string Rivals(TelemetrySnapshot t)
    {
        var cars = t.FieldCars;
        if (cars is null || cars.Count == 0)
            return "Non vedo ancora i tuoi rivali. Riprova quando la classifica è aggiornata.";

        var player = cars.FirstOrDefault(x => x.Id == 0);
        if (player is null) return "Non riesco a leggere la tua posizione in classifica in questo momento.";
        var length = t.TrackLengthMeters > 0 ? t.TrackLengthMeters : 0;
        var candidates = cars.Where(x => x.Id != player.Id && (x.Flags & 1) != 0)
            .Select(x => (Car: x, Delta: ProgressDelta(x, player, length)))
            .Where(x => Math.Abs(x.Delta) > 0.5f)
            .OrderBy(x => Math.Abs(x.Delta)).ToArray();

        if (candidates.Length == 0)
            return $"Sei P{t.Position}; al momento non ho un rivale abbastanza vicino da segnalarti.";

        var ahead = candidates.FirstOrDefault(x => x.Delta > 0);
        var behind = candidates.FirstOrDefault(x => x.Delta < 0);
        var parts = new List<string>();
        if (ahead.Car is not null)
            parts.Add(Describe(ahead.Car, ahead.Delta, "davanti", t.TrackLengthMeters, t.BestLapTimeSeconds));
        if (behind.Car is not null)
            parts.Add(Describe(behind.Car, behind.Delta, "dietro", t.TrackLengthMeters, t.BestLapTimeSeconds));
        if (parts.Count == 0)
            return $"Sei P{t.Position}; non riesco a stimare il distacco in questo momento.";
        return $"Sei P{t.Position}. {string.Join(" ", parts)}";
    }

    /// <summary>Short wording for automatic end-of-lap radio calls.</summary>
    public static string GetGapBrief(TelemetrySnapshot t)
    {
        var text = Rivals(t);
        return text.StartsWith("Sei P", StringComparison.Ordinal) ? text : "Distacchi non disponibili.";
    }

    private static string Describe(FieldCar car, float delta, string direction, float trackLength, float playerBestLapSeconds)
    {
        var name = string.IsNullOrWhiteSpace(car.Driver) ? car.Car : car.Driver;
        var distance = Math.Abs(delta);
        var lapSeconds = ValidLapSeconds(car.BestLapMs) ?? ValidLapSeconds(car.LastLapMs)
            ?? (playerBestLapSeconds is >= 10 and <= 900 ? playerBestLapSeconds : null);
        var secondsText = lapSeconds is { } paceSeconds && trackLength > 0
            ? $" (circa {distance / trackLength * paceSeconds:0.0} s)"
            : "";
        var pos = car.Position > 0 ? $" in P{car.Position}" : "";
        var pit = (car.Flags & 2) != 0 ? ", in pit lane" : "";
        var lap = trackLength > 0 && distance >= trackLength ? $", {distance / trackLength:0.#} giri" : "";
        var tip = distance <= 15 && direction == "davanti"
            ? " Sei in scia: prova a preparare una buona uscita dalla curva per attaccare."
            : distance <= 15 && direction == "dietro"
                ? " È vicino: cura l’uscita dalle curve e difendi la traiettoria."
                : "";
        return $"{name}{pos} è {direction} di circa {distance:0} m{secondsText}{lap}{pit}.{tip}";
    }

    private static float? ValidLapSeconds(int ms) => ms is >= 10000 and <= 900000 ? ms / 1000f : null;

    private static float ProgressDelta(FieldCar car, FieldCar player, float length)
    {
        if (length <= 0) return car.Position < player.Position ? 1 : car.Position > player.Position ? -1 : 0;
        var delta = ((car.Laps - player.Laps) + (car.Spline - player.Spline)) * length;
        if (car.Position > 0 && player.Position > 0)
        {
            if (car.Position < player.Position && delta <= 0) delta += length;
            if (car.Position > player.Position && delta >= 0) delta -= length;
        }
        return delta;
    }

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
            var rivalText = Rivals(t);
            parts.Add(rivalText.StartsWith("Sei P", StringComparison.Ordinal) ? rivalText : $"Sei P{t.Position}.");
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
