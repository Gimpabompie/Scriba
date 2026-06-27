# Interne uitrol op een (afgeschermd) netwerk

Deze handleiding beschrijft hoe je Notulen intern verspreidt, inclusief het
omgaan met de taalmodellen wanneer de machines **geen** internettoegang hebben.

## 1. Artefacten

De GitHub Actions-workflow **Build Windows EXE (.NET)** levert twee artefacten:

| Artefact | Wat | Gebruik |
|---|---|---|
| `notulen-installer` | `Notulen-Setup.exe` | Installer met snelkoppelingen — aanbevolen |
| `notulen-dotnet-app` | de losse self-contained map | Voor wie liever zonder installer werkt |

De installer installeert **per gebruiker** (geen adminrechten nodig) naar
`%LOCALAPPDATA%\Programs\Notulen`, met snelkoppelingen in Start (en optioneel
op het bureaublad).

**Stille installatie** (voor GPO/Intune/SCCM):

```
Notulen-Setup.exe /VERYSILENT /NORESTART
```

## 2. De modellen (belangrijk bij afgeschermd netwerk)

De app gebruikt twee modellen die normaal van Hugging Face komen:

| Doel | Bestand | Grootte |
|---|---|---|
| Transcriptie (Whisper) | `ggml-medium.bin` (of `ggml-small.bin`, …) | ~0,5–1,5 GB |
| Samenvatten (LLM) | `Qwen2.5-3B-Instruct-Q4_K_M.gguf` | ~2 GB |

Zonder internet kan de app deze niet zelf ophalen. Kies één aanpak:

### Aanpak A — modellen op een netwerkshare (aanbevolen)

1. Download op een machine **mét** internet de gewenste modelbestanden:
   - Whisper: `https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-medium.bin`
     (en/of `ggml-small.bin`, `ggml-large-v3.bin`)
   - LLM: `https://huggingface.co/bartowski/Qwen2.5-3B-Instruct-GGUF/resolve/main/Qwen2.5-3B-Instruct-Q4_K_M.gguf`
2. Zet ze samen in één map op een share, bijv. `\\server\share\Notulen\modellen`.
3. Zet op de clients de omgevingsvariabele (bijv. via GPO – *Computer/Gebruiker
   → Voorkeuren → Omgeving*):

   ```
   NOTULEN_MODELS_DIR = \\server\share\Notulen\modellen
   ```

De app zoekt modellen in deze volgorde: **`NOTULEN_MODELS_DIR`** → map
`modellen\` naast de app → `%APPDATA%\Notulen\modellen`. Wordt het bestand
gevonden, dan wordt er niets gedownload.

### Aanpak B — modellen meeleveren bij de installatie

Plaats de modelbestanden in een submap `modellen\` ín de installatiemap
(`%LOCALAPPDATA%\Programs\Notulen\modellen`). Handig als je de installer met de
modellen samen distribueert.

### Aanpak C — interne spiegel (eigen webserver)

Host de bestanden op een interne webserver en wijs de app daarheen:

```
NOTULEN_MODEL_BASEURL = https://intranet.example/notulen/whisper   (zonder slash op het eind)
NOTULEN_LLM_URL       = https://intranet.example/notulen/Qwen2.5-3B-Instruct-Q4_K_M.gguf
```

Bij `NOTULEN_MODEL_BASEURL` haalt de app `<baseurl>/ggml-<model>.bin` op.

## 3. Omgevingsvariabelen — overzicht

| Variabele | Effect |
|---|---|
| `NOTULEN_MODELS_DIR` | Map waarin eerst naar bestaande modellen wordt gezocht |
| `NOTULEN_MODEL_BASEURL` | Alternatieve basis-URL voor de Whisper-modellen |
| `NOTULEN_LLM_URL` | Alternatieve volledige URL voor het samenvattingsmodel |

## 4. SmartScreen / Defender (niet-ondertekend)

De `.exe`/installer is (nog) niet ondertekend. Bij de eerste start kan Windows
SmartScreen waarschuwen → **Meer info → Toch uitvoeren**. Voor een vlotte uitrol:

- Onderteken met een intern code-signing certificaat (kan later toegevoegd
  worden aan de build), of
- Voeg de uitgever/het bestand toe aan de SmartScreen/Defender-uitzonderingen
  via je beheertools.

## 5. Opslag van gebruikersdata

Per gebruiker, lokaal:

- Opnames: `%APPDATA%\Notulen\opnames`
- Gedownloade modellen: `%APPDATA%\Notulen\modellen`
- Instellingen: `%APPDATA%\Notulen\config.json`
