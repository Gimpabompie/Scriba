"""Command-line variant: transcribeer een audiobestand naar notulen.

Voorbeeld:
    python -m notulen.cli vergadering.mp3 --taal nl --model small -o notulen.md

Werkt zonder GUI/tkinter, handig voor losse bestanden of scripts.
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

from .transcriber import LANGUAGES, MODEL_SIZES, DEFAULT_MODEL, Transcriber

# Kortere taalcodes voor op de command line.
_LANG_CODES = {"auto": None, "nl": "nl", "en": "en"}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Offline spraak-naar-tekst notulen (Nederlands + Engels)."
    )
    parser.add_argument("audio", help="Pad naar het audiobestand.")
    parser.add_argument(
        "--taal",
        choices=_LANG_CODES.keys(),
        default="auto",
        help="Taal van de opname (default: auto detecteren).",
    )
    parser.add_argument(
        "--model",
        choices=MODEL_SIZES,
        default=DEFAULT_MODEL,
        help=f"Whisper-modelgrootte (default: {DEFAULT_MODEL}).",
    )
    parser.add_argument(
        "-o",
        "--output",
        help="Schrijf de notulen naar dit bestand i.p.v. naar het scherm.",
    )
    parser.add_argument(
        "--geen-tijdstempels",
        action="store_true",
        help="Laat de tijdstempels weg.",
    )
    args = parser.parse_args(argv)

    if not Path(args.audio).exists():
        print(f"Bestand niet gevonden: {args.audio}", file=sys.stderr)
        return 1

    transcriber = Transcriber(model_size=args.model)
    result = transcriber.transcribe(
        args.audio,
        language=_LANG_CODES[args.taal],
        on_status=lambda m: print(f"[…] {m}", file=sys.stderr),
    )

    text = result.as_minutes(with_timestamps=not args.geen_tijdstempels)
    if args.output:
        Path(args.output).write_text(text + "\n", encoding="utf-8")
        print(f"Notulen opgeslagen: {args.output}", file=sys.stderr)
    else:
        print(text)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
