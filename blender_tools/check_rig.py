# -*- coding: utf-8 -*-
"""
Проверяет, что каждая кость стоит там, где геометрия, которую она двигает.

Запуск:
  blender.exe --background --python check_rig.py -- --blend "<...dexguy.blend>"

Метод. У здоровой привязки кость лежит внутри своего куска меша: центр вершин,
привязанных к ней с весом больше половины, отстоит от отрезка кости не дальше,
чем на толщину этой части тела. Поэтому для каждой кости считается расстояние от
центра её вершин до ближайшей точки отрезка head-tail и сравнивается с длиной
самой кости. Отношение больше единицы — кость управляет мешем откуда-то со
стороны, и при анимации этот кусок поедет вокруг чужой точки.

Проверка нужна именно такая, численная: пока арматура в позе покоя, модификатор
ничего не делает, и разъехавшийся скелет выглядит совершенно нормально на любом
рендере и в T-позе в Unity.
"""

import argparse
import os
import sys

import bpy
import mathutils


def parse_args():
    argv = sys.argv
    argv = argv[argv.index("--") + 1:] if "--" in argv else []
    p = argparse.ArgumentParser()
    p.add_argument("--blend", required=True)
    p.add_argument("--mesh", default="DexGuy.body")
    p.add_argument("--weight", type=float, default=0.5)
    p.add_argument("--ratio", type=float, default=1.0,
                   help="во сколько длин кости считать промах")
    return p.parse_args(argv)


def distance_to_segment(point, a, b):
    ab = b - a
    length2 = ab.dot(ab)
    if length2 < 1e-12:
        return (point - a).length
    t = max(0.0, min(1.0, (point - a).dot(ab) / length2))
    return (point - (a + ab * t)).length


def main():
    args = parse_args()
    bpy.ops.wm.open_mainfile(filepath=os.path.abspath(args.blend))

    mesh_obj = bpy.data.objects[args.mesh]
    armature = next((m.object for m in mesh_obj.modifiers
                     if m.type == "ARMATURE" and m.object), None)
    if armature is None:
        raise SystemExit("у меша нет модификатора арматуры")

    print("[check] меш '%s', арматура '%s', костей %d"
          % (mesh_obj.name, armature.name, len(armature.data.bones)))

    # Поза сбрасывается до замера. Скачанные FBX часто везут внутри анимацию, и
    # на импорте арматура встаёт в её первый кадр; сравнивать деформированный
    # такой позой меш с положением костей покоя бессмысленно — расхождение
    # выйдет метровым на совершенно здоровом риге.
    ident = mathutils.Matrix.Identity(4)
    posed = [pb.name for pb in armature.pose.bones
             if max(abs(pb.matrix_basis[r][c] - ident[r][c])
                    for r in range(4) for c in range(4)) > 1e-5]
    for pb in armature.pose.bones:
        pb.matrix_basis = ident.copy()
    if armature.animation_data:
        armature.animation_data.action = None
    bpy.context.view_layer.update()

    print("[check] костей с ненулевой позой: %d%s"
          % (len(posed), " — сброшены в покой" if posed else ""))

    # Координаты берутся с вычисленного меша, а не из mesh.vertices.
    # MPFB лепит персонажа шейпкеями поверх базового меша MakeHuman, и в
    # data.vertices лежит именно базовая форма — не тот человек, под которого
    # подогнан скелет. Разница порядка метра, и по сырым координатам любая
    # кость выглядит промахнувшейся.
    #
    # Маски при этом выключаются: они выбрасывают вершины, и нумерация в
    # вычисленном меше перестаёт совпадать с весовыми группами оригинала.
    for mod in mesh_obj.modifiers:
        if mod.type == "MASK":
            mod.show_viewport = False

    deps = bpy.context.evaluated_depsgraph_get()
    evaluated = mesh_obj.evaluated_get(deps)
    ev_mesh = evaluated.to_mesh()
    if len(ev_mesh.vertices) != len(mesh_obj.data.vertices):
        raise SystemExit("вычисленный меш другой длины (%d против %d) — "
                         "остался модификатор, меняющий число вершин"
                         % (len(ev_mesh.vertices), len(mesh_obj.data.vertices)))
    coords = [v.co.copy() for v in ev_mesh.vertices]
    evaluated.to_mesh_clear()
    print("[check] координаты сняты с вычисленного меша, вершин %d" % len(coords))

    # Центр вершин каждой весовой группы, в пространстве арматуры.
    to_armature = armature.matrix_world.inverted() @ mesh_obj.matrix_world
    group_name = {g.index: g.name for g in mesh_obj.vertex_groups}
    centres = {}
    counts = {}
    for v in mesh_obj.data.vertices:
        point = to_armature @ coords[v.index]
        for g in v.groups:
            if g.weight < args.weight:
                continue
            name = group_name.get(g.group)
            if name is None:
                continue
            centres[name] = centres.get(name, mathutils.Vector((0.0, 0.0, 0.0))) + point
            counts[name] = counts.get(name, 0) + 1

    rows = []
    for name, total in centres.items():
        bone = armature.data.bones.get(name)
        if bone is None:
            continue
        centre = total / counts[name]
        gap = distance_to_segment(centre, bone.head_local, bone.tail_local)
        length = (bone.tail_local - bone.head_local).length
        rows.append((gap / max(length, 1e-6), gap, length, counts[name], name))

    # Сортировка по абсолютному промаху, а не по отношению: одинаковый сдвиг
    # всей арматуры выглядит катастрофой на коротких костях пальцев и мелочью
    # на длинных, и по отношению этого не увидеть.
    # Если скелет просто сдвинут относительно тела, вектор «центр вершин минус
    # голова кости» у всех костей будет один и тот же. Разброс вокруг среднего
    # и есть ответ: маленький — сдвиг, большой — скелет действительно кривой.
    offsets = []
    for name, total in centres.items():
        bone = armature.data.bones.get(name)
        if bone is None:
            continue
        offsets.append((total / counts[name]) - bone.head_local)

    if offsets:
        mean = mathutils.Vector((0.0, 0.0, 0.0))
        for o in offsets:
            mean += o
        mean /= len(offsets)
        spread = sum((o - mean).length for o in offsets) / len(offsets)
        print("[check] средний сдвиг тело-скелет: (%.3f %.3f %.3f), длина %.3f"
              % (mean.x, mean.y, mean.z, mean.length))
        print("[check] средний разброс вокруг него: %.3f" % spread)
        print("[check] вывод: %s"
              % ("скелет сдвинут целиком" if spread < mean.length * 0.35
                 else "не один сдвиг — кости разъехались по-разному"))

    rows.sort(key=lambda r: r[1], reverse=True)

    bad = [r for r in rows if r[0] > args.ratio]
    print("[check] костей с вершинами: %d, из них промахиваются: %d"
          % (len(rows), len(bad)))

    gaps = [r[1] for r in rows]
    print("[check] промах: минимум %.3f, максимум %.3f, разброс %.3f"
          % (min(gaps), max(gaps), max(gaps) - min(gaps)))

    print("[check] %-34s %7s %7s %7s %6s" % ("кость", "промах", "длина", "отнош.", "верш."))
    for ratio, gap, length, count, name in rows:
        flag = "  <-- мимо" if ratio > args.ratio else ""
        print("[check] %-34s %7.3f %7.3f %7.2f %6d%s"
              % (name, gap, length, ratio, count, flag))


if __name__ == "__main__":
    main()
