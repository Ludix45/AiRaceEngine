using System.IO;
using System.IO.Compression;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Media;
using System.Speech.Synthesis;
using System.Net.NetworkInformation;
using Microsoft.Win32;
using Windows.Globalization;
using Windows.Media.SpeechRecognition;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Whisper.net;

namespace AiRaceEngineer;

public sealed class VoiceService : IDisposable
{
    private const string WhisperTinyModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.bin";
    private const string WhisperBaseModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin";
    // Quantised large-v3-turbo: a large accuracy improvement over base without
    // the multi-gigabyte memory footprint of full large-v3.
    private const string WhisperTurboModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo-q5_0.bin";
    private const string TinyModelSha256 = "BE07E048E1E599AD46341C8D2A135645097A538221678B7ACDD1B1919C6E1B21";
    private const string PiperArchiveUrl = "https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip";
    private const string PiperModelUrl = "https://huggingface.co/rhasspy/piper-voices/resolve/main/it/it_IT/paola/medium/it_IT-paola-medium.onnx?download=true";
    private const string PiperConfigUrl = "https://huggingface.co/rhasspy/piper-voices/resolve/main/it/it_IT/paola/medium/it_IT-paola-medium.onnx.json?download=true";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly SpeechSynthesizer _synth = new();
    private readonly object _captureGate = new();
    private readonly SemaphoreSlim _transcriptionGate = new(1, 1);
    private readonly SemaphoreSlim _speechGate = new(1, 1);
    private MemoryStream? _recording;
    private WasapiCapture? _capture;
    private WaveFormat? _recordingFormat;
    private TaskCompletionSource? _captureStopped;
    private WhisperFactory? _whisperFactory;
    private WhisperProcessor? _whisperProcessor;
    private Task? _initializing;
    private string _recognitionModel = "turbo-q5";
    // Native Windows dictation (the engine behind the system speech tools).
    // It is preferred when the installed Windows speech language matches the
    // interface language, because it is far more accurate than small Whisper
    // models on short radio commands.
    private readonly object _windowsGate = new();
    private SpeechRecognizer? _windowsRecognizer;
    private Task? _windowsStart;
    private DateTime _windowsStartedAt;
    private StringBuilder? _windowsTranscript;
    private TaskCompletionSource? _windowsStopped;
    private bool _windowsListening;
    private bool _useWindowsRecognition;
    // Optional cloud speech service: audio is uploaded only after the player
    // releases push-to-talk, so the GPU and CPU stay free while driving.
    private bool _useCloudRecognition;
    private string _cloudEndpoint = "https://api.openai.com/v1/audio/transcriptions";
    private string _cloudModel = "gpt-4o-mini-transcribe";
    private string _cloudKey = "";
    private Task? _naturalVoiceInitializing;
    private string? _piperExecutable;
    private string? _piperModel;
    private bool _naturalVoiceReady;
    private bool _useNaturalVoice = true;
    private int _speechRatePercent = 100;
    private string _status = "Riconoscimento Whisper locale da inizializzare.";
    private string _outputVoiceStatus = "Voce italiana di Windows attiva.";

    public event Action<string>? Recognized;
    public event Action<string>? Diagnostic;
    public event Action<string>? StatusChanged;
    public event Action<string>? OutputVoiceStatusChanged;
    public string Status => _status;
    public string OutputVoiceStatus => _outputVoiceStatus;
    public IReadOnlyList<string> InstalledItalianVoices => _synth.GetInstalledVoices()
        .Where(v => v.Enabled && v.VoiceInfo.Culture.Name.StartsWith("it", StringComparison.OrdinalIgnoreCase))
        .Select(v => v.VoiceInfo.Name).ToArray();
    public int LastAudioLevel { get; private set; }
    public DateTime LastAudioAt { get; private set; } = DateTime.MinValue;

    public VoiceService()
    {
        try
        {
            var italianVoices = _synth.GetInstalledVoices().Where(v =>
                v.Enabled && v.VoiceInfo.Culture.Name.StartsWith("it", StringComparison.OrdinalIgnoreCase)).ToArray();
            var voice = italianVoices.FirstOrDefault(v => v.VoiceInfo.Name.Contains("Elsa", StringComparison.OrdinalIgnoreCase))
                ?? italianVoices.FirstOrDefault();
            if (voice is not null)
            {
                _synth.SelectVoice(voice.VoiceInfo.Name);
                _synth.Rate = -1;
            }
        }
        catch (Exception ex) { SetStatus($"Voce in uscita non disponibile: {ex.Message}"); }
    }

    public void ConfigureOutput(string? voiceName, int ratePercent, bool useNaturalVoice)
    {
        _speechRatePercent = Math.Clamp(ratePercent, 75, 135);
        _useNaturalVoice = useNaturalVoice;
        try
        {
            var selected = InstalledItalianVoices.FirstOrDefault(v => string.Equals(v, voiceName, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(selected)) _synth.SelectVoice(selected);
            _synth.Rate = Math.Clamp((int)Math.Round((_speechRatePercent - 100) / 5d), -10, 10);
        }
        catch (Exception ex) { SetOutputVoiceStatus($"Voce Windows non configurata: {ex.Message}"); return; }
        SetOutputVoiceStatus(_useNaturalVoice
            ? (_naturalVoiceReady ? $"Voce neurale italiana pronta · {_speechRatePercent}%" : "Voce neurale italiana in preparazione; per ora uso la voce Windows.")
            : $"Voce Windows selezionata · {_speechRatePercent}%");
    }

    public Task PrepareNaturalVoiceAsync()
    {
        lock (_captureGate)
        {
            if (_naturalVoiceReady) return Task.CompletedTask;
            if (_naturalVoiceInitializing is { IsCompleted: false }) return _naturalVoiceInitializing;
            return _naturalVoiceInitializing = PrepareNaturalVoiceCoreAsync();
        }
    }

    public Task InitializeAsync(string recognitionModel = "turbo-q5", bool force = false)
    {
        lock (_captureGate)
        {
            recognitionModel = NormalizeRecognitionModel(recognitionModel);
            if (!force && recognitionModel == "windows" && _useWindowsRecognition && _windowsRecognizer is not null) return Task.CompletedTask;
            if (!force && recognitionModel == "cloud" && _useCloudRecognition && _recognitionModel == "cloud") return Task.CompletedTask;
            if (!force && recognitionModel != "windows" && recognitionModel != "cloud" && _whisperProcessor is not null && _recognitionModel == recognitionModel) return Task.CompletedTask;
            if (_initializing is { IsCompleted: false }) return _initializing;
            _recognitionModel = recognitionModel;
            return _initializing = InitializeCoreAsync(recognitionModel);
        }
    }

    /// <summary>Endpoint, model and key of the optional cloud speech service.</summary>
    public void ConfigureCloud(string endpoint, string? model, string? apiKey)
    {
        _cloudEndpoint = string.IsNullOrWhiteSpace(endpoint) ? "https://api.openai.com/v1/audio/transcriptions" : endpoint.Trim();
        _cloudModel = string.IsNullOrWhiteSpace(model) ? "gpt-4o-mini-transcribe" : model.Trim();
        _cloudKey = apiKey?.Trim() ?? "";
    }

    private async Task InitializeCoreAsync(string recognitionModel)
    {
        if (recognitionModel == "cloud")
        {
            if (!string.IsNullOrWhiteSpace(_cloudKey) && !string.IsNullOrWhiteSpace(_cloudEndpoint))
            {
                _useCloudRecognition = true;
                _useWindowsRecognition = false;
                _windowsRecognizer?.Dispose();
                _windowsRecognizer = null;
                _recognitionModel = "cloud";
                SetStatus($"Riconoscimento cloud pronto · {_cloudModel} · audio inviato solo quando rilasci il PTT.");
                return;
            }

            // No key yet: keep the microphone usable with the local engine.
            _useCloudRecognition = false;
            SetStatus("Riconoscimento cloud selezionato ma manca la chiave API: uso il motore locale.");
            recognitionModel = "windows";
        }

        if (recognitionModel == "windows")
        {
            var windowsInit = await TryInitializeWindowsRecognizerAsync().ConfigureAwait(false);
            if (windowsInit.Ready)
            {
                _useWindowsRecognition = true;
                _recognitionModel = "windows";
                SetStatus($"Riconoscimento Windows pronto · {windowsInit.Detail}.");
                return;
            }
            var windowsDetail = windowsInit.Detail;

            // The Windows speech language pack is optional. Fall back to the
            // local Whisper model instead of leaving the microphone unusable.
            // The fallback is the small model: large-v3 turbo needs about ten
            // seconds per phrase on the CPU, which is useless on the radio.
            _useWindowsRecognition = false;
            recognitionModel = "base";
            _recognitionModel = recognitionModel;
            SetStatus($"Riconoscimento Windows non disponibile ({windowsDetail}). Uso Whisper base locale su CPU: la GPU resta al gioco.");
        }

        _useWindowsRecognition = false;
        _useCloudRecognition = false;
        try
        {
            _windowsRecognizer?.Dispose();
            _windowsRecognizer = null;
            var modelPath = GetModelPath(recognitionModel);
            if (!IsModelUsable(modelPath, recognitionModel))
                await DownloadModelAsync(modelPath, recognitionModel).ConfigureAwait(false);

            _whisperProcessor?.Dispose();
            _whisperFactory?.Dispose();
            _whisperFactory = WhisperFactory.FromPath(modelPath);
            _whisperProcessor = _whisperFactory.CreateBuilder()
                .WithLanguage("it")
                // Half the logical cores: the small models still finish in about
                // a second, and the game keeps the other cores and the whole GPU.
                .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 2, 8))
                .WithPrompt("Italiano. Radio di gara Assetto Corsa. Gomme, carburante, danni, box, pit stop, davanti, dietro, distacco, posizione, bandiere, sorpasso, curva, frenata, gas, strategia.")
                .WithNoContext()
                .WithSingleSegment()
                .WithTemperature(0f)
                .Build();
            var speed = recognitionModel switch
            {
                "tiny" => "molto veloce, circa 0,4 s",
                "base" => "bilanciato, circa 1 s",
                _ => "massima accuratezza ma circa 10 s su CPU: per la radio in gara conviene Windows o Cloud"
            };
            SetStatus($"Riconoscimento italiano pronto · locale su CPU, la GPU resta al gioco · modello {recognitionModel} ({speed}).");
        }
        catch (Exception ex)
        {
            SetStatus($"Whisper non pronto: {ex.Message}");
        }
    }

    /// <summary>
    /// Prepares the modern Windows speech recognizer, the engine behind system
    /// dictation. It uses Microsoft's online speech service when the network and
    /// the speech privacy setting allow it, and the on-device language models
    /// otherwise, so the microphone keeps working without a connection.
    /// </summary>
    private async Task<(bool Ready, string Detail)> TryInitializeWindowsRecognizerAsync()
    {
        var detail = "";
        SpeechRecognizer? recognizer = null;
        try
        {
            _windowsRecognizer?.Dispose();
            _windowsRecognizer = null;
            var culture = AppLanguage.IsEnglish ? new CultureInfo("en-US") : new CultureInfo("it-IT");
            var language = new Language(culture.Name);
            // Windows refuses to listen until the user accepts the speech privacy
            // policy (0x80045509), so report that instead of starting an engine
            // that cannot capture anything.
            if (!SpeechPrivacyAccepted())
                return (false, "manca il consenso di Windows al riconoscimento vocale: attivalo una volta in Impostazioni → Privacy e sicurezza → Riconoscimento vocale, poi riavvia l’app");
            recognizer = new SpeechRecognizer(language);
            recognizer.Constraints.Add(new SpeechRecognitionTopicConstraint(SpeechRecognitionScenario.Dictation, "dictation"));
            var compilation = await recognizer.CompileConstraintsAsync();
            if (compilation.Status != SpeechRecognitionResultStatus.Success)
            {
                recognizer.Dispose();
                recognizer = null;
                detail = $"Windows non prepara la dettatura {language.DisplayName} ({compilation.Status}). Installa il pacchetto da Impostazioni → Data e ora e lingua → Lingua e area → Italiano → Opzioni lingua → Riconoscimento vocale";
                return (false, detail);
            }

            var session = recognizer.ContinuousRecognitionSession;
            // A radio command is short: keep the session open for the whole key
            // hold instead of letting the first pause end it.
            session.AutoStopSilenceTimeout = TimeSpan.FromMinutes(2);
            session.ResultGenerated += OnWindowsResultGenerated;
            session.Completed += OnWindowsSessionCompleted;
            _windowsRecognizer = recognizer;
            detail = SpeechOnlinePreferred()
                ? $"{language.DisplayName} · online quando la rete risponde, modello locale come riserva"
                : $"{language.DisplayName} · modello locale (attiva «Riconoscimento vocale online» per usare il servizio cloud)";
            return (true, detail);
        }
        catch (Exception ex)
        {
            try { recognizer?.Dispose(); } catch { }
            _windowsRecognizer = null;
            return (false, ex.Message);
        }
    }

    private static bool SpeechPrivacyAccepted()
    {
        try
        {
            return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy", "HasAccepted", 0) is int accepted && accepted == 1;
        }
        catch { return false; }
    }

    private static bool SpeechOnlinePreferred() => SpeechPrivacyAccepted() && NetworkInterface.GetIsNetworkAvailable();

    private void OnWindowsResultGenerated(SpeechContinuousRecognitionSession sender, SpeechContinuousRecognitionResultGeneratedEventArgs args)
    {
        var result = args.Result;
        if (result is null) return;
        if (result.Status == SpeechRecognitionResultStatus.Success && result.Confidence != SpeechRecognitionConfidence.Rejected)
        {
            var text = result.Text?.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                lock (_windowsGate) _windowsTranscript?.Append(text).Append(' ');
            }
            LastAudioAt = DateTime.Now;
            if (LastAudioLevel < 20) LastAudioLevel = 45;
            return;
        }

        if (result.Status == SpeechRecognitionResultStatus.NetworkFailure)
            Diagnostic?.Invoke("Il servizio vocale online non ha risposto: Windows passa da solo al modello locale. Verifica la connessione se l’errore si ripete.");
        else if (result.Status is SpeechRecognitionResultStatus.MicrophoneUnavailable or SpeechRecognitionResultStatus.AudioQualityFailure)
            Diagnostic?.Invoke("Il motore vocale Windows non riceve il microfono. Controlla Impostazioni → Privacy e sicurezza → Microfono e il dispositivo predefinito.");
    }

    private void OnWindowsSessionCompleted(SpeechContinuousRecognitionSession sender, SpeechContinuousRecognitionCompletedEventArgs args)
    {
        TaskCompletionSource? stopped;
        lock (_windowsGate) stopped = _windowsStopped;
        stopped?.TrySetResult();
    }

    private static string GetModelPath(string recognitionModel)
    {
        var root = Environment.GetEnvironmentVariable("OLLAMA_MODELS");
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AiRaceEngineer", "Models");
        var folder = Path.Combine(root, "AiRaceEngineer", "Whisper");
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, $"ggml-{recognitionModel}.bin");
    }

    private async Task DownloadModelAsync(string path, string recognitionModel)
    {
        var temporaryPath = path + ".download";
        var url = recognitionModel switch
        {
            "tiny" => WhisperTinyModelUrl,
            "turbo-q5" => WhisperTurboModelUrl,
            _ => WhisperBaseModelUrl
        };
        var size = recognitionModel switch { "tiny" => "77 MB", "turbo-q5" => "547 MB", _ => "142 MB" };
        SetStatus($"Download riconoscimento Whisper {recognitionModel} · {size} · una sola volta…");
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            await using (var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true))
            {
                var buffer = new byte[131072];
                long received = 0;
                var lastPercent = -1;
                int read;
                while ((read = await input.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                    received += read;
                    if (total is > 0)
                    {
                        var percent = (int)(received * 100 / total.Value);
                        if (percent != lastPercent && percent % 10 == 0)
                        {
                            lastPercent = percent;
                            SetStatus($"Download Whisper · {percent}%");
                        }
                    }
                }
            }

            if (!IsModelUsable(temporaryPath, recognitionModel))
                throw new InvalidDataException("Il file del modello vocale non è valido.");
            File.Move(temporaryPath, path, true);
        }
        catch
        {
            try { File.Delete(temporaryPath); } catch { }
            throw;
        }
    }

    private static bool IsModelUsable(string path, string recognitionModel)
    {
        if (!File.Exists(path)) return false;
        if (recognitionModel == "turbo-q5") return new FileInfo(path).Length > 500_000_000;
        if (recognitionModel != "tiny") return new FileInfo(path).Length > 100_000_000;
        return HasExpectedHashAsync(path).GetAwaiter().GetResult();
    }

    private static async Task<bool> HasExpectedHashAsync(string path)
    {
        if (!File.Exists(path)) return false;
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream).ConfigureAwait(false);
        return Convert.ToHexString(hash).Equals(TinyModelSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRecognitionModel(string value) => value.ToLowerInvariant() switch
    {
        "windows" or "system" => "windows",
        "cloud" or "online" => "cloud",
        "tiny" => "tiny",
        "base" => "base",
        "turbo-q5" or "turbo" or "large-v3-turbo" => "turbo-q5",
        _ => "turbo-q5"
    };

    public void StartListening()
    {
        if (_useWindowsRecognition && _windowsRecognizer is not null)
        {
            StartWindowsListening();
            return;
        }

        if (_whisperProcessor is null && !_useCloudRecognition)
            throw new InvalidOperationException(Status.Contains("non pronto", StringComparison.OrdinalIgnoreCase)
                ? Status : "Il modello vocale locale sta ancora caricando. Riprova tra poco.");

        lock (_captureGate)
        {
            if (_capture is not null) return;
            var recording = new MemoryStream();
            var captureStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // Use the same Windows audio path as AiMotor: capture from the default
            // CoreAudio endpoint and convert its mix format after recording.
            var capture = new WasapiCapture();
            var captureFormat = capture.WaveFormat;
            capture.DataAvailable += (_, e) =>
            {
                lock (_captureGate)
                {
                    recording.Write(e.Buffer, 0, e.BytesRecorded);
                    var level = MeasureLevel(e.Buffer, e.BytesRecorded, captureFormat);
                    LastAudioLevel = level;
                    if (level > 2) LastAudioAt = DateTime.Now;
                }
            };
            capture.RecordingStopped += (_, e) =>
            {
                if (e.Exception is not null) captureStopped.TrySetException(e.Exception);
                else captureStopped.TrySetResult();
            };
            _recording = recording;
            _recordingFormat = captureFormat;
            _captureStopped = captureStopped;
            _capture = capture;
            LastAudioAt = DateTime.MinValue;
            LastAudioLevel = 0;
            try
            {
                capture.StartRecording();
                SetStatus(_useCloudRecognition
                    ? "Microfono aperto · registrazione per il cloud, tieni premuto per parlare."
                    : "Microfono aperto · Whisper locale in ascolto, tieni premuto per parlare.");
            }
            catch
            {
                _capture = null;
                _recording = null;
                _recordingFormat = null;
                capture.Dispose();
                recording.Dispose();
                throw;
            }
        }
    }

    public async Task StopListeningAsync()
    {
        if (_useWindowsRecognition && _windowsRecognizer is not null)
        {
            await StopWindowsListeningAsync().ConfigureAwait(false);
            return;
        }

        WasapiCapture? capture;
        MemoryStream? recording;
        WaveFormat? format;
        TaskCompletionSource? stopped;
        lock (_captureGate)
        {
            capture = _capture;
            recording = _recording;
            format = _recordingFormat;
            stopped = _captureStopped;
            _capture = null;
            _recording = null;
            _recordingFormat = null;
            _captureStopped = null;
        }
        if (capture is null || recording is null || format is null || stopped is null) return;

        try
        {
            capture.StopRecording();
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Diagnostic?.Invoke($"Errore cattura microfono: {ex.Message}");
            capture.Dispose();
            recording.Dispose();
            return;
        }
        finally
        {
            capture.Dispose();
        }

        byte[] bytes;
        lock (_captureGate) bytes = recording.ToArray();
        recording.Dispose();
        if (bytes.Length < 8000)
        {
            Diagnostic?.Invoke("Audio troppo breve: tieni premuto PTT mentre parli, poi rilascialo.");
            return;
        }
        if (LastAudioAt == DateTime.MinValue)
        {
            Diagnostic?.Invoke("WASAPI non ha rilevato voce. Verifica il microfono predefinito di Windows, mute e autorizzazioni microfono.");
            return;
        }

        if (_useCloudRecognition)
        {
            await TranscribeCloudAsync(bytes, format).ConfigureAwait(false);
            return;
        }

        await _transcriptionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            SetStatus("Audio acquisito · trascrizione locale in corso…");
            var timer = Stopwatch.StartNew();
            var samples = ConvertToWhisperSamples(bytes, format);

            var text = new System.Text.StringBuilder();
            await foreach (var segment in _whisperProcessor!.ProcessAsync(samples).ConfigureAwait(false))
                text.Append(segment.Text);
            timer.Stop();

            var phrase = text.ToString().Trim();
            if (phrase.Length > 0)
            {
                SetStatus($"Riconosciuto in {timer.Elapsed.TotalSeconds:0.0} s: «{phrase}»");
                Recognized?.Invoke(phrase);
            }
            else Diagnostic?.Invoke("Whisper non ha trovato parole nell’audio. Prova una frase più breve e vicina al microfono.");
        }
        catch (Exception ex)
        {
            Diagnostic?.Invoke($"Trascrizione locale non riuscita: {ex.Message}");
        }
        finally { _transcriptionGate.Release(); }
    }

    /// <summary>
    /// Uploads the push-to-talk phrase to an OpenAI-compatible transcription
    /// endpoint. Nothing is computed locally: the GPU and CPU stay with the game.
    /// </summary>
    private async Task TranscribeCloudAsync(byte[] audio, WaveFormat format)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            SetStatus("Audio acquisito · riconoscimento cloud in corso…");
            using var content = new MultipartFormDataContent();
            var file = new ByteArrayContent(BuildWav(audio, format));
            file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            content.Add(file, "file", "ptt.wav");
            content.Add(new StringContent(_cloudModel), "model");
            content.Add(new StringContent("it"), "language");
            content.Add(new StringContent("radio di gara: gomme, carburante, danni, box, distacchi"), "prompt");
            using var request = new HttpRequestMessage(HttpMethod.Post, _cloudEndpoint) { Content = content };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _cloudKey);
            using var response = await Http.SendAsync(request).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Diagnostic?.Invoke($"Servizio cloud non disponibile ({(int)response.StatusCode}). Controlla chiave, modello e connessione.");
                return;
            }

            using var json = JsonDocument.Parse(body);
            var phrase = (json.RootElement.TryGetProperty("text", out var text) ? text.GetString() : null)?.Trim() ?? "";
            timer.Stop();
            if (phrase.Length == 0)
            {
                Diagnostic?.Invoke("Il servizio cloud non ha riconosciuto parole nell’audio.");
                return;
            }

            SetStatus($"Riconosciuto dal cloud in {timer.Elapsed.TotalSeconds:0.0} s: «{phrase}»");
            Recognized?.Invoke(phrase);
        }
        catch (Exception ex)
        {
            Diagnostic?.Invoke($"Riconoscimento cloud non riuscito: {ex.Message}");
        }
    }

    /// <summary>16 kHz mono PCM: small upload, accepted by every speech API.</summary>
    private static byte[] BuildWav(byte[] audio, WaveFormat sourceFormat)
    {
        using var sourceData = new MemoryStream(audio, writable: false);
        using var source = new RawSourceWaveStream(sourceData, sourceFormat);
        using var resampler = new MediaFoundationResampler(source, new WaveFormat(16000, 16, 1))
        {
            ResamplerQuality = 60
        };
        using var pcm = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = resampler.Read(buffer, 0, buffer.Length)) > 0)
            pcm.Write(buffer, 0, read);
        var data = pcm.ToArray();

        using var wav = new MemoryStream(44 + data.Length);
        using (var writer = new BinaryWriter(wav, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8.ToArray());
            writer.Write(36 + data.Length);
            writer.Write("WAVE"u8.ToArray());
            writer.Write("fmt "u8.ToArray());
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(16000);
            writer.Write(32000);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("data"u8.ToArray());
            writer.Write(data.Length);
            writer.Write(data);
        }
        return wav.ToArray();
    }

    private void StartWindowsListening()
    {
        var recognizer = _windowsRecognizer;
        if (recognizer is null) return;
        lock (_windowsGate)
        {
            if (_windowsListening) return;
            _windowsTranscript = new StringBuilder();
            _windowsStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _windowsListening = true;
            _windowsStartedAt = DateTime.Now;
            _windowsStart = StartWindowsSessionAsync(recognizer);
        }

        LastAudioAt = DateTime.MinValue;
        LastAudioLevel = 0;
        SetStatus(SpeechOnlinePreferred()
            ? "Microfono Windows aperto · ascolto online, tieni premuto per parlare."
            : "Microfono Windows aperto · ascolto con modello locale, tieni premuto per parlare.");
    }

    private async Task StartWindowsSessionAsync(SpeechRecognizer recognizer)
    {
        try
        {
            await recognizer.ContinuousRecognitionSession.StartAsync();
        }
        catch (Exception ex)
        {
            TaskCompletionSource? stopped;
            lock (_windowsGate)
            {
                // Drop the session so the release path keeps this message on
                // screen instead of replacing it with "no voice recognised".
                _windowsListening = false;
                _windowsTranscript = null;
                _windowsStart = null;
                stopped = _windowsStopped;
                _windowsStopped = null;
            }
            Diagnostic?.Invoke(ex.HResult == unchecked((int)0x80045509)
                ? "Windows richiede il consenso al riconoscimento vocale: attivalo in Impostazioni → Privacy e sicurezza → Riconoscimento vocale e riavvia l’app."
                : $"Motore vocale Windows non avviato: {ex.Message}. Verifica Impostazioni → Privacy e sicurezza → Microfono (app desktop) e Riconoscimento vocale.");
            stopped?.TrySetResult();
        }
    }

    private async Task StopWindowsListeningAsync()
    {
        SpeechRecognizer? recognizer;
        TaskCompletionSource? stopped;
        StringBuilder? transcript;
        Task? start;
        lock (_windowsGate)
        {
            // Also handle an engine that stopped on its own after a pause.
            if (_windowsTranscript is null) return;
            _windowsListening = false;
            recognizer = _windowsRecognizer;
            stopped = _windowsStopped;
            transcript = _windowsTranscript;
            start = _windowsStart;
        }

        if (recognizer is null || transcript is null) return;
        try
        {
            if (start is not null) await start.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            await recognizer.ContinuousRecognitionSession.StopAsync();
            if (stopped is not null) await stopped.Task.WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
        }
        catch (TimeoutException) { }
        catch (Exception ex)
        {
            Diagnostic?.Invoke($"Riconoscimento Windows interrotto: {ex.Message}");
        }
        finally
        {
            lock (_windowsGate)
            {
                _windowsTranscript = null;
                _windowsStopped = null;
                _windowsStart = null;
            }
        }

        var phrase = transcript.ToString().Trim();
        if (phrase.Length == 0)
        {
            Diagnostic?.Invoke(LastAudioAt == DateTime.MinValue
                ? "Il riconoscimento Windows non ha ricevuto voce. Controlla il microfono predefinito di Windows."
                : "Windows ha ricevuto audio ma non ha riconosciuto parole. Parla più vicino al microfono.");
            return;
        }

        var seconds = (DateTime.Now - _windowsStartedAt).TotalSeconds;
        SetStatus($"Riconosciuto da Windows in {seconds:0.0} s: «{phrase}»");
        Recognized?.Invoke(phrase);
    }

    private static int MeasureLevel(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        double squares = 0;
        var count = 0;
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
            (format is WaveFormatExtensible extensible && extensible.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
        if (format.BitsPerSample == 32 && isFloat)
        {
            for (var offset = 0; offset + 4 <= bytesRecorded; offset += 4)
            {
                var sample = BitConverter.ToSingle(buffer, offset);
                if (!float.IsFinite(sample)) continue;
                squares += sample * sample;
                count++;
            }
        }
        else if (format.BitsPerSample == 16)
        {
            for (var offset = 0; offset + 2 <= bytesRecorded; offset += 2)
            {
                var sample = BitConverter.ToInt16(buffer, offset) / 32768.0;
                squares += sample * sample;
                count++;
            }
        }
        else if (format.BitsPerSample == 24)
        {
            for (var offset = 0; offset + 3 <= bytesRecorded; offset += 3)
            {
                var value = buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
                if ((value & 0x800000) != 0) value |= unchecked((int)0xFF000000);
                var sample = value / 8388608.0;
                squares += sample * sample;
                count++;
            }
        }
        else if (format.BitsPerSample == 8)
        {
            for (var offset = 0; offset < bytesRecorded; offset++)
            {
                var sample = (buffer[offset] - 128) / 128.0;
                squares += sample * sample;
                count++;
            }
        }
        else if (format.BitsPerSample == 32)
        {
            for (var offset = 0; offset + 4 <= bytesRecorded; offset += 4)
            {
                var sample = BitConverter.ToInt32(buffer, offset) / 2147483648.0;
                squares += sample * sample;
                count++;
            }
        }
        if (count == 0) return 0;
        var rms = Math.Sqrt((double)squares / count);
        return Math.Clamp((int)(rms * 100), 0, 100);
    }

    private static float[] ConvertToWhisperSamples(byte[] audio, WaveFormat sourceFormat)
    {
        using var sourceData = new MemoryStream(audio, writable: false);
        using var source = new RawSourceWaveStream(sourceData, sourceFormat);
        using var resampler = new MediaFoundationResampler(source, new WaveFormat(16000, 16, 1))
        {
            ResamplerQuality = 60
        };
        using var converted = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = resampler.Read(buffer, 0, buffer.Length)) > 0)
            converted.Write(buffer, 0, read);

        var pcm = converted.ToArray();
        var samples = new float[pcm.Length / 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = BitConverter.ToInt16(pcm, i * 2) / 32768f;
        return TrimSilenceAndNormalize(samples);
    }

    /// <summary>
    /// Removes leading/trailing silence around the push-to-talk utterance and
    /// lifts quiet microphone input. Whisper transcribes both more reliably
    /// than a raw recording that starts with cabin noise or ends with a pause.
    /// </summary>
    private static float[] TrimSilenceAndNormalize(float[] samples)
    {
        const int window = 320; // 20 ms at 16 kHz
        const float silenceThreshold = 0.012f;
        var first = -1;
        var last = -1;
        for (var offset = 0; offset + window <= samples.Length; offset += window)
        {
            double sum = 0;
            for (var i = offset; i < offset + window; i++) sum += samples[i] * samples[i];
            if (Math.Sqrt(sum / window) < silenceThreshold) continue;
            if (first < 0) first = offset;
            last = offset + window;
        }

        if (first < 0 || last <= first) return samples;
        var margin = 1600; // 100 ms of context on each side
        first = Math.Max(0, first - margin);
        last = Math.Min(samples.Length, last + margin);
        var trimmed = new float[last - first];
        Array.Copy(samples, first, trimmed, 0, trimmed.Length);

        var peak = 0f;
        foreach (var sample in trimmed) peak = Math.Max(peak, Math.Abs(sample));
        if (peak > 0.02f && peak < 0.95f)
        {
            var gain = Math.Min(6f, 0.95f / peak);
            for (var i = 0; i < trimmed.Length; i++) trimmed[i] = Math.Clamp(trimmed[i] * gain, -1f, 1f);
        }
        return trimmed;
    }

    public void Speak(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        _ = SpeakAsync(text);
    }

    private async Task SpeakAsync(string text)
    {
        await _speechGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_useNaturalVoice)
            {
                if (!_naturalVoiceReady)
                {
                    _ = PrepareNaturalVoiceAsync();
                    SpeakWithWindowsVoice(text);
                    return;
                }
                await SpeakWithPiperAsync(text).ConfigureAwait(false);
                return;
            }
            SpeakWithWindowsVoice(text);
        }
        catch (Exception ex)
        {
            SetOutputVoiceStatus($"Voce neurale non disponibile; uso la voce Windows. {ex.Message}");
            SpeakWithWindowsVoice(text);
        }
        finally { _speechGate.Release(); }
    }

    private void SpeakWithWindowsVoice(string text)
    {
        try { _synth.SpeakAsyncCancelAll(); _synth.SpeakAsync(text); }
        catch { }
    }

    private async Task PrepareNaturalVoiceCoreAsync()
    {
        try
        {
            SetOutputVoiceStatus("Download voce neurale italiana in corso: circa 100 MB, una sola volta.");
            var root = GetNaturalVoiceRoot();
            Directory.CreateDirectory(root);
            var model = Path.Combine(root, "it_IT-paola-medium.onnx");
            var config = model + ".json";
            if (!File.Exists(model)) await DownloadFileAsync(PiperModelUrl, model).ConfigureAwait(false);
            if (!File.Exists(config)) await DownloadFileAsync(PiperConfigUrl, config).ConfigureAwait(false);

            var engineRoot = Path.Combine(root, "piper");
            var executable = Directory.Exists(engineRoot)
                ? Directory.GetFiles(engineRoot, "piper.exe", SearchOption.AllDirectories).FirstOrDefault()
                : null;
            if (string.IsNullOrWhiteSpace(executable))
            {
                var archive = Path.Combine(root, "piper-windows-amd64.zip");
                if (!File.Exists(archive)) await DownloadFileAsync(PiperArchiveUrl, archive).ConfigureAwait(false);
                ZipFile.ExtractToDirectory(archive, root, overwriteFiles: true);
                executable = Directory.GetFiles(root, "piper.exe", SearchOption.AllDirectories).FirstOrDefault();
            }
            if (string.IsNullOrWhiteSpace(executable)) throw new FileNotFoundException("Piper non trovato dopo il download.");
            _piperExecutable = executable;
            _piperModel = model;
            _naturalVoiceReady = true;
            SetOutputVoiceStatus($"Voce neurale italiana pronta · {_speechRatePercent}% · locale.");
        }
        catch (Exception ex)
        {
            _naturalVoiceReady = false;
            SetOutputVoiceStatus($"Voce neurale non pronta: {ex.Message}");
        }
    }

    private static string GetNaturalVoiceRoot()
    {
        var root = Environment.GetEnvironmentVariable("OLLAMA_MODELS");
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AiRaceEngineer", "Models");
        return Path.Combine(root, "AiRaceEngineer", "Piper");
    }

    private static async Task DownloadFileAsync(string url, string path)
    {
        var temporary = path + ".download";
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            await using var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true);
            await input.CopyToAsync(output).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        catch
        {
            try { File.Delete(temporary); } catch { }
            throw;
        }
    }

    private async Task SpeakWithPiperAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(_piperExecutable) || string.IsNullOrWhiteSpace(_piperModel))
            throw new InvalidOperationException("Motore vocale non pronto.");
        var outputPath = Path.Combine(Path.GetTempPath(), $"AiRaceEngineer-{Guid.NewGuid():N}.wav");
        try
        {
            var start = new ProcessStartInfo(_piperExecutable)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(_piperExecutable)!
            };
            start.ArgumentList.Add("--model");
            start.ArgumentList.Add(_piperModel);
            start.ArgumentList.Add("--output_file");
            start.ArgumentList.Add(outputPath);
            start.ArgumentList.Add("--length_scale");
            start.ArgumentList.Add((100d / _speechRatePercent).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Impossibile avviare il motore vocale.");
            await process.StandardInput.WriteLineAsync(text).ConfigureAwait(false);
            process.StandardInput.Close();
            var errors = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);
            if (process.ExitCode != 0 || !File.Exists(outputPath))
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(errors) ? "Sintesi vocale non riuscita." : errors.Trim());

            using var reader = new AudioFileReader(outputPath);
            using var output = new WaveOutEvent();
            output.Init(reader);
            output.Play();
            while (output.PlaybackState == PlaybackState.Playing)
                await Task.Delay(20).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(outputPath); } catch { }
        }
    }

    private void SetStatus(string status)
    {
        _status = status;
        StatusChanged?.Invoke(status);
    }

    private void SetOutputVoiceStatus(string status)
    {
        _outputVoiceStatus = status;
        OutputVoiceStatusChanged?.Invoke(status);
    }

    public void Dispose()
    {
        lock (_captureGate)
        {
            try { _capture?.StopRecording(); _capture?.Dispose(); _capture = null; } catch { }
            _recording?.Dispose(); _recording = null;
        }
        _whisperProcessor?.Dispose();
        _whisperFactory?.Dispose();
        try { _windowsRecognizer?.Dispose(); } catch { }
        _windowsRecognizer = null;
        _transcriptionGate.Dispose();
        _speechGate.Dispose();
        _synth.Dispose();
    }
}

public sealed class PushToTalkService : IDisposable
{
    private const int VkF8 = 0x77;
    private const ushort XboxStart = 0x0010;
    private const ushort XboxGuide = 0x0400;
    private bool _wasDown;
    private readonly CancellationTokenSource _cts = new();
    public event Action<bool, string>? Changed;
    public bool XboxEnabled { get; set; } = true;

    public PushToTalkService() => _ = Task.Run(PollAsync);

    private async Task PollAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var source = "";
                var down = (GetAsyncKeyState(VkF8) & 0x8000) != 0;
                if (down) source = "F8";
                if (XboxEnabled)
                {
                    for (var user = 0; user < 4; user++)
                    {
                        if (XInputGetState(user, out var state) != 0) continue;
                        if ((state.Gamepad.Buttons & (XboxStart | XboxGuide)) != 0)
                        {
                            down = true;
                            source = "Xbox Menu";
                            break;
                        }
                    }
                }
                if (down != _wasDown) { _wasDown = down; Changed?.Invoke(down, source); }
                await Task.Delay(25, _cts.Token);
            }
            catch (TaskCanceledException) { break; }
            catch { await Task.Delay(500); }
        }
    }

    public void Dispose() { _cts.Cancel(); _cts.Dispose(); }

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")] private static extern int XInputGetState(int userIndex, out XInputState state);
    [StructLayout(LayoutKind.Sequential)] private struct XInputState { public uint PacketNumber; public XInputGamepad Gamepad; }
    [StructLayout(LayoutKind.Sequential)] private struct XInputGamepad { public ushort Buttons; public byte LeftTrigger; public byte RightTrigger; public short ThumbLX; public short ThumbLY; public short ThumbRX; public short ThumbRY; }
}

public static class AudioCue
{
    public static void PlayPushToTalk(bool pressed) => Play(pressed ? 900 : 620, 75);
    public static void PlayBrake() => Play(520, 115);
    public static void PlayThrottle() => Play(1250, 115);

    private static void Play(int frequency, int duration)
    {
        _ = Task.Run(() =>
        {
            try { Console.Beep(frequency, duration); }
            catch { try { SystemSounds.Beep.Play(); } catch { } }
        });
    }
}
