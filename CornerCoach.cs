using System.IO;
using System.Text.Json;

namespace AiRaceEngineer;

public enum CornerCueType { Brake, Throttle }
public sealed record CornerCoachUpdate(IReadOnlyList<CornerCueType> Cues, string? Status);

/// <summary>Learns brake and throttle marker positions from the driver's own completed laps.</summary>
public sealed class CornerCoach
{
    private sealed class TrackMap
    {
        public List<float> Brake { get; set; } = [];
        public List<float> Throttle { get; set; } = [];
        public float LapSeconds { get; set; }
    }

    private static readonly string MapFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AiRaceEngineer", "corner-coach.json");
    private readonly object _gate = new();
    private Dictionary<string, TrackMap> _maps = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<float> _lapBrake = [];
    private readonly List<float> _lapThrottle = [];
    private readonly HashSet<string> _cuedThisLap = new(StringComparer.Ordinal);
    private string _key = "";
    private TrackMap _reference = new();
    private int _lastLap = -1;
    private int _cueLap = -1;
    private bool _wasBraking;
    private bool _accelerating;
    private bool _brakedForCorner;
    private float _previousPosition = -1;
    private string? _lastStatus;

    public CornerCoachUpdate Update(TelemetrySnapshot t, bool brakeBeeps, bool throttleBeeps, int leadMilliseconds)
    {
        lock (_gate)
        {
            if (!t.Connected || t.InPitLane || t.InPit || string.IsNullOrWhiteSpace(t.Car) || string.IsNullOrWhiteSpace(t.Track))
                return new([], null);

            var newKey = $"{t.Car.Trim()}|{t.Track.Trim()}";
            if (!string.Equals(newKey, _key, StringComparison.OrdinalIgnoreCase)) LoadTrack(newKey);

            var position = Math.Clamp(t.NormalizedCarPosition, 0f, 0.99999f);
            if (_lastLap < 0) _lastLap = t.CompletedLaps;
            else if (t.CompletedLaps > _lastLap)
            {
                SaveCompletedLap(t.LastLapTimeSeconds);
                _lastLap = t.CompletedLaps;
                _lapBrake.Clear();
                _lapThrottle.Clear();
                _brakedForCorner = false;
                _cuedThisLap.Clear();
                _previousPosition = -1;
            }
            if (_cueLap != t.CompletedLaps)
            {
                _cueLap = t.CompletedLaps;
                _cuedThisLap.Clear();
            }

            var brake = t.Brake > 0.18f && t.SpeedKmh > 45;
            var throttle = t.Gas > 0.78f && t.Brake < 0.06f && t.SpeedKmh > 25;
            if (brake && !_wasBraking)
            {
                AddMarker(_lapBrake, position);
                _brakedForCorner = true;
            }
            if (throttle && !_accelerating && _brakedForCorner)
            {
                AddMarker(_lapThrottle, position);
                _brakedForCorner = false;
            }
            _wasBraking = brake;
            _accelerating = throttle;

            var cues = new List<CornerCueType>();
            if (_reference.Brake.Count + _reference.Throttle.Count > 0 && t.TrackLengthMeters > 0 && t.SpeedKmh > 30)
            {
                var leadMeters = Math.Max(0, t.SpeedKmh / 3.6f * Math.Clamp(leadMilliseconds, 0, 1500) / 1000f);
                if (brakeBeeps)
                    AddCues(_reference.Brake, "B", CornerCueType.Brake, position, t.TrackLengthMeters, leadMeters, cues);
                if (throttleBeeps)
                    AddCues(_reference.Throttle, "T", CornerCueType.Throttle, position, t.TrackLengthMeters, leadMeters, cues);
            }

            var advance = leadMilliseconds <= 0 ? AppLanguage.IsEnglish ? "at each marker" : "sui punti"
                : AppLanguage.IsEnglish ? $"{leadMilliseconds / 1000d:0.##} s before each marker" : $"{leadMilliseconds / 1000d:0.##} s prima dei punti";
            var status = _reference.Brake.Count + _reference.Throttle.Count > 0
                ? AppLanguage.IsEnglish
                    ? $"Coach active · personal reference {FormatLap(_reference.LapSeconds)} · {_reference.Brake.Count} braking and {_reference.Throttle.Count} throttle markers. Beeps {advance}."
                    : $"Coach attivo · riferimento personale {FormatLap(_reference.LapSeconds)} · {_reference.Brake.Count} frenate e {_reference.Throttle.Count} riaperture. Bip {advance} del tuo giro migliore."
                : AppLanguage.IsEnglish
                    ? $"Corner coach learning · complete a lap at {t.Track}: braking and throttle markers will be saved."
                    : $"Coach curva in apprendimento · completa un giro su {t.Track}: memorizzerò i tuoi punti di frenata e riapertura del gas.";
            var changed = !string.Equals(status, _lastStatus, StringComparison.Ordinal);
            _lastStatus = status;
            _previousPosition = position;
            return new(cues, changed ? status : null);
        }
    }

    private void LoadTrack(string key)
    {
        _key = key;
        try
        {
            if (_maps.Count == 0 && File.Exists(MapFile))
                _maps = JsonSerializer.Deserialize<Dictionary<string, TrackMap>>(File.ReadAllText(MapFile))
                    ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch { _maps = new(StringComparer.OrdinalIgnoreCase); }
        _reference = _maps.TryGetValue(key, out var map) ? map : new TrackMap();
        _lapBrake.Clear(); _lapThrottle.Clear(); _cuedThisLap.Clear();
        _lastLap = -1; _previousPosition = -1; _wasBraking = false; _accelerating = false;
        _brakedForCorner = false; _lastStatus = null;
    }

    private void SaveCompletedLap(float lapSeconds)
    {
        if (_lapBrake.Count == 0 && _lapThrottle.Count == 0) return;
        if (lapSeconds is < 10 or > 900) return;
        if (_reference.LapSeconds is >= 10 and <= 900 && lapSeconds >= _reference.LapSeconds - 0.02f) return;
        _reference = new TrackMap
        {
            Brake = _lapBrake.Distinct().Order().ToList(),
            Throttle = _lapThrottle.Distinct().Order().ToList(),
            LapSeconds = lapSeconds
        };
        _maps[_key] = _reference;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MapFile)!);
            File.WriteAllText(MapFile, JsonSerializer.Serialize(_maps, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
        _lastStatus = null;
    }

    private void AddCues(List<float> markers, string prefix, CornerCueType type, float position,
        float trackLength, float leadMeters, List<CornerCueType> cues)
    {
        for (var i = 0; i < markers.Count; i++)
        {
            var marker = markers[i];
            var distance = (marker - position + 1f) % 1f * trackLength;
            var id = $"{prefix}{i}";
            // At an exact marker a frame cannot have exactly zero distance;
            // a tight 3 m window produces the cue on that point in practice.
            var triggerDistance = Math.Max(leadMeters, 3f);
            if (distance <= triggerDistance && distance > 0.4f && _cuedThisLap.Add(id)) cues.Add(type);
        }
    }

    private static void AddMarker(List<float> markers, float position)
    {
        if (markers.Count == 0 || Math.Abs(markers[^1] - position) > 0.003f) markers.Add(position);
    }

    private static string FormatLap(float seconds) => seconds is >= 10 and <= 900
        ? TimeSpan.FromSeconds(seconds).ToString(@"m\:ss\.fff")
        : "in apprendimento";
}
