using AssettoCorsaSharedMemory;

namespace AiRaceEngineer;

public sealed class AssettoTelemetryService : IDisposable
{
    private readonly AssettoCorsa _ac = new();
    private readonly AcFieldService _field = new();
    private readonly object _gate = new();
    private Physics _physics;
    private Graphics _graphics;
    private StaticInfo _static;
    private bool _hasPhysics, _hasGraphics, _hasStatic;
    private int _lastLap = -1;
    private float _lastFuel = float.NaN, _lastWear = float.NaN;
    private float _previousDamageTotal;
    private float _fuelPerLap, _wearPerLap;
    private readonly float[] _peakTyreHealth = new float[4];

    public TelemetrySnapshot Current { get; private set; } = new();
    public event Action<TelemetrySnapshot>? Updated;
    public event Action<RaceEvent>? RaceEventDetected;

    public AssettoTelemetryService()
    {
        _ac.PhysicsInterval = 50;
        _ac.GraphicsInterval = 250;
        _ac.StaticInfoInterval = 1500;
        _ac.PhysicsUpdated += (_, e) => { lock (_gate) { _physics = e.Physics; _hasPhysics = true; Publish(); } };
        _ac.GraphicsUpdated += (_, e) => { lock (_gate) { _graphics = e.Graphics; _hasGraphics = true; Publish(); } };
        _ac.StaticInfoUpdated += (_, e) => { lock (_gate) { _static = e.StaticInfo; _hasStatic = true; Publish(); } };
        _ac.Start();
    }

    private void Publish()
    {
        if (!_hasPhysics || !_hasGraphics) return;
        var p = _physics;
        var g = _graphics;
        var wearValues = p.TyreWear ?? Array.Empty<float>();
        var tempValues = p.TyreCoreTemperature ?? Array.Empty<float>();
        var damageValues = p.CarDamage ?? Array.Empty<float>();
        var wearByWheel = EstimateTyreWearPercent(wearValues);
        var wear = wearByWheel.Length == 0 ? 0 : wearByWheel.Average();
        var temp = tempValues.Length == 0 ? 0f : tempValues.Average();
        var currentLap = Math.Max(0, g.CompletedLaps);

        if (_lastLap < 0)
        {
            _lastLap = currentLap;
            _lastFuel = p.Fuel;
            _lastWear = wear;
        }
        else if (currentLap > _lastLap)
        {
            var lapDelta = currentLap - _lastLap;
            if (!float.IsNaN(_lastFuel) && p.Fuel <= _lastFuel && lapDelta > 0)
                _fuelPerLap = (_lastFuel - p.Fuel) / lapDelta;
            if (!float.IsNaN(_lastWear) && wear >= _lastWear && lapDelta > 0)
                _wearPerLap = (wear - _lastWear) / lapDelta;
            _lastLap = currentLap;
            _lastFuel = p.Fuel;
            _lastWear = wear;
        }

        var flag = (int)g.Flag;
        var flagText = flag switch
        {
            1 => "BLU", 2 => "GIALLA", 3 => "NERA", 4 => "BIANCA",
            5 => "A SCACCHI", 6 => "PENALITÀ", _ => "NESSUNA"
        };
        var next = new TelemetrySnapshot(
            Connected: true, SpeedKmh: p.SpeedKmh, FuelLitres: p.Fuel,
            Gear: p.Gear, Rpm: p.Rpms, CompletedLaps: g.CompletedLaps,
            Position: g.Position, NumberOfLaps: g.NumberOfLaps, Session: (int)g.Session,
            Status: (int)g.Status, Flag: flag, PenaltyTime: g.PenaltyTime,
            InPit: g.IsInPit != 0, InPitLane: g.IsInPitLane != 0,
            Car: _hasStatic ? _static.CarModel ?? "" : "",
            Track: _hasStatic ? _static.Track ?? "" : "",
            TyreCompound: g.TyreCompound ?? "", TyreWearPercent: wear, TyreTempC: temp,
            Gas: p.Gas, Brake: p.Brake, NormalizedCarPosition: g.NormalizedCarPosition,
            TrackLengthMeters: _hasStatic ? _static.TrackSPlineLength : 0,
            CurrentLapTimeSeconds: g.iCurrentTime / 1000f,
            LastLapTimeSeconds: g.iLastTime / 1000f, BestLapTimeSeconds: g.iBestTime / 1000f,
            SessionTimeLeftSeconds: g.SessionTimeLeft, FuelPerLap: _fuelPerLap,
            WearPerLapPercent: _wearPerLap, FlagText: flagText, Timestamp: DateTime.Now,
            SteeringAngle: p.SteerAngle, Clutch: p.Clutch, TC: p.TC, ABS: p.Abs, Drs: p.Drs,
            TyreWearWheels: wearValues, TyreDirtyLevel: p.TyreDirtyLevel ?? [],
            TyreWearEstimateWheels: wearByWheel,
            TyrePressure: p.WheelsPressure ?? [], TyreInnerTemp: p.TyreTempI ?? [],
            TyreMidTemp: p.TyreTempM ?? [], TyreOuterTemp: p.TyreTempO ?? [],
            TyreCoreTempWheels: tempValues,
            BrakeTemp: p.BrakeTemp ?? [], WheelSlip: p.WheelSlip ?? [], WheelLoad: p.WheelLoad ?? [],
            SuspensionTravel: p.SuspensionTravel ?? [], CarDamage: damageValues,
            AccG: p.AccG ?? [], LocalVelocity: p.LocalVelocity ?? [],
            AirTempC: p.AirTemp, RoadTempC: p.RoadTemp, SurfaceGrip: g.SurfaceGrip,
            FuelRateMultiplier: _hasStatic ? _static.AidFuelRate : 0,
            TyreWearMultiplier: _hasStatic ? _static.AidTireRate : 0,
            DamageMultiplier: _hasStatic ? _static.AidMechanicalDamage : 0,
            MandatoryPitDone: g.MandatoryPitDone,
            PitWindowStart: _hasStatic ? _static.PitWindowStart : 0,
            PitWindowEnd: _hasStatic ? _static.PitWindowEnd : 0,
            FieldCars: _field.Current.ReceivedAt > DateTime.Now.AddSeconds(-1) ? _field.Current.Cars : []);

        var previous = Current;
        Current = next;
        var damageTotal = damageValues.Sum();
        foreach (var ev in RaceEventDetector.Compare(previous, next, _previousDamageTotal, damageTotal))
            RaceEventDetected?.Invoke(ev);
        _previousDamageTotal = damageTotal;
        Updated?.Invoke(next);
    }

    // AC tyreWear is a grip/health signal: it can rise during warm-up and decline with use.
    // Estimate degradation from each wheel's observed peak, rather than calling raw health "wear".
    private float[] EstimateTyreWearPercent(float[] values)
    {
        if (values.Length == 0) return [];
        var estimates = new List<float>(Math.Min(values.Length, _peakTyreHealth.Length));
        for (var i = 0; i < Math.Min(values.Length, _peakTyreHealth.Length); i++)
        {
            var health = values[i];
            if (health > 1.01f) health /= 100f;
            if (!float.IsFinite(health) || health <= 0 || health > 1.2f) continue;
            _peakTyreHealth[i] = Math.Max(_peakTyreHealth[i], health);
            estimates.Add(Math.Clamp((_peakTyreHealth[i] - health) / 0.06f * 100f, 0f, 100f));
        }
        return estimates.ToArray();
    }

    public void Dispose() { _ac.Dispose(); _field.Dispose(); }
}

internal static class RaceEventDetector
{
    public static IReadOnlyList<RaceEvent> Compare(TelemetrySnapshot old, TelemetrySnapshot now, float previousDamage, float currentDamage)
    {
        var events = new List<RaceEvent>();
        if (!old.Connected) return events;
        if (old.Flag != now.Flag && now.Flag != 0)
            events.Add(new(DateTime.Now, "BANDIERA", $"Bandiera {now.FlagText.ToLowerInvariant()} segnalata da Assetto Corsa."));
        else if (old.Flag != 0 && now.Flag == 0)
            events.Add(new(DateTime.Now, "BANDIERA", "Assetto Corsa ha revocato la bandiera; lo stato della pista non è verificabile dai dati condivisi."));
        if ((old.PenaltyTime <= 0.1f && now.PenaltyTime > 0.1f) || (now.Flag == 6 && old.Flag != 6))
            events.Add(new(DateTime.Now, "PENALITÀ", now.PenaltyTime > 0 ? $"Penalità AC: {now.PenaltyTime:0.#} secondi." : "Penalità segnalata da Assetto Corsa."));
        if (!old.InPitLane && now.InPitLane) events.Add(new(DateTime.Now, "PIT", "Ingresso in pit lane rilevato."));
        if (!old.InPit && now.InPit) events.Add(new(DateTime.Now, "PIT", "Auto ferma nella piazzola: sosta ai box rilevata."));
        if (old.InPit && !now.InPit) events.Add(new(DateTime.Now, "PIT", "Uscita dalla piazzola rilevata; sosta registrata."));
        if (old.CompletedLaps != now.CompletedLaps && now.CompletedLaps > old.CompletedLaps)
            events.Add(new(DateTime.Now, "GIRO", $"Giro {now.CompletedLaps} completato. Consumo stimato {now.FuelPerLap:0.00} L/giro."));
        if (now.SpeedKmh < old.SpeedKmh - 65 && currentDamage > previousDamage + 0.05f)
            events.Add(new(DateTime.Now, "INCIDENTE", "Possibile contatto o uscita: forte calo di velocità e danno rilevato. Verifica la vettura."));
        return events;
    }
}
