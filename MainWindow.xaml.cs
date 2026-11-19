using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AiRaceEngineer;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly OllamaClient _ollama = new();
    private readonly AssettoTelemetryService _telemetry = new();
    private readonly VoiceService _voice = new();
    private readonly PushToTalkService _ptt = new();
    private readonly CornerCoach _coach = new();
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly string _sessionDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AiRaceEngineer", "Sessions");
    private DateTime _lastCsvWrite = DateTime.MinValue;
    private bool _manualListening;
    private bool _sending;
    private bool _lowFuelAnnounced;
    private bool _highWearAnnounced;
    private DateTime _lastHealthCheck = DateTime.MinValue;
    private DateTime _pttStartedAt = DateTime.MinValue;
    private DateTime _pttReleasedAt = DateTime.MinValue;
    private DateTime _lastVoiceDiagnosticAt = DateTime.MinValue;
    private bool _pttActive;
    private bool _recognizedForPtt;
    private int _lastLapRadio = -1;
    private string _coachStatus = "Coach curva: avvia un giro pulito per imparare frenate e riapertura del gas.";

    public MainWindow()
    {
        InitializeComponent();
        if (string.IsNullOrWhiteSpace(_settings.Language))
        {
            var italian = MessageBox.Show(
                "Scegli la lingua / Choose language.\n\nSì = Italiano\nNo = English",
                "AI Race Engineer", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            _settings.Language = italian ? "it" : "en";
            _settings.Save();
        }
        AppLanguage.Current = _settings.Language;
        OllamaUrlInput.Text = _settings.OllamaUrl;
        ModelInput.Text = _settings.Model;
        RecognitionModelCombo.SelectedIndex = _settings.SpeechRecognitionModel.ToLowerInvariant() switch
        {
            "tiny" => 3,
            "base" => 2,
            "turbo-q5" or "turbo" or "large-v3-turbo" => 1,
            _ => 0
        };
        OutputVoiceCombo.ItemsSource = _voice.InstalledItalianVoices;
        if (!string.IsNullOrWhiteSpace(_settings.OutputVoice) && _voice.InstalledItalianVoices.Contains(_settings.OutputVoice))
            OutputVoiceCombo.SelectedItem = _settings.OutputVoice;
        else if (_voice.InstalledItalianVoices.Count > 0)
            OutputVoiceCombo.SelectedIndex = 0;
        SpeechSpeedSlider.Value = Math.Clamp(_settings.SpeechRatePercent, 75, 135);
        SpeechSpeedText.Text = $"{_settings.SpeechRatePercent}%";
        NaturalVoiceCheck.IsChecked = _settings.UseNaturalVoice;
        RaceEngineerCheck.IsChecked = _settings.RaceEngineerEnabled;
        CoachBrakeBeepCheck.IsChecked = _settings.CoachBrakeBeeps;
        CoachThrottleBeepCheck.IsChecked = _settings.CoachThrottleBeeps;
        CoachLeadSlider.Value = Math.Clamp(_settings.CoachCueLeadMilliseconds, 0, 1000);
        CoachLeadText.Text = FormatCoachLead(_settings.CoachCueLeadMilliseconds);
        LanguageCombo.SelectedIndex = AppLanguage.IsEnglish ? 1 : 0;
        _voice.ConfigureOutput(_settings.OutputVoice, _settings.SpeechRatePercent, _settings.UseNaturalVoice);
        OutputVoiceStatus.Text = _voice.OutputVoiceStatus;
        ModelFolderText.Text = $"Cartella modelli Ollama: {Environment.GetEnvironmentVariable("OLLAMA_MODELS") ?? @"%USERPROFILE%\.ollama\models"}";
        _ptt.XboxEnabled = _settings.XboxMenuPushToTalk;
        VoiceStatus.Text = _voice.Status;
        _telemetry.Updated += OnTelemetryUpdated;
        _telemetry.RaceEventDetected += OnRaceEvent;
        _voice.Recognized += OnVoiceRecognized;
        _voice.Diagnostic += OnVoiceDiagnostic;
        _voice.StatusChanged += OnVoiceStatusChanged;
        _voice.OutputVoiceStatusChanged += OnOutputVoiceStatusChanged;
        _ptt.Changed += OnPushToTalkChanged;
        _refresh.Tick += RefreshUi;
        _refresh.Start();
        Closed += OnClosed;
        Loaded += (_, _) => ApplyLanguageToVisualTree(this);
        _ = CheckOllamaAsync();
        _ = _voice.InitializeAsync(_settings.SpeechRecognitionModel);
        if (_settings.UseNaturalVoice) _ = _voice.PrepareNaturalVoiceAsync();
        EventsList.Items.Add("Pronto. Avvia Assetto Corsa per collegare la telemetria condivisa.");
        ChatHistory.Text = "AI RACE ENGINEER · modello locale\n\nCollega Assetto Corsa e chiedi carburante, gomme, bandiere, penalità o un riepilogo stint.\n";
    }

    private void OnTelemetryUpdated(TelemetrySnapshot snapshot)
    {
        var coach = _coach.Update(snapshot, _settings.CoachBrakeBeeps, _settings.CoachThrottleBeeps, _settings.CoachCueLeadMilliseconds);
        if (coach.Status is not null) _coachStatus = coach.Status;
        foreach (var cue in coach.Cues)
        {
            if (cue == CornerCueType.Brake) AudioCue.PlayBrake();
            else AudioCue.PlayThrottle();
        }
        if (snapshot.FuelPerLap > 0 && snapshot.NumberOfLaps > 0 && snapshot.CompletedLaps > 0)
        {
            var lapsLeft = Math.Max(0, snapshot.NumberOfLaps - snapshot.CompletedLaps);
            if (!_lowFuelAnnounced && snapshot.FuelLitres < snapshot.FuelPerLap * (lapsLeft + 1.3f))
            {
                _lowFuelAnnounced = true;
                OnRaceEvent(new(DateTime.Now, "STRATEGIA", "Carburante stimato vicino al limite: verifica il target di gara e la sosta."));
            }
        }
        if (!_highWearAnnounced && snapshot.TyreWearPercent >= 70)
        {
            _highWearAnnounced = true;
            OnRaceEvent(new(DateTime.Now, "GOMME", $"Usura gomme media {snapshot.TyreWearPercent:0}%. Valuta la sosta."));
        }
        if (snapshot.CompletedLaps > _lastLapRadio && snapshot.LastLapTimeSeconds is >= 10 and <= 900)
        {
            _lastLapRadio = snapshot.CompletedLaps;
            var report = RaceEngineerControl.BuildLapCall(snapshot, _settings);
            Dispatcher.BeginInvoke(() =>
            {
                EventsList.Items.Insert(0, $"[REPORT GIRO] {report}");
                while (EventsList.Items.Count > 100) EventsList.Items.RemoveAt(EventsList.Items.Count - 1);
                LapReports.Items.Insert(0, report);
                while (LapReports.Items.Count > 100) LapReports.Items.RemoveAt(LapReports.Items.Count - 1);
                if (_settings.RaceEngineerEnabled)
                {
                    if (_settings.SilentLaps > 0) _settings.SilentLaps--;
                    else if (_settings.SpeakReplies) _voice.Speak(report);
                }
            });
        }
        if ((DateTime.Now - _lastCsvWrite).TotalSeconds >= 1)
        {
            _lastCsvWrite = DateTime.Now;
            try
            {
                Directory.CreateDirectory(_sessionDirectory);
                var file = Path.Combine(_sessionDirectory, $"telemetria-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
                if (!File.Exists(file))
                    File.WriteAllText(file, "timestamp,car,track,speed_kmh,fuel_l,gear,rpm,lap,position,tyre,wear_pct,tyre_temp_c,flag,penalty_s,pit_lane,pit_box\r\n");
                var row = string.Join(",", new[]
                {
                    Csv(snapshot.Timestamp.ToString("O", CultureInfo.InvariantCulture)),
                    Csv(snapshot.Car), Csv(snapshot.Track),
                    snapshot.SpeedKmh.ToString("0.0", CultureInfo.InvariantCulture),
                    snapshot.FuelLitres.ToString("0.00", CultureInfo.InvariantCulture),
                    snapshot.Gear.ToString(CultureInfo.InvariantCulture), snapshot.Rpm.ToString(CultureInfo.InvariantCulture),
                    snapshot.CompletedLaps.ToString(CultureInfo.InvariantCulture), snapshot.Position.ToString(CultureInfo.InvariantCulture),
                    Csv(snapshot.TyreCompound), snapshot.TyreWearPercent.ToString("0.00", CultureInfo.InvariantCulture),
                    snapshot.TyreTempC.ToString("0.0", CultureInfo.InvariantCulture), Csv(snapshot.FlagText),
                    snapshot.PenaltyTime.ToString("0.0", CultureInfo.InvariantCulture),
                    snapshot.InPitLane ? "1" : "0", snapshot.InPit ? "1" : "0"
                });
                File.AppendAllText(file, row + "\r\n");
            }
            catch { }
        }
    }

    private void OnRaceEvent(RaceEvent ev)
    {
        Dispatcher.BeginInvoke(() =>
        {
            EventsList.Items.Insert(0, $"[{ev.Time:HH:mm:ss}] {ev.Category}: {ev.Message}");
            while (EventsList.Items.Count > 100) EventsList.Items.RemoveAt(EventsList.Items.Count - 1);
            if (ev.Category is "BANDIERA" or "PENALITÀ" or "INCIDENTE")
                _voice.Speak(ev.Message);
        });
    }

    private void RefreshUi(object? sender, EventArgs e)
    {
        RefreshVoiceDiagnostic();
        var t = _telemetry.GetFreshSnapshot();
        if (t.Connected && (DateTime.Now - t.Timestamp).TotalSeconds > 3) t = t with { Connected = false };
        ConnectionDot.Fill = t.Connected ? Brushes.LimeGreen : Brushes.Gray;
        var fieldCount = t.FieldCars?.Count ?? 0;
        ConnectionText.Text = t.Connected
            ? $"Connesso · {t.Car} · {t.Track} · campo {fieldCount} auto"
            : "In attesa di Assetto Corsa";
        EngineerStatus.Text = t.Connected
            ? $"Giro {Math.Max(1, t.CompletedLaps + 1)} · posizione {t.Position} · {t.FlagText} · {(t.InPitLane ? "in pit lane" : "in pista")}" + (fieldCount == 0 ? " · apri l’app AiRaceEngineer nel menu app AC per leggere i rivali" : "")
            : "Avvia una sessione Assetto Corsa; leggo la telemetria dai tre blocchi di memoria condivisa.";
        CoachStatus.Text = _coachStatus;
        SpeedText.Text = t.Connected ? $"{t.SpeedKmh:0} km/h" : "— km/h";
        FuelText.Text = t.Connected ? $"{t.FuelLitres:0.0} L" : "— L";
        LapText.Text = t.Connected ? $"{t.CompletedLaps}/{(t.NumberOfLaps > 0 ? t.NumberOfLaps : "∞")} · P{t.Position}" : "— / —";
        TyreText.Text = t.Connected ? $"{t.TyreCompound} · {t.TyreWearPercent:0}%" : "—";
        FlagText.Text = t.Connected ? t.FlagText : "VERDE / NESSUNA";
        FlagCard.Background = t.Flag switch
        {
            2 => new SolidColorBrush(Color.FromRgb(89, 71, 22)),
            3 or 6 => new SolidColorBrush(Color.FromRgb(93, 34, 34)),
            5 => new SolidColorBrush(Color.FromRgb(68, 68, 68)),
            _ => new SolidColorBrush(Color.FromRgb(32, 39, 45))
        };
        if ((DateTime.Now - _lastHealthCheck).TotalSeconds > 15) _ = CheckOllamaAsync();
    }

    private async Task CheckOllamaAsync()
    {
        _lastHealthCheck = DateTime.Now;
        try
        {
            var status = await _ollama.GetHealthAsync(_settings.OllamaUrl, _settings.Model);
            await Dispatcher.InvokeAsync(() => { ModelStatus.Text = status; SettingsStatus.Text = status; });
        }
        catch (Exception ex) { await Dispatcher.InvokeAsync(() => ModelStatus.Text = ex.Message); }
    }

    private async Task SendQuestionAsync(string? prompt = null, bool fromRadio = false)
    {
        var question = (prompt ?? ChatInput.Text).Trim();
        if (question.Length == 0 || _sending) return;
        var normalizedRadioQuestion = fromRadio ? RadioIntent.NormalizeQuestion(question) : null;
        var routedQuestion = normalizedRadioQuestion ?? question;
        var telemetry = GetUsableSnapshot();
        var factualAnswer = RaceEngineerControl.TryApply(routedQuestion, _settings, _voice)
            ?? RaceEngineerFacts.TryAnswer(routedQuestion, telemetry);
        if (factualAnswer is not null)
        {
            _sending = true;
            ChatInput.Clear();
            ChatHistory.AppendText($"\n\nTU: {question}\n\nINGEGNERE: {factualAnswer}\n");
            ChatHistory.ScrollToEnd();
            if (_settings.SpeakReplies) _voice.Speak(factualAnswer);
            _sending = false;
            return;
        }
        _sending = true;
        ChatInput.Clear();
        ChatHistory.AppendText($"\n\nTU: {question}\n\nINGEGNERE: ");
        ChatHistory.ScrollToEnd();
        try
        {
            // When no race-data intent matched, let the model answer the
            // question as a general one instead of forcing a canned reply.
            var answer = await _ollama.AskAsync(_settings.OllamaUrl, _settings.Model, question, telemetry,
                radio: fromRadio, general: normalizedRadioQuestion is null);
            ChatHistory.AppendText(answer + "\n");
            if (_settings.SpeakReplies) _voice.Speak(answer);
        }
        catch (Exception ex)
        {
            ChatHistory.AppendText($"Non riesco a contattare il modello locale. Apri Ollama, verifica il modello nelle impostazioni. Dettaglio: {ex.Message}\n");
        }
        finally { _sending = false; ChatHistory.ScrollToEnd(); }
    }

    private TelemetrySnapshot GetUsableSnapshot()
    {
        var t = _telemetry.GetFreshSnapshot(forceFieldRead: true);
        return t.Connected && (DateTime.Now - t.Timestamp).TotalSeconds <= 3 ? t : t with { Connected = false };
    }

    private void OnVoiceRecognized(string phrase)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _recognizedForPtt = true;
            _pttActive = false;
            VoiceStatus.Text = $"Riconosciuto dal microfono: «{phrase}»";
            ChatInput.Text = phrase;
            _ = SendQuestionAsync(phrase, fromRadio: true);
        });
    }

    private void OnVoiceDiagnostic(string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _lastVoiceDiagnosticAt = DateTime.Now;
            VoiceStatus.Text = message;
        });
    }

    private void OnVoiceStatusChanged(string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_pttActive) VoiceStatus.Text = message;
        });
    }

    private void OnOutputVoiceStatusChanged(string message)
    {
        Dispatcher.BeginInvoke(() => OutputVoiceStatus.Text = message);
    }

    private void RefreshVoiceDiagnostic()
    {
        if (_pttActive && (DateTime.Now - _pttStartedAt).TotalMilliseconds > 1200 &&
            (DateTime.Now - _lastVoiceDiagnosticAt).TotalMilliseconds > 900)
        {
            var audioRecent = _voice.LastAudioAt >= _pttStartedAt && (DateTime.Now - _voice.LastAudioAt).TotalMilliseconds < 900;
            VoiceStatus.Text = audioRecent
                ? $"Microfono riceve audio · livello {_voice.LastAudioLevel}% · parla ora"
                : "PTT attivo ma WASAPI non riceve audio. Controlla mute, autorizzazioni e microfono predefinito Windows.";
        }
        else if (!_pttActive && !_recognizedForPtt && _pttReleasedAt != DateTime.MinValue &&
            (DateTime.Now - _pttReleasedAt).TotalMilliseconds is > 1500 and < 8000 &&
            (DateTime.Now - _lastVoiceDiagnosticAt).TotalMilliseconds > 1500)
        {
            var audioReceived = _voice.LastAudioAt >= _pttStartedAt;
            VoiceStatus.Text = audioReceived
                ? $"Audio ricevuto (livello {_voice.LastAudioLevel}%), ma nessuna frase riconosciuta. Prova una frase breve e vicina al microfono."
                : "Nessun audio acquisito durante il PTT. Controlla mute, autorizzazioni e microfono predefinito Windows.";
            _pttReleasedAt = DateTime.MinValue;
        }
    }

    private void OnPushToTalkChanged(bool down, string source)
    {
        AudioCue.PlayPushToTalk(down);
        Dispatcher.BeginInvoke(() =>
        {
            if (down) VoiceStatus.Text = $"PTT {source} premuto: ascolto attivo. Rilascia per inviare.";
            SetListening(down);
        });
    }

    private void SetListening(bool down)
    {
        if (down)
        {
            try
            {
                _pttActive = true;
                _recognizedForPtt = false;
                _pttStartedAt = DateTime.Now;
                _pttReleasedAt = DateTime.MinValue;
                _voice.StartListening();
                RadioButton.Content = "Ascolto… rilascia il tasto";
            }
            catch (Exception ex) { _pttActive = false; VoiceStatus.Text = $"Microfono non avviato: {ex.Message}"; }
        }
        else
        {
            _pttActive = false;
            _pttReleasedAt = DateTime.Now;
            _ = _voice.StopListeningAsync();
            VoiceStatus.Text = "PTT rilasciato · trascrizione locale in corso…";
            RadioButton.Content = "Parla con l’ingegnere";
        }
    }

    private void AlwaysOnTopCheck_Changed(object sender, RoutedEventArgs e) => Topmost = AlwaysOnTopCheck.IsChecked == true;

    private void OutputVoice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _settings.OutputVoice = OutputVoiceCombo.SelectedItem as string ?? "";
        ApplyOutputVoiceSettings();
    }

    private void RecognitionModel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || RecognitionModelCombo.SelectedItem is not ComboBoxItem selected) return;
        _settings.SpeechRecognitionModel = selected.Tag as string ?? "turbo-q5";
        _settings.SpeechRecognitionModelExplicit = true;
        _settings.Save();
        VoiceStatus.Text = "Cambio modello vocale in corso…";
        _ = _voice.InitializeAsync(_settings.SpeechRecognitionModel);
    }

    private void SpeechSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        _settings.SpeechRatePercent = (int)Math.Round(SpeechSpeedSlider.Value);
        SpeechSpeedText.Text = $"{_settings.SpeechRatePercent}%";
        ApplyOutputVoiceSettings();
    }

    private void NaturalVoiceCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _settings.UseNaturalVoice = NaturalVoiceCheck.IsChecked == true;
        ApplyOutputVoiceSettings();
        if (_settings.UseNaturalVoice) _ = _voice.PrepareNaturalVoiceAsync();
    }

    private async void PrepareNaturalVoice_Click(object sender, RoutedEventArgs e)
    {
        OutputVoiceStatus.Text = "Preparazione della voce neurale italiana…";
        await _voice.PrepareNaturalVoiceAsync();
        OutputVoiceStatus.Text = _voice.OutputVoiceStatus;
    }

    private void TestVoice_Click(object sender, RoutedEventArgs e)
    {
        _voice.Speak("Radio check. Ingegnere pronto.");
    }

    private void RaceEngineerCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _settings.RaceEngineerEnabled = RaceEngineerCheck.IsChecked == true;
        _settings.Save();
    }

    private void CoachBeeps_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _settings.CoachBrakeBeeps = CoachBrakeBeepCheck.IsChecked == true;
        _settings.CoachThrottleBeeps = CoachThrottleBeepCheck.IsChecked == true;
        _settings.Save();
    }

    private void CoachLeadSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        _settings.CoachCueLeadMilliseconds = (int)Math.Round(CoachLeadSlider.Value);
        CoachLeadText.Text = FormatCoachLead(_settings.CoachCueLeadMilliseconds);
        _settings.Save();
    }

    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || LanguageCombo.SelectedItem is not ComboBoxItem selected) return;
        _settings.Language = selected.Tag as string ?? "it";
        AppLanguage.Current = _settings.Language;
        _settings.Save();
        ApplyLanguageToVisualTree(this);
    }

    private static string FormatCoachLead(int milliseconds) => milliseconds <= 0
        ? AppLanguage.IsEnglish ? "At marker" : "Sul punto"
        : AppLanguage.IsEnglish ? $"{milliseconds / 1000d:0.##} s early" : $"{milliseconds / 1000d:0.##} s prima";

    private static void ApplyLanguageToVisualTree(DependencyObject root)
    {
        if (root is TextBlock textBlock) textBlock.Text = AppLanguage.TranslateStatic(textBlock.Text);
        if (root is ContentControl contentControl && contentControl.Content is string content)
            contentControl.Content = AppLanguage.TranslateStatic(content);
        if (root is HeaderedContentControl headeredControl && headeredControl.Header is string header)
            headeredControl.Header = AppLanguage.TranslateStatic(header);
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            ApplyLanguageToVisualTree(VisualTreeHelper.GetChild(root, i));
    }

    private void ApplyOutputVoiceSettings()
    {
        _voice.ConfigureOutput(_settings.OutputVoice, _settings.SpeechRatePercent, _settings.UseNaturalVoice);
        _settings.Save();
    }

    private void ChatInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            _ = SendQuestionAsync();
        }
    }

    private void SendButton_Click(object sender, RoutedEventArgs e) => _ = SendQuestionAsync();

    private void RadioButton_Click(object sender, RoutedEventArgs e) =>
        _ = SendQuestionAsync("Fammi un riepilogo radio: bandiere, penalità, carburante, gomme e cosa devo fare adesso.");

    private void PushToTalkButton_Click(object sender, RoutedEventArgs e)
    {
        _manualListening = !_manualListening;
        SetListening(_manualListening);
    }

    private async void CalculateStrategy_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PlannedLapsInput.Text, out var laps) || laps <= 0 ||
            !double.TryParse(FuelPerLapInput.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var fuelPerLap) || fuelPerLap <= 0 ||
            !double.TryParse(WearPerLapInput.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var wearPerLap) || wearPerLap <= 0)
        {
            StrategyResult.Text = "Inserisci giri e consumi validi. Usa il punto come separatore decimale.";
            return;
        }
        var t = GetUsableSnapshot();
        var currentFuel = t.Connected ? t.FuelLitres : 0;
        var remainLaps = t.Connected && t.NumberOfLaps > 0 ? Math.Max(0, t.NumberOfLaps - t.CompletedLaps) : laps;
        var requiredFuel = remainLaps * fuelPerLap * 1.05;
        var tyreStint = 70.0 / wearPerLap;
        var needsPit = (t.Connected && currentFuel > 0 && currentFuel < requiredFuel) || remainLaps > tyreStint;
        StrategyResult.Text = needsPit
            ? $"Sosta consigliata entro circa {Math.Max(1, (int)Math.Floor(tyreStint))} giri per usura gomme. Carburante stimato per {remainLaps} giri: {requiredFuel:0.0} L con 5% di margine; a bordo: {(t.Connected ? currentFuel.ToString("0.0") : "dato non disponibile")} L. Rivedi la stima dopo un giro completo."
            : $"Con la stima attuale puoi completare lo stint senza una sosta obbligatoria per gomme. Servono circa {requiredFuel:0.0} L per {remainLaps} giri con margine. Usura media: {wearPerLap:0.0}%/giro.";
        if (t.Connected && t.FuelPerLap > 0)
        {
            FuelPerLapInput.Text = t.FuelPerLap.ToString("0.00", CultureInfo.InvariantCulture);
            if (t.WearPerLapPercent > 0) WearPerLapInput.Text = t.WearPerLapPercent.ToString("0.0", CultureInfo.InvariantCulture);
        }
        await Task.CompletedTask;
    }

    private async void SetupButton_Click(object sender, RoutedEventArgs e)
    {
        SetupOutput.Text = "Analisi locale in corso…";
        var t = GetUsableSnapshot();
        var prompt = $"Proponi una diagnosi di assetto prudente usando questi dettagli: {SetupContextInput.Text}. Auto rilevata: {t.Car}; pista: {t.Track}; pneumatico: {t.TyreCompound}. Fornisci poche modifiche, spiega l'effetto atteso e indica cosa verificare con un giro di prova.";
        try { SetupOutput.Text = await _ollama.AskAsync(_settings.OllamaUrl, _settings.Model, prompt, t); }
        catch (Exception ex) { SetupOutput.Text = $"Ollama non disponibile: {ex.Message}"; }
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        _settings.OllamaUrl = OllamaUrlInput.Text.Trim().TrimEnd('/');
        _settings.Model = ModelInput.Text.Trim();
        _settings.ModelSelectionExplicit = true;
        if (RecognitionModelCombo.SelectedItem is ComboBoxItem recognition)
        {
            _settings.SpeechRecognitionModel = recognition.Tag as string ?? "turbo-q5";
            _settings.SpeechRecognitionModelExplicit = true;
        }
        _settings.OutputVoice = OutputVoiceCombo.SelectedItem as string ?? _settings.OutputVoice;
        _settings.SpeechRatePercent = (int)Math.Round(SpeechSpeedSlider.Value);
        _settings.UseNaturalVoice = NaturalVoiceCheck.IsChecked == true;
        _settings.RaceEngineerEnabled = RaceEngineerCheck.IsChecked == true;
        _settings.CoachBrakeBeeps = CoachBrakeBeepCheck.IsChecked == true;
        _settings.CoachThrottleBeeps = CoachThrottleBeepCheck.IsChecked == true;
        _settings.CoachCueLeadMilliseconds = (int)Math.Round(CoachLeadSlider.Value);
        _settings.Language = LanguageCombo.SelectedItem is ComboBoxItem language ? language.Tag as string ?? "it" : _settings.Language;
        _voice.ConfigureOutput(_settings.OutputVoice, _settings.SpeechRatePercent, _settings.UseNaturalVoice);
        _settings.Save();
        SettingsStatus.Text = "Impostazioni salvate.";
        _ = CheckOllamaAsync();
    }

    private async void CheckOllama_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings_Click(sender, e);
        SettingsStatus.Text = await _ollama.GetHealthAsync(_settings.OllamaUrl, _settings.Model);
    }

    private async void PullModel_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings_Click(sender, e);
        PullProgress.Value = 0;
        SettingsStatus.Text = $"Download di {_settings.Model} tramite Ollama. I file vengono salvati nella cartella modelli configurata da Ollama.";
        var progress = new Progress<string>(s =>
        {
            SettingsStatus.Text = string.IsNullOrWhiteSpace(s) ? "Download…" : s;
            var percentAt = s.LastIndexOf('%');
            if (percentAt > 0)
            {
                var start = percentAt - 1;
                while (start > 0 && char.IsDigit(s[start - 1])) start--;
                if (double.TryParse(s[start..percentAt], CultureInfo.InvariantCulture, out var percent))
                    PullProgress.Value = Math.Clamp(percent, 0, 100);
            }
        });
        try
        {
            await _ollama.PullAsync(_settings.OllamaUrl, _settings.Model, progress);
            PullProgress.Value = 100;
            SettingsStatus.Text = $"Modello {_settings.Model} pronto.";
            await CheckOllamaAsync();
        }
        catch (Exception ex) { SettingsStatus.Text = $"Download non riuscito. Avvia Ollama e riprova. {ex.Message}"; }
    }

    private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    private void OnClosed(object? sender, EventArgs e)
    {
        _refresh.Stop();
        _ptt.Dispose();
        _voice.Dispose();
        _telemetry.Dispose();
        _settings.Save();
    }
}
