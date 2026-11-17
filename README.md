# AI Race Engineer

Assistente Windows per Assetto Corsa: chat locale con Ollama, riconoscimento PTT italiano con Whisper e telemetria dal gioco. Non richiede AiMotor, chiavi API o crediti cloud.

## Requisiti

- Windows 10/11 x64.
- Assetto Corsa per la telemetria di gioco e l’app Python in pista.
- Ollama per le risposte generative; il modello è scaricabile dalle impostazioni dell’app.
- Connessione Internet solo al primo avvio Whisper, per scaricare il modello vocale da circa 77 MB. In seguito la trascrizione è locale.
- .NET 10 SDK solo se si compila dai sorgenti. Il pacchetto Release è autonomo.

## Installazione del pacchetto Windows

1. Scarica `AiRaceEngineer-win-x64.zip` dalla sezione **Releases** di GitHub ed estrailo in una cartella a scelta.
2. Avvia `AiRaceEngineer.exe` e, in **Impostazioni**, verifica Ollama e scarica il modello conversazionale configurato.
3. Installa il ponte telemetrico nell’installazione di Assetto Corsa:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\scripts\install-ac-bridge.ps1 -AssettoCorsaPath "D:\SteamLibrary\steamapps\common\assettocorsa"
   ```

   Esegui il comando dalla cartella della repository. Sostituisci il percorso con quello di Assetto Corsa sul PC. Lo script salva una copia di `python.ini`, attiva l’app senza cancellare le altre impostazioni e installa il file in `apps\python\AiRaceEngineer`.

4. Riavvia Assetto Corsa, avvia una sessione e apri **AiRaceEngineer** dal selettore delle app in pista.
5. Tieni Ollama avviato. Al primo uso PTT l’app scarica e verifica Whisper tiny. Il file vocale viene conservato in `%OLLAMA_MODELS%\AiRaceEngineer\Whisper`; senza la variabile, usa `Documenti\AiRaceEngineer\Models\AiRaceEngineer\Whisper`. I modelli conversazionali di Ollama restano nella cartella configurata in Ollama.

## Build da sorgente

```powershell
dotnet publish .\AiRaceEngineer.csproj -c Release -r win-x64 --self-contained true -o .\publish\win-x64
```

Oppure avvia `scripts\build.ps1`. L’output include l’app Python AC in `ac_bridge\AiRaceEngineer.py`.

La cartella del programma è `publish\win-x64`; avvialo con:

```powershell
.\publish\win-x64\AiRaceEngineer.exe
```

Da questa repository installa anche il ponte nel gioco con:

```powershell
.\scripts\install-ac-bridge.ps1 -AssettoCorsaPath "PERCORSO\assettocorsa"
```

GitHub Actions compila automaticamente Windows x64 a ogni push su `main` e allega il pacchetto alle Release create con un tag `v*`.

Per pubblicare una versione: `git tag v0.1.0` e `git push origin main v0.1.0`. GitHub eseguirà la build e allegherà il pacchetto alla Release.

### Pubblicazione manuale su GitHub

L’autenticazione GitHub non è inclusa nella repository. Crea sul sito GitHub una repository privata vuota, senza aggiungere README o licenza, poi esegui dalla cartella della repository:

```powershell
.\scripts\publish-github.ps1 -Remote "https://github.com/OWNER/REPOSITORY.git"
```

Puoi usare anche un URL SSH se la tua chiave è già registrata su GitHub. Dopo il primo push, i commit su `main` avviano la build automatica.

## Funzioni incluse

- Lettura dei blocchi Shared Memory di Assetto Corsa: dati fisici, giri, posizione, pit lane, penalità e bandiera corrente. Legge inoltre il campo completo (nomi, posizioni, giri, spline, velocità, pit, input e tempi) da una piccola app Python AC installata in `apps/python/AiRaceEngineer`.
- Risposte radio in italiano con valutazioni leggibili di danni, gomme, carburante e distacchi; il riepilogo gara mette in evidenza anche penalità, bandiere e cali di ritmo quando disponibili.
- Report automatico a ogni giro valido, con confronto al riferimento personale, chiamate di gomme/carburante e strategia box.
- Avvisi locali per bandiere/penalità del gioco, ingresso e uscita box, consumo carburante, usura gomme e possibile incidente.
- Stima carburante/usura e consiglio di pit stop.
- Suggerimenti di assetto generici dal modello locale.
- Registrazione CSV della telemetria in Documenti\AiRaceEngineer\Sessions.
- Push-to-talk con F8 o pulsante Xbox Menu/Start su tutti gli slot XInput, con bip distinti alla pressione e al rilascio; l’audio viene acquisito dal microfono predefinito e trascritto in italiano da Whisper tiny in locale.
- Coach curva che conserva il giro personale più veloce per auto e pista, con bip distinti di frenata e riapertura del gas dal giro successivo.
- Voce neurale italiana locale facoltativa: si scarica una volta insieme al motore Piper e viene salvata accanto ai modelli locali. Nelle impostazioni puoi scegliere anche una voce Windows e regolare la velocità fra 75% e 135%.
- Comandi radio: “parla più veloce”, “parla più piano”, “dimmi i distacchi”, “non dirmi i distacchi”, “ogni giro dimmi il carburante”, “box al giro 12”, “annulla piano box”, “non parlare per il prossimo giro”, “ingegnere on/off”.
- La radio corregge le trascrizioni vocali comuni prima di rispondere: ad esempio “Dania macchina” viene interpretato come danni alla macchina e “ciao davanti” come chi hai davanti. Se una frase non corrisponde a un comando di gara, chiede di ripeterla invece di inventare consigli generici.
- Opzione finestra sempre in primo piano.
- Pulsante per scaricare il modello configurato attraverso l’API locale Ollama.

## Avvio

Il coach curva salva i riferimenti in `Documenti\AiRaceEngineer\corner-coach.json`, distinti per auto e pista. Sono punti imparati dai giri guidati, non una mappa ufficiale o preimpostata.

Il ponte legge nomi, posizioni, giri, tempi, velocità, spline, pit lane e input disponibili di ogni auto. La memoria standard AC non contiene il campo avversari. Le distanze tra rivali sono stime dalla progressione sul giro. La telemetria standard AC espone temperature aria/asfalto e grip, ma non una misura completa di pioggia/meteo o danno/usura delle auto avversarie.

## Confini dei dati

Il programma mostra lo stato bandiera che Assetto Corsa pubblica nella sua memoria condivisa. La memoria standard non espone tutti i dati dei rivali, meteo dettagliato, né comandi per imporre una Safety Car o una VSC: l’app non simula questi stati e non cambia le regole/overlay del gioco. Il rilevamento d’incidente è una stima basata su calo di velocità e danno.

La pagina Set-up usa il modello come consulente generico; non contiene librerie proprietarie di assetti o strategie di altri prodotti. Il coach curva usa punti imparati dai giri locali e non sostituisce un database professionale di riferimenti.

## Codice di terze parti

Vendor/AssettoCorsaSharedMemory include la libreria di lettura AC di Mathias Djärv, con licenza MIT e copyright incluso. Il codice è stato integrato nel progetto per collegarsi ai formati Shared Memory pubblicati per Assetto Corsa.

Riferimenti:
- Assetto Corsa Shared Memory: https://labs.assettocorsa.jp/documents/reference/shared_memory
- Libreria C# MIT: https://github.com/mdjarv/assettocorsasharedmemory
- Ollama Windows e OLLAMA_MODELS: https://github.com/ollama/ollama/blob/main/docs/windows.mdx
- Whisper.net: https://github.com/sandrohanea/whisper.net
- Modello multilingue Whisper tiny: https://huggingface.co/ggerganov/whisper.cpp

Il codice della libreria Shared Memory in `Vendor/AssettoCorsaSharedMemory` mantiene il file di licenza MIT e l’attribuzione dell’autore. Le altre dipendenze mantengono le rispettive licenze NuGet.
