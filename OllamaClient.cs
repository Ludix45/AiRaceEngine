using System.Net.Http;
using System.Net.Http.Json;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiRaceEngineer;

public sealed class OllamaClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };

    public async Task<IReadOnlyList<string>> GetModelsAsync(string baseUrl, CancellationToken ct = default)
    {
        using var response = await Http.GetAsync($"{Normalize(baseUrl)}/api/tags", ct);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return json.RootElement.GetProperty("models").EnumerateArray()
            .Select(x => x.GetProperty("name").GetString() ?? "").Where(x => x.Length > 0).ToArray();
    }

    public async Task<string> AskAsync(string baseUrl, string model, string question, TelemetrySnapshot telemetry, CancellationToken ct = default, bool radio = false, bool general = false)
    {
        var responseLanguage = AppLanguage.IsEnglish ? "Rispondi in inglese." : "Rispondi in italiano.";
        // Questions that the radio router could not classify are answered by
        // the local model as normal questions, not forced into a data intent.
        var generalSystem = "Sei un assistente locale competente. Rispondi alla domanda in modo chiaro e utile. Se la domanda riguarda la sessione in corso, la vettura o i dati di gara (carburante, gomme, danni, distacchi, posizione, box), usa solo la telemetria fornita e non inventare valori: se un dato manca, dillo. Per ogni altro argomento — cultura generale, regole e storia dello sport, meteo, tecnologia, consigli pratici — rispondi con le tue conoscenze senza limitarti ai dati di gara. Massimo tre frasi, salvo richiesta esplicita di approfondimento. /no_think";
        var system = general
            ? generalSystem
            : radio
            ? "Sei l'ingegnere di gara dell'utente alla radio. La trascrizione vocale italiana può contenere parole sbagliate: deduci l'intento dal suono, dal contesto della corsa e dai dati, senza trattarla come una lista fissa di comandi. Rispondi anche a saluti, preparazione gara, strategia, box, bandiere, meteo, guida e impostazioni del coach. Risposta radio: una frase, massimo 18 parole, salvo richiesta esplicita di spiegazione. Per domande sui dati di gara dai solo il dato chiesto e, se serve, un solo consiglio concreto. Non aggiungere frasi generiche su traiettoria, sterzo, frenata o motore se non sono richieste. Non dire mai da dove arrivano i dati e non elencare telemetria grezza. I campi a zero possono essere assenti. Non trasformare indici gomme o danni in percentuali se il contesto non lo dichiara. Per i rivali usa solo Campo e presenta distanze o tempi come stime, mai come distacchi ufficiali. Se un dato manca, dillo chiaramente in una frase. Non inventare meteo, bandiere, penalità, nomi, danni o punti di frenata. Per consigli in curva usa solo un riferimento personale disponibile; se non c'è, chiedi un giro pulito. /no_think"
            : "Sei l'ingegnere di gara dell'utente. Rispondi in italiano come alla radio: apri con la situazione utile, spiega cosa deve fare adesso e fermati. Dai un solo consiglio concreto, leggibile mentre guida. Non dire mai da dove arrivano i dati, non elencare telemetria grezza, indici o campi tecnici. I campi a zero possono indicare un dato assente. Non trasformare indice gomme o danni grezzi in percentuali se il contesto non lo dichiara. Per i rivali usa solo FieldCars e presenta distanze/tempi come stime, mai come distacchi ufficiali. Se manca un dato, dillo in modo semplice e non inventare. Non inventare meteo, bandiere, penalità, nomi, danni o punti di frenata. Per i consigli in curva usa solo il riferimento personale realmente disponibile; se non c'è, chiedi un giro pulito. /no_think";
        system += " " + responseLanguage;
        var field = telemetry.FieldCars ?? [];
        var context = new
        {
            Telemetria = new
            {
                telemetry.Connected, telemetry.Car, telemetry.Track, telemetry.Session,
                telemetry.CompletedLaps, telemetry.NumberOfLaps, telemetry.Position,
                telemetry.SpeedKmh, telemetry.Gear, telemetry.Rpm, telemetry.FuelLitres,
                telemetry.FuelPerLap, telemetry.TyreCompound, telemetry.TyreWearPercent,
                telemetry.WearPerLapPercent, telemetry.TyreTempC, telemetry.TyreWearWheels,
                telemetry.CurrentLapTimeSeconds, telemetry.LastLapTimeSeconds, telemetry.BestLapTimeSeconds,
                telemetry.TyreWearEstimateWheels, telemetry.TyrePressure, telemetry.TyreCoreTempWheels,
                telemetry.TyreInnerTemp, telemetry.TyreMidTemp, telemetry.TyreOuterTemp,
                telemetry.BrakeTemp, telemetry.CarDamage, telemetry.Gas, telemetry.Brake,
                telemetry.SteeringAngle, telemetry.AccG, telemetry.AirTempC, telemetry.RoadTempC,
                telemetry.SurfaceGrip, telemetry.FlagText, telemetry.PenaltyTime, telemetry.InPitLane,
                telemetry.InPit, telemetry.MandatoryPitDone, telemetry.PitWindowStart, telemetry.PitWindowEnd,
                telemetry.NormalizedCarPosition, telemetry.TrackLengthMeters, telemetry.CurrentSectorIndex,
                telemetry.DistanceTravelled, telemetry.Drs, telemetry.DrsAvailable, telemetry.DrsEnabled,
                telemetry.PitLimiterOn, telemetry.EngineBrake, telemetry.ErsRecoveryLevel, telemetry.ErsPowerLevel,
                telemetry.KersCharge, telemetry.KersInput, telemetry.KersCurrentKJ, telemetry.BrakeBias,
                telemetry.TurboBoost, telemetry.NumberOfTyresOut, telemetry.DamageReferenceLapSeconds,
                telemetry.DamageEstimatedLossSeconds, telemetry.DamageEstimateLapCount, telemetry.Timestamp
            },
            Campo = field.Select(c => new
            {
                c.Driver, c.Car, PosizioneUfficiale = c.Position, PosizioneLive = c.RealtimePosition,
                c.Laps, c.Spline, c.SpeedMs, c.BestLapMs, c.LastLapMs,
                PitLane = (c.Flags & 2) != 0, Ai = (c.Flags & 32) != 0
            }).OrderBy(c => c.PosizioneLive >= 0 ? c.PosizioneLive : c.PosizioneUfficiale).ToArray()
        };
        var payload = new
        {
            model,
            stream = false,
            think = false,
            messages = new[]
            {
                new { role = "system", content = system },
                new { role = "user", content = $"STATO TELEMETRICO STRUTTURATO (AC): {JsonSerializer.Serialize(context)}\n\nRICHIESTA{(radio ? " RADIO TRASCRITTA" : "")}: {question}" }
            },
            options = new { temperature = general ? 0.4 : radio ? 0.15 : 0.25, num_ctx = 4096, num_predict = general ? 260 : radio ? 72 : 128 }
        };
        using var response = await Http.PostAsJsonAsync($"{Normalize(baseUrl)}/api/chat", payload, ct);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var answer = json.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "Nessuna risposta.";
        return Regex.Replace(answer, "<think>[\\s\\S]*?</think>", "", RegexOptions.IgnoreCase).Trim();
    }

    public async Task PullAsync(string baseUrl, string model, IProgress<string>? progress, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Normalize(baseUrl)}/api/pull")
        {
            Content = JsonContent.Create(new { name = model, stream = true })
        };
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        string? line;
        while (!ct.IsCancellationRequested && (line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                var status = root.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
                if (root.TryGetProperty("completed", out var done) && root.TryGetProperty("total", out var total) && total.GetInt64() > 0)
                    status += $" ({100d * done.GetInt64() / total.GetInt64():0}%)";
                if (root.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.GetString());
                progress?.Report(status);
            }
            catch (JsonException) { }
        }
    }

    public async Task<string> GetHealthAsync(string baseUrl, string model, CancellationToken ct = default)
    {
        try
        {
            var models = await GetModelsAsync(baseUrl, ct);
            return models.Contains(model, StringComparer.OrdinalIgnoreCase) ? $"Ollama pronto · {model}" : $"Ollama connesso · {model} da scaricare";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return "Ollama non raggiungibile su localhost:11434";
        }
    }

    private static string Normalize(string value) => value.Trim().TrimEnd('/');
}
