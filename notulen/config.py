"""Eenvoudige, persistente instellingen (woordenlijst, model, apparaat, …).

Opgeslagen als JSON in ~/.notulen/config.json zodat keuzes onthouden worden
tussen sessies. Volledig lokaal.
"""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any

CONFIG_DIR = Path.home() / ".notulen"
CONFIG_PATH = CONFIG_DIR / "config.json"

DEFAULTS: dict[str, Any] = {
    "language": "Automatisch detecteren",
    "model": "small",
    "vocabulary": "",          # vrije tekst: namen en termen, komma/regel gescheiden
    "timestamps": True,
    "diarize": False,
    "device": None,            # naam van het gekozen audio-apparaat
    "save_audio": True,        # ruwe opname als .wav bewaren
}


def load() -> dict[str, Any]:
    data = dict(DEFAULTS)
    try:
        if CONFIG_PATH.exists():
            data.update(json.loads(CONFIG_PATH.read_text(encoding="utf-8")))
    except Exception:
        # Een kapotte config mag de app nooit blokkeren.
        pass
    return data


def save(data: dict[str, Any]) -> None:
    try:
        CONFIG_DIR.mkdir(parents=True, exist_ok=True)
        # Alleen bekende sleutels bewaren.
        clean = {k: data.get(k, v) for k, v in DEFAULTS.items()}
        CONFIG_PATH.write_text(
            json.dumps(clean, ensure_ascii=False, indent=2), encoding="utf-8"
        )
    except Exception:
        pass
