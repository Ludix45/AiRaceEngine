using System.Globalization;
using System.Text;

namespace AiRaceEngineer;

/// <summary>Answers telemetry fact queries directly, so a small language model cannot invent measurements.</summary>
public static class RaceEngineerFacts
{
    private static readonly string[] Wheels = ["anteriore sinistra", "anteriore destra", "posteriore sinistra", "posteriore destra"];

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
        return null;
    }

    private static string Damage(TelemetrySnapshot t)
    {
        var damage = t.CarDamage ?? [];
        if (damage.Length == 0) return "Assetto Corsa non ha fornito i valori dei danni in questa sessione.";
        var values = string.Join(", ", damage.Select((v, i) => $"zona {i + 1}: {v.ToString("0.###", CultureInfo.CurrentCulture)}"));
        return $"Danni riportati da Assetto Corsa (valori grezzi, 5 zone): {values}.";
    }

    private static string Tyres(TelemetrySnapshot t)
    {
        if (t.TyreWearWheels is not { Length: > 0 }) return "Non riesco a leggere lo stato gomme dalla telemetria di questa sessione.";
        var lines = new List<string>(4);
        for (var i = 0; i < Math.Min(4, t.TyreWearWheels.Length); i++)
        {
            var health = t.TyreWearWheels[i];
            var wear = At(t.TyreWearEstimateWheels, i);
            var pressure = At(t.TyrePressure, i);
            var core = At(t.TyreCoreTempWheels, i);
            var inner = At(t.TyreInnerTemp, i);
            var middle = At(t.TyreMidTemp, i);
            var outer = At(t.TyreOuterTemp, i);
            lines.Add($"{Wheels[i]}: indice AC {health:0.000}, usura stimata media {wear:0}%" +
                (pressure > 0 ? $", {pressure:0.0} psi" : "") +
                (core > 0 ? $", nucleo {core:0}°C, I/M/E {inner:0}/{middle:0}/{outer:0}°C" : ""));
        }
        return $"{t.TyreCompound}: {string.Join("; ", lines)}. L’indice AC indica grip/stato, non una percentuale diretta di gomma consumata; l’usura è una stima rispetto al picco osservato in questa sessione.";
    }

    private static string Rivals(TelemetrySnapshot t)
    {
        var cars = t.FieldCars;
        if (cars is null || cars.Count == 0)
            return "Non ricevo ancora i dati delle altre auto. Avvia l’app Python AiRaceEngineer dalla barra delle app di Assetto Corsa; la memoria standard del gioco contiene solo la tua auto.";

        var player = cars.FirstOrDefault(x => x.Id == 0);
        if (player is null) return "Il ponte Assetto Corsa è attivo, ma non ha pubblicato la tua auto nella classifica.";
        var length = t.TrackLengthMeters > 0 ? t.TrackLengthMeters : 0;
        var candidates = cars.Where(x => x.Id != player.Id && (x.Flags & 1) != 0)
            .Select(x => (Car: x, Delta: ProgressDelta(x, player, length)))
            .Where(x => Math.Abs(x.Delta) > 0.5f)
            .OrderBy(x => Math.Abs(x.Delta)).ToArray();

        if (candidates.Length == 0)
            return $"Sei in posizione {t.Position}; non ci sono altri rivali collegati al campo telemetrico.";

        var ahead = candidates.FirstOrDefault(x => x.Delta > 0);
        var behind = candidates.FirstOrDefault(x => x.Delta < 0);
        var parts = new List<string>();
        if (ahead.Car is not null)
            parts.Add(Describe(ahead.Car, ahead.Delta, "davanti", t.Position, t.TrackLengthMeters));
        if (behind.Car is not null)
            parts.Add(Describe(behind.Car, behind.Delta, "dietro", t.Position, t.TrackLengthMeters));
        if (parts.Count == 0)
            return $"Classifica rilevata, ma non riesco a stabilire il distacco in pista. Posizione AC: {t.Position}.";
        return $"Posizione {t.Position}. {string.Join(" ", parts)} Distanze e tempi sono stime dalla posizione lungo il tracciato.";
    }

    private static string Describe(FieldCar car, float delta, string direction, int playerPosition, float trackLength)
    {
        var name = string.IsNullOrWhiteSpace(car.Driver) ? car.Car : car.Driver;
        var distance = Math.Abs(delta);
        var speed = Math.Max(1, car.SpeedMs);
        var seconds = distance / speed;
        var pos = car.Position > 0 ? $" in P{car.Position}" : "";
        var pit = (car.Flags & 2) != 0 ? ", in pit lane" : "";
        var lap = trackLength > 0 && distance >= trackLength ? $", {distance / trackLength:0.#} giri" : "";
        return $"{name}{pos} è {direction} a circa {distance:0} m ({seconds:0.0} s stimati{lap}){pit}.";
    }

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
        if (use <= 0) return $"Hai {t.FuelLitres:0.0} L. Sto ancora misurando il consumo: completa un giro per stimare carburante e sosta.";
        var estimatedLaps = t.FuelLitres / use;
        var result = $"Hai {t.FuelLitres:0.0} L; consumo medio {use:0.00} L/giro, autonomia stimata {estimatedLaps:0.0} giri";
        if (lapsLeft > 0) result += $", ne restano {lapsLeft}";
        if (t.PitWindowStart > 0 || t.PitWindowEnd > 0)
            result += $". Finestra pit AC: giri {t.PitWindowStart}–{t.PitWindowEnd}; sosta obbligatoria {(t.MandatoryPitDone != 0 ? "completata" : "non segnata come completata")}";
        return result + ".";
    }

    private static float At(float[]? values, int index) => values is not null && index < values.Length ? values[index] : 0;
    private static bool ContainsAny(string value, params string[] terms) => terms.Any(value.Contains);
    private static string Normalize(string value)
    {
        var decomposed = value.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        return new string(decomposed.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
    }
}
