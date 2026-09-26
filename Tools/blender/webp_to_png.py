# Blender headless WebP -> PNG (lossless, alpha kept, no colour transform). Allowed "re-export" (docs/design/09 §3.1).
#   blender -b --factory-startup -P Tools/blender/webp_to_png.py -- <pairs.tsv> [--verify]
# pairs.tsv: one "<src>\t<dst>" per line. Prints "ok <dst> <w>x<h> maxdiff=<d>" / "fail <src> <error>".
import bpy, sys, os
args = sys.argv[sys.argv.index("--") + 1:]
verify = "--verify" in args
scene = bpy.context.scene
scene.view_settings.view_transform = "Standard"
scene.view_settings.look = "None"
scene.view_settings.exposure = 0.0
scene.view_settings.gamma = 1.0
scene.display_settings.display_device = "sRGB"
s = scene.render.image_settings
s.file_format = "PNG"; s.color_mode = "RGBA"; s.color_depth = "8"; s.compression = 90
with open(args[0], encoding="utf-8") as f:
    pairs = [line.rstrip("\n").split("\t") for line in f if line.strip()]
for src, dst in pairs:
    try:
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        img = bpy.data.images.load(src, check_existing=False)
        img.colorspace_settings.name = "sRGB"
        w, h = img.size
        img.save_render(dst, scene=scene)
        diff = -1.0
        if verify:
            out = bpy.data.images.load(dst, check_existing=False)
            a, b = list(img.pixels), list(out.pixels)
            diff = max(abs(x - y) for x, y in zip(a, b)) if len(a) == len(b) else 99.0
            bpy.data.images.remove(out)
        bpy.data.images.remove(img)
        print("ok", dst, f"{w}x{h}", f"maxdiff={diff:.4f}", flush=True)
    except Exception as e:
        print("fail", src, e, flush=True)
