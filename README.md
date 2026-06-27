# Notulen — offline spraak naar tekst (Nederlands + Engels)

Een **volledig offline** desktop-app die vergaderingen omzet in geschreven
notulen. De audio verlaat je computer niet: transcriberen gebeurt lokaal met
[faster-whisper](https://github.com/SYSTRAN/faster-whisper) (Whisper van
OpenAI, lokaal uitgevoerd). Ondersteunt **Nederlands** en **Engels** (en
automatische taaldetectie).

> Het taalmodel wordt alléén de eerste keer eenmalig gedownload. Daarna werkt
> alles offline.

## Functies

- 🎙️ Opnemen via de microfoon (start/stop) — live transcript in beeld.
- 📁 Bestaand audiobestand laden (`.wav`, `.mp3`, `.m4a`, `.flac`, …).
- 🇳🇱🇬🇧 Nederlands, Engels of automatische detectie.
- ⏱️ Notulen met of zonder tijdstempels.
- 💾 Opslaan als `.txt` of `.md`.
- 🧠 Keuze uit modelgroottes (`tiny` … `large-v3`): groter = nauwkeuriger maar
  trager.

## Installatie

Vereist Python 3.9+.

```bash
pip install -r requirements.txt
```

Op **Linux** is tkinter soms een aparte package:

```bash
sudo apt install python3-tk
```

Op **Windows** en **macOS** zit tkinter standaard bij Python.

## Gebruik

### Desktop-app (met venster)

```bash
python -m notulen
```

1. Kies de **taal** en eventueel een **model** (begin met `small`).
2. Klik **Opname starten** en spreek, of **Audiobestand laden…**.
3. Klik **Opname stoppen** — de tekst verschijnt.
4. Klik **Notulen opslaan…**.

### Command line (zonder venster)

Handig voor losse bestanden of scripts:

```bash
python -m notulen.cli vergadering.mp3 --taal nl --model small -o notulen.md
```

Opties: `--taal {auto,nl,en}`, `--model {tiny,base,small,medium,large-v3}`,
`-o/--output BESTAND`, `--geen-tijdstempels`.

## Tips voor nauwkeurigheid

- Een goede microfoon en weinig achtergrondgeluid helpen het meest.
- `small` is een prima balans; `medium` of `large-v3` is nauwkeuriger maar
  vraagt meer geheugen en tijd.
- Heb je een NVIDIA-GPU? In `transcriber.py` kun je `device="cuda"` en
  `compute_type="float16"` zetten voor flink snellere transcriptie.

## Projectstructuur

```
notulen/
  app.py          # Tkinter-GUI
  recorder.py     # microfoon-opname -> numpy-buffer
  transcriber.py  # offline Whisper-transcriptie
  cli.py          # command-line variant
  __main__.py     # 'python -m notulen' start de GUI
```
