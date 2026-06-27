# Scriba (.NET / WPF) — de stabiele Windows-versie

**Scriba** is een native Windows-app voor **offline spraak naar tekst**
(Nederlands & Engels), geschreven in C# / WPF. Bedoeld als de **stabiele,
makkelijk te distribueren** variant: een self-contained app, geen Python nodig.

## Waarom deze versie stabieler is

- **Whisper.net** (whisper.cpp) als transcriptie-motor — een zelfstandige native
  engine, geen Python-ML-stack om te bundelen.
- **NAudio / WASAPI** voor opname, inclusief betrouwbare **loopback** voor
  systeem-/vergaderaudio.
- **Self-contained** via `dotnet publish` — draait zonder geïnstalleerde runtime.

## Functies

- 🎙️ Opnemen via microfoon of systeemaudio (apparaatkeuze).
- 📊 Live niveaumeter met waarschuwing bij te zacht / oversturing.
- 💽 Opname automatisch bewaren als `.wav` (knop "Opnamemap openen").
- 📁 Bestaand audiobestand laden (wav/mp3/m4a/…).
- 🇳🇱🇬🇧 Nederlands, Engels of automatisch.
- 📒 Woordenlijst (namen/jargon) — wordt onthouden.
- ⏱️ Tijdstempels aan/uit; opslaan als `.txt` of `.md`.
- ⚡ **Live transcriberen** tijdens het spreken (knipt op stiltes).
- ✨ **Samenvatten** met een lokaal taalmodel: korte samenvatting, besluiten en
  actiepunten in het Nederlands. Het samenvattingsmodel (~2 GB) wordt eenmalig
  gedownload; daarna offline.

> Sprekerherkenning ("wie zei wat") zit nog niet in deze .NET-versie. De
> Python-versie (map hierboven) heeft die functie wel.

## Downloaden (geen Python nodig)

GitHub bouwt de app automatisch:

1. Repo → tabblad **Actions** → workflow **Build Windows EXE (.NET)**.
2. Open de bovenste (groene) run, of klik **Run workflow**.
3. Bij **Artifacts**:
   - **`scriba-installer`** → `Scriba-Setup.exe` (installer met snelkoppeling) — aanbevolen
   - **`scriba-app`** → de losse map (uitpakken en `Scriba.exe` starten)
4. Installeer of pak uit en start **Scriba**.

> Bij de losse map: houd de bestanden bij elkaar — `Scriba.exe` heeft de map
> `runtimes\` met de native `whisper.dll` ernaast nodig.

Bij de eerste transcriptie wordt het Whisper-model eenmalig gedownload naar
`%APPDATA%\Scriba\modellen`; daarna werkt alles offline. Voor uitrol op een
afgeschermd netwerk: zie **INTERNE-UITROL.md**.

## Zelf bouwen

Vereist de [.NET 8 SDK](https://dotnet.microsoft.com/download).

```powershell
cd dotnet/Notulen
dotnet publish -c Release -r win-x64 --self-contained true -o publish
# Resultaat: dotnet/Notulen/publish/Scriba.exe
```

## Projectstructuur

```
dotnet/Notulen/                # projectmap (assembly: Scriba)
  App.xaml(.cs)                # thema/kleuren + app-start
  MainWindow.xaml(.cs)         # venster + besturing
  SummaryWindow.xaml(.cs)      # samenvattingsvenster
  Services/
    AppSettings.cs             # instellingen (%APPDATA%\Scriba\config.json)
    AudioRecorder.cs           # WASAPI-opname (mic/systeem) + niveaumeting
    SampleHelpers.cs           # resampling naar 16 kHz mono + WAV
    Transcriber.cs             # Whisper.net + modeldownload
    Summarizer.cs              # LLamaSharp-samenvatting
installer/notulen.iss          # Inno Setup-installer (Scriba-Setup.exe)
```
