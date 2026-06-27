"""Tkinter-GUI voor de offline notulen-app.

Functies:
- Opnemen via microfoon of systeem-/vergaderaudio (apparaatkeuze).
- Live VU-meter met waarschuwing bij oversturing/te zacht.
- Ruwe opname automatisch als .wav bewaren (tegen dataverlies).
- Offline transcriberen met Whisper (Nederlands, Engels of automatisch).
- Woordenlijst (namen/jargon) voor betere spelling — wordt onthouden.
- Optionele sprekerherkenning ("wie zei wat").
- Notulen opslaan als .txt of .md.

Zwaar werk draait in een achtergrond-thread; communicatie terug naar de GUI
loopt via een thread-veilige queue die periodiek wordt uitgelezen.
"""

from __future__ import annotations

import queue
import threading
from datetime import datetime
from pathlib import Path

from . import config as cfg
from .recorder import Recorder
from .transcriber import (
    LANGUAGES,
    MODEL_SIZES,
    Segment,
    Transcriber,
    TranscriptionResult,
    _fmt_ts,
)

OPNAMES_DIR = cfg.CONFIG_DIR / "opnames"


def _require_tk():
    try:
        import tkinter as tk
        from tkinter import filedialog, messagebox, ttk
    except Exception as exc:  # pragma: no cover
        raise RuntimeError(
            "De GUI vereist tkinter. Op Linux: 'sudo apt install python3-tk'. "
            "Op Windows/macOS zit het standaard bij Python.\n"
            f"(onderliggende fout: {exc})"
        ) from exc
    return tk, ttk, filedialog, messagebox


class NotulenApp:
    def __init__(self) -> None:
        tk, ttk, filedialog, messagebox = _require_tk()
        self.tk = tk
        self.ttk = ttk
        self.filedialog = filedialog
        self.messagebox = messagebox

        self.settings = cfg.load()
        self.recorder = Recorder()
        self.transcriber: Transcriber | None = None
        self.result: TranscriptionResult | None = None
        self.devices = []  # lijst AudioDevice
        self._events: "queue.Queue[tuple]" = queue.Queue()
        self._busy = False
        self._keep_audio = True
        self._temp_audio: str | None = None  # te verwijderen na transcriptie

        self._build_ui()
        self._refresh_devices()
        self.root.after(100, self._drain_events)

    # ---------- UI opbouw ----------
    def _build_ui(self) -> None:
        tk, ttk = self.tk, self.ttk
        self.root = tk.Tk()
        self.root.title("Notulen — offline spraak naar tekst")
        self.root.geometry("820x680")
        self.root.minsize(680, 560)
        self.root.protocol("WM_DELETE_WINDOW", self._on_close)

        pad = {"padx": 4, "pady": 3}

        # --- Rij 1: taal / model / opties ---
        top = ttk.Frame(self.root, padding=(10, 10, 10, 4))
        top.pack(fill="x")

        ttk.Label(top, text="Taal:").grid(row=0, column=0, sticky="w", **pad)
        self.lang_var = tk.StringVar(value=self.settings["language"])
        ttk.OptionMenu(
            top, self.lang_var, self.lang_var.get(), *LANGUAGES.keys()
        ).grid(row=0, column=1, sticky="w", **pad)

        ttk.Label(top, text="Model:").grid(row=0, column=2, sticky="w", **pad)
        self.model_var = tk.StringVar(value=self.settings["model"])
        ttk.OptionMenu(
            top, self.model_var, self.model_var.get(), *MODEL_SIZES
        ).grid(row=0, column=3, sticky="w", **pad)

        self.ts_var = tk.BooleanVar(value=self.settings["timestamps"])
        ttk.Checkbutton(top, text="Tijdstempels", variable=self.ts_var).grid(
            row=0, column=4, sticky="w", **pad
        )
        self.diar_var = tk.BooleanVar(value=self.settings["diarize"])
        ttk.Checkbutton(top, text="Sprekers", variable=self.diar_var).grid(
            row=0, column=5, sticky="w", **pad
        )

        # --- Rij 2: audiobron ---
        src = ttk.Frame(self.root, padding=(10, 0))
        src.pack(fill="x")
        ttk.Label(src, text="Audiobron:").grid(row=0, column=0, sticky="w", **pad)
        self.device_var = tk.StringVar(value="(standaard microfoon)")
        self.device_menu = ttk.OptionMenu(src, self.device_var, self.device_var.get())
        self.device_menu.grid(row=0, column=1, sticky="we", **pad)
        ttk.Button(src, text="↻", width=3, command=self._refresh_devices).grid(
            row=0, column=2, **pad
        )
        self.save_audio_var = tk.BooleanVar(value=self.settings["save_audio"])
        ttk.Checkbutton(
            src, text="Opname bewaren (.wav)", variable=self.save_audio_var
        ).grid(row=0, column=3, sticky="w", **pad)
        src.columnconfigure(1, weight=1)

        # --- Rij 3: woordenlijst ---
        vocab = ttk.LabelFrame(
            self.root, text="Woordenlijst — namen & vaktermen (komma's of regels)",
            padding=8,
        )
        vocab.pack(fill="x", padx=10, pady=(4, 0))
        self.vocab_text = tk.Text(vocab, height=2, wrap="word")
        self.vocab_text.pack(fill="x")
        self.vocab_text.insert("1.0", self.settings["vocabulary"])

        # --- Knoppenrij ---
        btns = ttk.Frame(self.root, padding=(10, 6))
        btns.pack(fill="x")
        self.record_btn = ttk.Button(
            btns, text="● Opname starten", command=self._toggle_record
        )
        self.record_btn.pack(side="left")
        self.file_btn = ttk.Button(
            btns, text="Audiobestand laden…", command=self._load_file
        )
        self.file_btn.pack(side="left", padx=6)
        self.save_btn = ttk.Button(
            btns, text="Notulen opslaan…", command=self._save, state="disabled"
        )
        self.save_btn.pack(side="left", padx=6)
        self.clear_btn = ttk.Button(btns, text="Wissen", command=self._clear)
        self.clear_btn.pack(side="left", padx=6)

        # --- VU-meter ---
        meter = ttk.Frame(self.root, padding=(10, 0))
        meter.pack(fill="x")
        ttk.Label(meter, text="Niveau:").pack(side="left")
        self.level_bar = ttk.Progressbar(meter, maximum=100, length=200)
        self.level_bar.pack(side="left", padx=8)
        self.level_hint = tk.StringVar(value="")
        ttk.Label(meter, textvariable=self.level_hint, foreground="#b00").pack(
            side="left"
        )

        # --- Transcriptveld ---
        body = ttk.Frame(self.root, padding=10)
        body.pack(fill="both", expand=True)
        self.text = tk.Text(body, wrap="word", font=("TkDefaultFont", 11))
        scroll = ttk.Scrollbar(body, command=self.text.yview)
        self.text.configure(yscrollcommand=scroll.set)
        self.text.pack(side="left", fill="both", expand=True)
        scroll.pack(side="right", fill="y")

        # --- Statusbalk ---
        self.status_var = tk.StringVar(value="Klaar.")
        status = ttk.Frame(self.root, padding=(10, 4))
        status.pack(fill="x")
        ttk.Label(status, textvariable=self.status_var, anchor="w").pack(fill="x")

    # ---------- Apparaten ----------
    def _refresh_devices(self) -> None:
        labels = ["(standaard microfoon)"]
        self.devices = []
        try:
            from .audio_io import list_input_devices

            self.devices = list_input_devices()
            labels += [d.label for d in self.devices]
        except Exception as exc:  # noqa: BLE001
            self.status_var.set(f"Kon apparaten niet ophalen: {exc}")

        menu = self.device_menu["menu"]
        menu.delete(0, "end")
        for lbl in labels:
            menu.add_command(
                label=lbl, command=lambda v=lbl: self.device_var.set(v)
            )
        # Eerder gekozen apparaat herstellen indien nog aanwezig.
        saved = self.settings.get("device")
        if saved in labels:
            self.device_var.set(saved)
        elif self.device_var.get() not in labels:
            self.device_var.set(labels[0])

    def _selected_device(self):
        label = self.device_var.get()
        for d in self.devices:
            if d.label == label:
                return d
        return None  # standaard microfoon

    # ---------- Achtergrond-communicatie ----------
    def _drain_events(self) -> None:
        try:
            while True:
                kind, payload = self._events.get_nowait()
                if kind == "status":
                    self.status_var.set(payload)
                elif kind == "segment":
                    self._append_segment(payload)
                elif kind == "level":
                    self._update_level(*payload)
                elif kind == "done":
                    self._on_done(payload)
                elif kind == "error":
                    self._busy = False
                    self._set_controls_enabled(True)
                    self._cleanup_temp_audio()
                    self.messagebox.showerror("Fout", payload)
                    self.status_var.set("Fout opgetreden.")
        except queue.Empty:
            pass
        self.root.after(80, self._drain_events)

    def _post(self, kind: str, payload) -> None:
        self._events.put((kind, payload))

    # ---------- Opname ----------
    def _toggle_record(self) -> None:
        if self.recorder.is_recording:
            self._stop_record()
        else:
            self._start_record()

    def _start_record(self) -> None:
        if self._busy:
            return
        # Altijd naar WAV opnemen (crash-veilig). Bij 'niet bewaren' ruimen we
        # het bestand na de transcriptie weer op.
        OPNAMES_DIR.mkdir(parents=True, exist_ok=True)
        stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
        path = str(OPNAMES_DIR / f"notulen-{stamp}.wav")
        self._keep_audio = bool(self.save_audio_var.get())
        try:
            self.recorder.start(
                path,
                device=self._selected_device(),
                on_level=lambda d, f, c: self._post("level", (d, f, c)),
            )
        except RuntimeError as exc:
            self.messagebox.showerror("Audiobron", str(exc))
            return
        self.record_btn.configure(text="■ Opname stoppen")
        self.file_btn.configure(state="disabled")
        msg = "Opname loopt… spreek maar."
        if self._keep_audio:
            msg += f"  (opslag: {path})"
        self.status_var.set(msg)

    def _stop_record(self) -> None:
        path = self.recorder.stop()
        self.record_btn.configure(text="● Opname starten")
        self.file_btn.configure(state="normal")
        self.level_bar["value"] = 0
        self.level_hint.set("")
        if not path:
            self.status_var.set("Geen audio opgenomen.")
            return
        if self.recorder.clipped:
            self.status_var.set("Let op: oversturing gedetecteerd. Transcriberen…")
        else:
            self.status_var.set("Opname gestopt. Transcriberen…")
        # Tijdelijk opnamebestand opruimen na transcriptie indien niet bewaren.
        self._temp_audio = None if self._keep_audio else path
        self._start_transcription(path)

    def _update_level(self, dbfs: float, fraction: float, clipping: bool) -> None:
        self.level_bar["value"] = max(0, min(100, int(fraction * 100)))
        if clipping:
            self.level_hint.set("⚠ te luid (oversturing)")
        elif dbfs < -45:
            self.level_hint.set("⚠ erg zacht")
        else:
            self.level_hint.set("")

    # ---------- Bestand ----------
    def _load_file(self) -> None:
        if self._busy:
            return
        path = self.filedialog.askopenfilename(
            title="Kies een audiobestand",
            filetypes=[
                ("Audio", "*.wav *.mp3 *.m4a *.flac *.ogg *.aac *.wma *.mp4"),
                ("Alle bestanden", "*.*"),
            ],
        )
        if path:
            self._temp_audio = None  # nooit een door de gebruiker geladen bestand wissen
            self._start_transcription(path)

    # ---------- Transcriptie ----------
    def _start_transcription(self, audio) -> None:
        self._clear()
        self._busy = True
        self._set_controls_enabled(False)

        model_size = self.model_var.get()
        if self.transcriber is None or self.transcriber.model_size != model_size:
            self.transcriber = Transcriber(model_size=model_size)

        language = LANGUAGES[self.lang_var.get()]
        vocabulary = self.vocab_text.get("1.0", "end").strip()
        diarize = self.diar_var.get()

        thread = threading.Thread(
            target=self._work,
            args=(audio, language, vocabulary, diarize),
            daemon=True,
        )
        thread.start()

    def _work(self, audio, language, vocabulary, diarize) -> None:
        try:
            assert self.transcriber is not None
            result = self.transcriber.transcribe(
                audio,
                language=language,
                vocabulary=vocabulary,
                diarize=diarize,
                on_status=lambda m: self._post("status", m),
                on_segment=lambda s: self._post("segment", s),
            )
            self._post("done", result)
        except Exception as exc:  # noqa: BLE001
            self._post("error", str(exc))

    # ---------- GUI-updates ----------
    def _append_segment(self, seg: Segment) -> None:
        prefix = ""
        if self.ts_var.get():
            prefix += f"[{_fmt_ts(seg.start)} - {_fmt_ts(seg.end)}] "
        if seg.speaker:
            prefix += f"{seg.speaker}: "
        line = f"{prefix}{seg.text.strip()}"
        if not self.ts_var.get() and not seg.speaker:
            line = seg.text.strip() + " "
        else:
            line += "\n"
        self.text.insert("end", line)
        self.text.see("end")

    def _on_done(self, result: TranscriptionResult) -> None:
        self.result = result
        # Bij sprekerherkenning is het transcript opnieuw opgebouwd met labels.
        if any(s.speaker for s in result.segments):
            self.text.delete("1.0", "end")
            self.text.insert(
                "1.0", result.as_minutes(with_timestamps=self.ts_var.get())
            )
        self._busy = False
        self._set_controls_enabled(True)
        self.save_btn.configure(state="normal")
        self._cleanup_temp_audio()

    def _cleanup_temp_audio(self) -> None:
        """Verwijder een tijdelijk opnamebestand (alleen als 'niet bewaren')."""
        if self._temp_audio:
            try:
                Path(self._temp_audio).unlink(missing_ok=True)
            except Exception:
                pass
            self._temp_audio = None

    def _set_controls_enabled(self, enabled: bool) -> None:
        state = "normal" if enabled else "disabled"
        self.record_btn.configure(state=state)
        self.file_btn.configure(state=state)

    def _clear(self) -> None:
        self.text.delete("1.0", "end")
        self.result = None
        self.save_btn.configure(state="disabled")

    def _save(self) -> None:
        if self.result is None:
            return
        path = self.filedialog.asksaveasfilename(
            title="Notulen opslaan",
            defaultextension=".md",
            filetypes=[("Markdown", "*.md"), ("Tekst", "*.txt")],
        )
        if not path:
            return
        content = self.result.as_minutes(with_timestamps=self.ts_var.get())
        header = (
            f"# Notulen\n\nDatum: {datetime.now():%Y-%m-%d %H:%M}\n"
            f"Taal: {self.result.language}\n\n---\n\n"
            if path.endswith(".md")
            else ""
        )
        Path(path).write_text(header + content + "\n", encoding="utf-8")
        self.status_var.set(f"Opgeslagen: {path}")

    # ---------- Afsluiten / persistentie ----------
    def _collect_settings(self) -> None:
        self.settings.update(
            {
                "language": self.lang_var.get(),
                "model": self.model_var.get(),
                "timestamps": bool(self.ts_var.get()),
                "diarize": bool(self.diar_var.get()),
                "save_audio": bool(self.save_audio_var.get()),
                "device": self.device_var.get(),
                "vocabulary": self.vocab_text.get("1.0", "end").strip(),
            }
        )

    def _on_close(self) -> None:
        if self.recorder.is_recording:
            try:
                self.recorder.stop()
            except Exception:
                pass
        self._collect_settings()
        cfg.save(self.settings)
        self.root.destroy()

    def run(self) -> None:
        self.root.mainloop()


def main() -> None:
    NotulenApp().run()


if __name__ == "__main__":
    main()
