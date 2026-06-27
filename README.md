# Notulen — offline spraak naar tekst (Nederlands + Engels)

Een **volledig offline** desktop-app die vergaderingen omzet in geschreven
notulen. De audio verlaat je computer niet: transcriberen gebeurt lokaal met
[faster-whisper](https://github.com/SYSTRAN/faster-whisper) (Whisper van
OpenAI, lokaal uitgevoerd). Ondersteunt **Nederlands** en **Engels** (en
automatische taaldetectie).

> Het taalmodel wordt alléén de eerste keer eenmalig gedownload. Daarna werkt
> alles offline.

## Functies

- 🎙️ Opnemen via **microfoon óf systeem-/vergaderaudio** (Teams/Zoom/Meet) — zo
  worden ook de andere deelnemers vastgelegd, niet alleen jijzelf.
- 📊 **Live niveaumeter** met waarschuwing bij te zacht of oversturing.
- 💽 **Opname automatisch bewaren** als `.wav`, zodat je nooit een vergadering
  kwijtraakt als de transcriptie faalt.
- 📁 Bestaand audiobestand laden (`.wav`, `.mp3`, `.m4a`, `.flac`, …).
- 🇳🇱🇬🇧 Nederlands, Engels of automatische detectie.
- 📒 **Woordenlijst** (namen & vaktermen) voor correcte spelling — wordt onthouden.
- 🗣️ Optionele **sprekerherkenning**: "wie zei wat".
- ⏱️ Notulen met of zonder tijdstempels.
- 💾 Opslaan als `.txt` of `.md`.
- 🧠 Keuze uit modelgroottes (`tiny` … `large-v3`).

## Snelste weg: kant-en-klare Windows-app (.exe)

Je hoeft geen Python te installeren. GitHub bouwt automatisch een `notulen.exe`:

1. Open de repo op GitHub → tabblad **Actions**.
2. Kies links de workflow **Build Windows EXE** en open de bovenste (groene) run.
   Loopt er nog geen? Klik **Run workflow** en wacht een paar minuten.
3. Onderaan bij **Artifacts** download je **`notulen-windows-exe`** (een ZIP).
4. Pak uit en **dubbelklik `notulen.exe`**.

> Windows SmartScreen kan waarschuwen omdat de .exe niet ondertekend is: klik
> **Meer info → Toch uitvoeren**. Bij de eerste transcriptie wordt het taalmodel
> eenmalig gedownload; daarna werkt alles offline.

Liever zelf bouwen op je eigen Windows-laptop? Dubbelklik dan `build_windows.bat`
(vereist wél Python). De .exe komt in de map `dist\`.

## Installatie (vanuit broncode)

Vereist Python 3.9+.

```bash
pip install -r requirements.txt
```

Op **Linux** is tkinter soms een aparte package:

```bash
sudo apt install python3-tk
```

Op **Windows** en **macOS** zit tkinter standaard bij Python.

### Sprekerherkenning (optioneel)

```bash
pip install pyannote.audio torch
```

Het diarization-model is 'gated' op HuggingFace: accepteer eenmalig de
voorwaarden en zet een token, daarna draait het lokaal.

```bash
export HUGGINGFACE_TOKEN=hf_xxx   # Windows: set HUGGINGFACE_TOKEN=hf_xxx
```

## Systeem-/vergaderaudio opnemen

Om de **andere deelnemers** van een online vergadering vast te leggen, kies je
in het menu **Audiobron** een systeemaudio-bron i.p.v. de microfoon:

- **Windows:** verschijnt automatisch als loopback van je uitvoerapparaat
  (WASAPI).
- **Linux (PulseAudio/PipeWire):** kies de bron met *"Monitor"* in de naam.
- **macOS:** installeer een virtueel apparaat zoals
  [BlackHole](https://github.com/ExistentialAudio/BlackHole) en kies dat.

Tip: wil je tegelijk jezelf én de vergadering opnemen, maak dan op je systeem
een gecombineerd apparaat (mix van mic + monitor) en kies die als bron.

## Gebruik

### Desktop-app (met venster)

```bash
python -m notulen
```

1. Kies **taal**, **model** (begin met `small`) en de **audiobron**.
2. Vul eventueel de **woordenlijst** met namen/termen.
3. Klik **Opname starten** en spreek, of **Audiobestand laden…**.
4. Klik **Opname stoppen** — de tekst verschijnt.
5. Klik **Notulen opslaan…**.

### Command line (zonder venster)

```bash
python -m notulen.cli vergadering.mp3 --taal nl --model small \
    --woordenlijst "Jan Jansen, Acme BV, sprint" --sprekers -o notulen.md
```

Opties: `--taal {auto,nl,en}`, `--model {tiny,base,small,medium,large-v3}`,
`--woordenlijst "…"`, `--sprekers`, `-o/--output BESTAND`,
`--geen-tijdstempels`.

## Tips voor nauwkeurigheid

- Een goede microfoon en weinig achtergrondgeluid helpen het meest; let op de
  niveaumeter (niet in het rood, niet bijna stil).
- Vul de woordenlijst met deelnemersnamen en jargon.
- `small` is een prima balans; `medium`/`large-v3` is nauwkeuriger maar trager.
- Heb je een NVIDIA-GPU? In `transcriber.py` kun je `device="cuda"` en
  `compute_type="float16"` zetten voor flink snellere transcriptie.

## Projectstructuur

```
notulen/
  app.py          # Tkinter-GUI
  recorder.py     # opname (mic/systeem) -> numpy, met niveaumeting + WAV-backup
  audio_io.py     # apparaatlijst, niveau-berekening, WAV-schrijver
  transcriber.py  # offline Whisper-transcriptie + woordenlijst
  diarization.py  # optionele sprekerherkenning
  config.py       # onthoudt instellingen (~/.notulen/config.json)
  cli.py          # command-line variant
  __main__.py     # 'python -m notulen' start de GUI
```

## Tests

De pure logica (niveau-berekening, woordenlijst, sprekerkoppeling, opmaak)
heeft tests die zonder audio/modellen draaien:

```bash
python -m pytest        # of: python tests/test_logic.py
```
