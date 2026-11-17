using System.IO;
using System.IO.Compression;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Media;
using System.Speech.Synthesis;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Whisper.net;

namespace AiRaceEngineer;

public sealed class VoiceService : IDisposable
{
    private const string WhisperModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.bin";
    private const string ModelSha256 = "BE07E048E1E599AD46341C8D2A135645097A538221678B7ACDD1B1919C6E1B21";
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

    public Task InitializeAsync()
    {
        lock (_captureGate) return _initializing ??= InitializeCoreAsync();
    }

    private async Task InitializeCoreAsync()
    {
        try
        {
            var modelPath = GetModelPath();
            if (!File.Exists(modelPath) || !await HasExpectedHashAsync(modelPath).ConfigureAwait(false))
                await DownloadModelAsync(modelPath).ConfigureAwait(false);

            _whisperFactory = WhisperFactory.FromPath(modelPath);
            _whisperProcessor = _whisperFactory.CreateBuilder().WithLanguage("it").Build();
            SetStatus("Whisper italiano pronto · locale, modello tiny.");
        }
        catch (Exception ex)
        {
            SetStatus($"Whisper non pronto: {ex.Message}");
        }
    }

    private static string GetModelPath()
    {
        var root = Environment.GetEnvironmentVariable("OLLAMA_MODELS");
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AiRaceEngineer", "Models");
        var folder = Path.Combine(root, "AiRaceEngineer", "Whisper");
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, "ggml-tiny.bin");
    }

    private async Task DownloadModelAsync(string path)
    {
        var temporaryPath = path + ".download";
        SetStatus("Download modello vocale Whisper · 77 MB · nella cartella modelli Ollama…");
        try
        {
            using var response = await Http.GetAsync(WhisperModelUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
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

            if (!await HasExpectedHashAsync(temporaryPath).ConfigureAwait(false))
                throw new InvalidDataException("Verifica SHA-256 del modello non riuscita.");
            File.Move(temporaryPath, path, true);
        }
        catch
        {
            try { File.Delete(temporaryPath); } catch { }
            throw;
        }
    }

    private static async Task<bool> HasExpectedHashAsync(string path)
    {
        if (!File.Exists(path)) return false;
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream).ConfigureAwait(false);
        return Convert.ToHexString(hash).Equals(ModelSha256, StringComparison.OrdinalIgnoreCase);
    }

    public void StartListening()
    {
        if (_whisperProcessor is null)
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
                SetStatus("Microfono aperto · Whisper locale in ascolto, tieni premuto per parlare.");
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

        await _transcriptionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            SetStatus("Audio acquisito · trascrizione locale in corso…");
            var samples = ConvertToWhisperSamples(bytes, format);

            var text = new System.Text.StringBuilder();
            await foreach (var segment in _whisperProcessor!.ProcessAsync(samples).ConfigureAwait(false))
                text.Append(segment.Text);

            var phrase = text.ToString().Trim();
            if (phrase.Length > 0)
            {
                SetStatus($"Riconosciuto dal microfono: «{phrase}»");
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
        return samples;
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
