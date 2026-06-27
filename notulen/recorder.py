"""Microfoon-/systeemaudio-opname naar een WAV-bestand.

We nemen op met de *eigen* samplerate van het gekozen apparaat (belangrijk:
WASAPI-loopback op Windows weigert een afwijkende rate zoals 16 kHz) en
schrijven incrementeel naar WAV. De transcriptie leest dat bestand en resamplet
zelf netjes naar 16 kHz. Zo gaat een opname ook nooit verloren als de
transcriptie faalt.
"""

from __future__ import annotations

import threading
from pathlib import Path
from typing import Callable, Optional

import numpy as np

from .audio_io import CLIP_PEAK, AudioDevice, WavWriter, meter_fraction, rms_to_dbfs

# Callback-types
LevelCallback = Callable[[float, float, bool], None]  # (dbfs, fractie, clipping)


class Recorder:
    """Neemt audio op tot het stoppen, met niveaumeting en WAV-backup."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._stream = None
        self._writer: Optional[WavWriter] = None
        self._on_level: Optional[LevelCallback] = None
        self._clipped = False
        self._wrote_audio = False
        self.path: Optional[str] = None
        self.sample_rate: int = 16_000

    @property
    def is_recording(self) -> bool:
        return self._stream is not None

    @property
    def clipped(self) -> bool:
        return self._clipped

    def _callback(self, indata, frames, time_info, status) -> None:  # noqa: ANN001
        data = indata.copy()  # sounddevice hergebruikt de buffer
        with self._lock:
            if self._writer is not None:
                try:
                    self._writer.write(data)
                    self._wrote_audio = True
                except Exception:
                    pass  # backup mag de opname nooit breken

        # Niveaumeting voor de VU-meter.
        if self._on_level is not None and data.size:
            mono = data.mean(axis=1) if data.ndim > 1 else data
            rms = float(np.sqrt(np.mean(np.square(mono))))
            peak = float(np.max(np.abs(mono)))
            clipping = peak >= CLIP_PEAK
            if clipping:
                self._clipped = True
            dbfs = rms_to_dbfs(rms)
            try:
                self._on_level(dbfs, meter_fraction(dbfs), clipping)
            except Exception:
                pass

    def _resolve_params(self, sd, device: Optional[AudioDevice]):
        """Bepaal apparaat-index, kanalen en samplerate voor de stream."""
        if device is not None:
            info = sd.query_devices(device.index)
            channels = max(1, device.channels)
            index = device.index
        else:
            info = sd.query_devices(kind="input")
            channels = min(2, max(1, int(info["max_input_channels"])))
            index = None
        sample_rate = int(info.get("default_samplerate") or 16_000)
        return index, channels, sample_rate

    def start(
        self,
        path: str | Path,
        device: Optional[AudioDevice] = None,
        on_level: Optional[LevelCallback] = None,
    ) -> None:
        """Begin met opnemen naar `path` (WAV) van het gekozen apparaat."""
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

        index, channels, sample_rate = self._resolve_params(sd, device)

        # Windows WASAPI-loopback voor systeemaudio (andere OS'en: monitor-bron).
        extra_settings = None
        if device is not None and device.is_loopback:
            try:
                extra_settings = sd.WasapiSettings(loopback=True)
            except Exception:
                extra_settings = None

        Path(path).parent.mkdir(parents=True, exist_ok=True)
        self._on_level = on_level
        self._clipped = False
        self._wrote_audio = False
        self.path = str(path)
        self.sample_rate = sample_rate
        with self._lock:
            self._writer = WavWriter(path, sample_rate)

        try:
            self._stream = sd.InputStream(
                samplerate=sample_rate,
                channels=channels,
                dtype="float32",
                device=index,
                callback=self._callback,
                extra_settings=extra_settings,
            )
            self._stream.start()
        except Exception as exc:
            with self._lock:
                if self._writer is not None:
                    self._writer.close()
                    self._writer = None
            self._stream = None
            raise RuntimeError(
                f"Kon de audiobron niet openen ({exc}). "
                "Controleer of het juiste apparaat gekozen is en niet door een "
                "andere app wordt geblokkeerd."
            ) from exc

    def stop(self) -> Optional[str]:
        """Stop de opname en geef het pad naar het WAV-bestand terug (of None)."""
        if self._stream is None:
            raise RuntimeError("Er is geen opname bezig.")

        self._stream.stop()
        self._stream.close()
        self._stream = None
        self._on_level = None

        with self._lock:
            if self._writer is not None:
                self._writer.close()
                self._writer = None

        return self.path if self._wrote_audio else None
