"""Audio-hulpmiddelen: apparaatkeuze, niveaumeting en WAV-backup.

De numpy-afhankelijke delen worden pas gebruikt tijdens een echte opname; de
pure rekenfuncties (dBFS, meterstand) zijn los testbaar.
"""

from __future__ import annotations

import math
import wave
from dataclasses import dataclass
from pathlib import Path
from typing import TYPE_CHECKING, Optional

if TYPE_CHECKING:
    import numpy as np

SAMPLE_RATE = 16_000


@dataclass
class AudioDevice:
    """Een selecteerbaar audio-apparaat."""

    index: int
    name: str
    channels: int
    is_loopback: bool = False  # True = systeem-/vergaderaudio (uitgaand)

    @property
    def label(self) -> str:
        soort = "systeemaudio" if self.is_loopback else "microfoon"
        return f"{self.name}  ({soort})"


# ---------- Pure rekenhelpers (testbaar zonder audio/numpy) ----------

def rms_to_dbfs(rms: float) -> float:
    """Zet een lineaire RMS-waarde (0..1) om naar dBFS (<= 0)."""
    if rms <= 1e-9:
        return -120.0
    return 20.0 * math.log10(min(rms, 1.0))


def meter_fraction(dbfs: float, floor: float = -60.0) -> float:
    """Map dBFS naar een meterstand 0.0..1.0 voor de VU-balk."""
    if dbfs <= floor:
        return 0.0
    if dbfs >= 0.0:
        return 1.0
    return (dbfs - floor) / (0.0 - floor)


# Drempels voor waarschuwingen.
CLIP_PEAK = 0.99       # vrijwel vol bereik -> oversturing
TOO_QUIET_DBFS = -45.0  # vrijwel niets binnengekomen


# ---------- Apparaatlijst ----------

def list_input_devices() -> list[AudioDevice]:
    """Geef opneembare apparaten terug: microfoons en (waar mogelijk) systeemaudio.

    Op Linux verschijnen 'monitor'-bronnen vanzelf als invoerapparaat. Op
    Windows wordt via de WASAPI-hostapi loopback van uitvoerapparaten
    aangeboden, zodat ook vergaderaudio kan worden opgenomen.
    """
    import sounddevice as sd

    devices = sd.query_devices()
    hostapis = sd.query_hostapis()
    result: list[AudioDevice] = []

    for i, d in enumerate(devices):
        api = hostapis[d["hostapi"]]["name"] if d.get("hostapi") is not None else ""
        if d["max_input_channels"] > 0:
            is_mon = "monitor" in d["name"].lower()  # PulseAudio loopback
            result.append(
                AudioDevice(
                    index=i,
                    name=d["name"],
                    channels=min(2, d["max_input_channels"]),
                    is_loopback=is_mon,
                )
            )
        # Windows WASAPI: uitvoerapparaten kunnen via loopback worden opgenomen.
        elif d["max_output_channels"] > 0 and "WASAPI" in api:
            result.append(
                AudioDevice(
                    index=i,
                    name=d["name"],
                    channels=min(2, d["max_output_channels"]),
                    is_loopback=True,
                )
            )
    return result


def default_input_device() -> Optional[AudioDevice]:
    devs = list_input_devices()
    for d in devs:
        if not d.is_loopback:
            return d
    return devs[0] if devs else None


# ---------- WAV-backup ----------

class WavWriter:
    """Schrijft float32-frames incrementeel weg als 16-bit mono WAV-bestand."""

    def __init__(self, path: str | Path, sample_rate: int = SAMPLE_RATE) -> None:
        self.path = str(path)
        self._wav = wave.open(self.path, "wb")
        self._wav.setnchannels(1)
        self._wav.setsampwidth(2)  # int16
        self._wav.setframerate(sample_rate)

    def write(self, frame: "np.ndarray") -> None:
        import numpy as np

        mono = frame.mean(axis=1) if frame.ndim > 1 else frame
        clipped = np.clip(mono, -1.0, 1.0)
        int16 = (clipped * 32767.0).astype("<i2")
        self._wav.writeframes(int16.tobytes())

    def close(self) -> None:
        try:
            self._wav.close()
        except Exception:
            pass
