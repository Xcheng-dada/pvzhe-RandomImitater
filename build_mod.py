# -*- coding: utf-8 -*-
"""「随机模仿者」Mod —— 编译 → 打包 → 装机。

用法：
    python build_mod.py              # 只编译 + 打包
    python build_mod.py --install    # 编译 + 打包 + 安装到游戏 Mods 目录

与「经典模仿者」同构：
  · 完全复用内置模仿者的艺术与脚本（不改动游戏任何现有资源）：
      角色场景 instance TowerDefensePlantImitater.tscn
      精灵场景 instance Imitater.tscn
  · 唯一差别：场景上的 packetBank 指向运行时注册的混合卡池 RandomImitaterMixed
    ⇒ 内置模仿者 Explode() 里硬编码的 GetCategory("White") 就抽到植物+僵尸混合池。
  · CHAR_KEY 四处同名：目录名 / 场景文件名 / characterConfig.name / packet.saveKey。
"""
import hashlib
import json
import os
import shutil
import subprocess
import sys
import zipfile

BASE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(BASE, "src")
DIST = os.path.join(BASE, "dist", "RandomImitater.pmod")

# ---------------- 本机路径 ----------------
GAME = r"F:\桌面\Game\植物大战僵尸杂交版0.29\植物大战僵尸杂交重制版"
REF_DIR = os.path.join(GAME, "data_PlantsVsZombies_windows_x86_64")
UD = r"C:\Users\xiaochengc\AppData\Roaming\Godot\app_userdata\植物大战僵尸杂交版"
MODS_DIR = os.path.join(UD, "Mods")
DOTNET = os.path.join(BASE, ".cache", "dotnet9", "dotnet.exe")
DOTNET_ROOT = os.path.dirname(DOTNET)

# ---------------- 常量 ----------------
CHAR_KEY = "RandomImitater"
MOD_NAME = "随机模仿者"
MOD_ID = "randomimitater"
CFG_FILE = "TowerDefensePlantRandomImitater.tres"
SCENE_FILE = "RandomImitater.tscn"
SPRITE_FILE = "RandomImitater.tscn"
CSET_FILE = "RandomImitaterComponentSet.tres"
PKG = "Resources/Characters/Plants/" + CHAR_KEY
CARD_REL = "Resources/Cards/" + CHAR_KEY + ".tres"
CUSTOM_BANK = "RandomImitaterMixed"

PN = "随机模仿者"

# ── 图鉴文案 ──────────────────────────────────────────────────────────
# 三个字段各对应一个控件（见 Almanac.PlantInformationSet → InformationPanel.InitPacket）：
#   describe        → RichTextLabel  「卡片介绍」（选卡界面/图鉴正文）
#   handbookDescribe→ RichTextLabel  「图鉴·描述」
#   handbookStory   → RichTextLabel  「图鉴·故事」
# 三个都是 RichTextLabel ⇒ 支持 BBCode，照游戏内置卡的格式写。
#
# 内置卡的颜色惯例（从游戏 PCK 里的真实文案扒出来的）：
#   字段名 FF0000 红 / 获取方式 0000cd 蓝 / 数值 cc241d 暗红 / 特性 C71585 紫 / 升级 0000FF 蓝
#
# ⚠️ 硬性约束：文案里禁止出现英文双引号 "（会截断 .tres 字符串字面量）。
#    要引用一律用中文引号『』。RichTextLabel 支持换行，Python 里写 \\n。

# 卡片介绍：一句话概括，不带字段模板（对齐内置卡的写法）
PD = "完全随机的模仿者，惊喜和惊吓并存！"

# 图鉴·描述：走内置卡的「字段块」格式
# ⚠️ 这里的百分比要和 src/RandomImitaterEntry.cs 的 PlantChance 保持一致（现在是 0.65）。
PHD = ("[color=FF0000]获取方式[/color]：[color=0000cd]Mod 植物（无需解锁）[/color]\\n"
       "韧性：[color=cc241d]1000[/color]\\n"
       "特点：[color=cc241d]65%概率开出随机植物 35%概率开出随机僵尸（敌对）[/color]\\n")

# 图鉴·故事：一句风味文字
PHS = ("随机模仿者旋转着，宛如风暴般裹挟着植物与僵尸，随机地投放到每个召唤它的人面前。"
       "有人因它而欣喜，也有人因它而愤怒，时不时也有挽留它的声音传来：“还会再见吗！”"
       "但随机模仿者不语，只是一味地旋转。")


# 内置资源（全部 res:// 引游戏自带）
BASE_IMITATER_SCENE = "res://Asset/Anime/Character/Plant/Chapter0/Imitater/Scene/TowerDefensePlantImitater.tscn"
BASE_IMITATER_SPRITE = "res://Asset/Anime/Character/Plant/Chapter0/Imitater/Imitater.tscn"
BASE_IMITATER_CSET = "res://Asset/Anime/Character/Plant/Chapter0/Imitater/Scene/TowerDefensePlantImitaterExplodeDefinition.tres"
BASE_PLANT_CSET = "res://Prefab/TowerDefense/Character/ComponentSets/TowerDefensePlantComponentSet.tres"
S_PLANT_CONFIG = "res://Resource/TowerDefense/Character/Config/TowerDefensePlantConfig.cs"
S_CSET = "res://Script/Component/Runtime/CharacterComponentSet.cs"
S_PACKET = "res://Registry/Battle/Feature/PacketBank/Resource/Packet/TowerDefensePacketConfig.cs"

# PACKET_TYPE: NOONE=-1, WHITE=0, GOLD=1, DIAMOND=2, COLOUR=3, STAR=4, ORIGINAL=5, ZOMBIE=6
PACKET_TYPE_COLOUR = 3

out = []


def log(s):
    out.append(str(s))
    try:
        with open(os.path.join(BASE, "_build.log"), "w", encoding="utf-8") as f:
            f.write("\n".join(out))
    except Exception:
        pass


def w(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    if os.path.isfile(path):
        with open(path, "r", encoding="utf-8", newline="") as f:
            if f.read() == text:
                return False
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    return True


# ---------------- 资源内容 ----------------

def cfg_tres():
    # plantGridType = [2, 6, 5]（GROUND/POT/LILYPAD）= TowerDefenseCharacterConfig 的
    # **默认值**，也就是「普通植物」的落位规则。这正是用户要的「和普通植物一致」：
    #   · 不许叠种模仿者（占住 slot[GROUND]，同类自然放不进）
    #   · 花盆 / 睡莲 / 南瓜壳 / 咖啡豆：按普通植物的规则走（空位可放、有植物则按引擎判定）
    #   · 墓碑、弹坑：不能种（与墓碑的拦截在 CanPacketPlant 的 FindPlantingBlocker，
    #     弹坑靠收窄后不再走 ALL 早退，跟普通植物一样被拒）
    #   · 会占格 ⇒ 不许叠种、容器按普通植物判定、墓碑/弹坑被拒
    #     （注意：「能不能被僵尸咬」和占格**无关**，那是 ExplodeComponent.ProtectsFromBites
    #      这道独立闸门决定的，由 src/RandomImitaterEntry.cs 里逐帧改组件字段打开）
    #
    # ⚠️ 收窄必须和「自定义 Explode」一起上（见 src/RandomImitaterEntry.cs 的 RiExplode）。
    #   旧版只收窄不改变身会 100% 出僵尸：`TowerDefensePlantImitater.Explode()` 校验植物
    #   用的是模仿者**自己那格**（IL_00DB ldarg.0.cell → IL_00E5 CanPacketPlant），
    #   而那时本体还占着槽位 ⇒ 植物一律被拒 ⇒ IL_00EC `Remove` 掉重抽，僵尸分支不校验
    #   ⇒ 循环必然落到僵尸。现在 Explode 第一件事是 `Destroy(false)` 把自己腾走
    #   （`cell.CharacterDestroy` 里的 slot 置空是**同步**的，发生在第一个 await 之前），
    #   校验看到的就是空地，植物照常种得下。这套顺序是官方第 7 章
    #   `TowerDefensePlantImitaterW.Explode()`（IL_0009）的原样做法。
    return f"""[gd_resource type="Resource" script_class="TowerDefensePlantConfig" format=3]

[ext_resource type="Script" path="{S_PLANT_CONFIG}" id="1"]

[resource]
script = ExtResource("1")
name = "{CHAR_KEY}"
canUsePlantfood = false
canCopy = false
damagePointData = null
armorData = null
customData = null
ashScene = null
homeWorld = 1
costRise = -1
cost = 0
packetCooldown = 7.5
plantGridType = [2, 6, 5]
hitpoints = 1000
collisionFlags = 11
maskFlags = 9
metadata/_custom_type_script = "{S_PLANT_CONFIG}"
"""


def cset_tres():
    return f"""[gd_resource type="Resource" script_class="CharacterComponentSet" format=3]

[ext_resource type="Resource" path="{BASE_IMITATER_CSET}" id="1"]
[ext_resource type="Resource" path="{BASE_PLANT_CSET}" id="2"]
[ext_resource type="Script" path="{S_CSET}" id="3"]

[resource]
script = ExtResource("3")
ParentSet = ExtResource("2")
Components = [ExtResource("1")]
"""


def scene_tscn():
    return f"""[gd_scene format=3]

[ext_resource type="PackedScene" path="{BASE_IMITATER_SCENE}" id="1"]
[ext_resource type="Resource" path="../Config/{CFG_FILE}" id="2"]
[ext_resource type="Resource" path="./{CSET_FILE}" id="3"]

[node name="{CHAR_KEY}" instance=ExtResource("1")]
ComponentSet = ExtResource("3")
config = ExtResource("2")
packetBank = "{CUSTOM_BANK}"
metadata/mod_resource_kind = "Character"
metadata/mod_display_name = "{PN}"
metadata/mod_character_category = "Plant"
metadata/mod_character_config_path = "../Config/{CFG_FILE}"
metadata/mod_character_script_path = "./{SCENE_FILE}"
metadata/mod_character_script_binding = "CompanionOnly"
metadata/mod_character_sprite_scene = "../Sprite/{SPRITE_FILE}"
"""


def sprite_tscn():
    return f"""[gd_scene format=3]

[ext_resource type="PackedScene" path="{BASE_IMITATER_SPRITE}" id="1"]

[node name="{CHAR_KEY}" instance=ExtResource("1")]
metadata/mod_resource_kind = "CharacterSprite"
"""


def packet_body(cfg_rel):
    return f"""[gd_resource type="Resource" script_class="TowerDefensePacketConfig" format=3]

[ext_resource type="Resource" path="{cfg_rel}" id="1"]
[ext_resource type="Script" path="{S_PACKET}" id="2"]

[resource]
script = ExtResource("2")
saveKey = "{CHAR_KEY}"
unlockCheckList = []
name = "{PN}"
describe = "{PD}"
handbookDescribe = "{PHD}"
handbookStory = "{PHS}"
packetAnimeClip = "Idle"
packetAnimeOffset = Vector2(21, 25)
packetAnimeScale = Vector2(0.5, 0.5)
characterConfig = ExtResource("1")
type = {PACKET_TYPE_COLOUR}
metadata/_custom_type_script = "{S_PACKET}"
"""


def manifest():
    resources = sorted([
        CARD_REL,
        f"{PKG}/Config/{CFG_FILE}",
        f"{PKG}/Packet/{CHAR_KEY}.tres",
        f"{PKG}/Scene/{CSET_FILE}",
        f"{PKG}/Scene/{SCENE_FILE}",
        f"{PKG}/Sprite/{SPRITE_FILE}",
        "Runtime/ModAssembly.dll",
    ], key=lambda p: p.lower())
    return {
        "schemaVersion": 2,
        "id": MOD_ID,
        "name": MOD_NAME,
        "version": "1.0.0",
        "author": "小橙c",
        "description": (
            f"新增植物「{PN}」（彩卡）：种下后随机开出植物或僵尸"
            f"（65% 开出随机植物，35% 开出随机僵尸·敌对）。"
        ),
        "dependencies": [],
        "conflicts": [],
        "provides": {
            "Character": [CHAR_KEY],
            "CharacterSprite": [CHAR_KEY],
            "Packet": [CHAR_KEY],
        },
        "overrides": {},
        "scripts": [],
        "runtimeAssembly": "Runtime/ModAssembly.dll",
        "runtimeEntryType": "RandomImitaterEntry",
        "runtimeApiVersion": 1,
        "runtimeAssemblyPolicy": "optional",
        "blueprints": [],
        "translations": [],
        "resources": resources,
    }


# ---------------- 编译 ----------------

def compile_asm():
    home = os.path.join(BASE, ".cache", "dotnet_home")
    tmpd = os.path.join(home, "tmp")
    nuget = os.path.join(BASE, ".cache", "nuget")
    for d in (home, tmpd, nuget):
        os.makedirs(d, exist_ok=True)
    env = dict(os.environ)
    env["DOTNET_ROOT"] = DOTNET_ROOT
    env["DOTNET_CLI_HOME"] = home
    env["TEMP"] = tmpd
    env["TMP"] = tmpd
    env["TMPDIR"] = tmpd
    env["NUGET_PACKAGES"] = nuget
    env["DOTNET_NOLOGO"] = "1"
    env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    env["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1"
    env["MSBUILDDISABLENODEREUSE"] = "1"

    if not os.path.isfile(os.path.join(REF_DIR, "PlantsVsZombies.dll")):
        log("[compile] ❌ 找不到 " + REF_DIR)
        return False

    logs = []

    def run(args, timeout):
        p = subprocess.run([DOTNET] + args, cwd=SRC, env=env, capture_output=True,
                           text=True, encoding="utf-8", errors="replace", timeout=timeout)
        logs.append("$ dotnet " + " ".join(args) + "  -> RC=%d" % p.returncode)
        if p.stdout:
            logs.append(p.stdout.strip())
        if p.stderr:
            logs.append(p.stderr.strip())
        return p.returncode

    rc = run(["build", "-c", "Release",
              "-p:GodotRefDir=" + REF_DIR,
              "-p:UseSharedCompilation=false", "-m:1", "-nodeReuse:false",
              "-v:q", "-nologo"], 600)

    with open(os.path.join(SRC, "build.log"), "w", encoding="utf-8") as f:
        f.write("\n".join(logs))
    log("[compile] RC=%d" % rc)
    return rc == 0


# ---------------- 打包 + 装机 ----------------

def md5(path):
    h = hashlib.md5()
    with open(path, "rb") as f:
        for c in iter(lambda: f.read(65536), b""):
            h.update(c)
    return h.hexdigest()


def package():
    binsrc = os.path.join(SRC, "bin", "Release", "JTYRandomImitater.dll")
    if not os.path.isfile(binsrc):
        log("[package] ❌ 缺编译产物 " + binsrc)
        return False
    runtime_dir = os.path.join(BASE, "Runtime")
    os.makedirs(runtime_dir, exist_ok=True)
    dll = os.path.join(runtime_dir, "ModAssembly.dll")
    shutil.copyfile(binsrc, dll)
    log("[package] DLL md5=" + md5(dll))

    os.makedirs(os.path.dirname(DIST), exist_ok=True)
    res_root = os.path.join(BASE, "Resources")
    with zipfile.ZipFile(DIST, "w", zipfile.ZIP_DEFLATED) as z:
        z.write(os.path.join(BASE, "mod.json"), "mod.json")
        for root, _dirs, files in os.walk(res_root):
            for fn in files:
                fp = os.path.join(root, fn)
                arc = os.path.relpath(fp, BASE).replace("\\", "/")
                z.write(fp, arc)
        z.write(dll, "Runtime/ModAssembly.dll")

    with zipfile.ZipFile(DIST) as z:
        first = z.namelist()[0]
        nl = sorted(z.namelist())
    log("[package] %s  %d B" % (DIST, os.path.getsize(DIST)))
    for n in nl:
        log("          " + n)

    if first != "mod.json" or nl.count("mod.json") != 1:
        log("[package] ❌ mod.json 不在根或不止一个（首个条目=%s）" % first)
        return False

    declared = sorted(manifest()["resources"], key=lambda p: p.lower())
    actual = sorted([n for n in nl if n != "mod.json"], key=lambda p: p.lower())
    if declared != actual:
        log("[package] ❌ resources 声明与包内实际不一致")
        log("  声明: " + str(declared))
        log("  实际: " + str(actual))
        return False
    log("[package] ✅ resources 声明与实际一致（%d 项）" % len(actual))
    return True


def install():
    dst = os.path.join(MODS_DIR, "RandomImitater.pmod")
    os.makedirs(MODS_DIR, exist_ok=True)
    shutil.copyfile(DIST, dst)
    log("[install] %s (%d B)" % (dst, os.path.getsize(dst)))
    log("[install] sha256=" + hashlib.sha256(open(dst, "rb").read()).hexdigest().upper())

    # 清掉旧缓存（否则游戏可能用上一次解出来的旧文件）
    cache = os.path.join(UD, "ModsCache")
    src = os.path.join(cache, "RandomImitater")
    if os.path.isdir(src):
        import time
        dstc = src + ".bak_" + time.strftime("%H%M%S")
        try:
            r = subprocess.run(["cmd", "/c", "move", src, dstc],
                               capture_output=True, errors="replace",
                               encoding="gbk", timeout=60)
            log("[cache] move rc=%d %s" % (r.returncode,
                ((r.stdout or "") + (r.stderr or "")).strip().replace("\n", " ")))
        except Exception as ex:
            log("[cache] move 失败（已忽略）：%r" % ex)

    en = os.path.join(MODS_DIR, "enabled_mods.json")
    try:
        ids = []
        if os.path.isfile(en):
            with open(en, encoding="utf-8") as f:
                ids = json.load(f)
        if MOD_ID not in ids:
            ids.append(MOD_ID)
            with open(en, "w", encoding="utf-8") as f:
                json.dump(ids, f, ensure_ascii=False, indent=2)
            log("[install] enabled_mods.json += " + MOD_ID)
        else:
            log("[install] enabled_mods.json 已含 " + MOD_ID)
        log("[install] enabled_mods.json = " + json.dumps(ids, ensure_ascii=False))
    except Exception as e:
        log("[install] enabled_mods.json 合并失败: %r" % e)
    return True


# ---------------- 主流程 ----------------

def main():
    files = {
        os.path.join(BASE, PKG, "Config", CFG_FILE): cfg_tres(),
        os.path.join(BASE, PKG, "Scene", CSET_FILE): cset_tres(),
        os.path.join(BASE, PKG, "Scene", SCENE_FILE): scene_tscn(),
        os.path.join(BASE, PKG, "Sprite", SPRITE_FILE): sprite_tscn(),
        os.path.join(BASE, PKG, "Packet", CHAR_KEY + ".tres"):
            packet_body(f"../Config/{CFG_FILE}"),
        os.path.join(BASE, CARD_REL.replace("/", os.sep)):
            packet_body(f"../Characters/Plants/{CHAR_KEY}/Config/{CFG_FILE}"),
    }
    for p, t in files.items():
        ch = w(p, t)
        log("[gen] %s %s" % ("updated" if ch else "same   ", os.path.relpath(p, BASE)))

    w(os.path.join(BASE, "mod.json"),
      json.dumps(manifest(), ensure_ascii=False, indent=2) + "\n")
    log("[gen] mod.json")

    if not compile_asm():
        log("❌ 编译失败，终止")
        return 1
    if not package():
        return 1
    if "--install" in sys.argv:
        install()
    return 0


if __name__ == "__main__":
    rc = 0
    try:
        rc = main()
    except Exception as e:
        import traceback
        log("EXC: %r" % e)
        log(traceback.format_exc())
        rc = 1
    txt = "\n".join(out)
    with open(os.path.join(BASE, "_build.log"), "w", encoding="utf-8") as f:
        f.write(txt)
    print(txt)
    sys.exit(rc)
