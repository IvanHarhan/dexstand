# -*- coding: utf-8 -*-
"""
Генерит недостающие текстуры персонажа и раскладывает их по местам.

Запуск:
  blender.exe --background --python make_textures.py -- --blend "...\\dexguy.blend"

Зачем вообще: у ассета ushanka в .mhmat прописана diffuseTexture ushanka.png,
а самого файла в комплекте нет — меш приезжает белым. Папка skins у MPFB пустая,
поэтому кожа тела тоже без текстуры. Обе картинки здесь и рисуются.

Почему шум, а не запекание через Cycles: и мех, и поры кожи — это равномерный
мелкий шум. Для него UV-раскладка не важна, картинка ложится одинаково хорошо
куда угодно, и не нужно ни настраивать бейк, ни держать сцену. Лицо от этого
деталей не получит — бровей и губ в шуме нет, это честное ограничение.

Размеры под телефон: 512 на ушанку, 1024 на тело. 4K тут не нужен никому.
"""

import argparse
import os
import sys

import bpy
import numpy as np


# Каталог ассетов MPFB — туда кладётся ushanka.png, чтобы .mhmat её нашёл.
MPFB_DATA = os.path.join(
    os.environ["APPDATA"], "Blender Foundation", "Blender", "5.2",
    "extensions", ".user", "blender_org", "mpfb", "data")


def parse_args():
    argv = sys.argv
    argv = argv[argv.index("--") + 1:] if "--" in argv else []
    p = argparse.ArgumentParser()
    p.add_argument("--blend", required=True, help=".blend с персонажем, правится на месте")
    p.add_argument("--seed", type=int, default=7)
    return p.parse_args(argv)


def value_noise(size, cells, rng):
    """
    Шум как сглаженная случайная сетка cells x cells, растянутая до size.

    Берётся именно такой, а не np.random по пикселям: попиксельный шум на
    текстуре читается как плёночное зерно и пропадает при мипмапинге, а нам
    нужны пятна размером с ворсинку, которые переживут уменьшение.
    """
    grid = rng.random((cells + 1, cells + 1)).astype(np.float32)

    ys = np.linspace(0, cells, size, endpoint=False, dtype=np.float32)
    xs = np.linspace(0, cells, size, endpoint=False, dtype=np.float32)
    y0 = ys.astype(np.int32)
    x0 = xs.astype(np.int32)
    fy = (ys - y0)[:, None]
    fx = (xs - x0)[None, :]

    # Сглаживание по smoothstep, иначе на стыках клеток видны прямые складки.
    fy = fy * fy * (3.0 - 2.0 * fy)
    fx = fx * fx * (3.0 - 2.0 * fx)

    g00 = grid[np.ix_(y0, x0)]
    g01 = grid[np.ix_(y0, x0 + 1)]
    g10 = grid[np.ix_(y0 + 1, x0)]
    g11 = grid[np.ix_(y0 + 1, x0 + 1)]

    top = g00 + (g01 - g00) * fx
    bottom = g10 + (g11 - g10) * fx
    return top + (bottom - top) * fy


def fractal(size, octaves, rng, base_cells=8):
    """Сумма октав: крупные пятна задают тон, мелкие — фактуру."""
    out = np.zeros((size, size), dtype=np.float32)
    amplitude = 1.0
    total = 0.0
    cells = base_cells
    for _ in range(octaves):
        out += value_noise(size, cells, rng) * amplitude
        total += amplitude
        amplitude *= 0.5
        cells *= 2
    return out / total


def save_png(path, rgb):
    """
    Кладёт массив HxWx3 в PNG.

    Картинка создаётся байтовой: у неё pixels отдаются как есть, делением на
    255, без цветоуправления. Значит записанные сюда sRGB-числа доедут до файла
    ровно теми же, а не пройдут лишний раз через гамму.
    """
    size = rgb.shape[0]
    name = os.path.basename(path)

    if name in bpy.data.images:
        bpy.data.images.remove(bpy.data.images[name])
    img = bpy.data.images.new(name, width=size, height=size, alpha=False, float_buffer=False)
    img.colorspace_settings.name = "sRGB"

    # Blender читает буфер снизу вверх, поэтому строки переворачиваются.
    rgba = np.ones((size, size, 4), dtype=np.float32)
    rgba[:, :, :3] = np.clip(rgb, 0.0, 1.0)
    img.pixels.foreach_set(rgba[::-1].ravel())

    os.makedirs(os.path.dirname(path), exist_ok=True)
    img.file_format = "PNG"
    img.filepath_raw = path
    img.save()
    print("[textures] записано: %s (%dx%d)" % (path, size, size))
    return img


def make_fur(size, rng):
    """Тёмно-коричневый мех: крупные подпалины плюс вытянутые вертикально ворсинки."""
    patches = fractal(size, 4, rng, base_cells=6)

    # Ворс вытянут: шум берётся из узкой по вертикали сетки и растягивается,
    # отчего пятна превращаются в штрихи вдоль ворса.
    strands = value_noise(size, 96, rng)
    strands = np.repeat(strands[::4], 4, axis=0)[:size]

    value = 0.55 * patches + 0.45 * strands
    value = 0.40 + 0.60 * value                      # не уводим в чёрное

    base = np.array([0.29, 0.19, 0.12], dtype=np.float32)   # выделанная овчина
    rgb = base[None, None, :] * value[:, :, None]

    # Мех чуть теплее на светлых кончиках, холоднее в глубине — иначе читается
    # как крашеный пластик.
    warm = (value - value.mean())[:, :, None] * np.array([0.10, 0.05, -0.02], dtype=np.float32)
    return rgb + warm


def make_skin(size, rng):
    """Ровный тон плюс поры. Без лица: бровей и губ в шуме взяться неоткуда."""
    pores = fractal(size, 3, rng, base_cells=64)
    blotch = fractal(size, 2, rng, base_cells=5)

    base = np.array([0.76, 0.58, 0.48], dtype=np.float32)
    value = 1.0 + (pores - 0.5) * 0.07 + (blotch - 0.5) * 0.10

    rgb = base[None, None, :] * value[:, :, None]

    # Красноты в пятнах чуть больше, чем зелени: так кожа перестаёт выглядеть
    # серой при игровом свете.
    rgb[:, :, 0] += (blotch - 0.5) * 0.05
    return rgb


def ensure_material(obj, name):
    """
    Возвращает материал объекта, заводя его, если слотов нет вовсе.

    Телу MPFB материал не выдаёт: слот пустой, и Blender с Unity рисуют его
    дефолтным серым. Никакая текстура сюда не встанет, пока материала нет —
    поэтому проверка идёт до, а не после.
    """
    for slot in obj.material_slots:
        if slot.material is not None:
            return slot.material

    material = bpy.data.materials.new(name)
    obj.data.materials.append(material)
    print("[textures] у %s не было материала — завёл %s" % (obj.name, name))
    return material


def wire_texture(material, image):
    """
    Вставляет картинку прямо в Base Color.

    Прямо — принципиально: FBX-экспортёр записывает текстуру в материал только
    если она воткнута в Base Color без промежуточных нодов. MPFB гонит диффуз
    через MIX_RGB, и из-за этого всё уже приезжало в Unity серым.
    """
    if not material.use_nodes:
        material.use_nodes = True
    nodes = material.node_tree.nodes
    links = material.node_tree.links

    principled = next((n for n in nodes if n.type == "BSDF_PRINCIPLED"), None)
    if principled is None:
        print("[textures] у материала %s нет Principled — пропускаю" % material.name)
        return False

    # Если нода с картинкой уже есть — заполняем её, а не добавляем вторую.
    # У ушанки MPFB завёл ноду под ushanka.png, которой на диске не было, и она
    # осталась пустой. Вторая нода рядом ничего не меняла: и Workbench, и
    # экспортёр смотрят на активную, а активной оставалась пустая.
    tex = next((n for n in nodes if n.type == "TEX_IMAGE"), None)
    if tex is None:
        tex = nodes.new("ShaderNodeTexImage")
        tex.location = (principled.location.x - 340, principled.location.y)
    tex.image = image
    nodes.active = tex

    socket = principled.inputs["Base Color"]
    for link in list(socket.links):
        links.remove(link)
    links.new(tex.outputs["Color"], socket)
    print("[textures] %s <- %s" % (material.name, image.name))
    return True


def main():
    args = parse_args()
    rng = np.random.default_rng(args.seed)

    bpy.ops.wm.open_mainfile(filepath=os.path.abspath(args.blend))

    print("[textures] меши в сцене:")
    for obj in bpy.data.objects:
        if obj.type == "MESH":
            mats = [s.material.name if s.material else "—" for s in obj.material_slots]
            print("[textures]   %-28s материалы: %s" % (obj.name, ", ".join(mats) or "нет"))

    # Ушанка — в папку ассета, под именем, которое уже ждёт .mhmat.
    fur_path = os.path.join(MPFB_DATA, "clothes", "ushanka", "ushanka.png")
    fur = save_png(fur_path, make_fur(512, rng))

    # Кожа — в skins, чтобы будущие персонажи могли её подхватить.
    skin_path = os.path.join(MPFB_DATA, "skins", "dexskin", "dexskin.png")
    skin = save_png(skin_path, make_skin(1024, rng))

    touched = 0
    for obj in bpy.data.objects:
        if obj.type != "MESH":
            continue
        lower = obj.name.lower()
        if "ushanka" in lower:
            picked, fallback_name = fur, "DexGuy.ushanka"
        elif lower.endswith("body"):
            picked, fallback_name = skin, "DexGuy.skin"
        else:
            continue

        if wire_texture(ensure_material(obj, fallback_name), picked):
            touched += 1

    if touched == 0:
        raise SystemExit("ни один материал не поправлен — проверь имена объектов выше")

    bpy.ops.wm.save_mainfile()
    print("[textures] материалов поправлено: %d, .blend сохранён" % touched)


if __name__ == "__main__":
    main()
