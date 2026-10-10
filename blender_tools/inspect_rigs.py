# -*- coding: utf-8 -*-
"""
Сравнивает скелет рук Infima со скелетом DexGuy — что с чем стыковать.

Запуск:
  blender.exe --background --python inspect_rigs.py -- --fp "<...SK_FP_CH_Default_Cubic.fbx>" --blend "<...dexguy.blend>"

Нужно это затем, что руки от первого лица придётся пересаживать: у Infima
скелет с именами Unreal (hand_r, lowerarm_r), у DexGuy — mixamorig. Анимации
пака написаны под первый, геометрия нужна от второго. Прежде чем что-то
привязывать, надо знать обе позы покоя и обе длины костей — иначе руки выйдут
не того размера и не в том месте относительно камеры.
"""

import argparse
import os
import sys

import bpy
import mathutils


ARM_HINTS = ("clavicle", "upperarm", "lowerarm", "hand", "shoulder", "arm",
             "index", "middle", "ring", "pinky", "thumb")


def parse_args():
    argv = sys.argv
    argv = argv[argv.index("--") + 1:] if "--" in argv else []
    p = argparse.ArgumentParser()
    p.add_argument("--fp", required=True, help="FBX с руками Infima")
    p.add_argument("--blend", required=True, help=".blend с DexGuy")
    return p.parse_args(argv)


def describe(armature, label, side="r"):
    """Печатает длины и положения костей руки в метрах мировых координат."""
    print("\n===== %s: арматура '%s', %d костей ====="
          % (label, armature.name, len(armature.data.bones)))
    print("%s масштаб объекта: %s" % (label, tuple(round(v, 4) for v in armature.scale)))

    arm_bones = [b for b in armature.data.bones
                 if any(h in b.name.lower() for h in ARM_HINTS)]
    print("%s костей, похожих на руку: %d" % (label, len(arm_bones)))

    for bone in arm_bones:
        if side and not (bone.name.lower().endswith(side)
                         or ("right" in bone.name.lower())
                         or ("_%s_" % side) in bone.name.lower()):
            continue
        head = armature.matrix_world @ bone.head_local
        tail = armature.matrix_world @ bone.tail_local
        print("  %-34s len %.4f  head (%.3f %.3f %.3f)"
              % (bone.name, (tail - head).length, head.x, head.y, head.z))


def mesh_report(label):
    for obj in bpy.context.scene.objects:
        if obj.type != "MESH":
            continue
        lo = mathutils.Vector((1e9, 1e9, 1e9))
        hi = mathutils.Vector((-1e9, -1e9, -1e9))
        for corner in obj.bound_box:
            w = obj.matrix_world @ mathutils.Vector(corner)
            lo = mathutils.Vector((min(lo[i], w[i]) for i in range(3)))
            hi = mathutils.Vector((max(hi[i], w[i]) for i in range(3)))
        size = hi - lo
        uv = len(obj.data.uv_layers)
        print("  %-30s %6d полигонов, UV-слоёв %d, габарит %.3f x %.3f x %.3f"
              % (obj.name, len(obj.data.polygons), uv, size.x, size.y, size.z))


def main():
    args = parse_args()

    # --- Infima ---
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    bpy.ops.import_scene.fbx(filepath=os.path.abspath(args.fp))

    print("\n##### INFIMA #####")
    mesh_report("infima")
    for obj in bpy.context.scene.objects:
        if obj.type == "ARMATURE":
            describe(obj, "infima")

    # --- DexGuy ---
    bpy.ops.wm.open_mainfile(filepath=os.path.abspath(args.blend))

    print("\n##### DEXGUY #####")
    mesh_report("dexguy")
    for obj in bpy.context.scene.objects:
        if obj.type == "ARMATURE":
            describe(obj, "dexguy", side="rightarm")


if __name__ == "__main__":
    main()
