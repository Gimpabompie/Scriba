"""Optionele sprekerherkenning (diarization).

De zware afhankelijkheid (pyannote.audio) wordt lui geladen en is optioneel.
De koppel-logica (`assign_speakers`) is een pure functie en los testbaar.

Let op: pyannote's voorgetrainde model is 'gated' op HuggingFace. Je accepteert
eenmalig de voorwaarden en zet een token (HUGGINGFACE_TOKEN). Daarna draait het
model lokaal/offline.
"""

from __future__ import annotations

import os
from dataclasses import dataclass
from typing import TYPE_CHECKING, Callable, Optional

if TYPE_CHECKING:
    import numpy as np

    from .transcriber import Segment


@dataclass
class SpeakerTurn:
    start: float
    end: float
    speaker: str


def _overlap(a_start: float, a_end: float, b_start: float, b_end: float) -> float:
    """Lengte van de overlap tussen twee tijdsintervallen (>= 0)."""
    return max(0.0, min(a_end, b_end) - max(a_start, b_start))


def assign_speakers(segments, turns: list[SpeakerTurn]) -> None:
    """Ken aan elk transcript-segment de spreker met de grootste tijdsoverlap toe.

    Past `segment.speaker` in-place aan. Pure functie: geen audio of modellen
    nodig, daarom goed testbaar.
    """
    for seg in segments:
        best_speaker: Optional[str] = None
        best_overlap = 0.0
        for t in turns:
            ov = _overlap(seg.start, seg.end, t.start, t.end)
            if ov > best_overlap:
                best_overlap = ov
                best_speaker = t.speaker
        seg.speaker = best_speaker


def diarize(
    audio,
    sample_rate: int = 16_000,
    on_status: Optional[Callable[[str], None]] = None,
) -> list[SpeakerTurn]:
    """Bepaal sprekerbeurten met pyannote. Geeft een lijst SpeakerTurn terug."""
    if on_status:
        on_status("Sprekers herkennen…")
    try:
        import torch  # noqa: F401
        from pyannote.audio import Pipeline
    except Exception as exc:  # pragma: no cover
        raise RuntimeError(
            "Sprekerherkenning vereist 'pyannote.audio' en 'torch'.\n"
            "Installeer met: pip install pyannote.audio torch\n"
            f"(onderliggende fout: {exc})"
        ) from exc

    token = os.environ.get("HUGGINGFACE_TOKEN") or os.environ.get("HF_TOKEN")
    pipeline = Pipeline.from_pretrained(
        "pyannote/speaker-diarization-3.1", use_auth_token=token
    )

    import numpy as np
    import torch

    if isinstance(audio, str):
        diarization = pipeline(audio)
    else:
        waveform = torch.from_numpy(np.asarray(audio, dtype="float32")).unsqueeze(0)
        diarization = pipeline({"waveform": waveform, "sample_rate": sample_rate})

    turns = [
        SpeakerTurn(start=seg.start, end=seg.end, speaker=str(speaker))
        for seg, _, speaker in diarization.itertracks(yield_label=True)
    ]
    if on_status:
        n = len({t.speaker for t in turns})
        on_status(f"{n} spreker(s) herkend.")
    return turns
