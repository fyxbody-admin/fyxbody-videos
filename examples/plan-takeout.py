"""Build an exact-ID Takeout manifest and preview CSV matches. No network access."""
import argparse
import csv
import json
import re
from collections import defaultdict
from pathlib import Path
from urllib.parse import parse_qs, urlsplit

ID = re.compile(r"[A-Za-z0-9_-]{11}")
MEDIA = {".mp4", ".mov", ".webm", ".mkv", ".m4v"}


def youtube_id(url):
    try:
        parsed = urlsplit(url.strip())
        if parsed.scheme != "https":
            return None
        if parsed.netloc.lower() == "youtu.be":
            value = parsed.path.removeprefix("/")
        elif parsed.netloc.lower() in {"www.youtube.com", "youtube.com", "m.youtube.com"}:
            if parsed.path == "/watch":
                values = parse_qs(parsed.query).get("v", [])
                value = values[0] if len(values) == 1 else ""
            elif parsed.path.startswith(("/shorts/", "/embed/")):
                value = parsed.path.split("/", 2)[2]
            else:
                return None
        else:
            return None
        return value if ID.fullmatch(value) else None
    except ValueError:
        return None


def sidecar_ids(path):
    ids = set()
    for candidate in (path.with_suffix(".json"), path.with_name(path.name + ".json")):
        if not candidate.is_file() or candidate.is_symlink():
            continue
        try:
            if candidate.stat().st_size > 1024 * 1024:
                continue
            data = json.loads(candidate.read_text(encoding="utf-8-sig"))
        except (ValueError, OSError):
            continue
        if not isinstance(data, dict):
            continue
        for key in ("videoId", "youtubeId"):
            value = data.get(key)
            if isinstance(value, str) and ID.fullmatch(value):
                ids.add(value)
        for key in ("url", "webpage_url", "videoUrl"):
            value = data.get(key)
            if isinstance(value, str) and (video_id := youtube_id(value)):
                ids.add(video_id)
    return ids


def inventory(root, expected):
    candidates = defaultdict(set)
    conflicts = set()
    for path in root.rglob("*"):
        if not path.is_file() or path.is_symlink() or path.suffix.lower() not in MEDIA:
            continue
        if path.resolve() != path or not path.is_relative_to(root):
            continue
        ids = sidecar_ids(path)
        filename_id = ID.fullmatch(path.stem.removeprefix("[").removesuffix("]") if path.stem.startswith("[") and path.stem.endswith("]") else path.stem)
        if filename_id and filename_id.group(0) in expected:
            ids.add(filename_id.group(0))
        if len(ids) > 1:
            conflicts.update(ids & expected)
        if len(ids) == 1:
            video_id = next(iter(ids))
            if video_id in expected:
                candidates[video_id].add(path.relative_to(root).as_posix())
    return candidates, conflicts


def heights(value):
    tokens = {part.strip().lower().removesuffix("p") for part in (value or "").split(";")}
    return tokens if tokens and tokens <= {"1080", "720", "480", "360"} else set()


def ready_index(path):
    if not path:
        return {}
    with path.open(encoding="utf-8-sig", newline="") as source:
        return {(row["Referencia"], row["YouTubeId"]): heights(row["ReadyHeights"])
                for row in csv.DictReader(source)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("csv", type=Path)
    parser.add_argument("takeout_directory", type=Path)
    parser.add_argument("report", type=Path, help="Output CSV with exact, unmatched or ambiguous status")
    parser.add_argument("--write-manifest", action="store_true", help="Create youtube-id-map.json in the Takeout root")
    parser.add_argument("--batch-directory", type=Path, help="Write matched rows as importable CSV batches")
    parser.add_argument("--existing", type=Path, help="Optional current ready rendition inventory CSV")
    args = parser.parse_args()
    root = args.takeout_directory.resolve(strict=True)
    if not root.is_dir():
        parser.error("Takeout root must be a directory")
    with args.csv.open(encoding="utf-8-sig", newline="") as source:
        rows = list(csv.DictReader(source))
    if not rows or not {"Referencia", "URL"} <= rows[0].keys():
        parser.error("Expected a CSV with Referencia and URL headers")
    expected = {video_id for row in rows if (video_id := youtube_id(row["URL"] or ""))}
    candidates, conflicts = inventory(root, expected)
    manifest = {video_id: next(iter(paths)) for video_id, paths in candidates.items() if len(paths) == 1 and video_id not in conflicts}
    ready = ready_index(args.existing)
    counts = defaultdict(int)
    lanes = defaultdict(list)
    occurrences = defaultdict(int)
    with args.report.open("x", encoding="utf-8", newline="") as output:
        writer = csv.writer(output)
        writer.writerow(["row", "Referencia", "YouTubeId", "status", "file"])
        for number, row in enumerate(rows, 2):
            video_id = youtube_id(row["URL"] or "")
            status = ("invalid-url" if not video_id else "matched" if video_id in manifest
                      else "ambiguous" if candidates.get(video_id) or video_id in conflicts else "unmatched")
            requested = heights(row.get("Calidades"))
            reference = row["Referencia"] or ""
            title = row.get("Titulo") or ""
            if requested and requested <= ready.get((reference, video_id), set()):
                status = "already-ready"
            elif status == "matched" and args.batch_directory and (
                not requested or len(title.encode("utf-16-le")) // 2 > 300
                or len(reference.encode("utf-16-le")) // 2 > 128
                or any(ord(char) < 32 or 127 <= ord(char) <= 159 for char in title + reference)
            ):
                status = "invalid-metadata"
            counts[status] += 1
            if status == "matched" and args.batch_directory:
                lane = occurrences[video_id]
                occurrences[video_id] += 1
                lanes[lane].append([row["Referencia"], row.get("Titulo", ""),
                                    f"https://www.youtube.com/watch?v={video_id}",
                                    row.get("Calidades", "")])
            writer.writerow([number, row["Referencia"], video_id or "", status, manifest.get(video_id, "")])
    if args.write_manifest:
        with (root / "youtube-id-map.json").open("x", encoding="utf-8") as output:
            json.dump(manifest, output, indent=2, ensure_ascii=False, sort_keys=True)
            output.write("\n")
    if args.batch_directory:
        args.batch_directory.mkdir()
        for lane, matches in sorted(lanes.items()):
            for offset in range(0, len(matches), 250):
                path = args.batch_directory / f"takeout-{lane:03d}-{offset // 250:03d}.csv"
                with path.open("x", encoding="utf-8", newline="") as output:
                    writer = csv.writer(output)
                    writer.writerow(["Referencia", "Titulo", "URL", "Calidades"])
                    writer.writerows(matches[offset:offset + 250])
    print(f"{len(rows)} rows, {len(manifest)} unique files, {dict(counts)}")


if __name__ == "__main__":
    main()
