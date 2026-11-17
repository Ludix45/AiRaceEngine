using System.IO;
using System.Text.Json;

namespace AiRaceEngineer;

public sealed class AppSettings
{
    public string OllamaUrl { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "qwen2.5:3b";
    public bool ModelSelectionExplicit { get; set; }
    public string PushToTalkKey { get; set; } = "F8";
    public bool XboxMenuPushToTalk { get; set; } = true;
    public bool SpeakReplies { get; set; } = true;
    public bool RaceEngineerEnabled { get; set; } = true;
    public bool AnnounceFuelEachLap { get; set; }
    public bool AnnounceGapsEachLap { get; set; } = true;
    public int SilentLaps { get; set; }
    public int PlannedPitLap { get; set; }
    public int SpeechRatePercent { get; set; } = 100;
    public string OutputVoice { get; set; } = "";
    public bool UseNaturalVoice { get; set; } = true;
    public string SpeechRecognitionModel { get; set; } = "base";

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AiRaceEngineer", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new();
                // One-time migration from the old default: Qwen3's reasoning mode caused radio replies to ramble.
                if (!loaded.ModelSelectionExplicit && loaded.Model.Equals("qwen3:4b", StringComparison.OrdinalIgnoreCase))
                    loaded.Model = "qwen2.5:3b";
                return loaded;
            }
        }
        catch { }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public sealed record TelemetrySnapshot(
    bool Connected = false,
    float SpeedKmh = 0,
    float FuelLitres = 0,
    int Gear = 0,
    int Rpm = 0,
    int CompletedLaps = 0,
    int Position = 0,
    int NumberOfLaps = 0,
    int Session = -1,
    int Status = 0,
    int Flag = 0,
    float PenaltyTime = 0,
    bool InPit = false,
    bool InPitLane = false,
    string Car = "",
    string Track = "",
    string TyreCompound = "",
    float TyreWearPercent = 0,
    float TyreTempC = 0,
    float CurrentLapTimeSeconds = 0,
    float LastLapTimeSeconds = 0,
    float BestLapTimeSeconds = 0,
    float SessionTimeLeftSeconds = 0,
    float FuelPerLap = 0,
    float WearPerLapPercent = 0,
    float Gas = 0,
    float Brake = 0,
    float NormalizedCarPosition = 0,
    float TrackLengthMeters = 0,
    string FlagText = "Nessuna",
    DateTime Timestamp = default,
    float SteeringAngle = 0,
    float Clutch = 0,
    float TC = 0,
    float ABS = 0,
    float Drs = 0,
    int DrsAvailable = 0,
    int DrsEnabled = 0,
    int PitLimiterOn = 0,
    int EngineBrake = 0,
    int ErsRecoveryLevel = 0,
    int ErsPowerLevel = 0,
    float KersCharge = 0,
    float KersInput = 0,
    float KersCurrentKJ = 0,
    float BrakeBias = 0,
    float TurboBoost = 0,
    int NumberOfTyresOut = 0,
    int CurrentSectorIndex = 0,
    float DistanceTravelled = 0,
    float[] TyreWearWheels = null!,
    float[] TyreWearEstimateWheels = null!,
    float[] TyreDirtyLevel = null!,
    float[] TyrePressure = null!,
    float[] TyreInnerTemp = null!,
    float[] TyreMidTemp = null!,
    float[] TyreOuterTemp = null!,
    float[] TyreCoreTempWheels = null!,
    float[] BrakeTemp = null!,
    float[] WheelSlip = null!,
    float[] WheelLoad = null!,
    float[] SuspensionTravel = null!,
    float[] CarDamage = null!,
    float[] AccG = null!,
    float[] LocalVelocity = null!,
    float AirTempC = 0,
    float RoadTempC = 0,
    float SurfaceGrip = 0,
    float FuelRateMultiplier = 0,
    float TyreWearMultiplier = 0,
    float DamageMultiplier = 0,
    int MandatoryPitDone = 0,
    int PitWindowStart = 0,
    int PitWindowEnd = 0,
    IReadOnlyList<FieldCar>? FieldCars = null);

public sealed record RaceEvent(DateTime Time, string Category, string Message);
