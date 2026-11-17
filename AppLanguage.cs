namespace AiRaceEngineer;

/// <summary>Small local UI language selector. More languages can be added without changing settings format.</summary>
public static class AppLanguage
{
    public static string Current { get; set; } = "it";
    public static bool IsEnglish => string.Equals(Current, "en", StringComparison.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> ItalianToEnglish = new(StringComparer.Ordinal)
    {
        ["Assistente locale per Assetto Corsa"] = "Local assistant for Assetto Corsa",
        ["Sempre in primo piano"] = "Always on top",
        ["Gara"] = "Race", ["VELOCITÀ"] = "SPEED", ["CARBURANTE"] = "FUEL",
        ["GIRO / POSIZIONE"] = "LAP / POSITION", ["GOMME"] = "TYRES", ["BANDIERA AC"] = "AC FLAG",
        ["VERDE / NESSUNA"] = "GREEN / NONE", ["Radio e avvisi"] = "Radio and alerts",
        ["INGEGNERE DI GARA"] = "RACE ENGINEER", ["Parla con l’ingegnere"] = "Talk to engineer",
        ["Modello locale"] = "Local model", ["Chat AI"] = "AI chat", ["Invia"] = "Send",
        ["Voce on/off"] = "Voice on/off", ["Report sessione"] = "Session report",
        ["Report automatico giro per giro"] = "Automatic lap-by-lap report", ["Strategia pit"] = "Pit strategy",
        ["Stima strategia"] = "Strategy estimate", ["Giri gara previsti"] = "Planned race laps",
        ["Consumo medio (L/giro)"] = "Average use (L/lap)", ["Usura media gomme (%/giro)"] = "Average tyre wear (%/lap)",
        ["Calcola"] = "Calculate", ["Consiglio"] = "Advice", ["Set-up"] = "Setup",
        ["Consigli di assetto dal modello locale"] = "Setup advice from local model", ["Analizza assetto"] = "Analyse setup",
        ["Impostazioni"] = "Settings", ["Modello locale Ollama"] = "Local Ollama model",
        ["Indirizzo API locale"] = "Local API address", ["Modello"] = "Model", ["Salva"] = "Save",
        ["Verifica Ollama"] = "Check Ollama", ["Scarica modello"] = "Download model",
        ["Voce, radio e controller"] = "Voice, radio and controller", ["Riconoscimento vocale"] = "Speech recognition",
        ["Voce dell’ingegnere"] = "Engineer voice", ["Velocità risposta radio"] = "Radio response speed",
        ["Voce neurale italiana locale (più fluida)"] = "Local Italian neural voice (smoother)",
        ["Prepara voce neurale"] = "Prepare neural voice", ["Ascolta prova"] = "Play test",
        ["Ingegnere di gara attivo"] = "Race engineer enabled", ["Coach curva · beep"] = "Corner coach · beeps",
        ["Bip per frenata"] = "Braking beep", ["Bip per riapertura gas"] = "Throttle beep",
        ["Anticipo beep"] = "Beep advance", ["Lingua / Language"] = "Language",
        ["Dati letti dal gioco: velocità, carburante, giro, gomme, pit lane, penalità e bandiere."] = "Game data: speed, fuel, lap, tyres, pit lane, penalties and flags.",
        ["Push-to-talk: tieni premuto F8 o il pulsante Menu (tre linee) del controller Xbox. Un bip all’inizio e uno al rilascio confermano il tasto; il riconoscimento usa il microfono predefinito di Windows senza inviare l’audio a un servizio cloud."] = "Push-to-talk: hold F8 or the Xbox Menu button. A beep at press and release confirms the key; recognition uses the default Windows microphone locally."
    };

    public static string TranslateStatic(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        if (IsEnglish) return ItalianToEnglish.TryGetValue(text, out var english) ? english : text;
        var italian = ItalianToEnglish.FirstOrDefault(pair => string.Equals(pair.Value, text, StringComparison.Ordinal)).Key;
        return string.IsNullOrEmpty(italian) ? text : italian;
    }
}
