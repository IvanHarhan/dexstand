# -*- coding: utf-8 -*-
"""
Пересаживает руки DexGuy на скелет Infima, не трогая анимации.

Запуск:
  blender.exe --background --python transplant_arms.py --
      --fp "<...SK_FP_CH_Default_Cubic.fbx>"
      --blend "<...dexguy.blend>"
      --out "<...Assets/Models/Viewmodel/DexArms.fbx>"

Идея. Анимации пака — клипы под конкретный скелет: перезарядка, отдача, всё
адресует кости по именам вида hand_r. Значит скелет надо оставить ровно тот же,
а поменять только меш, который на нём висит. Тогда ни один клип не узнает о
подмене.

Как меш переезжает. Кости двух скелетов совпадают один в один (clavicle_r ~
mixamorig:RightShoulder и так далее вниз до третьих фаланг), поэтому каждая
вершина переносится обычным линейным блендингом: её координату переводят в
пространство «своей» кости DexGuy и оттуда выкладывают в пространство парной
кости Infima, с весами из исходной привязки. Это разом решает и другую позу
покоя, и другие пропорции — подгонять руками ничего не нужно.

Про масштаб костей. Кости Infima другой длины: плечо короче, пальцы длиннее —
руки viewmodel специально так сложены, чтобы читаться у самого объектива. Внутри
каждой кости поэтому стоит ещё и равномерный масштаб по отношению длин. Без него
геометрия сохранила бы пропорции DexGuy, но сгибалась бы не в тех местах:
сустав анимации приходился бы мимо костяшки на меше.
"""

import argparse
import math
import os
import sys

import bpy
import mathutils


# Кость DexGuy -> кость Infima. Скручивающие кости Infima
# (lowerarm_twist_01_r и подобные) намеренно не участвуют: веса для них
# в исходной привязке взяться неоткуда, а деформацию несут основные.
def build_bone_map():
    pairs = {}
    for side, s in (("Right", "r"), ("Left", "l")):
        pairs["mixamorig:%sShoulder" % side] = "clavicle_%s" % s
        pairs["mixamorig:%sArm" % side] = "upperarm_%s" % s
        pairs["mixamorig:%sForeArm" % side] = "lowerarm_%s" % s
        pairs["mixamorig:%sHand" % side] = "hand_%s" % s
        for finger, name in (("Index", "index"), ("Middle", "middle"),
                             ("Ring", "ring"), ("Pinky", "pinky"),
                             ("Thumb", "thumb")):
            for j in (1, 2, 3):
                pairs["mixamorig:%sHand%s%d" % (side, finger, j)] = \
                    "%s_%02d_%s" % (name, j, s)
    return pairs


# По этим костям решается, что вообще считать рукой. Ключица не в списке
# намеренно: она тянет за собой плечо и кусок груди, а в кадре нужны руки,
# а не половина торса.
def limb_bones(bone_map):
    return {d for d in bone_map
            if "Shoulder" not in d}


def parse_args():
    argv = sys.argv
    argv = argv[argv.index("--") + 1:] if "--" in argv else []
    p = argparse.ArgumentParser()
    p.add_argument("--fp", required=True)
    p.add_argument("--blend", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--body", default="DexGuy.body")
    p.add_argument("--keep-threshold", type=float, default=0.5,
                   help="доля веса на костях руки, ниже которой вершина не рука")
    p.add_argument("--no-retarget", action="store_true",
                   help="вырезать руки и остановиться: проверка, цела ли геометрия "
                        "до переноса")
    return p.parse_args(argv)


def clear_scene():
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)


def find_armature(objects):
    for obj in objects:
        if obj.type == "ARMATURE":
            return obj
    return None


def append_from_blend(path, names):
    """Тащит named-объекты из другого .blend вместе с их данными."""
    with bpy.data.libraries.load(os.path.abspath(path), link=False) as (src, dst):
        dst.objects = [n for n in src.objects if n in names]
    brought = []
    for obj in dst.objects:
        if obj is not None:
            bpy.context.scene.collection.objects.link(obj)
            brought.append(obj)
    return brought


def bone_spaces(armature):
    """Матрица покоя и длина каждой кости в пространстве арматуры."""
    out = {}
    for bone in armature.data.bones:
        out[bone.name] = (bone.matrix_local.copy(),
                          (bone.tail_local - bone.head_local).length)
    return out


def strip_to_arms(mesh_obj, keep_groups, bone_groups, threshold):
    """
    Удаляет всё, что не держится на костях руки.

    Доля считается только по костям. У базового меша MakeHuman есть служебные
    весовые группы — 'body' с весом 1.0, по которой работает маска, и другие
    вроде неё. Если брать их в знаменатель, доля руки у любой вершины падает
    вдвое, порог перестаёт отсекать, и в руки попадают вершины с ничтожным
    весом на костях. Такая вершина после нормировки уезжает целиком за одной
    случайной костью — меш превращается в иглы.
    """
    indices = {g.index for g in mesh_obj.vertex_groups if g.name in keep_groups}
    bones = {g.index for g in mesh_obj.vertex_groups if g.name in bone_groups}
    if not indices:
        raise SystemExit("не нашёл ни одной весовой группы руки — проверь имена костей")

    doomed = []
    for v in mesh_obj.data.vertices:
        share = sum(g.weight for g in v.groups if g.group in indices)
        total = sum(g.weight for g in v.groups if g.group in bones)
        if total < 1e-6 or share / total < threshold:
            doomed.append(v.index)

    mesh = mesh_obj.data
    import bmesh
    bm = bmesh.new()
    bm.from_mesh(mesh)
    bm.verts.ensure_lookup_table()
    bmesh.ops.delete(bm, geom=[bm.verts[i] for i in doomed], context="VERTS")
    bm.to_mesh(mesh)
    bm.free()
    mesh.update()
    print("[arms] срезано вершин: %d, осталось %d" % (len(doomed), len(mesh.vertices)))


def stable_frame(head, tail, reference=mathutils.Vector((0.0, 0.0, 1.0))):
    """
    Система координат кости, построенная без её собственного roll.

    Родные матрицы костей для переноса не годятся: у mixamorig и у скелета
    Unreal кости по-разному повёрнуты вокруг собственной оси. Перенос через
    них доворачивал каждый кусок геометрии на свой угол, и меш расходился по
    швам на каждом суставе — ровно те лоскуты, что были видны на рендере.

    Здесь ось Y идёт вдоль кости, а две поперечные достраиваются от общего
    опорного вектора. Правило одно для обоих скелетов, поэтому произвола в
    развороте не остаётся и шов сходится.
    """
    along = tail - head
    length = along.length
    if length < 1e-9:
        return mathutils.Matrix.Translation(head), 1e-9
    along = along / length

    # Опора меняется, если кость смотрит почти вдоль неё: иначе векторное
    # произведение вырождается и поперечные оси становятся случайными.
    ref = reference
    if abs(along.dot(ref)) > 0.99:
        ref = mathutils.Vector((0.0, 1.0, 0.0))

    across = ref.cross(along).normalized()
    third = along.cross(across)

    frame = mathutils.Matrix((
        (across.x, along.x, third.x, head.x),
        (across.y, along.y, third.y, head.y),
        (across.z, along.z, third.z, head.z),
        (0.0, 0.0, 0.0, 1.0),
    ))
    return frame, length


def arm_span(armature, bone_map, target=False):
    """Расстояние от плеча до кисти — мерка, по которой равняются две руки."""
    want = ("clavicle_r", "hand_r") if target else ("mixamorig:RightShoulder",
                                                    "mixamorig:RightHand")
    heads = []
    for name in want:
        bone = armature.data.bones.get(name)
        if bone is None:
            return 1.0
        heads.append(armature.matrix_world @ bone.head_local)
    return (heads[1] - heads[0]).length


def clip_above(mesh_obj, limit):
    """
    Срезает всё, что выше исходных рук пака.

    Наш срез по весам захватывает дельтовидную мышцу, и плечо оказывается выше
    того места, где кончались руки Infima. В кадре от первого лица камера сидит
    как раз на этой высоте, и лишняя геометрия закрывает пол-экрана сплошной
    плоскостью. Граница берётся от оригинального меша: он-то в объектив не лез.
    """
    import bmesh

    doomed = [v.index for v in mesh_obj.data.vertices if v.co.z > limit]
    if not doomed:
        return

    bm = bmesh.new()
    bm.from_mesh(mesh_obj.data)
    bm.verts.ensure_lookup_table()
    bmesh.ops.delete(bm, geom=[bm.verts[i] for i in doomed], context="VERTS")
    bm.to_mesh(mesh_obj.data)
    bm.free()
    mesh_obj.data.update()
    print("[arms] срезано выше z=%.3f: %d вершин, осталось %d"
          % (limit, len(doomed), len(mesh_obj.data.vertices)))


def chain_parent(dex_arm, name, bone_map):
    """Ближайший предок кости, который тоже участвует в переносе."""
    bone = dex_arm.data.bones.get(name)
    bone = bone.parent if bone else None
    while bone is not None:
        if bone.name in bone_map:
            return bone.name
        bone = bone.parent
    return None


def chained_rotations(dex_arm, inf_arm, bone_map):
    """
    Складывает руки DexGuy в позу рук Infima — как это делает анимация.

    Ребёнок наследует весь разворот родителя и добавляет только минимальную
    поправку до собственного направления. Соседние кости поэтому отличаются
    ровно на угол в суставе, и меш между ними не расходится.

    Так пришлось делать после того, как не сработал прямой перенос, где каждая
    кость сама укладывала свой кусок геометрии в парную кость Infima. Довороты
    между двумя позами лежат в диапазоне 55-125°, соседние кости расходились
    градусов на семьдесят, и вершину на стыке разрывало. Длины костей при этом
    остаются наши: масштабировать каждую по своей паре тем более нельзя, у
    кисти отношение 3.4, у плеча 0.64, и меш рвало уже на этом.
    """
    order, seen = [], set()

    def visit(name):
        if name in seen or dex_arm.data.bones.get(name) is None:
            return
        parent = chain_parent(dex_arm, name, bone_map)
        if parent is not None:
            visit(parent)
        seen.add(name)
        order.append(name)

    for name in bone_map:
        visit(name)

    # Первый потомок каждой кости, который тоже участвует в переносе. По нему
    # меряется отрезок, который кость реально одевает геометрией: у mixamorig
    # кость кисти длиной 3 см при ладони в 11 см, и масштабировать по длинам
    # самих костей значило бы раздуть ладонь втрое.
    child_of = {}
    for name in order:
        parent = chain_parent(dex_arm, name, bone_map)
        if parent is not None and parent not in child_of:
            child_of[parent] = name

    def span(armature, names, bone_getter):
        head, child = names
        start = armature.matrix_world @ bone_getter(head).head_local
        if child is not None:
            return (armature.matrix_world @ bone_getter(child).head_local) - start
        return (armature.matrix_world @ bone_getter(head).tail_local) - start

    # Масштаб один на все кости. Своё отношение у каждой кости сажает её точнее,
    # но соседние кости тогда растягивают общую вершину по-разному, и меш рвёт:
    # на пробе таких рёбер было 1256 против 73 при едином множителе. Берётся
    # средним по отношениям — так он не зависит от одной случайной кости.
    ratios = []
    for name in order:
        inf_name = bone_map.get(name)
        if inf_name not in inf_arm.data.bones:
            continue
        child = child_of.get(name)
        a = span(dex_arm, (name, child), lambda n: dex_arm.data.bones[n]).length
        b = span(inf_arm, (inf_name, bone_map[child] if child else None),
                 lambda n: inf_arm.data.bones[n]).length
        if a > 1e-6:
            ratios.append(b / a)
    ratio = sum(ratios) / len(ratios) if ratios else 1.0
    scale = mathutils.Matrix.Scale(ratio, 4)
    print("[arms] общий масштаб %.3f (по %d костям, разброс %.2f-%.2f)"
          % (ratio, len(ratios), min(ratios), max(ratios)))

    rotations, heads, transforms = {}, {}, {}
    for name in order:
        inf_name = bone_map[name]
        if inf_name not in inf_arm.data.bones:
            continue

        dex_bone = dex_arm.data.bones[name]
        rest_head = dex_arm.matrix_world @ dex_bone.head_local
        rest_dir = (dex_arm.matrix_world @ dex_bone.tail_local) - rest_head
        want = ((inf_arm.matrix_world @ inf_arm.data.bones[inf_name].tail_local)
                - (inf_arm.matrix_world @ inf_arm.data.bones[inf_name].head_local))
        if rest_dir.length < 1e-9 or want.length < 1e-9:
            continue

        parent = chain_parent(dex_arm, name, bone_map)
        inherited = rotations.get(parent, mathutils.Quaternion())
        carried = inherited @ rest_dir.normalized()
        rotations[name] = carried.rotation_difference(want.normalized()) @ inherited

        # Кости руки садятся прямо на свои места в скелете Infima, фаланги —
        # ведутся от кисти по цепочке.
        #
        # Привязать так каждую кость нельзя: соседние сегменты перестают
        # сходиться концами, и меш рвёт на каждом суставе — 1269 рёбер против
        # 73. Вести же всё цепочкой тоже нельзя: она набирает положение от
        # пропорций DexGuy, и к пальцам накапливалась треть метра. Развилка
        # проходит по кисти: выше неё четыре кости ставятся точно, ниже —
        # три фаланги подряд, где накопить нечего.
        if inf_name.split("_")[0] in ("clavicle", "upperarm", "lowerarm", "hand"):
            heads[name] = inf_arm.matrix_world @ inf_arm.data.bones[inf_name].head_local
        else:
            parent_head = dex_arm.matrix_world @ dex_arm.data.bones[parent].head_local
            heads[name] = heads[parent] + rotations[parent] @ (
                (rest_head - parent_head) * ratio)

        transforms[name] = (mathutils.Matrix.Translation(heads[name])
                            @ rotations[name].to_matrix().to_4x4()
                            @ scale
                            @ mathutils.Matrix.Translation(-rest_head))

    print("[arms] костей в цепочке: %d" % len(transforms))
    return transforms, heads


def fit_similarity(pairs):
    """
    Общий поворот, масштаб и сдвиг, лучше всего сажающий суставы на кости Infima.

    Метод Кабша по всем суставам сразу. Преобразование одно на весь меш, так что
    добавить разрывов оно не может — в отличие от любого попыточного масштаба
    по отдельным костям.
    """
    import numpy as np

    src = np.array([[p.x, p.y, p.z] for p, _ in pairs])
    dst = np.array([[q.x, q.y, q.z] for _, q in pairs])
    src_centre, dst_centre = src.mean(axis=0), dst.mean(axis=0)
    a, b = src - src_centre, dst - dst_centre

    u, sigma, vt = np.linalg.svd(a.T @ b)
    flip = np.sign(np.linalg.det(vt.T @ u.T))
    rot = vt.T @ np.diag([1.0, 1.0, flip]) @ u.T
    scale = float((sigma * np.array([1.0, 1.0, flip])).sum() / (a ** 2).sum())

    shift = dst_centre - scale * (rot @ src_centre)
    place = mathutils.Matrix.Identity(4)
    for i in range(3):
        for j in range(3):
            place[i][j] = float(rot[i][j]) * scale
        place[i][3] = float(shift[i])

    moved = (scale * (rot @ src.T).T) + shift
    residual = float(np.linalg.norm(moved - dst, axis=1).mean())
    return place, scale, residual


def retarget_vertices(mesh_obj, dex_arm, inf_arm, bone_map):
    """Переносит руки DexGuy на скелет Infima."""
    group_name = {g.index: g.name for g in mesh_obj.vertex_groups}

    transforms, heads = chained_rotations(dex_arm, inf_arm, bone_map)
    if not transforms:
        raise SystemExit("ни одной пары костей не нашлось")

    before = [v.co.copy() for v in mesh_obj.data.vertices]

    moved = 0
    orphans = 0
    coords = []
    sides = []
    for v in mesh_obj.data.vertices:
        source = mesh_obj.matrix_world @ v.co

        total = 0.0
        heaviest = 0.0
        side = None
        acc = mathutils.Vector((0.0, 0.0, 0.0))
        for g in v.groups:
            name = group_name.get(g.group)
            matrix = transforms.get(name)
            if matrix is None or g.weight <= 0.0:
                continue
            acc += (matrix @ source) * g.weight
            total += g.weight
            if g.weight > heaviest:
                heaviest = g.weight
                side = "Right" if "Right" in name else "Left"

        sides.append(side)
        if total <= 1e-9:
            orphans += 1
            coords.append(source)
            continue
        coords.append(acc / total)
        moved += 1

    # Повороты поставили руки как надо, но цепочка всё ещё начинается у плеча
    # DexGuy и живёт его длинами костей. Посадка на кости Infima идёт отдельно
    # для каждой руки: скелет viewmodel несимметричен — правая держит оружие —
    # и одна общая посадка промахивалась левой кистью на 12 см. Внутри руки
    # преобразование одно на все её вершины, так что рвать ему нечего.
    # Посадка больше не нужна: каждая кость встаёт сразу на своё место в
    # скелете Infima, подгонять цепочку целиком нечего.
    places = {}
    for side in ():
        # Только ключица, плечо, предплечье и кисть. Фаланг вчетверо больше, и
        # посадка по всем суставам сразу подгоняется под кучку пальцев, уводя
        # запястье: кисть уезжала от своей кости на 19 см.
        joints = [(heads[name],
                   inf_arm.matrix_world @ inf_arm.data.bones[bone_map[name]].head_local)
                  for name in heads
                  if side in name and bone_map[name] in inf_arm.data.bones
                  and bone_map[name].split("_")[0] in ("clavicle", "upperarm",
                                                       "lowerarm", "hand")]
        if len(joints) < 3:
            continue
        places[side], scale, residual = fit_similarity(joints)
        print("[arms] посадка %s: масштаб %.3f, невязка по суставам %.3f м"
              % (side, scale, residual))

    # Геометрия пишется в мировых координатах, а объект ставится в ноль:
    # дальше меш идёт ребёнком арматуры, и лишний трансформ на нём приехал бы
    # в Unity смещением, которого никто не ждёт.
    #
    # Родитель снимается до обнуления. Меш всё ещё висел на арматуре DexGuy, у
    # которой свой сдвиг от постановки ступней на пол; обнуление мирового
    # трансформа прятало компенсацию этого сдвига в локальную матрицу, и она
    # переезжала вместе с объектом к новому родителю — руки оказывались на
    # 77 см ниже своих костей.
    mesh_obj.parent = None
    mesh_obj.matrix_world = mathutils.Matrix.Identity(4)
    for v, point, side in zip(mesh_obj.data.vertices, coords, sides):
        place = places.get(side)
        v.co = (place @ point) if place else point
    mesh_obj.data.update()

    print("[arms] перенесено вершин: %d, без костей руки осталось %d" % (moved, orphans))
    report_stretch(mesh_obj, before, group_name)


def report_stretch(mesh_obj, before, group_name):
    """
    Ищет рёбра, которые перенос растянул сильнее всего.

    Рваный меш всегда виден по рёбрам: соседние вершины уехали в разные
    стороны. Имена костей у концов такого ребра сразу показывают, между какими
    костями расходится шов, — гадать по картинке бессмысленно.
    """
    def dominant(vertex):
        best, name = 0.0, "—"
        for g in vertex.groups:
            if g.weight > best:
                best, name = g.weight, group_name.get(g.group, "?")
        return name

    worst = []
    for edge in mesh_obj.data.edges:
        a, b = edge.vertices
        was = (before[a] - before[b]).length
        now = (mesh_obj.data.vertices[a].co - mesh_obj.data.vertices[b].co).length
        if was < 1e-6:
            continue
        worst.append((now / was, now, a, b))

    worst.sort(reverse=True)
    stretched = sum(1 for r, _, _, _ in worst if r > 3.0)
    print("[arms] рёбер всего %d, растянуто больше чем втрое: %d"
          % (len(worst), stretched))

    for ratio, now, a, b in worst[:6]:
        print("[arms]   x%.1f (%.3f м) между '%s' и '%s'"
              % (ratio, now, dominant(mesh_obj.data.vertices[a]),
                 dominant(mesh_obj.data.vertices[b])))


def ordered_pairs(dex_arm, bone_map):
    """Пары кость-кость, родители раньше детей."""
    depth = {}

    def level(bone):
        if bone.name not in depth:
            depth[bone.name] = 0 if bone.parent is None else level(bone.parent) + 1
        return depth[bone.name]

    pairs = []
    for bone in dex_arm.data.bones:
        if bone.name in bone_map:
            pairs.append((level(bone), bone.name, bone_map[bone.name]))
    pairs.sort()
    return [(d, i) for _, d, i in pairs]


def pose_onto(dex_arm, inf_arm, bone_map):
    """
    Складывает скелет DexGuy в позу скелета Infima, кость за костью.

    Раньше здесь стоял прямой перенос вершин через локальные пространства
    костей, и он рвал меш в лоскуты. Причина — roll: у mixamorig и у скелета
    Unreal кости по-разному повёрнуты вокруг собственной оси, и каждая кость
    доворачивала свой кусок геометрии на свой угол. Соседние вершины с разными
    доминирующими костями разъезжались, и шов расходился на каждом суставе.

    Поза этой болезнью не страдает. Костям задаётся только направление,
    а разворот вокруг оси приходит по наследству от родителя — цепочка
    остаётся цельной, как ей и положено.
    """
    bpy.context.view_layer.objects.active = dex_arm
    bpy.ops.object.mode_set(mode="POSE")

    turned = 0
    for dex_name, inf_name in ordered_pairs(dex_arm, bone_map):
        pb = dex_arm.pose.bones.get(dex_name)
        inf_bone = inf_arm.data.bones.get(inf_name)
        if pb is None or inf_bone is None:
            continue

        target = (inf_arm.matrix_world @ inf_bone.tail_local
                  - inf_arm.matrix_world @ inf_bone.head_local)
        current = (dex_arm.matrix_world @ pb.tail
                   - dex_arm.matrix_world @ pb.head)
        if target.length < 1e-6 or current.length < 1e-6:
            continue

        turn = current.normalized().rotation_difference(target.normalized())

        head = pb.head.copy()
        around = (mathutils.Matrix.Translation(head)
                  @ turn.to_matrix().to_4x4()
                  @ mathutils.Matrix.Translation(-head))
        pb.matrix = around @ pb.matrix

        # Дети читают положение родителя, поэтому пересчёт нужен на каждом шаге,
        # а не один раз в конце.
        bpy.context.view_layer.update()
        turned += 1

    bpy.ops.object.mode_set(mode="OBJECT")
    print("[arms] костей развёрнуто по образцу Infima: %d" % turned)

    # Контроль: после разворота кисть DexGuy должна оказаться примерно там же,
    # где кисть Infima. Разойдутся — значит поза не легла, и дальше смотреть
    # на меш бессмысленно.
    for dex_name, inf_name in (("mixamorig:RightHand", "hand_r"),
                               ("mixamorig:LeftHand", "hand_l")):
        pb = dex_arm.pose.bones.get(dex_name)
        inf_bone = inf_arm.data.bones.get(inf_name)
        if pb is None or inf_bone is None:
            continue
        ours = dex_arm.matrix_world @ pb.head
        theirs = inf_arm.matrix_world @ inf_bone.head_local
        print("[arms]   %-20s наша (%.3f %.3f %.3f)  их (%.3f %.3f %.3f)  расхождение %.3f"
              % (inf_name, ours.x, ours.y, ours.z,
                 theirs.x, theirs.y, theirs.z, (ours - theirs).length))


def bake_pose(mesh_obj, dex_arm):
    """Запекает позу в геометрию — дальше меш живёт уже без скелета DexGuy."""
    skin = mesh_obj.modifiers.new("PoseBake", "ARMATURE")
    skin.object = dex_arm
    bpy.ops.object.select_all(action="DESELECT")
    mesh_obj.select_set(True)
    bpy.context.view_layer.objects.active = mesh_obj
    bpy.ops.object.convert(target="MESH")


def align_to_chain(mesh_obj, dex_arm, inf_arm, bone_map):
    """
    Двигает руки так, чтобы они сели на плечи Infima.

    Поза задала направления, но начало цепочки осталось там, где плечо у
    DexGuy. Совмещение идёт по головам ключиц: дальше вниз кости обоих
    скелетов расходятся не больше чем на пятую часть длины, и этого хватает.
    """
    shifts = []
    for dex_name, inf_name in bone_map.items():
        if "Shoulder" not in dex_name:
            continue
        dex_bone = dex_arm.data.bones.get(dex_name)
        inf_bone = inf_arm.data.bones.get(inf_name)
        if dex_bone is None or inf_bone is None:
            continue
        pb = dex_arm.pose.bones[dex_name]
        shifts.append((inf_arm.matrix_world @ inf_bone.head_local)
                      - (dex_arm.matrix_world @ pb.head))

    if not shifts:
        print("[arms] ключиц не нашлось — сдвиг не применён")
        return

    shift = sum(shifts, mathutils.Vector((0.0, 0.0, 0.0))) / len(shifts)

    # Сдвиг пишется в саму геометрию, а не в трансформ объекта: меш поедет в
    # FBX ребёнком арматуры, и лишний трансформ на нём привёз бы в Unity
    # смещение, которого никто не ждёт.
    local = mesh_obj.matrix_world.inverted() @ mathutils.Matrix.Translation(shift) @ mesh_obj.matrix_world
    mesh_obj.data.transform(local)
    mesh_obj.data.update()
    print("[arms] сдвиг на плечи Infima: (%.3f %.3f %.3f)" % (shift.x, shift.y, shift.z))


def report_fit(mesh_obj, inf_arm):
    """
    Насколько каждая кость Infima попала внутрь геометрии, которой управляет.

    Считается до экспорта, чтобы отличать свои ошибки от чужих. Кость, стоящая
    дальше собственной длины от своих вершин, при повороте потащит их вокруг
    чужой точки — так руки и выглядят растянутыми.
    """
    rows = []
    for group in mesh_obj.vertex_groups:
        bone = inf_arm.data.bones.get(group.name)
        if bone is None:
            continue
        points = [mesh_obj.matrix_world @ v.co for v in mesh_obj.data.vertices
                  if any(g.group == group.index and g.weight > 0.5 for g in v.groups)]
        if not points:
            continue
        centre = sum(points, mathutils.Vector((0.0, 0.0, 0.0))) / len(points)
        head = inf_arm.matrix_world @ bone.head_local
        tail = inf_arm.matrix_world @ bone.tail_local
        rows.append((distance_to_segment(centre, head, tail), group.name, len(points)))

    rows.sort(reverse=True)
    bad = [r for r in rows if r[0] > 0.05]
    print("[arms] посадка костей: костей %d, дальше 5 см от своих вершин %d"
          % (len(rows), len(bad)))
    for gap, name, count in rows[:8]:
        print("[arms]   %-16s %.3f м (%d вершин)" % (name, gap, count))


def distance_to_segment(point, a, b):
    """Расстояние от точки до отрезка кости."""
    ab = b - a
    length2 = ab.dot(ab)
    if length2 < 1e-12:
        return (point - a).length
    t = max(0.0, min(1.0, (point - a).dot(ab) / length2))
    return (point - (a + ab * t)).length


def rename_groups(mesh_obj, bone_map):
    """Переименовывает весовые группы в имена Infima, лишние выбрасывает."""
    kept, dropped = 0, 0
    for group in list(mesh_obj.vertex_groups):
        target = bone_map.get(group.name)
        if target is None:
            mesh_obj.vertex_groups.remove(group)
            dropped += 1
        else:
            group.name = target
            kept += 1
    print("[arms] групп переименовано %d, выброшено %d" % (kept, dropped))


def main():
    args = parse_args()
    bone_map = build_bone_map()

    clear_scene()
    bpy.ops.import_scene.fbx(filepath=os.path.abspath(args.fp))
    inf_objects = list(bpy.context.scene.objects)
    inf_arm = find_armature(inf_objects)
    if inf_arm is None:
        raise SystemExit("в FBX Infima нет арматуры")
    cubic = [o for o in inf_objects if o.type == "MESH"]
    print("[arms] Infima: арматура '%s', %d костей, мешей %d"
          % (inf_arm.name, len(inf_arm.data.bones), len(cubic)))

    # Потолок исходных рук — по нему потом обрезается наша геометрия.
    ceiling = max((o.matrix_world @ mathutils.Vector(c)).z
                  for o in cubic for c in o.bound_box)
    print("[arms] руки Infima кончаются на z=%.3f" % ceiling)

    brought = append_from_blend(args.blend, {args.body, "DexGuy"})
    body = next((o for o in brought if o.name.startswith(args.body)), None)
    dex_arm = find_armature(brought)
    if body is None or dex_arm is None:
        raise SystemExit("не приехали DexGuy.body и его арматура: %s"
                         % [o.name for o in brought])
    print("[arms] DexGuy: меш '%s' (%d полигонов), арматура '%s'"
          % (body.name, len(body.data.polygons), dex_arm.name))

    # Шейпкеи и маски запекаются в геометрию до всего остального.
    #
    # Без этого в работу пошёл бы базовый меш MakeHuman вместо нашего
    # персонажа — телосложение MPFB лепит шейпкеями, и в data.vertices лежит
    # не то, что видно. Вместе с ним приехала бы и вспомогательная оболочка
    # для подгонки одежды: тысячи вершин вокруг тела, которые прячет маска и
    # которым в руках взяться неоткуда.
    bpy.ops.object.select_all(action="DESELECT")
    body.select_set(True)
    bpy.context.view_layer.objects.active = body
    bpy.ops.object.convert(target="MESH")
    print("[arms] после запекания шейпкеев и масок: %d вершин, %d полигонов"
          % (len(body.data.vertices), len(body.data.polygons)))

    # Модификатор арматуры снимается: дальше вершины двигаются руками,
    # и старая привязка только мешала бы.
    for mod in list(body.modifiers):
        if mod.type == "ARMATURE":
            body.modifiers.remove(mod)

    strip_to_arms(body, limb_bones(bone_map),
                  set(dex_arm.data.bones.keys()), args.keep_threshold)
    if not args.no_retarget:
        retarget_vertices(body, dex_arm, inf_arm, bone_map)
        clip_above(body, ceiling)
    rename_groups(body, bone_map)

    report_fit(body, inf_arm)

    body.name = "SK_FP_CH_DexArms"
    body.parent = inf_arm
    # Инверсия родителя — обратная его трансформу, а не единичная: арматура
    # приехала из FBX со своим поворотом, и с единичной меш доворачивало на
    # четверть оборота вместе с ней.
    body.matrix_parent_inverse = inf_arm.matrix_world.inverted()
    skin = body.modifiers.new("Armature", "ARMATURE")
    skin.object = inf_arm

    # Кубический меш и арматура DexGuy в экспорт не идут.
    for obj in cubic + [dex_arm]:
        bpy.data.objects.remove(obj, do_unlink=True)

    bpy.ops.object.select_all(action="DESELECT")
    for obj in (inf_arm, body):
        obj.select_set(True)
    bpy.context.view_layer.objects.active = inf_arm

    out = os.path.abspath(args.out)
    os.makedirs(os.path.dirname(out), exist_ok=True)
    bpy.ops.export_scene.fbx(
        filepath=out,
        use_selection=True,
        object_types={"ARMATURE", "MESH"},
        global_scale=1.0,
        apply_unit_scale=True,
        apply_scale_options="FBX_SCALE_ALL",
        axis_forward="-Z",
        axis_up="Y",
        use_mesh_modifiers=False,
        mesh_smooth_type="FACE",
        use_tspace=True,
        # Ось костей и листовые кости — те же, с какими скелет приехал из пака.
        # Клипы адресуют кости по путям в иерархии, и лишняя кость на конце
        # цепочки сдвигает эти пути, после чего анимация к мешу не привязывается.
        add_leaf_bones=False,
        primary_bone_axis="Y",
        secondary_bone_axis="X",
        armature_nodetype="NULL",
        use_armature_deform_only=False,
        bake_anim=False,
        path_mode="COPY",
        embed_textures=True,
    )

    print("[arms] готово: %s (%.1f KB)" % (out, os.path.getsize(out) / 1024.0))


if __name__ == "__main__":
    main()
