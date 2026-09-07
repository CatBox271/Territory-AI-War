# -*- coding: utf-8 -*-
"""
把目录里的图片亮度线性映射到 [0.8, 1.0]。

我的理解（复述）：
    “亮度转换为 0.8~1” 指按每张图自己的最暗/最亮像素做线性归一化，
    让这张图最暗部分的亮度变成 0.8（相当于 204/255），
    最亮部分的亮度变成 1.0（255/255），中间像素按比例过渡。

用法：
    python adjust_brightness.py                    # 保存到 brightness_0.8_1 子目录
    python adjust_brightness.py --in-place         # 直接覆盖原图（先备份到 _originals_backup）
    python adjust_brightness.py --target-min 0.8 --target-max 1.0
"""
from __future__ import annotations

import argparse
import shutil
from pathlib import Path

import numpy as np
from PIL import Image

IMAGE_EXTS = {".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".webp"}


def adjust_brightness(
    img: Image.Image,
    target_min: float = 0.8,
    target_max: float = 1.0,
) -> Image.Image:
    """把单张图片的亮度线性拉伸到 [target_min, target_max]。"""
    if target_max < target_min:
        raise ValueError("target_max 必须大于等于 target_min")

    # 转成带通道的数组，保留透明通道
    if img.mode in ("RGBA", "LA") or (img.mode == "P" and "transparency" in img.info):
        img = img.convert("RGBA")
    else:
        img = img.convert("RGB")

    arr = np.asarray(img).astype(np.float32) / 255.0
    has_alpha = arr.shape[2] == 4

    if has_alpha:
        alpha = arr[:, :, 3]
        visible = alpha > 1e-6
        if not visible.any():
            return img
    else:
        alpha = None
        visible = np.ones(arr.shape[:2], dtype=bool)

    rgb = arr[:, :, :3]

    # 亮度使用流行的 Rec.709 亮度公式（灰度图结果和像素值一致）
    lum = 0.2126 * rgb[..., 0] + 0.7152 * rgb[..., 1] + 0.0722 * rgb[..., 2]

    if visible.any():
        lum_visible = lum[visible]
        lum_min = float(lum_visible.min())
        lum_max = float(lum_visible.max())
    else:
        lum_min = 0.0
        lum_max = 0.0

    # 线性映射：最暗 -> target_min，最亮 -> target_max
    if lum_max - lum_min < 1e-8:
        target = np.full_like(lum, (target_min + target_max) / 2.0)
    else:
        target = target_min + (lum - lum_min) / (lum_max - lum_min) * (target_max - target_min)

    # 按 RGB 等比放大到目标亮度，尽量保持颜色/色相不变
    scale = np.zeros_like(lum)
    nonzero = lum > 1e-8
    scale[nonzero] = target[nonzero] / lum[nonzero]
    new_rgb = rgb * scale[..., None]

    # 纯黑像素无法用比例缩放，直接设为目标亮度对应的灰色
    black = np.logical_not(nonzero)
    if black.any():
        gray = np.repeat(target[black][:, None], 3, axis=1)
        new_rgb[black] = gray

    new_arr = np.clip(new_rgb, 0.0, 1.0)
    if has_alpha:
        new_arr = np.concatenate([new_arr, alpha[..., None]], axis=2)

    result = (new_arr * 255.0 + 0.5).astype(np.uint8)
    return Image.fromarray(result)


def main() -> None:
    parser = argparse.ArgumentParser(description="把图片亮度线性映射到目标区间")
    parser.add_argument("--folder", type=Path, default=Path(__file__).resolve().parent,
                        help="要处理的图片目录（默认是脚本所在目录）")
    parser.add_argument("--target-min", type=float, default=0.8,
                        help="最暗像素对应的亮度，默认 0.8")
    parser.add_argument("--target-max", type=float, default=1.0,
                        help="最亮像素对应的亮度，默认 1.0")
    parser.add_argument("--output", type=Path, default=Path("brightness_0.8_1"),
                        help="输出子目录名（配合 --in-place 时不生效）")
    parser.add_argument("--in-place", action="store_true",
                        help="直接覆盖原图；覆盖前自动备份到 _originals_backup")
    args = parser.parse_args()

    folder = args.folder.resolve()
    if not folder.is_dir():
        raise SystemExit(f"目录不存在: {folder}")

    files = sorted(
        p for p in folder.glob("*")
        if p.is_file() and p.suffix.lower() in IMAGE_EXTS
    )
    if not files:
        print(f"{folder} 中没有可处理的图片")
        return

    if args.in_place:
        backup_dir = folder / "_originals_backup"
        backup_dir.mkdir(exist_ok=True)
    else:
        output_dir = folder / args.output
        output_dir.mkdir(exist_ok=True)

    print(f"目标: 亮度映射到 [{args.target_min:.3f}, {args.target_max:.3f}]")
    print(f"找到 {len(files)} 张图片")

    for src in files:
        try:
            with Image.open(src) as im:
                im.load()
                out = adjust_brightness(im, args.target_min, args.target_max)

            if args.in_place:
                # 只备份一次，避免重复运行时把备份目录里的东西也当原图备份
                backup_file = backup_dir / src.name
                if not backup_file.exists():
                    shutil.copy2(src, backup_file)
                out.save(src)
                print(f"[覆盖] {src.name} (备份: {backup_file.name})")
            else:
                out.save(output_dir / src.name)
                print(f"[输出] {src.name} -> {output_dir / src.name}")
        except Exception as exc:  # noqa: BLE001
            print(f"[跳过] {src.name}: {exc}")

    if args.in_place:
        print(f"原图备份在: {backup_dir}")
    else:
        print(f"转换后图片在: {output_dir}")


if __name__ == "__main__":
    main()
