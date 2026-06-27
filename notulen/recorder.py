"""Microfoon-opname naar een numpy-buffer.

We nemen mono audio op met 16 kHz, precies wat Whisper verwacht, zodat we de
opname direct (zonder tussenbestand) aan de transcriber kunnen doorgeven.
"""

from __future__ import annotations

import threading

import numpy as np

# Whisper-modellen verwachten 16 kHz mono audio.
SAMPLE_RATE = 16_000
CHANNELS = 1


class Recorder:
    """Neemt audio op van de standaard-microfoon tot het stoppen.

    De daadwerkelijke afhankelijkheid (`sounddevice`) wordt pas bij `start()`
    geïmporteerd, zodat de rest van de app ook bruikbaar blijft (bv. het
    transcriberen van bestaande audiobestanden) als er geen audio-backend is.
    """

    def __init__(self, sample_rate: int = SAMPLE_RATE) -> None:
        self.sample_rate = sample_rate
        self._frames: list[np.ndarray] = []
        self._lock = threading.Lock()
        self._stream = None

    @property
    def is_recording(self) -> bool:
        return self._stream is not None

    def _callback(self, indata, frames, time_info, status) -> None:  # noqa: ANN001
        # Kopiëren is belangrijk: sounddevice hergebruikt de buffer.
        with self._lock:
            self._frames.append(indata.copy())

    def start(self) -> None:
        """Begin met opnemen. Gooit RuntimeError als al bezig."""
        if self._stream is not None:
            raise RuntimeError("Opname is al bezig.")

        try:
            import sounddevice as sd
        except Exception as exc:  # pragma: no cover - hangt van systeem af
            raise RuntimeError(
                "Microfoon-opname vereist 'sounddevice'. "
                "Installeer met: pip install sounddevice\n"
                f"(onderliggende fout: {exc})"
            ) from exc

        with self._lock:
            self._frames = []

        self._stream = sd.InputStream(
            samplerate=self.sample_rate,
            channels=CHANNELS,
            dtype="float32",
            callback=self._callback,
        )
        self._stream.start()

    def stop(self) -> np.ndarray:
        """Stop de opname en geef de volledige audio terug als float32-array."""
        if self._stream is None:
            raise RuntimeError("Er is geen opname bezig.")

        self._stream.stop()
        self._stream.close()
        self._stream = None

        with self._lock:
            frames = self._frames
            self._frames = []

        if not frames:
            return np.zeros(0, dtype=np.float32)

        audio = np.concatenate(frames, axis=0)
        # Mono maken en plat slaan naar 1D.
        if audio.ndim > 1:
            audio = audio.mean(axis=1)
        return audio.astype(np.float32)
