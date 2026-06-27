# Interne uitrol van Scriba op een (afgeschermd) netwerk

Deze handleiding beschrijft hoe je **Scriba** intern verspreidt, inclusief het
omgaan met de taalmodellen wanneer de machines **geen** internettoegang hebben.

## 1. Artefacten

De GitHub Actions-workflow **Build Windows EXE (.NET)** levert twee artefacten:

| Artefact | Wat | Gebruik |
|---|---|---|
| `scriba-installer` | `Scriba-Setup.exe` | Installer met snelkoppelingen — aanbevolen |
| `scriba-app` | de losse self-contained map | Voor wie liever zonder installer werkt |

De installer installeert **per gebruiker** (geen adminrechten nodig) naar
`%LOCALAPPDATA%\Programs\Scriba`, met snelkoppelingen in Start (en optioneel
op het bureaublad).

**Stille installatie** (voor GPO/Intune/SCCM):

```
Scriba-Setup.exe /VERYSILENT /NORESTART
```

## 2. De modellen (belangrijk bij afgeschermd netwerk)

Scriba gebruikt twee modellen die normaal van Hugging Face komen:

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
2. Zet ze samen in één map op een share, bijv. `\\server\share\Scriba\modellen`.
3. Zet op de clients de omgevingsvariabele (bijv. via GPO – *Computer/Gebruiker
   → Voorkeuren → Omgeving*):

   ```
   SCRIBA_MODELS_DIR = \\server\share\Scriba\modellen
   ```

Scriba zoekt modellen in deze volgorde: **`SCRIBA_MODELS_DIR`** → map
`modellen\` naast de app → `%APPDATA%\Scriba\modellen` → (oude)
`%APPDATA%\Notulen\modellen`. Wordt het bestand gevonden, dan wordt er niets
gedownload.

### Aanpak B — modellen meeleveren bij de installatie

Plaats de modelbestanden in een submap `modellen\` ín de installatiemap
(`%LOCALAPPDATA%\Programs\Scriba\modellen`).

### Aanpak C — interne spiegel (eigen webserver)

Host de bestanden op een interne webserver en wijs Scriba daarheen:

```
SCRIBA_MODEL_BASEURL = https://intranet.example/scriba/whisper   (zonder slash op het eind)
SCRIBA_LLM_URL       = https://intranet.example/scriba/Qwen2.5-3B-Instruct-Q4_K_M.gguf
```

Bij `SCRIBA_MODEL_BASEURL` haalt de app `<baseurl>/ggml-<model>.bin` op.

## 3. Omgevingsvariabelen — overzicht

| Variabele | Effect |
|---|---|
| `SCRIBA_MODELS_DIR` | Map waarin eerst naar bestaande modellen wordt gezocht |
| `SCRIBA_MODEL_BASEURL` | Alternatieve basis-URL voor de Whisper-modellen |
| `SCRIBA_LLM_URL` | Alternatieve volledige URL voor het samenvattingsmodel |

## 4. SmartScreen / Defender (niet-ondertekend)

De `.exe`/installer is niet ondertekend zolang er geen certificaat is ingesteld.
Bij de eerste start kan Windows SmartScreen waarschuwen → **Meer info → Toch
uitvoeren**, of voeg de uitgever/het bestand toe aan de SmartScreen/Defender-
uitzonderingen via je beheertools.

### Ondertekenen activeren (aanbevolen)

De build ondertekent **automatisch** zodra je een certificaat als GitHub-secret
toevoegt. Doe dit eenmalig in de repo (Settings → Secrets and variables →
Actions):

| Naam | Type | Waarde |
|---|---|---|
| `CODE_SIGN_PFX_BASE64` | Secret | je `.pfx`-certificaat, base64-gecodeerd |
| `CODE_SIGN_PASSWORD` | Secret | wachtwoord van het `.pfx` |
| `CODE_SIGN_TIMESTAMP_URL` | Variable (optioneel) | eigen RFC3161 timestamp-server |

Het `.pfx` base64-coderen kan zo:

```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes("certificaat.pfx")) | Set-Clipboard
```

Daarna ondertekent elke build automatisch `Scriba.exe` én `Scriba-Setup.exe`.
Zonder deze secrets wordt het ondertekenen overgeslagen en blijft de build
gewoon werken.

## 5. Opslag van gebruikersdata

Per gebruiker, lokaal:

- Opnames: `%APPDATA%\Scriba\opnames`
- Gedownloade modellen: `%APPDATA%\Scriba\modellen`
- Instellingen: `%APPDATA%\Scriba\config.json`

> Eerder gedownloade modellen in de oude map `%APPDATA%\Notulen\modellen`
> worden nog steeds gevonden en hoeven dus niet opnieuw te worden gedownload.
