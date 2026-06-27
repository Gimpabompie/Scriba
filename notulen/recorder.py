"""Microfoon-/systeemaudio-opname naar een numpy-buffer.

We nemen mono audio op met 16 kHz (wat Whisper verwacht), meten live het
niveau voor de VU-meter, en schrijven optioneel alles incrementeel naar een
WAV-bestand zodat een opname nooit verloren gaat als de transcriptie faalt.
"""

from __future__ import annotations

import threading
from pathlib import Path
from typing import Callable, Optional

import numpy as np

from .audio_io import (
    CLIP_PEAK,
    SAMPLE_RATE,
    AudioDevice,
    WavWriter,
    rms_to_dbfs,
)

CHANNELS = 1

# Callback-types
LevelCallback = Callable[[float, float, bool], None]  # (dbfs, fractie, clipping)


class Recorder:
    """Neemt audio op tot het stoppen, met niveaumeting en optionele backup."""

    def __init__(self, sample_rate: int = SAMPLE_RATE) -> None:
        self.sample_rate = sample_rate
        self._frames: list[np.ndarray] = []
        self._lock = threading.Lock()
        self._stream = None
        self._writer: Optional[WavWriter] = None
        self._on_level: Optional[LevelCallback] = None
        self._clipped = False
        self.backup_path: Optional[str] = None

    @property
    def is_recording(self) -> bool:
        return self._stream is not None

    @property
    def clipped(self) -> bool:
        return self._clipped

    def _callback(self, indata, frames, time_info, status) -> None:  # noqa: ANN001
        data = indata.copy()  # sounddevice hergebruikt de buffer
        with self._lock:
            self._frames.append(data)
            if self._writer is not None:
                try:
                    self._writer.write(data)
                except Exception:
                    pass  # backup mag de opname nooit breken

        # Niveaumeting voor de VU-meter.
        if self._on_level is not None:
            mono = data.mean(axis=1) if data.ndim > 1 else data
            rms = float(np.sqrt(np.mean(np.square(mono)))) if mono.size else 0.0
            peak = float(np.max(np.abs(mono))) if mono.size else 0.0
            clipping = peak >= CLIP_PEAK
            if clipping:
                self._clipped = True
            dbfs = rms_to_dbfs(rms)
            from .audio_io import meter_fraction

            try:
                self._on_level(dbfs, meter_fraction(dbfs), clipping)
            except Exception:
                pass

    def start(
        self,
        device: Optional[AudioDevice] = None,
        on_level: Optional[LevelCallback] = None,
        backup_path: Optional[str | Path] = None,
    ) -> None:
        """Begin met opnemen van het gekozen apparaat (of de standaard-microfoon)."""
        if self._stream is not None:
            raise RuntimeError("Opname is al bezig.")

        try:
            import sounddevice as sd
        except Exception as exc:  # pragma: no cover
            raise RuntimeError(
                "Audio-opname vereist 'sounddevice'. "
                "Installeer met: pip install sounddevice\n"
                f"(onderliggende fout: {exc})"
            ) from exc

        self._on_level = on_level
        self._clipped = False
        with self._lock:
            self._frames = []
            self._writer = WavWriter(backup_path, self.sample_rate) if backup_path else None
        self.backup_path = str(backup_path) if backup_path else None

        # Windows WASAPI-loopback voor systeemaudio.
        extra_settings = None
        device_index = device.index if device else None
        in_channels = CHANNELS
        if device is not None:
            in_channels = min(2, max(1, device.channels))
            if device.is_loopback:
                try:
                    extra_settings = sd.WasapiSettings(loopback=True)
                except Exception:
                    extra_settings = None  # niet-Windows: monitor-bron werkt direct

        self._stream = sd.InputStream(
            samplerate=self.sample_rate,
            channels=in_channels,
            dtype="float32",
            device=device_index,
            callback=self._callback,
            extra_settings=extra_settings,
        )
        self._stream.start()

    def stop(self) -> np.ndarray:
        """Stop de opname en geef de volledige audio terug als float32 mono-array."""
        if self._stream is None:
            raise RuntimeError("Er is geen opname bezig.")

        self._stream.stop()
        self._stream.close()
        self._stream = None
        self._on_level = None

        with self._lock:
            frames = self._frames
            self._frames = []
            if self._writer is not None:
                self._writer.close()
                self._writer = None

        if not frames:
            return np.zeros(0, dtype=np.float32)

        audio = np.concatenate(frames, axis=0)
        if audio.ndim > 1:
            audio = audio.mean(axis=1)
        return audio.astype(np.float32)
