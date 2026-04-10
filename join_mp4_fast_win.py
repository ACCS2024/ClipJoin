import os
import re
import sys
import time
import shutil
import subprocess
from pathlib import Path

SRC_ROOT = Path(r"D:\04月08日")
OUT_ROOT = Path(r"D:\04月08日_{join}")

# 仅匹配 mp4（大小写不敏感）
MP4_RE = re.compile(r"\.mp4$", re.IGNORECASE)


def bytes_to_gb(n: int) -> float:
    return n / (1024 ** 3)


def safe_filename(s: str) -> str:
    # Windows 文件名非法字符替换
    return re.sub(r'[<>:"/\\|?*\x00-\x1F]', "_", s).strip().rstrip(".")


def find_mp4_files(folder: Path):
    files = []
    try:
        for p in folder.iterdir():
            if p.is_file() and MP4_RE.search(p.name):
                files.append(p)
    except Exception:
        raise
    # 默认按文件名排序（通常集数在文件名里，这样顺序更稳定）
    files.sort(key=lambda x: x.name.lower())
    return files


def write_concat_list(list_path: Path, mp4_files):
    # concat demuxer 要求：file 'path'
    # 这里用绝对路径，且统一使用 /，避免反斜杠转义问题
    with list_path.open("w", encoding="utf-8", newline="\n") as f:
        for p in mp4_files:
            ap = p.resolve().as_posix()
            f.write(f"file '{ap}'\n")


def ensure_ffmpeg():
    ffmpeg = shutil.which("ffmpeg")
    if not ffmpeg:
        raise RuntimeError("未找到 ffmpeg：请确保 ffmpeg.exe 已加入 PATH（cmd 里能运行 ffmpeg -version）")
    return ffmpeg


def join_one_folder(ffmpeg_path: str, subfolder: Path):
    folder_name = subfolder.name
    mp4s = find_mp4_files(subfolder)
    if not mp4s:
        return  # 没有 mp4 就跳过，不输出

    total_size = sum(p.stat().st_size for p in mp4s)
    episodes = len(mp4s)

    out_dir = OUT_ROOT / safe_filename(folder_name)
    out_dir.mkdir(parents=True, exist_ok=True)

    out_file = out_dir / (safe_filename(folder_name) + ".mp4")
    list_file = out_dir / "_concat_list.txt"
    log_file = out_dir / "_ffmpeg.log.txt"

    write_concat_list(list_file, mp4s)

    # 速度优先：-c copy 不转码；concat demuxer
    # -safe 0 允许绝对路径
    # -y 覆盖输出（压测时更方便）
    cmd = [
        ffmpeg_path,
        "-hide_banner",
        "-nostdin",
        "-f", "concat",
        "-safe", "0",
        "-i", str(list_file),
        "-c", "copy",
        "-movflags", "+faststart",
        "-y",
        str(out_file),
    ]

    t0 = time.perf_counter()
    with log_file.open("w", encoding="utf-8", errors="replace") as lf:
        p = subprocess.run(
            cmd,
            stdout=lf,
            stderr=lf,
            cwd=str(out_dir),
            creationflags=subprocess.CREATE_NO_WINDOW,
        )
    dt = time.perf_counter() - t0

    if p.returncode != 0:
        # 报错时才输出（并指出日志位置）
        print(f"[ERROR] {folder_name} 合并失败，returncode={p.returncode}，日志：{log_file}", file=sys.stderr)
        return

    # 成功：控制台只输出  名字 集数 总大小 合并用时
    # 总大小用 GB（更适合压测看吞吐）
    print(f"{folder_name}\t{episodes}\t{bytes_to_gb(total_size):.3f}GB\t{dt:.2f}s")


def main():
    ffmpeg_path = ensure_ffmpeg()

    if not SRC_ROOT.exists():
        raise RuntimeError(f"源目录不存在：{SRC_ROOT}")

    OUT_ROOT.mkdir(parents=True, exist_ok=True)

    # 只遍历一级子文件夹
    subfolders = [p for p in SRC_ROOT.iterdir() if p.is_dir()]
    subfolders.sort(key=lambda x: x.name.lower())

    for sf in subfolders:
        join_one_folder(ffmpeg_path, sf)


if __name__ == "__main__":
    try:
        main()
    except Exception as e:
        print(f"[FATAL] {e}", file=sys.stderr)
        sys.exit(1)