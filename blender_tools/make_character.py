# -*- coding: utf-8 -*-
"""
Генерит человека через MPFB (MakeHuman внутри Blender) и сохраняет .blend.

Запуск:
  blender.exe --background --python make_character.py -- --out "C:\\...\\dexguy.blend"

Опции после `--`:
  --out PATH     куда сохранить .blend (обязательно)
  --gender F     0 = женщина, 1 = мужчина
  --age F        0 = ребёнок, 0.5 = взрослый, 1 = старик
  --muscle F     0..1
  --weight F     0..1
  --height F     0..1  (0.5 — средний рост)
  --rig NAME     скелет, по умолчанию mixamo_unity
  --name NAME    имя объекта

Про скелет: MPFB несёт mixamo_unity — это скелет Mixamo кость в кость, с теми же
именами (mixamorig:Hips, mixamorig:Spine, ...). Значит риг ставится прямо здесь,
загонять модель на сайт Mixamo не нужно, а клипы оттуда всё равно лягут: пути
кривых адресуют ровно эти имена. Unity его же читает как Humanoid.

Остальное:
  subdiv_levels=0   лишние полигоны в игре ни к чему; сглаживание — дело шейдера
  scale=0.1         у MakeHuman родная единица — дециметр; 0.1 даёт метры,
                    то есть человек ~1.8 единиц, как ждёт Unity
  feet_on_ground    ступни в 0 по Z, иначе персонаж висит над полом
"""

import argparse
import os
import sys

import bpy

ADDON = "bl_ext.blender_org.mpfb"


def parse_args():
    argv = sys.argv
    argv = argv[argv.index("--") + 1:] if "--" in argv else []
    p = argparse.ArgumentParser()
    p.add_argument("--out", required=True)
    p.add_argument("--gender", type=float, default=0.9)
    p.add_argument("--age", type=float, default=0.5)
    p.add_argument("--muscle", type=float, default=0.65)
    p.add_argument("--weight", type=float, default=0.5)
    p.add_argument("--height", type=float, default=0.55)
    p.add_argument("--proportions", type=float, default=0.5)
    p.add_argument("--rig", default="mixamo_unity")
    p.add_argument("--clothes", default="",
                   help="имена .mhclo с расширением через запятую, например "
                        "'ushanka.mhclo,sneakers.mhclo'; список даёт --list-clothes")
    p.add_argument("--list-clothes", action="store_true",
                   help="показать, какая одежда вообще установлена, и выйти")
    p.add_argument("--name", default="DexGuy")
    p.add_argument("--feet-on-ground", dest="feet_on_ground",
                   action=argparse.BooleanOptionalAction, default=True,
                   help="ставить ступни в ноль по Z (по умолчанию да)")
    return p.parse_args(argv)


def clear_scene():
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)


def evaluated_world_z(obj):
    """
    Z вершин с учётом модификаторов и шейпкеев.

    Через obj.data.vertices смотреть нельзя: MPFB лепит телосложение шейпкеями
    поверх базового меша MakeHuman, и там лежит не тот человек, которого видно.
    """
    deps = bpy.context.evaluated_depsgraph_get()
    evaluated = obj.evaluated_get(deps)
    mesh = evaluated.to_mesh()
    zs = [(obj.matrix_world @ v.co).z for v in mesh.vertices]
    evaluated.to_mesh_clear()
    return zs


def drop_to_floor(basemesh, armature):
    """
    Ставит персонажа ступнями в ноль, двигая скелет вместе с телом.

    Штатный feet_on_ground у MPFB поднимает только меш, а арматуру оставляет
    на месте: скелет оказывается на 86 см ниже своего тела (измерено). Пока
    арматура в позе покоя, этого не видно совсем — модификатор в покое ничего
    не делает, и персонаж выглядит правильно и в Blender, и в T-позе в Unity.
    Дефект вылезает только в движении: каждая кость крутит свой кусок меша
    вокруг точки почти в метре от него.

    Подъём поэтому делается здесь и по объекту арматуры. Меши — её дети, они
    едут вместе с ней, и взаимное положение костей и вершин не меняется.
    """
    zs = evaluated_world_z(basemesh)
    armature.location.z -= min(zs)
    bpy.context.view_layer.update()


def main():
    args = parse_args()

    try:
        bpy.ops.preferences.addon_enable(module=ADDON)
    except Exception as exc:                       # уже включён — это не ошибка
        print("[make_character] addon_enable: %s" % exc)

    from bl_ext.blender_org.mpfb.services.humanservice import HumanService
    from bl_ext.blender_org.mpfb.services.assetservice import AssetService
    from bl_ext.blender_org.mpfb.services.locationservice import LocationService

    if args.list_clothes:
        found = AssetService.get_asset_list("clothes", "mhclo")
        print("[make_character] папка одежды: %s" % LocationService.get_user_data("clothes"))
        print("[make_character] установлено комплектов: %d" % len(found))
        # В --clothes вещь адресуется именем файла с расширением: MPFB-шный
        # find_asset_absolute_path понимает "ushanka.mhclo" и возвращает None на
        # ярлык "Ushanka". Поэтому печатаем то, что можно скопировать как есть.
        for label in sorted(found):
            print("[make_character]   %s   (ярлык: %s)" % (found[label]["basename"], label))
        return

    clear_scene()

    human_info = HumanService._create_default_human_info_dict()
    human_info["name"] = args.name
    human_info["rig"] = args.rig
    human_info["clothes"] = [c.strip() for c in args.clothes.split(",") if c.strip()]
    human_info["phenotype"].update({
        "gender": args.gender,
        "age": args.age,
        "muscle": args.muscle,
        "weight": args.weight,
        "height": args.height,
        "proportions": args.proportions,
    })

    settings = HumanService.get_default_deserialization_settings()
    settings.update({
        "subdiv_levels": 0,
        "load_clothes": bool(human_info["clothes"]),
        "detailed_helpers": False,
        "extra_vertex_groups": False,
        "mask_helpers": True,
        # Всегда False: постановку на пол делает drop_to_floor, потому что
        # штатная двигает меш и забывает про скелет.
        "feet_on_ground": False,
        "scale": 0.1,
        "override_rig": "PRESET",      # берём скелет из human_info, а не глушим
    })

    basemesh = HumanService.deserialize_from_dict(human_info, settings)
    bpy.context.view_layer.update()

    # Без скелета в Unity нечего анимировать, так что его наличие проверяется
    # отдельно — add_builtin_rig молча ничего не делает, если имя не найдено.
    armatures = [o for o in bpy.data.objects if o.type == "ARMATURE"]
    if not armatures:
        raise SystemExit("скелет '%s' не встал — проверь имя рига" % args.rig)

    arm = armatures[0]

    if args.feet_on_ground:
        drop_to_floor(basemesh, arm)

    # Габариты — единственная быстрая проверка, что человек вышел человеком,
    # а не схлопнутым или раздутым в сто раз мешем.
    zs = evaluated_world_z(basemesh)
    height = max(zs) - min(zs)
    print("[make_character] %s: %d вершин, рост %.3f м, ступни на z=%.3f"
          % (basemesh.name, len(basemesh.data.vertices), height, min(zs)))

    bones = list(arm.data.bones.keys())
    skinned = any(m.type == "ARMATURE" and m.object == arm for m in basemesh.modifiers)
    print("[make_character] скелет '%s': %d костей, привязан=%s" % (arm.name, len(bones), skinned))
    print("[make_character] корень/бёдра: %s"
          % ", ".join(b for b in bones if b.endswith(("Root", "Hips", "Spine", "Head"))))

    out = os.path.abspath(args.out)
    os.makedirs(os.path.dirname(out), exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=out)
    print("[make_character] сохранено: %s" % out)


if __name__ == "__main__":
    main()
