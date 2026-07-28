#!/usr/bin/env python3
"""Local Stage 6 music analyzer CLI. User-facing failures are always JSON."""

from __future__ import annotations

import argparse
import json
import math
import os
import subprocess
import sys
import tempfile
import traceback
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable

NAME = "cs2-music-analyzer"
VERSION = "0.1.1"
SCHEMA = "1.0"
SUPPORTED = {".mp3", ".wav", ".flac", ".m4a", ".aac"}


@dataclass
class CliFailure(Exception):
    code: str
    message: str
    exit_code: int


def _run(arguments: list[str]) -> subprocess.CompletedProcess[str]:
    try:
        return subprocess.run(
            arguments,
            check=False,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            shell=False,
        )
    except OSError as error:
        raise CliFailure("DECODER_UNAVAILABLE", str(error), 20) from error


def probe(path: Path) -> dict[str, Any]:
    result = _run([
        "ffprobe", "-v", "error", "-show_entries",
        "format=duration:stream=codec_type,sample_rate,channels",
        "-of", "json", str(path),
    ])
    if result.returncode != 0:
        raise CliFailure("DECODING_FAILED", result.stderr.strip() or "FFprobe failed.", 20)
    try:
        document = json.loads(result.stdout)
        audio = next(
            stream for stream in document.get("streams", [])
            if stream.get("codec_type") == "audio"
        )
        duration = float(document["format"]["duration"])
        return {
            "duration": duration,
            "sampleRate": int(audio.get("sample_rate") or 0),
            "channels": int(audio.get("channels") or 0),
        }
    except (KeyError, StopIteration, TypeError, ValueError) as error:
        raise CliFailure("NO_AUDIO_STREAM", "FFprobe found no usable audio stream.", 12) from error


def validate_input(path_text: str) -> tuple[Path, dict[str, Any]]:
    path = Path(path_text).expanduser().resolve()
    if not path.is_file():
        raise CliFailure("INPUT_NOT_FOUND", f"Input was not found: {path}", 10)
    if path.suffix.lower() not in SUPPORTED:
        raise CliFailure("UNSUPPORTED_FORMAT", f"Unsupported audio extension: {path.suffix}", 11)
    metadata = probe(path)
    if metadata["duration"] <= 0:
        raise CliFailure("INVALID_DURATION", "Audio duration must be positive.", 13)
    decode = _run([
        "ffmpeg", "-v", "error", "-t", "3", "-i", str(path),
        "-map", "0:a:0", "-f", "null", "-",
    ])
    if decode.returncode != 0:
        raise CliFailure("DECODING_FAILED", decode.stderr.strip() or "Audio decode failed.", 20)
    return path, metadata


def normalize(values: Iterable[float]) -> list[float]:
    result = [float(value) for value in values]
    maximum = max(result, default=0.0)
    if maximum <= 0:
        return [0.0 for _ in result]
    return [max(0.0, min(1.0, value / maximum)) for value in result]


def composite_drop_score(
    onset: float,
    energy_change: float,
    low_frequency_change: float,
    section_proximity: float,
    downbeat_proximity: float,
    confidence: float,
) -> float:
    raw = (
        0.25 * onset
        + 0.30 * energy_change
        + 0.20 * low_frequency_change
        + 0.15 * section_proximity
        + 0.10 * downbeat_proximity
    )
    penalty = max(0.0, 0.5 - confidence) * 0.2
    return max(0.0, min(1.0, raw - penalty))


def _near(time_value: float, points: Iterable[float], tolerance: float) -> float:
    distance = min((abs(time_value - point) for point in points), default=math.inf)
    return max(0.0, 1.0 - distance / tolerance) if distance <= tolerance else 0.0


def analyze(path: Path, metadata: dict[str, Any]) -> dict[str, Any]:
    try:
        import librosa  # type: ignore
        import numpy as np  # type: ignore
    except ImportError as error:
        raise CliFailure(
            "ANALYZER_DEPENDENCY_MISSING",
            "librosa is not installed; install tools/music-analyzer/requirements.txt.",
            21,
        ) from error

    try:
        samples, sample_rate = librosa.load(str(path), sr=None, mono=True)
        if samples.size == 0:
            raise ValueError("Decoded waveform is empty.")
        hop = 512
        onset_envelope = librosa.onset.onset_strength(
            y=samples, sr=sample_rate, hop_length=hop)
        tempo, beat_frames = librosa.beat.beat_track(
            onset_envelope=onset_envelope, sr=sample_rate, hop_length=hop)
        beat_times = librosa.frames_to_time(
            beat_frames, sr=sample_rate, hop_length=hop)
        onset_frames = librosa.onset.onset_detect(
            onset_envelope=onset_envelope,
            sr=sample_rate,
            hop_length=hop,
            backtrack=False,
        )
        onset_times = librosa.frames_to_time(
            onset_frames, sr=sample_rate, hop_length=hop)
        normalized_onsets = np.asarray(normalize(onset_envelope))
        rms = librosa.feature.rms(y=samples, hop_length=hop)[0]
        normalized_energy = np.asarray(normalize(rms))
        stft = np.abs(librosa.stft(samples, hop_length=hop))
        frequencies = librosa.fft_frequencies(sr=sample_rate)
        low = stft[frequencies <= 180].mean(axis=0)
        normalized_low = np.asarray(normalize(low))
        chroma = librosa.feature.chroma_stft(
            S=stft, sr=sample_rate, hop_length=hop)
        section_count = max(1, min(12, int(metadata["duration"] // 20) + 1))
        boundaries = librosa.segment.agglomerative(chroma, section_count)
        boundary_frames = sorted({0, *[int(value) for value in boundaries], len(rms) - 1})
    except Exception as error:
        raise CliFailure("RHYTHM_ANALYSIS_FAILED", str(error), 21) from error

    beat_strengths = [
        float(normalized_onsets[min(int(frame), len(normalized_onsets) - 1)])
        for frame in beat_frames
    ]
    tempo_value = float(np.asarray(tempo).reshape(-1)[0]) if np.size(tempo) else 0.0
    tempo_confidence = min(1.0, len(beat_times) / max(1.0, metadata["duration"] / 2))
    phase = max(
        range(min(4, len(beat_strengths) or 1)),
        key=lambda value: sum(beat_strengths[value::4]),
    )
    downbeat_indexes = list(range(phase, len(beat_times), 4))
    section_times = [
        float(librosa.frames_to_time(frame, sr=sample_rate, hop_length=hop))
        for frame in boundary_frames
    ]
    sections: list[dict[str, Any]] = []
    for index in range(len(boundary_frames) - 1):
        start_frame, end_frame = boundary_frames[index:index + 2]
        energy = float(np.mean(normalized_energy[start_frame:max(start_frame + 1, end_frame)]))
        sections.append({
            "index": index + 1,
            "startSeconds": section_times[index],
            "endSeconds": min(metadata["duration"], section_times[index + 1]),
            "label": "HighEnergy" if energy >= 0.65 else ("LowEnergy" if energy < 0.35 else "MidEnergy"),
            "energy": energy,
        })

    onset_items: list[dict[str, Any]] = []
    drop_items: list[dict[str, Any]] = []
    last_drop = -math.inf
    for index, (frame_value, time_value) in enumerate(zip(onset_frames, onset_times), 1):
        frame = min(int(frame_value), len(normalized_onsets) - 1)
        strength = float(normalized_onsets[frame])
        onset_items.append({"index": index, "timeSeconds": float(time_value), "strength": strength})
        window = max(1, int(sample_rate / hop))
        before = float(np.mean(normalized_energy[max(0, frame - window):frame + 1]))
        after = float(np.mean(normalized_energy[frame:min(len(normalized_energy), frame + window)]))
        low_before = float(np.mean(normalized_low[max(0, frame - window):frame + 1]))
        low_after = float(np.mean(normalized_low[frame:min(len(normalized_low), frame + window)]))
        energy_change = max(0.0, min(1.0, after - before))
        low_change = max(0.0, min(1.0, low_after - low_before))
        section_near = _near(float(time_value), section_times[1:-1], 1.0)
        downbeat_near = _near(
            float(time_value), (float(beat_times[i]) for i in downbeat_indexes), 0.15)
        confidence = min(1.0, 0.5 * tempo_confidence + 0.5 * strength)
        score = composite_drop_score(
            strength, energy_change, low_change, section_near, downbeat_near, confidence)
        if score >= 0.65 and float(time_value) - last_drop >= 4:
            drop_items.append({
                "index": len(drop_items) + 1,
                "timeSeconds": float(time_value),
                "score": score,
                "energyChange": energy_change,
                "onsetStrength": strength,
                "lowFrequencyImpact": low_change,
                "confidence": confidence,
            })
            last_drop = float(time_value)

    warnings = [
        "Downbeats are a four-beat phase estimate, not semantic meter detection.",
        "Integrated loudness is unavailable in the librosa analyzer and is measured during final mix.",
    ]
    if tempo_confidence < 0.5:
        warnings.append("Tempo confidence is low; planner should prefer onset or natural timing fallback.")
    if not drop_items:
        warnings.append("No probable strong musical accents met the configured drop threshold.")
    return {
        "schemaVersion": SCHEMA,
        "analyzer": {"name": NAME, "version": VERSION, "engine": "librosa"},
        "audio": {
            "fileName": path.name,
            "durationSeconds": metadata["duration"],
            "sampleRate": int(sample_rate),
            "channels": metadata["channels"],
            "tempoBpm": tempo_value or None,
            "tempoConfidence": tempo_confidence,
            "integratedLoudnessLufs": None,
        },
        "beats": [
            {
                "index": index + 1,
                "timeSeconds": float(time_value),
                "strength": beat_strengths[index],
                "confidence": tempo_confidence,
            }
            for index, time_value in enumerate(beat_times)
        ],
        "downbeats": [
            {
                "index": offset + 1,
                "timeSeconds": float(beat_times[index]),
                "strength": beat_strengths[index],
                "confidence": tempo_confidence * 0.8,
            }
            for offset, index in enumerate(downbeat_indexes)
        ],
        "onsets": onset_items,
        "sections": sections,
        "dropCandidates": drop_items,
        "warnings": warnings,
    }


def write_json(path: Path, document: dict[str, Any], pretty: bool) -> None:
    path = path.resolve()
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + ".tmp")
    try:
        with temporary.open("x", encoding="utf-8", newline="\n") as stream:
            json.dump(
                document, stream, ensure_ascii=False,
                indent=2 if pretty else None, separators=None if pretty else (",", ":"))
            stream.write("\n")
        os.replace(temporary, path)
    except OSError as error:
        temporary.unlink(missing_ok=True)
        raise CliFailure("OUTPUT_WRITE_FAILED", str(error), 30) from error


def parser() -> argparse.ArgumentParser:
    result = argparse.ArgumentParser(prog="music-analyzer")
    commands = result.add_subparsers(dest="command", required=True)
    for command in ("validate", "analyze"):
        child = commands.add_parser(command)
        child.add_argument("--input", required=True)
        if command == "analyze":
            child.add_argument("--output", required=True)
            child.add_argument("--pretty", action="store_true")
    commands.add_parser("version")
    return result


def main(arguments: list[str] | None = None) -> int:
    try:
        options = parser().parse_args(arguments)
        if options.command == "version":
            print(json.dumps({"name": NAME, "version": VERSION, "schemaVersion": SCHEMA}))
            return 0
        path, metadata = validate_input(options.input)
        if options.command == "validate":
            print(json.dumps({"valid": True, "audio": metadata}))
            return 0
        document = analyze(path, metadata)
        write_json(Path(options.output), document, options.pretty)
        print(json.dumps({"success": True, "output": str(Path(options.output).resolve())}))
        return 0
    except CliFailure as error:
        print(json.dumps({"error": {"code": error.code, "message": error.message}}), file=sys.stderr)
        return error.exit_code
    except SystemExit as error:
        return int(error.code) if isinstance(error.code, int) else 2
    except Exception as error:  # diagnostic traceback never becomes the user result
        diagnostic = Path(tempfile.gettempdir()) / "cs2-music-analyzer-unexpected.log"
        diagnostic.write_text(traceback.format_exc(), encoding="utf-8")
        print(json.dumps({
            "error": {
                "code": "UNEXPECTED_ERROR",
                "message": str(error),
                "diagnosticLog": str(diagnostic),
            }
        }), file=sys.stderr)
        return 99


if __name__ == "__main__":
    raise SystemExit(main())
