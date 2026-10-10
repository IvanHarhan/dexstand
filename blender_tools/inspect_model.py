# -*- coding: utf-8 -*-
"""
Проверяет любую скачанную модель до того, как она поедет в Unity.

Запуск:
  blender.exe --background --python inspect_model.py -- --in "C:\\...\\model.fbx"

Понимает .fbx, .glb, .gltf, .obj — то есть всё, чем отдают Meshy, Tripo,
Mixamo и прочие.

Что проверяет и почему именно это:
  масштаб      генераторы отдают кто в метрах, кто в сантиметрах, кто вообще
               в безразмерных единицах; в Unity человек должен быть ~1.8
  скелет       без него анимации вешать не на что; если костей нет — дорога
               в Mixamo на авторигинг
  имена костей mixamorig / Hips / Spine — значит гуманоид, Unity смапит сам
  полигонаж    фото-генераторы легко выдают 200-500k треугольников, для
               игрового персонажа это надо резать
  текстуры     есть ли вообще картинки в материалах, или меш приедет серым
  n-гоны       топология из marching cubes мнётся на сгибах; доля треугольников
               и наличие n-гонов показывает, ремешили её или нет
"""

import argparse
import os
import sys

import bpy

HUMANOID_HINTS = ("hips", "pelvis", "spine", "neck", "head",
                  "upleg", "thigh", "shoulder", "forearm", "clavicle")


def parse_args():
    argv = sys.argv
    argv = argv[argv.index("--") + 1:] if "--" in argv else []
    p = argparse.ArgumentParser()
    p.add_argument("--in", dest="path", required=True)
    return p.parse_args(argv)


def clear_scene():
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)


def load(path):
    ext = os.path.splitext(path)[1].lower()
    if ext == ".fbx":
        bpy.ops.import_scene.fbx(filepath=path)
    elif ext in (".glb", ".gltf"):
        bpy.ops.import_scene.gltf(filepath=path)
    elif ext == ".obj":
        bpy.ops.wm.obj_import(filepath=path)
    else:
        raise SystemExit("не знаю формат %s" % ext)


def main():
    args = parse_args()
    path = os.path.abspath(args.path)
    if not os.path.isfile(path):
        raise SystemExit("нет файла %s" % path)

    clear_scene()
    load(path)

    meshes = [o for o in bpy.context.scene.objects if o.type == "MESH"]
    armatures = [o for o in bpy.context.scene.objects if o.type == "ARMATURE"]

    print("\n=== %s (%.1f MB) ===" % (os.path.basename(path), os.path.getsize(path) / 1048576.0))

    # --- размер ---
    lo = [1e9] * 3
    hi = [-1e9] * 3
    tris = 0
    ngons = 0
    quads = 0
    for obj in meshes:
        for corner in obj.bound_box:
            w = obj.matrix_world @ __import__("mathutils").Vector(corner)
            for i in range(3):
                lo[i] = min(lo[i], w[i])
                hi[i] = max(hi[i], w[i])
        for poly in obj.data.polygons:
            n = len(poly.vertices)
            if n == 3:
                tris += 1
            elif n == 4:
                quads += 1
            else:
                ngons += 1

    if meshes:
        size = [hi[i] - lo[i] for i in range(3)]
        tall = max(size)
        print("габариты   : %.3f x %.3f x %.3f (высота %.3f)" % (size[0], size[1], size[2], tall))
        if 1.4 <= tall <= 2.2:
            print("           -> метры, для Unity в самый раз")
        elif 140 <= tall <= 220:
            print("           -> САНТИМЕТРЫ: в Unity ставь Scale Factor 0.01")
        elif tall < 0.5:
            print("           -> подозрительно мелко, проверь единицы")
        else:
            print("           -> нестандартный масштаб, придётся подгонять руками")
        print("низ по Z   : %.3f %s" % (lo[2], "(ступни на нуле)" if abs(lo[2]) < 0.05 else "(НЕ на нуле — будет висеть или тонуть)"))

    # --- геометрия ---
    faces = tris + quads + ngons
    print("мешей      : %d, полигонов %d (тр %d / квадов %d / n-гонов %d)" % (len(meshes), faces, tris, quads, ngons))
    if faces:
        share = 100.0 * quads / faces
        print("квадов     : %.0f%% %s" % (share, "— сетка ремешена, деформация будет приличная"
                                          if share > 50 else "— треугольная каша, на сгибах будет мять"))
    if faces > 80000:
        print("           -> тяжело для игрового персонажа, стоит децимировать")

    # --- скелет ---
    if not armatures:
        print("скелет     : НЕТ — анимации вешать не на что, нужен авториг (Mixamo)")
    for arm in armatures:
        bones = list(arm.data.bones.keys())
        low = " ".join(b.lower() for b in bones)
        hits = [h for h in HUMANOID_HINTS if h in low]
        print("скелет     : '%s', %d костей" % (arm.name, len(bones)))
        print("гуманоид   : %s (совпало: %s)"
              % ("да, Unity смапит как Humanoid" if len(hits) >= 5 else "СОМНИТЕЛЬНО",
                 ", ".join(hits) if hits else "ничего"))
        print("пример имён: %s" % ", ".join(bones[:6]))
        skinned = [o.name for o in meshes
                   if any(m.type == "ARMATURE" and m.object == arm for m in o.modifiers)]
        print("привязано  : %s" % (", ".join(skinned) if skinned else "НИЧЕГО — меш не привязан к костям"))

    # --- материалы ---
    textured = 0
    total_mats = 0
    for obj in meshes:
        for slot in obj.material_slots:
            mat = slot.material
            if mat is None:
                continue
            total_mats += 1
            if mat.use_nodes and any(n.type == "TEX_IMAGE" and n.image for n in mat.node_tree.nodes):
                textured += 1
    print("материалов : %d, с текстурой %d %s"
          % (total_mats, textured, "" if textured else "— приедет серым, текстуры надо искать отдельно"))
    print()


if __name__ == "__main__":
    main()
