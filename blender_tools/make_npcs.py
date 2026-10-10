# -*- coding: utf-8 -*-
"""
Пешеходы для карты: несколько разных людей через MPFB (MakeHuman), каждый в свой .fbx для Unity.

Запуск:
  blender.exe --background --python make_npcs.py -- --out "C:\\dexstand\\Assets\\Models\\NPC"

Одежда, причёски, брови, ресницы и кожи — из makehuman_system_assets (CC0), распакованного в данные MPFB.
Скелет mixamo_unity, как у игрока (make_character.py): Unity читает его как Humanoid, и шагает человек
той же анимацией, что и мишени. Постановка на пол и выгрузка — функциями make_character / export_to_unity.
"""
import argparse
import os
import sys

import bpy

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import make_character as mc  # noqa: E402
import export_to_unity as eu  # noqa: E402

# name, gender, age, weight, height, skin, hair, clothes (top/suit + shoes)
PEOPLE = [
    ("npc_m1", 1.0, 0.45, 0.5, 0.55, "young_caucasian_male", "short02", ["male_casualsuit01", "shoes01"]),
    ("npc_m2", 1.0, 0.65, 0.6, 0.5, "middleage_caucasian_male", "short01", ["male_casualsuit03", "shoes02"]),
    ("npc_m3", 1.0, 0.45, 0.45, 0.6, "young_african_male", "short04", ["male_casualsuit04", "shoes03"]),
    ("npc_m4", 1.0, 0.9, 0.55, 0.45, "old_caucasian_male", "short03", ["male_elegantsuit01", "shoes04"]),
    ("npc_m5", 1.0, 0.6, 0.65, 0.5, "middleage_asian_male", "short02", ["male_worksuit01", "shoes05"]),
    ("npc_m6", 1.0, 0.4, 0.4, 0.58, "young_caucasian_male2", "short04", ["male_casualsuit06", "shoes06"]),
    ("npc_f1", 0.0, 0.45, 0.45, 0.5, "young_caucasian_female", "long01", ["female_casualsuit01", "shoes02"]),
    ("npc_f2", 0.0, 0.65, 0.55, 0.45, "middleage_caucasian_female", "bob01", ["female_casualsuit02", "shoes03"]),
    ("npc_f3", 0.0, 0.4, 0.4, 0.55, "young_african_female", "ponytail01", ["female_sportsuit01", "shoes01"]),
    ("npc_f4", 0.0, 0.9, 0.55, 0.4, "old_caucasian_female", "bob02", ["female_elegantsuit01", "shoes04"]),
    ("npc_f5", 0.0, 0.45, 0.45, 0.48, "young_asian_female", "braid01", ["female_casualsuit01", "shoes06"]),
]


def build(name, gender, age, weight, height, skin, hair, clothes):
    from bl_ext.blender_org.mpfb.services.humanservice import HumanService
    mc.clear_scene()
    info = HumanService._create_default_human_info_dict()
    info.update({"name": name, "rig": "mixamo_unity", "hair": hair + ".mhclo", "eyebrows": "eyebrow001.mhclo",
                 "eyelashes": "eyelashes01.mhclo", "eyes": "low-poly.mhclo",
                 "skin_mhmat": skin + ".mhmat", "skin_material_type": "GAMEENGINE",
                 "clothes": [c + ".mhclo" for c in clothes]})
    info["phenotype"].update({"gender": gender, "age": age, "muscle": 0.5, "weight": weight, "height": height,
                              "proportions": 0.5})
    settings = HumanService.get_default_deserialization_settings()
    settings.update({"subdiv_levels": 0, "load_clothes": True, "detailed_helpers": False, "extra_vertex_groups": False,
                     "mask_helpers": True, "feet_on_ground": False, "scale": 0.1, "override_rig": "PRESET"})
    basemesh = HumanService.deserialize_from_dict(info, settings)
    bpy.context.view_layer.update()
    arm = next(o for o in bpy.data.objects if o.type == "ARMATURE")
    mc.drop_to_floor(basemesh, arm)
    zs = mc.evaluated_world_z(basemesh)
    print("[npc] %s: height %.2f m, %d objects" % (name, max(zs) - min(zs), len(bpy.data.objects)))


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    p = argparse.ArgumentParser()
    p.add_argument("--out", required=True)
    p.add_argument("--only", default="")
    args = p.parse_args(argv)
    try:
        bpy.ops.preferences.addon_enable(module=mc.ADDON)
    except Exception as exc:
        print("[npc] addon_enable: %s" % exc)
    os.makedirs(args.out, exist_ok=True)
    for person in PEOPLE:
        if args.only and person[0] not in args.only.split(","):
            continue
        build(*person)
        eu.flatten_materials()
        path = os.path.join(args.out, person[0] + ".fbx")
        bpy.ops.export_scene.fbx(filepath=path, apply_scale_options="FBX_SCALE_ALL", global_scale=1.0,
                                 axis_forward="-Z", axis_up="Y", add_leaf_bones=False, primary_bone_axis="Y",
                                 # maps beside the .fbx, not inside: Unity binds loose ones by name, embedded ones
                                 # it leaves unassigned (the people came out white)
                                 secondary_bone_axis="X", path_mode="COPY", embed_textures=False, bake_anim=False,
                                 mesh_smooth_type="FACE", object_types={"ARMATURE", "MESH"})
        print("[npc] exported %s" % path)


if __name__ == "__main__":
    main()
