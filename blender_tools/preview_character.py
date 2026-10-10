# -*- coding: utf-8 -*-
"""
Рендерит персонажа с нескольких сторон — быстрая проверка, что вышло.

Запуск:
  blender.exe --background model.blend --python preview_character.py -- --out DIR

Рендер идёт через Workbench: материалов на персонаже может ещё не быть, а
силуэт и пропорции он показывает честнее и в сто раз быстрее, чем трассировка.
"""

import argparse
import math
import os
import sys

import bpy
import mathutils


def parse_args():
    argv = sys.argv
    argv = argv[argv.index("--") + 1:] if "--" in argv else []
    p = argparse.ArgumentParser()
    p.add_argument("--out", required=True)
    p.add_argument("--width", type=int, default=540)
    p.add_argument("--height", type=int, default=960)
    p.add_argument("--color", default="SINGLE", choices=["SINGLE", "TEXTURE", "MATERIAL"],
                   help="SINGLE — ровный серый, честный силуэт; TEXTURE — как одет")
    return p.parse_args(argv)


def visible_bounds():
    """
    Габариты по тому, что реально видно.

    Считается по bound_box, а не по вершинам через to_mesh(): временный меш
    надо освобождать, и depsgraph после этого роняет Blender на втором рендере
    — первый кадр сохранялся, дальше падало с кодом 255.
    """
    lo = mathutils.Vector((1e9, 1e9, 1e9))
    hi = mathutils.Vector((-1e9, -1e9, -1e9))

    found = False
    for obj in bpy.context.scene.objects:
        if obj.type != "MESH" or not obj.visible_get():
            continue
        for corner in obj.bound_box:
            w = obj.matrix_world @ mathutils.Vector(corner)
            lo = mathutils.Vector((min(lo.x, w.x), min(lo.y, w.y), min(lo.z, w.z)))
            hi = mathutils.Vector((max(hi.x, w.x), max(hi.y, w.y), max(hi.z, w.z)))
            found = True

    if not found:
        raise SystemExit("в сцене нет видимых мешей")
    return lo, hi


def main():
    args = parse_args()
    out = os.path.abspath(args.out)
    os.makedirs(out, exist_ok=True)

    scene = bpy.context.scene
    scene.render.engine = "BLENDER_WORKBENCH"
    scene.render.resolution_x = args.width
    scene.render.resolution_y = args.height
    scene.render.resolution_percentage = 100
    scene.render.film_transparent = False

    shading = scene.display.shading
    shading.light = "STUDIO"
    shading.color_type = args.color
    shading.single_color = (0.62, 0.60, 0.58)
    shading.show_shadows = True
    shading.show_cavity = True

    lo, hi = visible_bounds()
    centre = (lo + hi) / 2.0
    size = hi - lo
    print("[preview] рост %.3f м, ширина %.3f, глубина %.3f" % (size.z, size.x, size.y))

    cam_data = bpy.data.cameras.new("PreviewCam")
    cam_data.lens = 70.0
    cam = bpy.data.objects.new("PreviewCam", cam_data)
    scene.collection.objects.link(cam)
    scene.camera = cam

    # Отступ от наибольшего габарита. Для человека это рост, и кадр выходит
    # плотный; но руки от первого лица лежат поперёк, высоты у них почти нет, и
    # по ней камера уезжала вплотную к предплечью.
    distance = max(size.x, size.y, size.z) * 1.6

    for label, angle in (("front", 0.0), ("side", 90.0), ("back", 180.0)):
        rad = math.radians(angle)
        offset = mathutils.Vector((math.sin(rad), -math.cos(rad), 0.0)) * distance
        cam.location = centre + offset
        direction = centre - cam.location
        cam.rotation_euler = direction.to_track_quat("-Z", "Y").to_euler()

        path = os.path.join(out, "char_%s.png" % label)
        scene.render.filepath = path
        bpy.ops.render.render(write_still=True)
        print("[preview] %s -> %s" % (label, path))


if __name__ == "__main__":
    main()
