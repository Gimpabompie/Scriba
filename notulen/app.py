"""Tkinter-GUI voor de offline notulen-app.

Functies:
- Opnemen via de microfoon (start/stop) of een bestaand audiobestand laden.
- Offline transcriberen met Whisper (Nederlands, Engels of automatisch).
- Transcript live in beeld, met of zonder tijdstempels.
- Notulen opslaan als .txt of .md.

Zwaar werk (model laden, transcriberen) draait in een achtergrond-thread,
zodat de interface niet bevriest. Communicatie terug naar de GUI loopt via
een thread-veilige queue die periodiek wordt uitgelezen.
"""

from __future__ import annotations

import queue
import threading
from pathlib import Path

from .recorder import Recorder
from .transcriber import (
    DEFAULT_MODEL,
    LANGUAGES,
    MODEL_SIZES,
    Segment,
    Transcriber,
    TranscriptionResult,
)


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

        self.recorder = Recorder()
        self.transcriber: Transcriber | None = None
        self.result: TranscriptionResult | None = None
        self._events: "queue.Queue[tuple]" = queue.Queue()
        self._busy = False

        self._build_ui()
        self.root.after(100, self._drain_events)

    # ---------- UI opbouw ----------
    def _build_ui(self) -> None:
        tk, ttk = self.tk, self.ttk
        self.root = tk.Tk()
        self.root.title("Notulen — offline spraak naar tekst")
        self.root.geometry("780x560")
        self.root.minsize(640, 480)

        top = ttk.Frame(self.root, padding=10)
        top.pack(fill="x")

        ttk.Label(top, text="Taal:").grid(row=0, column=0, sticky="w")
        self.lang_var = tk.StringVar(value="Automatisch detecteren")
        ttk.OptionMenu(
            top, self.lang_var, self.lang_var.get(), *LANGUAGES.keys()
        ).grid(row=0, column=1, sticky="w", padx=(4, 16))

        ttk.Label(top, text="Model:").grid(row=0, column=2, sticky="w")
        self.model_var = tk.StringVar(value=DEFAULT_MODEL)
        ttk.OptionMenu(
            top, self.model_var, self.model_var.get(), *MODEL_SIZES
        ).grid(row=0, column=3, sticky="w", padx=(4, 16))

        self.ts_var = tk.BooleanVar(value=True)
        ttk.Checkbutton(top, text="Tijdstempels", variable=self.ts_var).grid(
            row=0, column=4, sticky="w"
        )

        # Knoppenrij
        btns = ttk.Frame(self.root, padding=(10, 0))
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

        # Transcriptveld
        body = ttk.Frame(self.root, padding=10)
        body.pack(fill="both", expand=True)
        self.text = tk.Text(body, wrap="word", font=("TkDefaultFont", 11))
        scroll = ttk.Scrollbar(body, command=self.text.yview)
        self.text.configure(yscrollcommand=scroll.set)
        self.text.pack(side="left", fill="both", expand=True)
        scroll.pack(side="right", fill="y")

        # Statusbalk
        self.status_var = tk.StringVar(value="Klaar.")
        status = ttk.Frame(self.root, padding=(10, 4))
        status.pack(fill="x")
        ttk.Label(status, textvariable=self.status_var, anchor="w").pack(fill="x")

    # ---------- Achtergrond-communicatie ----------
    def _drain_events(self) -> None:
        """Verwerk berichten uit de werk-thread op de GUI-thread."""
        try:
            while True:
                kind, payload = self._events.get_nowait()
                if kind == "status":
                    self.status_var.set(payload)
                elif kind == "segment":
                    self._append_segment(payload)
                elif kind == "done":
                    self._on_done(payload)
                elif kind == "error":
                    self._busy = False
                    self._set_controls_enabled(True)
                    self.messagebox.showerror("Fout", payload)
                    self.status_var.set("Fout opgetreden.")
        except queue.Empty:
            pass
        self.root.after(100, self._drain_events)

    def _post(self, kind: str, payload) -> None:
        self._events.put((kind, payload))

    # ---------- Acties ----------
    def _toggle_record(self) -> None:
        if self.recorder.is_recording:
            self._stop_record()
        else:
            self._start_record()

    def _start_record(self) -> None:
        if self._busy:
            return
        try:
            self.recorder.start()
        except RuntimeError as exc:
            self.messagebox.showerror("Microfoon", str(exc))
            return
        self.record_btn.configure(text="■ Opname stoppen")
        self.file_btn.configure(state="disabled")
        self.status_var.set("Opname loopt… spreek maar.")

    def _stop_record(self) -> None:
        audio = self.recorder.stop()
        self.record_btn.configure(text="● Opname starten")
        self.file_btn.configure(state="normal")
        if audio.size == 0:
            self.status_var.set("Geen audio opgenomen.")
            return
        self.status_var.set("Opname gestopt. Transcriberen…")
        self._start_transcription(audio)

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
            self._start_transcription(path)

    def _start_transcription(self, audio) -> None:
        self._clear()
        self._busy = True
        self._set_controls_enabled(False)

        model_size = self.model_var.get()
        if self.transcriber is None or self.transcriber.model_size != model_size:
            self.transcriber = Transcriber(model_size=model_size)

        language = LANGUAGES[self.lang_var.get()]

        thread = threading.Thread(
            target=self._work, args=(audio, language), daemon=True
        )
        thread.start()

    def _work(self, audio, language) -> None:
        try:
            assert self.transcriber is not None
            result = self.transcriber.transcribe(
                audio,
                language=language,
                on_status=lambda m: self._post("status", m),
                on_segment=lambda s: self._post("segment", s),
            )
            self._post("done", result)
        except Exception as exc:  # noqa: BLE001
            self._post("error", str(exc))

    # ---------- GUI-updates ----------
    def _append_segment(self, seg: Segment) -> None:
        if self.ts_var.get():
            from .transcriber import _fmt_ts

            line = f"[{_fmt_ts(seg.start)} - {_fmt_ts(seg.end)}] {seg.text.strip()}\n"
        else:
            line = seg.text.strip() + " "
        self.text.insert("end", line)
        self.text.see("end")

    def _on_done(self, result: TranscriptionResult) -> None:
        self.result = result
        self._busy = False
        self._set_controls_enabled(True)
        self.save_btn.configure(state="normal")

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
            f"# Notulen\n\nTaal: {self.result.language}\n\n---\n\n"
            if path.endswith(".md")
            else ""
        )
        Path(path).write_text(header + content + "\n", encoding="utf-8")
        self.status_var.set(f"Opgeslagen: {path}")

    def run(self) -> None:
        self.root.mainloop()


def main() -> None:
    NotulenApp().run()


if __name__ == "__main__":
    main()
