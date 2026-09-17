"""Move embedded RGBA8 ImageTextures out of a Godot text scene, without changing nodes.

Example (from the repository root):
    python tools/externalize_scene_images.py client/scenes/Intro.tscn
    python tools/externalize_scene_images.py client/scenes/NewTable.tscn --output client/assets/textures/ui/in_game/generated

Uses only the Python standard library. Run after a Figma reimport if it embeds
images again; Godot imports the emitted PNGs losslessly with unchanged pixels.
"""

import argparse
import hashlib
import re
import struct
import zlib
from pathlib import Path


def png_bytes(width: int, height: int, pixels: bytes) -> bytes:
    def chunk(kind: bytes, data: bytes) -> bytes:
        return (struct.pack(">I", len(data)) + kind + data
                + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF))

    stride = width * 4
    rows = b"".join(b"\0" + pixels[start:start + stride]
                    for start in range(0, len(pixels), stride))
    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(rows, 9)) + chunk(b"IEND", b""))


def import_settings(resource_path: str) -> str:
    digest = hashlib.md5(resource_path.encode()).hexdigest()
    imported = f"res://.godot/imported/{Path(resource_path).name}-{digest}.ctex"
    return f'''[remap]

importer="texture"
type="CompressedTexture2D"
path="{imported}"
metadata={{
"vram_texture": false
}}

[deps]

source_file="{resource_path}"
dest_files=["{imported}"]

[params]

compress/mode=0
mipmaps/generate=false
process/fix_alpha_border=false
process/premult_alpha=false
process/size_limit=0
detect_3d/compress_to=0
'''


def externalize(scene: Path, destination: Path | None = None) -> None:
    scene = scene.resolve()
    project = next((p for p in scene.parents if (p / "project.godot").is_file()), None)
    if project is None:
        raise ValueError("Scene must be inside a Godot project")
    destination = (destination.resolve() if destination is not None else
                   project / "assets/textures/ui" / scene.stem.lower() / "generated")
    if not destination.is_relative_to(project):
        raise ValueError("Output directory must be inside the Godot project")
    # Keep line endings and every node/property outside the replaced resources intact.
    original_bytes = scene.read_bytes()
    original = original_bytes.decode("utf-8")
    newline = "\r\n" if "\r\n" in original else "\n"
    text = original.replace("\r\n", "\n")
    blocks = re.split(r"(?=^\[)", text, flags=re.MULTILINE)
    images = {}
    textures = {}
    removed = set()
    assets = {}
    for index, block in enumerate(blocks):
        image = re.match(r'\[sub_resource type="Image" id="([^"]+)"\]', block)
        texture = re.match(r'\[sub_resource type="ImageTexture" id="([^"]+)"\]', block)
        if image:
            if '"format": "RGBA8"' not in block or '"mipmaps": false' not in block:
                raise ValueError(f"Unsupported image format or mipmaps: {image[1]}")
            width = int(re.search(r'"width": (\d+)', block)[1])
            height = int(re.search(r'"height": (\d+)', block)[1])
            array = re.search(r'"data": PackedByteArray\(([^)]*)\)', block)[1]
            pixels = bytes(map(int, array.split(",")))
            if len(pixels) != width * height * 4:
                raise ValueError(f"Unexpected pixel data size: {image[1]}")
            digest = hashlib.sha256(struct.pack(">II", width, height) + pixels).hexdigest()[:16]
            name = f"rgba-{width}x{height}-{digest}.png"
            images[image[1]] = name
            if name not in assets:
                assets[name] = png_bytes(width, height, pixels)
            removed.add(index)
        elif texture:
            source = re.fullmatch(r'image = SubResource\("([^"]+)"\)\s*', block.split("\n", 1)[1])
            if source is None:
                raise ValueError(f"ImageTexture has additional properties: {texture[1]}")
            textures[texture[1]] = source[1]
            removed.add(index)

    if not images:
        print("No embedded images; scene already uses external textures.")
        return
    if set(textures.values()) != set(images):
        raise ValueError("Images must be referenced only by embedded ImageTextures")

    remaining = "".join(block for index, block in enumerate(blocks) if index not in removed)
    for image_id in images:
        if f'SubResource("{image_id}")' in remaining:
            raise ValueError(f"Image is used directly, not just as a texture: {image_id}")
    resources = []
    for name in assets:
        resource_id = "png_" + name.removesuffix(".png").rsplit("-", 1)[1]
        if f'id="{resource_id}"' in remaining:
            raise ValueError(f"Resource ID already exists: {resource_id}")
        path = "res://" + (destination / name).relative_to(project).as_posix()
        resources.append(f'[ext_resource type="Texture2D" path="{path}" id="{resource_id}"]\n')
        for texture_id, image_id in textures.items():
            if images[image_id] == name:
                remaining = remaining.replace(f'SubResource("{texture_id}")', f'ExtResource("{resource_id}")')
    # ext_resource declarations precede all subresources and nodes.
    first_resource = re.search(r'^\[(?:sub_resource|node) ', remaining, re.MULTILINE).start()
    updated = remaining[:first_resource] + "".join(resources) + "\n" + remaining[first_resource:]
    if "load_steps=" in updated.split("\n", 1)[0]:
        count = len(re.findall(r'^\[(?:ext_resource|sub_resource) ', updated, re.MULTILINE)) + 1
        updated = re.sub(r'load_steps=\d+', f'load_steps={count}', updated, count=1)

    destination.mkdir(parents=True, exist_ok=True)
    for name, data in assets.items():
        asset = destination / name
        if asset.exists() and asset.read_bytes() != data:
            raise ValueError(f"Refusing to overwrite different asset: {asset}")
        if not asset.exists():
            asset.write_bytes(data)
        settings = asset.with_suffix(".png.import")
        if not settings.exists():
            resource_path = "res://" + asset.relative_to(project).as_posix()
            settings.write_text(import_settings(resource_path), encoding="utf-8", newline="\n")
    if scene.read_bytes() != original_bytes:
        raise RuntimeError("Scene changed during conversion; generated textures saved, scene left untouched")
    scene.write_bytes(updated.replace("\n", newline).encode("utf-8"))
    print(f"{len(images)} embedded images -> {len(assets)} unique lossless PNGs")
    print(f"Scene: {len(original_bytes):,} -> {scene.stat().st_size:,} bytes")
    print(f"PNGs: {sum(map(len, assets.values())):,} bytes")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("scene", type=Path)
    parser.add_argument("--output", type=Path, help="PNG directory inside the Godot project (relative to the working directory or absolute)")
    args = parser.parse_args()
    externalize(args.scene, args.output)
