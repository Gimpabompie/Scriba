"""Offline transcriptie met faster-whisper.

Het model wordt eenmalig gedownload (HuggingFace) en daarna lokaal gecachet.
Na die eerste download werkt transcriberen volledig offline.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import TYPE_CHECKING, Any, Callable, Optional, Union

if TYPE_CHECKING:  # alleen voor type-hints; numpy is pas bij opname nodig
    import numpy as np

# Ondersteunde talen voor de notulen. "auto" laat Whisper zelf detecteren.
LANGUAGES = {
    "Automatisch detecteren": None,
    "Nederlands": "nl",
    "Engels": "en",
}

# Modelgroottes: groter = nauwkeuriger maar trager en meer geheugen.
MODEL_SIZES = ["tiny", "base", "small", "medium", "large-v3"]
DEFAULT_MODEL = "small"


@dataclass
class Segment:
    """Eén stuk transcript met start-/eindtijd in seconden."""

    start: float
    end: float
    text: str
    speaker: Optional[str] = None  # ingevuld bij sprekerherkenning


@dataclass
class TranscriptionResult:
    language: str
    segments: list[Segment] = field(default_factory=list)

    @property
    def text(self) -> str:
        return " ".join(s.text.strip() for s in self.segments).strip()

    def as_minutes(self, with_timestamps: bool = True) -> str:
        """Formatteer het transcript als leesbare notulen."""
        lines = []
        for s in self.segments:
            prefix = ""
            if with_timestamps:
                prefix += f"[{_fmt_ts(s.start)} - {_fmt_ts(s.end)}] "
            if s.speaker:
                prefix += f"{s.speaker}: "
            lines.append(f"{prefix}{s.text.strip()}".strip())
        if with_timestamps or any(s.speaker for s in self.segments):
            return "\n".join(lines)
        # Geen tijdstempels en geen sprekers: één doorlopende tekst.
        return self.text


def build_initial_prompt(vocabulary: str) -> Optional[str]:
    """Maak van een vrije woordenlijst (namen/termen) een Whisper initial_prompt.

    Whisper gebruikt deze context om eigennamen en jargon correct te spellen.
    """
    if not vocabulary or not vocabulary.strip():
        return None
    # Sta zowel komma's, puntkomma's als nieuwe regels toe als scheidingsteken.
    raw = vocabulary.replace(";", ",").replace("\n", ",")
    terms = [t.strip() for t in raw.split(",") if t.strip()]
    if not terms:
        return None
    return "Namen en termen: " + ", ".join(terms) + "."


def _fmt_ts(seconds: float) -> str:
    seconds = max(0, int(round(seconds)))
    h, rem = divmod(seconds, 3600)
    m, s = divmod(rem, 60)
    if h:
        return f"{h:02d}:{m:02d}:{s:02d}"
    return f"{m:02d}:{s:02d}"


class Transcriber:
    """Laadt het Whisper-model lui en transcribeert audio of bestanden."""

    def __init__(self, model_size: str = DEFAULT_MODEL, compute_type: str = "int8") -> None:
        self.model_size = model_size
        self.compute_type = compute_type
        self._model = None

    def load(self, on_status: Optional[Callable[[str], None]] = None) -> None:
        """Laad het model in (download bij eerste keer)."""
        if self._model is not None:
            return
        if on_status:
            on_status(f"Model '{self.model_size}' laden… (eerste keer kan even duren)")
        try:
            from faster_whisper import WhisperModel
        except Exception as exc:  # pragma: no cover
            raise RuntimeError(
                "Transcriptie vereist 'faster-whisper'. "
                "Installeer met: pip install faster-whisper\n"
                f"(onderliggende fout: {exc})"
            ) from exc

        # CPU met int8 is een goede, brede default. Op een GPU kun je
        # device='cuda' en compute_type='float16' gebruiken.
        self._model = WhisperModel(
            self.model_size, device="cpu", compute_type=self.compute_type
        )
        if on_status:
            on_status("Model geladen.")

    def transcribe(
        self,
        audio: Union[str, "np.ndarray", Any],
        language: Optional[str] = None,
        vocabulary: str = "",
        diarize: bool = False,
        sample_rate: int = 16_000,
        on_status: Optional[Callable[[str], None]] = None,
        on_segment: Optional[Callable[[Segment], None]] = None,
    ) -> TranscriptionResult:
        """Transcribeer een audiobestand (pad) of een numpy-buffer (16 kHz mono).

        `vocabulary` geeft namen/jargon mee voor betere spelling.
        `diarize` voegt sprekerlabels toe (vereist pyannote.audio).
        `on_segment` wordt aangeroepen zodra een segment klaar is, zodat de GUI
        het transcript live kan opbouwen.
        """
        self.load(on_status=on_status)
        assert self._model is not None

        if on_status:
            on_status("Bezig met transcriberen…")

        # Bij sprekerherkenning bouwen we het transcript eerst volledig op en
        # voegen we daarna de labels toe; live segmenten zijn dan minder zinvol.
        emit_live = on_segment is not None and not diarize

        segments_iter, info = self._model.transcribe(
            audio,
            language=language,
            initial_prompt=build_initial_prompt(vocabulary),
            vad_filter=True,  # negeer stiltes -> betere notulen
            beam_size=5,
        )

        result = TranscriptionResult(language=info.language)
        for seg in segments_iter:
            s = Segment(start=seg.start, end=seg.end, text=seg.text)
            result.segments.append(s)
            if emit_live:
                on_segment(s)

        if diarize:
            try:
                from .diarization import assign_speakers, diarize as run_diarize

                turns = run_diarize(audio, sample_rate=sample_rate, on_status=on_status)
                assign_speakers(result.segments, turns)
            except Exception as exc:  # noqa: BLE001
                if on_status:
                    on_status(f"Sprekerherkenning overgeslagen: {exc}")
            if on_segment:
                for s in result.segments:
                    on_segment(s)

        if on_status:
            on_status(f"Klaar. Taal: {result.language}")
        return result
