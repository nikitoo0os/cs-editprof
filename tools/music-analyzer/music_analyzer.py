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
VERSION = "0.3.0"
SCHEMA = "2.1"
WAVEFORM_SAMPLES_PER_SECOND = 160
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


def build_waveform_envelope(
    samples: Any,
    sample_rate: int,
    samples_per_second: int = WAVEFORM_SAMPLES_PER_SECOND,
    numeric: Any | None = None,
) -> dict[str, Any]:
    """Build deterministic mono negative/positive peak magnitudes in 0..1."""
    values = (
        numeric.asarray(samples, dtype=float).reshape(-1)
        if numeric is not None
        else [float(value) for value in samples]
    )
    if sample_rate <= 0 or samples_per_second <= 0:
        raise ValueError("waveform sample rates must be positive")
    value_count = int(values.size) if numeric is not None else len(values)
    if value_count == 0:
        raise ValueError("decoded waveform is empty")
    maximum = (
        float(numeric.max(numeric.abs(values)))
        if numeric is not None
        else max((abs(value) for value in values), default=0.0)
    )
    samples_per_bucket = max(1, int(round(sample_rate / samples_per_second)))
    peaks: list[dict[str, float]] = []
    for start in range(0, value_count, samples_per_bucket):
        bucket = values[start:min(value_count, start + samples_per_bucket)]
        minimum = float(numeric.min(bucket) if numeric is not None else min(bucket))
        maximum_value = float(numeric.max(bucket) if numeric is not None else max(bucket))
        negative = abs(min(0.0, minimum)) / maximum if maximum > 0 else 0.0
        positive = max(0.0, maximum_value) / maximum if maximum > 0 else 0.0
        peaks.append({
            "timeSeconds": round(start / sample_rate, 6),
            "min": _round(negative),
            "max": _round(positive),
        })
    actual_rate = sample_rate / samples_per_bucket
    return {
        "schemaVersion": "1.0",
        "channelLayout": "mono",
        "normalization": "global-absolute-peak",
        "samplesPerSecond": round(actual_rate, 6),
        "sourceStartSeconds": 0.0,
        "sourceEndSeconds": round(value_count / sample_rate, 6),
        "peaks": peaks,
    }


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


def _round(value: float) -> float:
    return round(max(0.0, min(1.0, float(value))), 6)


def _rolling_mean(values: Any, radius: int, np: Any) -> Any:
    if len(values) == 0:
        return values
    width = max(1, radius * 2 + 1)
    kernel = np.ones(width, dtype=float) / width
    return np.convolve(values, kernel, mode="same")


def classify_section(
    energy: float,
    energy_slope: float,
    bass: float,
    onset_density: float,
    spectral_flux: float,
    novelty: float,
    downbeat_near: float,
) -> str:
    """Conservative multi-signal preclassification; C# stores final rationale."""
    drop = (
        0.20 * novelty
        + 0.20 * onset_density
        + 0.20 * max(0.0, min(1.0, energy_slope * 2))
        + 0.20 * bass
        + 0.20 * downbeat_near
    )
    build = (
        0.40 * max(0.0, min(1.0, energy_slope * 2))
        + 0.25 * onset_density
        + 0.20 * spectral_flux
        + 0.15 * novelty
    )
    high = 0.50 * energy + 0.25 * bass + 0.25 * onset_density
    if (
        drop >= 0.58
        and energy_slope >= 0.12
        and bass >= 0.55
        and onset_density >= 0.35
    ):
        return "Drop"
    if build >= 0.50 and energy_slope >= 0.08:
        return "BuildUp"
    if high >= 0.62 and energy >= 0.62:
        return "HighEnergy"
    if energy <= 0.35 and onset_density <= 0.30 and spectral_flux <= 0.35:
        return "Calm"
    if energy_slope <= -0.12 and energy <= 0.50:
        return "Breakdown"
    return "Verse" if energy < 0.55 else "Chorus"


def analyze(
    path: Path,
    metadata: dict[str, Any],
    hop_milliseconds: float = 40.0,
) -> dict[str, Any]:
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
        if hop_milliseconds < 20 or hop_milliseconds > 50:
            raise ValueError("hop interval must be between 20 and 50 ms")
        hop = max(256, int(round(sample_rate * hop_milliseconds / 1000.0)))
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
        mid_mask = (frequencies > 180) & (frequencies < 4000)
        high_mask = frequencies >= 4000
        mid = stft[mid_mask].mean(axis=0) if np.any(mid_mask) else np.zeros(stft.shape[1])
        high = stft[high_mask].mean(axis=0) if np.any(high_mask) else np.zeros(stft.shape[1])
        normalized_mid = np.asarray(normalize(mid))
        normalized_high = np.asarray(normalize(high))
        centroid = librosa.feature.spectral_centroid(
            S=stft, sr=sample_rate)[0] / max(1.0, sample_rate / 2)
        rolloff = librosa.feature.spectral_rolloff(
            S=stft, sr=sample_rate, roll_percent=0.85)[0] / max(1.0, sample_rate / 2)
        contrast = librosa.feature.spectral_contrast(
            S=stft, sr=sample_rate).mean(axis=0)
        normalized_contrast = np.asarray(normalize(contrast))
        spectral_delta = np.diff(stft, axis=1, prepend=stft[:, :1])
        flux = np.sqrt(np.square(np.maximum(spectral_delta, 0)).sum(axis=0))
        normalized_flux = np.asarray(normalize(flux))
        chroma = librosa.feature.chroma_stft(
            S=stft, sr=sample_rate, hop_length=hop)
        chroma_delta = np.abs(np.diff(chroma, axis=1, prepend=chroma[:, :1])).mean(axis=0)
        harmonic_change = np.asarray(normalize(chroma_delta))
        rhythmic_density = _rolling_mean(
            normalized_onsets,
            max(1, int(round(sample_rate / hop / 2))),
            np,
        )
        novelty = np.asarray(normalize(
            0.45 * normalized_flux
            + 0.35 * harmonic_change
            + 0.20 * normalized_onsets
        ))
        frame_count = min(
            len(normalized_onsets),
            len(normalized_energy),
            len(normalized_low),
            len(normalized_mid),
            len(normalized_high),
            len(centroid),
            len(rolloff),
            len(normalized_contrast),
            len(normalized_flux),
            len(harmonic_change),
            len(rhythmic_density),
            len(novelty),
        )
        if frame_count < 2:
            raise ValueError("Insufficient decoded frames for structure analysis.")
        normalized_onsets = normalized_onsets[:frame_count]
        normalized_energy = normalized_energy[:frame_count]
        normalized_low = normalized_low[:frame_count]
        normalized_mid = normalized_mid[:frame_count]
        normalized_high = normalized_high[:frame_count]
        centroid = centroid[:frame_count]
        rolloff = rolloff[:frame_count]
        normalized_contrast = normalized_contrast[:frame_count]
        normalized_flux = normalized_flux[:frame_count]
        harmonic_change = harmonic_change[:frame_count]
        rhythmic_density = rhythmic_density[:frame_count]
        novelty = novelty[:frame_count]
        section_count = max(1, min(12, int(metadata["duration"] // 20) + 1))
        boundaries = librosa.segment.agglomerative(chroma, section_count)
        boundary_frames = sorted({
            0,
            *[
                min(frame_count - 1, int(value))
                for value in boundaries
                if frame_count > 0
            ],
            frame_count - 1,
        })
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
        slice_end = max(start_frame + 1, end_frame)
        energy_slice = normalized_energy[start_frame:slice_end]
        energy = float(np.mean(energy_slice))
        bass = float(np.mean(normalized_low[start_frame:slice_end]))
        rhythm = float(np.mean(rhythmic_density[start_frame:slice_end]))
        brightness = float(np.mean(centroid[start_frame:slice_end]))
        section_flux = float(np.mean(normalized_flux[start_frame:slice_end]))
        section_novelty = float(np.mean(novelty[start_frame:slice_end]))
        section_onset = float(np.mean(normalized_onsets[start_frame:slice_end]))
        half = max(1, len(energy_slice) // 2)
        energy_slope = float(
            np.mean(energy_slice[half:]) - np.mean(energy_slice[:half])
        ) if len(energy_slice) > 1 else 0.0
        dynamic_contrast = float(np.max(energy_slice) - np.min(energy_slice))
        start_time = section_times[index]
        downbeat_near = _near(
            start_time,
            (float(beat_times[i]) for i in downbeat_indexes),
            0.20,
        )
        section_type = classify_section(
            energy,
            energy_slope,
            bass,
            section_onset,
            section_flux,
            section_novelty,
            downbeat_near,
        )
        confidence = max(
            0.35,
            min(
                0.95,
                0.45
                + 0.20 * dynamic_contrast
                + 0.15 * section_novelty
                + 0.15 * tempo_confidence,
            ),
        )
        sections.append({
            "index": index + 1,
            "id": f"section-{index + 1:03d}",
            "startSeconds": start_time,
            "endSeconds": (
                metadata["duration"]
                if index == len(boundary_frames) - 2
                else min(metadata["duration"], section_times[index + 1])
            ),
            "label": section_type,
            "type": section_type,
            "energy": _round(energy),
            "rhythmicDensity": _round(rhythm),
            "bassEnergy": _round(bass),
            "spectralBrightness": _round(brightness),
            "dynamicContrast": _round(dynamic_contrast),
            "confidence": _round(confidence),
            "anchors": [],
            "scoreBreakdown": {
                "energySlope": round(energy_slope, 6),
                "onsetDensity": _round(section_onset),
                "spectralFlux": _round(section_flux),
                "novelty": _round(section_novelty),
                "downbeatAtStart": _round(downbeat_near),
            },
        })

    frame_times = librosa.frames_to_time(
        np.arange(frame_count),
        sr=sample_rate,
        hop_length=hop,
    )
    frame_items = [
        {
            "timeSeconds": round(float(frame_times[index]), 6),
            "energy": _round(normalized_energy[index]),
            "bassEnergy": _round(normalized_low[index]),
            "onsetStrength": _round(normalized_onsets[index]),
            "spectralFlux": _round(normalized_flux[index]),
            "spectralBrightness": _round(
                0.65 * centroid[index] + 0.35 * normalized_high[index]
            ),
            "novelty": _round(novelty[index]),
            "rhythmicDensity": _round(rhythmic_density[index]),
            "harmonicChange": _round(harmonic_change[index]),
        }
        for index in range(frame_count)
        if float(frame_times[index]) <= metadata["duration"] + 0.001
    ]

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
    waveform = build_waveform_envelope(samples, sample_rate, numeric=np)
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
        "frameHopSeconds": round(hop / sample_rate, 6),
        "waveform": waveform,
        "frames": frame_items,
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
            child.add_argument(
                "--hop-ms",
                type=float,
                default=40.0,
                help="Frame feature hop interval in milliseconds (20-50).",
            )
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
        document = analyze(path, metadata, options.hop_ms)
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
