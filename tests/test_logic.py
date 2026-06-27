"""Tests voor de pure logica (geen audio, modellen of GUI nodig)."""

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from notulen.audio_io import meter_fraction, rms_to_dbfs  # noqa: E402
from notulen.diarization import SpeakerTurn, assign_speakers  # noqa: E402
from notulen.transcriber import (  # noqa: E402
    Segment,
    TranscriptionResult,
    _fmt_ts,
    build_initial_prompt,
)


def test_fmt_ts():
    assert _fmt_ts(0) == "00:00"
    assert _fmt_ts(65) == "01:05"
    assert _fmt_ts(3661) == "01:01:01"


def test_rms_to_dbfs():
    assert rms_to_dbfs(0.0) <= -100
    assert rms_to_dbfs(1.0) == 0.0
    assert -7 < rms_to_dbfs(0.5) < -5  # ~ -6 dBFS


def test_meter_fraction():
    assert meter_fraction(-60) == 0.0
    assert meter_fraction(0) == 1.0
    assert meter_fraction(-120) == 0.0
    assert abs(meter_fraction(-30) - 0.5) < 1e-6


def test_build_initial_prompt():
    assert build_initial_prompt("") is None
    assert build_initial_prompt("   ") is None
    p = build_initial_prompt("Jan Jansen, Acme BV\nsprint; backlog")
    assert "Jan Jansen" in p and "Acme BV" in p
    assert "sprint" in p and "backlog" in p
    assert p.startswith("Namen en termen:")


def test_minutes_plain():
    r = TranscriptionResult("nl", [Segment(0, 2, " Hallo "), Segment(2, 4, " wereld")])
    assert r.text == "Hallo wereld"
    assert r.as_minutes(with_timestamps=False) == "Hallo wereld"


def test_minutes_with_timestamps():
    r = TranscriptionResult("nl", [Segment(0, 2, "Hallo")])
    assert r.as_minutes(with_timestamps=True) == "[00:00 - 00:02] Hallo"


def test_assign_speakers():
    segs = [Segment(0, 2, "Hoi"), Segment(2, 4, "Dag")]
    turns = [
        SpeakerTurn(0.0, 1.9, "SPEAKER_00"),
        SpeakerTurn(2.0, 4.0, "SPEAKER_01"),
    ]
    assign_speakers(segs, turns)
    assert segs[0].speaker == "SPEAKER_00"
    assert segs[1].speaker == "SPEAKER_01"
    # Met sprekers verschijnt het label in de notulen.
    r = TranscriptionResult("nl", segs)
    out = r.as_minutes(with_timestamps=False)
    assert "SPEAKER_00: Hoi" in out and "SPEAKER_01: Dag" in out


def test_assign_speakers_no_overlap():
    segs = [Segment(10, 12, "Stilte")]
    turns = [SpeakerTurn(0.0, 1.0, "SPEAKER_00")]
    assign_speakers(segs, turns)
    assert segs[0].speaker is None


def _run_all():
    fns = [v for k, v in sorted(globals().items()) if k.startswith("test_")]
    for fn in fns:
        fn()
        print(f"ok  {fn.__name__}")
    print(f"\n{len(fns)} tests geslaagd.")


if __name__ == "__main__":
    _run_all()
