# -*- coding: utf-8 -*-
"""
Экспорт персонажа из Blender в Unity с правильными настройками FBX.

Запуск:
  blender.exe --background model.blend --python export_to_unity.py -- --out "<Assets/...>.fbx"

Опции после `--`:
  --out PATH        куда писать .fbx (обязательно)
  --collection NAME экспортировать только эту коллекцию (иначе — всю сцену)
  --anim            включить запекание анимации (по умолчанию выключено:
                    анимации берём с Mixamo отдельными файлами)
  --embed           зашить текстуры внутрь .fbx. Так их ждёт DexstandCharacterImport:
                    он зовёт ExtractTextures и сам раскладывает по папкам. Без
                    флага текстуры кладутся рядом в .fbm, и Unity их подхватит,
                    но вытащить уже не сможет

Почему именно такие настройки:
  apply_scale_options='FBX_SCALE_ALL' + global_scale=1  -> в Unity Scale Factor 1,
      модель приходит в метрах, ничего не надо домножать на 0.01
  axis_forward='-Z', axis_up='Y'                        -> оси Unity
  add_leaf_bones=False                                  -> Unity иначе видит лишние
      кости на концах цепочек и ломает маппинг Humanoid
  primary_bone_axis='Y', secondary_bone_axis='X'        -> ориентация костей,
      которую ждёт импортёр Unity
  path_mode='COPY', embed_textures=False                -> текстуры кладутся рядом,
      а не запекаются внутрь fbx (в Unity так удобнее их переназначать)
"""

import argparse
import os
import sys

import bpy


def parse_args():
    argv = sys.argv
    argv = argv[argv.index("--") + 1:] if "--" in argv else []
    p = argparse.ArgumentParser()
    p.add_argument("--out", required=True)
    p.add_argument("--collection", default=None)
    p.add_argument("--anim", action="store_true")
    p.add_argument("--embed", action="store_true")
    p.add_argument("--keep-materials", action="store_true",
                   help="не трогать нодовые деревья материалов перед экспортом")
    return p.parse_args(argv)


def _find_image_node(node, seen):
    """Идёт вверх по нодам, пока не найдёт картинку."""
    if node is None or node in seen:
        return None
    seen.add(node)
    if node.type == "TEX_IMAGE" and node.image:
        return node
    for socket in node.inputs:
        for link in socket.links:
            found = _find_image_node(link.from_node, seen)
            if found:
                return found
    return None


def flatten_materials():
    """
    Втыкает диффузную текстуру напрямую в Base Color и отцепляет её от Alpha.

    Экспортёр FBX узнаёт текстуру, только если она подключена в Base Color
    непосредственно. MPFB же гонит её через MIX_RGB ("diffuseIntensity"), и
    через эту прокладку экспортёр её не видит: файл картинки рядом с .fbx он
    кладёт, а ссылки в материале не пишет — в Unity материал приезжает без карты.

    Alpha отцепляется отдельно: MPFB подаёт туда тот же диффуз, и Unity,
    увидев альфу, делает одежду полупрозрачной.
    """
    flattened = 0
    unlinked_alpha = 0

    for mat in bpy.data.materials:
        if not mat.use_nodes or mat.node_tree is None:
            continue

        nodes = mat.node_tree.nodes
        out = next((n for n in nodes if n.type == "OUTPUT_MATERIAL" and n.is_active_output),
                   next((n for n in nodes if n.type == "OUTPUT_MATERIAL"), None))
        if out is None or not out.inputs["Surface"].is_linked:
            continue

        bsdf = out.inputs["Surface"].links[0].from_node
        if bsdf.type != "BSDF_PRINCIPLED":
            continue

        alpha = bsdf.inputs["Alpha"]
        if alpha.is_linked:
            for link in list(alpha.links):
                mat.node_tree.links.remove(link)
            alpha.default_value = 1.0
            unlinked_alpha += 1

        base = bsdf.inputs["Base Color"]
        if not base.is_linked:
            continue

        source = base.links[0].from_node
        if source.type == "TEX_IMAGE":
            continue                                    # уже напрямую

        image_node = _find_image_node(source, set())
        if image_node is None:
            continue

        mat.node_tree.links.new(image_node.outputs["Color"], base)
        flattened += 1
        print("[export_to_unity] %s: %s -> Base Color напрямую"
              % (mat.name, image_node.image.name))

    print("[export_to_unity] материалов распрямлено %d, альфа отцеплена у %d"
          % (flattened, unlinked_alpha))


def select_for_export(collection_name):
    """Выделяет то, что пойдёт в FBX. Возвращает число объектов."""
    bpy.ops.object.select_all(action="DESELECT")

    if collection_name:
        coll = bpy.data.collections.get(collection_name)
        if coll is None:
            raise SystemExit("нет коллекции '%s'" % collection_name)
        objects = list(coll.all_objects)
    else:
        objects = list(bpy.context.scene.objects)

    exportable = [o for o in objects if o.type in {"MESH", "ARMATURE", "EMPTY"}]
    for o in exportable:
        o.select_set(True)

    if exportable:
        bpy.context.view_layer.objects.active = exportable[0]
    return len(exportable)


def main():
    args = parse_args()

    out = os.path.abspath(args.out)
    os.makedirs(os.path.dirname(out), exist_ok=True)

    if not args.keep_materials:
        flatten_materials()

    count = select_for_export(args.collection)
    if count == 0:
        raise SystemExit("нечего экспортировать: не нашёл мешей/арматур")

    bpy.ops.export_scene.fbx(
        filepath=out,
        use_selection=True,
        object_types={"ARMATURE", "MESH", "EMPTY"},

        # масштаб и оси
        global_scale=1.0,
        apply_unit_scale=True,
        apply_scale_options="FBX_SCALE_ALL",
        axis_forward="-Z",
        axis_up="Y",

        # геометрия
        use_mesh_modifiers=True,
        mesh_smooth_type="FACE",
        use_tspace=True,           # тангенты для normal map
        use_triangles=False,       # Unity триангулирует сама

        # скелет
        add_leaf_bones=False,
        primary_bone_axis="Y",
        secondary_bone_axis="X",
        armature_nodetype="NULL",
        use_armature_deform_only=True,   # кости без веса в FBX не идут

        # анимация
        bake_anim=args.anim,
        bake_anim_use_all_bones=args.anim,
        bake_anim_use_nla_strips=False,
        bake_anim_use_all_actions=args.anim,
        bake_anim_force_startend_keying=True,

        # текстуры
        path_mode="COPY",
        embed_textures=args.embed,
    )

    size_kb = os.path.getsize(out) / 1024.0
    print("[export_to_unity] OK: %s (%d объектов, %.1f KB)" % (out, count, size_kb))


if __name__ == "__main__":
    main()
