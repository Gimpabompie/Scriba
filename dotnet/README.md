# Notulen (.NET / WPF) — de stabiele Windows-versie

Een native Windows-app voor **offline spraak naar tekst** (Nederlands & Engels),
geschreven in C# / WPF. Deze versie is bedoeld als de **stabiele, makkelijk te
distribueren** variant: één self-contained `.exe`, geen Python nodig.

## Waarom deze versie stabieler is

- **Whisper.net** (whisper.cpp) als transcriptie-motor — een zelfstandige native
  engine, geen Python-ML-stack om te bundelen.
- **NAudio / WASAPI** voor opname, inclusief betrouwbare **loopback** voor
  systeem-/vergaderaudio.
- **Self-contained single-file `.exe`** via `dotnet publish` — draait zonder
  geïnstalleerde runtime.

## Functies

- 🎙️ Opnemen via microfoon of systeemaudio (apparaatkeuze).
- 📊 Live niveaumeter met waarschuwing bij te zacht / oversturing.
- 💽 Opname automatisch bewaren als `.wav`.
- 📁 Bestaand audiobestand laden (wav/mp3/m4a/…).
- 🇳🇱🇬🇧 Nederlands, Engels of automatisch.
- 📒 Woordenlijst (namen/jargon) — wordt onthouden.
- ⏱️ Tijdstempels aan/uit; opslaan als `.txt` of `.md`.

> Sprekerherkenning ("wie zei wat") zit nog niet in deze .NET-versie; die komt
> eventueel later. De Python-versie in de map hierboven heeft die functie wel.

## Downloaden (geen Python nodig)

GitHub bouwt de `.exe` automatisch:

1. Repo → tabblad **Actions** → workflow **Build Windows EXE (.NET)**.
2. Open de bovenste (groene) run, of klik **Run workflow**.
3. Download bij **Artifacts** het bestand **`notulen-dotnet-exe`**.
4. Pak uit en **dubbelklik `Notulen.exe`**.

Bij de eerste transcriptie wordt het Whisper-model eenmalig gedownload naar
`%APPDATA%\Notulen\modellen`; daarna werkt alles offline.

## Zelf bouwen

Vereist de [.NET 8 SDK](https://dotnet.microsoft.com/download).

```powershell
cd dotnet/Notulen
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -o publish
# Resultaat: dotnet/Notulen/publish/Notulen.exe
```

## Projectstructuur

```
dotnet/Notulen/
  App.xaml(.cs)            # thema/kleuren + app-start
  MainWindow.xaml(.cs)     # venster + besturing
  Services/
    AppSettings.cs         # instellingen (%APPDATA%\Notulen\config.json)
    AudioRecorder.cs       # WASAPI-opname (mic/systeem) + niveaumeting
    SampleHelpers.cs       # resampling naar 16 kHz mono + WAV
    Transcriber.cs         # Whisper.net + modeldownload
```
