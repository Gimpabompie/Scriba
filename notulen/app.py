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

De vormgeving gebruikt alleen ttk/tk (geen extra libraries), zodat de .exe
licht en betrouwbaar blijft.
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

# --- Kleurenpalet (rustig, modern licht thema) ---
BG = "#eef1f6"        # app-achtergrond
CARD = "#ffffff"      # kaarten
INK = "#1f2430"       # primaire tekst
MUTED = "#6b7280"     # secundaire tekst
BORDER = "#e2e6ee"    # randen
ACCENT = "#4f46e5"    # indigo (primaire actie)
ACCENT_HOVER = "#4338ca"
REC = "#dc2626"       # opname-rood
REC_HOVER = "#b91c1c"
SOFT = "#f3f4f8"      # subtiele knop-achtergrond
GOOD = "#16a34a"
WARN = "#f59e0b"
BAD = "#dc2626"

FONT = "Segoe UI"     # standaard op Windows; Tk valt anders terug op een default


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

    # ---------- Stijl ----------
    def _setup_style(self) -> None:
        tk, ttk = self.tk, self.ttk
        style = ttk.Style(self.root)
        try:
            style.theme_use("clam")
        except tk.TclError:
            pass

        self.f_base = (FONT, 10)
        self.f_title = (FONT, 17, "bold")
        self.f_sub = (FONT, 9)
        self.f_head = (FONT, 10, "bold")
        self.f_text = (FONT, 11)

        style.configure(".", background=BG, foreground=INK, font=self.f_base)
        style.configure("TFrame", background=BG)
        style.configure("Card.TFrame", background=CARD)
        style.configure("TLabel", background=BG, foreground=INK)
        style.configure("Card.TLabel", background=CARD, foreground=INK)
        style.configure("Head.TLabel", background=CARD, foreground=INK, font=self.f_head)
        style.configure("Muted.TLabel", background=CARD, foreground=MUTED, font=self.f_sub)
        style.configure("Status.TLabel", background=CARD, foreground=MUTED, font=self.f_sub)

        style.configure(
            "Card.TCheckbutton", background=CARD, foreground=INK, font=self.f_base
        )
        style.map("Card.TCheckbutton", background=[("active", CARD)])

        # Comboboxen
        style.configure(
            "TCombobox", fieldbackground="white", background="white",
            bordercolor=BORDER, arrowcolor=INK, padding=4,
        )
        style.map(
            "TCombobox",
            fieldbackground=[("readonly", "white")],
            selectbackground=[("readonly", "white")],
            selectforeground=[("readonly", INK)],
            bordercolor=[("focus", ACCENT)],
        )

        # Secundaire (lichte) knoppen
        style.configure(
            "Secondary.TButton", background=SOFT, foreground=INK,
            bordercolor=BORDER, focuscolor=SOFT, relief="flat",
            padding=(12, 8), font=self.f_base,
        )
        style.map(
            "Secondary.TButton",
            background=[("active", "#e8eaf2"), ("disabled", "#f6f7fa")],
            foreground=[("disabled", "#aab")],
        )

        # Kleur-gecodeerde niveaumeter
        for name, color in (("Good", GOOD), ("Warn", WARN), ("Bad", BAD)):
            style.configure(
                f"{name}.Horizontal.TProgressbar",
                troughcolor="#e6e9f0", bordercolor="#e6e9f0",
                background=color, lightcolor=color, darkcolor=color,
            )

    # ---------- Kaart-helper ----------
    def _card(self, parent):
        """Een witte 'kaart' met subtiele rand."""
        return self.tk.Frame(
            parent, bg=CARD, highlightbackground=BORDER,
            highlightthickness=1, bd=0,
        )

    def _accent_button(self, parent, text, command):
        btn = self.tk.Button(
            parent, text=text, command=command, font=(FONT, 11, "bold"),
            bg=ACCENT, fg="white", activebackground=ACCENT_HOVER,
            activeforeground="white", relief="flat", bd=0,
            padx=22, pady=11, cursor="hand2", highlightthickness=0,
        )
        btn.bind("<Enter>", lambda e: btn.config(bg=self._hover_for(btn)))
        btn.bind("<Leave>", lambda e: btn.config(bg=self._base_for(btn)))
        return btn

    def _base_for(self, btn):
        return REC if getattr(btn, "_recording", False) else ACCENT

    def _hover_for(self, btn):
        return REC_HOVER if getattr(btn, "_recording", False) else ACCENT_HOVER

    # ---------- UI opbouw ----------
    def _build_ui(self) -> None:
        tk, ttk = self.tk, self.ttk
        self.root = tk.Tk()
        self.root.title("Notulen — offline spraak naar tekst")
        self.root.geometry("860x740")
        self.root.minsize(720, 600)
        self.root.configure(bg=BG)
        self.root.protocol("WM_DELETE_WINDOW", self._on_close)
        self._setup_style()

        # === Koptekst ===
        header = tk.Frame(self.root, bg=CARD, highlightbackground=BORDER,
                          highlightthickness=1)
        header.pack(fill="x")
        inner = tk.Frame(header, bg=CARD)
        inner.pack(fill="x", padx=18, pady=12)
        tk.Label(inner, text="📝  Notulen", bg=CARD, fg=INK,
                 font=self.f_title).pack(anchor="w")
        tk.Label(inner, text="Offline spraak naar tekst · Nederlands & Engels",
                 bg=CARD, fg=MUTED, font=self.f_sub).pack(anchor="w")

        # === Hoofdgebied ===
        main = tk.Frame(self.root, bg=BG)
        main.pack(fill="both", expand=True, padx=16, pady=12)

        # --- Kaart: instellingen ---
        cfg_card = self._card(main)
        cfg_card.pack(fill="x")
        cf = tk.Frame(cfg_card, bg=CARD)
        cf.pack(fill="x", padx=14, pady=12)

        ttk.Label(cf, text="Taal", style="Head.TLabel").grid(
            row=0, column=0, sticky="w", padx=(0, 8), pady=(0, 2))
        ttk.Label(cf, text="Model", style="Head.TLabel").grid(
            row=0, column=1, sticky="w", padx=8, pady=(0, 2))

        self.lang_var = tk.StringVar(value=self.settings["language"])
        ttk.Combobox(cf, textvariable=self.lang_var, values=list(LANGUAGES.keys()),
                     state="readonly", width=22).grid(
            row=1, column=0, sticky="w", padx=(0, 8))

        self.model_var = tk.StringVar(value=self.settings["model"])
        ttk.Combobox(cf, textvariable=self.model_var, values=MODEL_SIZES,
                     state="readonly", width=12).grid(row=1, column=1, sticky="w", padx=8)

        opts = tk.Frame(cf, bg=CARD)
        opts.grid(row=1, column=2, sticky="w", padx=(16, 0))
        self.ts_var = tk.BooleanVar(value=self.settings["timestamps"])
        ttk.Checkbutton(opts, text="Tijdstempels", variable=self.ts_var,
                        style="Card.TCheckbutton").pack(anchor="w")
        self.diar_var = tk.BooleanVar(value=self.settings["diarize"])
        ttk.Checkbutton(opts, text="Sprekers herkennen", variable=self.diar_var,
                        style="Card.TCheckbutton").pack(anchor="w")
        cf.columnconfigure(2, weight=1)

        # Scheidingslijn
        tk.Frame(cf, bg=BORDER, height=1).grid(
            row=2, column=0, columnspan=3, sticky="we", pady=12)

        # Audiobron
        ttk.Label(cf, text="Audiobron", style="Head.TLabel").grid(
            row=3, column=0, columnspan=3, sticky="w", pady=(0, 2))
        srow = tk.Frame(cf, bg=CARD)
        srow.grid(row=4, column=0, columnspan=3, sticky="we")
        self.device_var = tk.StringVar(value="(standaard microfoon)")
        self.device_combo = ttk.Combobox(
            srow, textvariable=self.device_var, state="readonly")
        self.device_combo.pack(side="left", fill="x", expand=True)
        ttk.Button(srow, text="↻ Verversen", style="Secondary.TButton",
                   command=self._refresh_devices).pack(side="left", padx=(8, 0))
        self.save_audio_var = tk.BooleanVar(value=self.settings["save_audio"])
        ttk.Checkbutton(cf, text="Opname bewaren als .wav",
                        variable=self.save_audio_var, style="Card.TCheckbutton").grid(
            row=5, column=0, columnspan=3, sticky="w", pady=(8, 0))

        # --- Kaart: woordenlijst ---
        voc_card = self._card(main)
        voc_card.pack(fill="x", pady=(12, 0))
        vf = tk.Frame(voc_card, bg=CARD)
        vf.pack(fill="x", padx=14, pady=12)
        ttk.Label(vf, text="Woordenlijst", style="Head.TLabel").pack(anchor="w")
        ttk.Label(vf, text="Namen & vaktermen (komma's of nieuwe regels) — voor "
                           "correcte spelling.", style="Muted.TLabel").pack(
            anchor="w", pady=(0, 6))
        self.vocab_text = tk.Text(vf, height=2, wrap="word", font=self.f_base,
                                  relief="flat", bg=SOFT, bd=8,
                                  highlightthickness=1, highlightbackground=BORDER,
                                  highlightcolor=ACCENT)
        self.vocab_text.pack(fill="x")
        self.vocab_text.insert("1.0", self.settings["vocabulary"])

        # --- Actierij: opnameknop + meter ---
        act = tk.Frame(main, bg=BG)
        act.pack(fill="x", pady=(12, 0))
        self.record_btn = self._accent_button(act, "●  Opname starten",
                                               self._toggle_record)
        self.record_btn.pack(side="left")
        self.file_btn = ttk.Button(act, text="📁  Audiobestand…",
                                   style="Secondary.TButton", command=self._load_file)
        self.file_btn.pack(side="left", padx=(10, 0))

        meter = tk.Frame(act, bg=BG)
        meter.pack(side="right")
        self.level_hint = tk.StringVar(value="")
        self.level_lbl = tk.Label(meter, textvariable=self.level_hint, bg=BG,
                                  fg=MUTED, font=self.f_sub)
        self.level_lbl.pack(side="right", padx=(8, 0))
        self.level_bar = ttk.Progressbar(meter, maximum=100, length=200,
                                         style="Good.Horizontal.TProgressbar")
        self.level_bar.pack(side="right")
        tk.Label(meter, text="Niveau", bg=BG, fg=MUTED,
                 font=self.f_sub).pack(side="right", padx=(0, 8))

        # --- Kaart: transcript ---
        tcard = self._card(main)
        tcard.pack(fill="both", expand=True, pady=(12, 0))
        thead = tk.Frame(tcard, bg=CARD)
        thead.pack(fill="x", padx=14, pady=(10, 6))
        ttk.Label(thead, text="Transcript", style="Head.TLabel").pack(side="left")
        self.save_btn = ttk.Button(thead, text="💾  Notulen opslaan",
                                   style="Secondary.TButton", command=self._save,
                                   state="disabled")
        self.save_btn.pack(side="right")
        self.clear_btn = ttk.Button(thead, text="Wissen", style="Secondary.TButton",
                                    command=self._clear)
        self.clear_btn.pack(side="right", padx=(0, 8))

        body = tk.Frame(tcard, bg=CARD)
        body.pack(fill="both", expand=True, padx=14, pady=(0, 12))
        self.text = tk.Text(body, wrap="word", font=self.f_text, relief="flat",
                            bg="white", bd=10, highlightthickness=1,
                            highlightbackground=BORDER, highlightcolor=BORDER,
                            insertbackground=INK, spacing3=4)
        scroll = ttk.Scrollbar(body, command=self.text.yview)
        self.text.configure(yscrollcommand=scroll.set)
        self.text.pack(side="left", fill="both", expand=True)
        scroll.pack(side="right", fill="y")
        # Subtiel grijze placeholder als het veld leeg is.
        self.text.tag_configure("placeholder", foreground=MUTED)
        self._show_placeholder()

        # === Statusbalk ===
        statusbar = tk.Frame(self.root, bg=CARD, highlightbackground=BORDER,
                             highlightthickness=1)
        statusbar.pack(fill="x", side="bottom")
        self.status_var = tk.StringVar(value="Klaar.")
        self.status_dot = tk.Label(statusbar, text="●", bg=CARD, fg=GOOD,
                                   font=(FONT, 9))
        self.status_dot.pack(side="left", padx=(12, 6), pady=5)
        ttk.Label(statusbar, textvariable=self.status_var,
                  style="Status.TLabel").pack(side="left", pady=5)

    def _show_placeholder(self) -> None:
        if not self.text.get("1.0", "end").strip():
            self.text.insert("1.0",
                             "Het transcript verschijnt hier zodra je opneemt "
                             "of een audiobestand laadt.", "placeholder")
            self._has_placeholder = True

    def _clear_placeholder(self) -> None:
        if getattr(self, "_has_placeholder", False):
            self.text.delete("1.0", "end")
            self._has_placeholder = False

    def _set_status(self, text: str, kind: str = "idle") -> None:
        self.status_var.set(text)
        self.status_dot.config(
            fg={"busy": WARN, "error": BAD, "rec": REC}.get(kind, GOOD))

    # ---------- Apparaten ----------
    def _refresh_devices(self) -> None:
        labels = ["(standaard microfoon)"]
        self.devices = []
        try:
            from .audio_io import list_input_devices

            self.devices = list_input_devices()
            labels += [d.label for d in self.devices]
        except Exception as exc:  # noqa: BLE001
            self._set_status(f"Kon apparaten niet ophalen: {exc}", "error")

        self.device_combo["values"] = labels
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
                    self._set_status(payload, "busy" if self._busy else "idle")
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
                    self._set_status("Fout opgetreden.", "error")
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

    def _set_record_state(self, recording: bool) -> None:
        self.record_btn._recording = recording
        if recording:
            self.record_btn.config(text="■  Opname stoppen", bg=REC)
        else:
            self.record_btn.config(text="●  Opname starten", bg=ACCENT)

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
        self._set_record_state(True)
        self.file_btn.configure(state="disabled")
        msg = "Opname loopt… spreek maar."
        if self._keep_audio:
            msg += f"  (opslag: {path})"
        self._set_status(msg, "rec")

    def _stop_record(self) -> None:
        path = self.recorder.stop()
        self._set_record_state(False)
        self.file_btn.configure(state="normal")
        self.level_bar["value"] = 0
        self.level_hint.set("")
        if not path:
            self._set_status("Geen audio opgenomen.", "idle")
            return
        if self.recorder.clipped:
            self._set_status("Let op: oversturing gedetecteerd. Transcriberen…", "busy")
        else:
            self._set_status("Opname gestopt. Transcriberen…", "busy")
        # Tijdelijk opnamebestand opruimen na transcriptie indien niet bewaren.
        self._temp_audio = None if self._keep_audio else path
        self._start_transcription(path)

    def _update_level(self, dbfs: float, fraction: float, clipping: bool) -> None:
        self.level_bar["value"] = max(0, min(100, int(fraction * 100)))
        if clipping:
            self.level_bar.configure(style="Bad.Horizontal.TProgressbar")
            self.level_hint.set("⚠ te luid")
            self.level_lbl.config(fg=BAD)
        elif dbfs < -45:
            self.level_bar.configure(style="Warn.Horizontal.TProgressbar")
            self.level_hint.set("⚠ erg zacht")
            self.level_lbl.config(fg=WARN)
        else:
            self.level_bar.configure(style="Good.Horizontal.TProgressbar")
            self.level_hint.set("")
            self.level_lbl.config(fg=MUTED)

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
        self._clear_placeholder()
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
        self._set_status("Klaar.", "idle")

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
        self._has_placeholder = False

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
        self._set_status(f"Opgeslagen: {path}", "idle")

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
