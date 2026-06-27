# Scriba — offline notuleren (Nederlands & Engels)

**Scriba** is een **volledig offline** Windows-app die vergaderingen omzet in
geschreven notulen. De audio verlaat je computer niet: transcriberen én
samenvatten gebeurt **lokaal**. Ideaal als privacy belangrijk is.

> *Scriba* (Latijn voor schrijver/klerk) doet wat een notulist doet: meeluisteren
> en optekenen.

## ✨ Functies

- 🎙️ **Opnemen** via microfoon of **systeem-/vergaderaudio** (Teams/Zoom/Meet) —
  ook de andere deelnemers worden vastgelegd.
- 📝 **Transcriberen** met Whisper (whisper.cpp), Nederlands & Engels.
- ⚡ **Live transcriberen** tijdens het spreken (optioneel).
- ✨ **Samenvatten** met een lokaal taalmodel: korte samenvatting, **besluiten**
  en **actiepunten** in het Nederlands.
- 📊 Niveaumeter, 💽 automatische WAV-backup, 📒 woordenlijst (namen/jargon),
  ⏱️ tijdstempels, opslaan als `.txt`/`.md`.
- 🔒 **Offline**: modellen worden eenmalig gedownload, daarna geen internet nodig.

## ⬇️ Downloaden (Windows, geen installatie van tools nodig)

GitHub bouwt de app automatisch via **Actions → Build Windows EXE (.NET)**.
Bij **Artifacts** van de laatste groene run:

| Artefact | Wat |
|---|---|
| **`scriba-installer`** | `Scriba-Setup.exe` — installer met snelkoppeling (aanbevolen) |
| **`scriba-app`** | losse map; uitpakken en `Scriba.exe` starten |

> Niet ondertekend? Windows SmartScreen → **Meer info → Toch uitvoeren**. Bij de
> eerste transcriptie/samenvatting wordt het model eenmalig gedownload.

Voor uitrol op een (afgeschermd) bedrijfsnetwerk: zie
**[dotnet/INTERNE-UITROL.md](dotnet/INTERNE-UITROL.md)**.

## 🧩 Twee versies in deze repo

| Versie | Map | Voor wie |
|---|---|---|
| **Scriba (C# / WPF)** — aanbevolen | [`dotnet/`](dotnet/) | Native Windows-app, self-contained, met live + samenvatten |
| **Notulen (Python / Tkinter)** | [`notulen/`](notulen/) | Cross-platform, met optionele **sprekerherkenning** |

Zie [`dotnet/README.md`](dotnet/README.md) en [`notulen/`](notulen/) voor details.

## 🛠️ Zelf bouwen (C#)

Vereist de [.NET 8 SDK](https://dotnet.microsoft.com/download):

```powershell
cd dotnet/Notulen
dotnet publish -c Release -r win-x64 --self-contained true -o publish
# Resultaat: dotnet/Notulen/publish/Scriba.exe
```

## 🔐 Privacy

Alle verwerking is lokaal. De enige netwerkactie is de **eenmalige
modeldownload** (Whisper + het samenvattingsmodel). Op afgeschermde netwerken
kun je de modellen vooraf op een share plaatsen — zie de uitrolhandleiding.

## 📄 Licentie

Zie [LICENSE](LICENSE).
