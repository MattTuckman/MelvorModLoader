# Builds One Style's data/elites.json and the downscaled elite art (WebP) from the hack-and-slash design folder.
import csv, json, os, sys
hs = r"C:\Users\falor\OneDrive\Belgeler\Ortak-Calisma\hack-and-slash"
mod = r"C:\Program Files (x86)\Steam\steamapps\common\Melvor Idle 2\MelvorModLoader_src\Mods\OneStyle"
gamedata = r"C:\Users\falor\AppData\Local\Temp\claude\03b84662444bbca5\fd0c168e-251b-4966-adc0-e80ece298b6f\scratchpad\gamedata\MelvorBase.json"


def tsv(p):
    with open(p, encoding="utf-8", newline="") as f:
        return list(csv.DictReader(f, delimiter="\t"))


kim = tsv(os.path.join(hs, "denge", "unique-kimlik.tsv"))
names = {r["dosya"]: r for r in tsv(os.path.join(hs, "assets", "isimler.tsv"))}
game = json.load(open(gamedata, encoding="utf-8"))
attacks = {a["Id"]: a for a in game["Objects"]["Attack"]}


def peak(attack_id):
    """The biggest single hit of an attack as a share of the attacker's max hit; -1 when a hit is worked out from
    the defender (a share of the player's own hit points): the mod leaves such signatures out."""
    a = attacks.get(attack_id)
    if not a:
        return -1
    best = 0.0
    for h in a["Hits"]:
        total = 0.0
        for dm in h["Damage"]:
            if dm["Character"] != "Attacker" or dm["Formula"]["Type"] not in ("MelvorBase:NormalDamage", "MelvorBase:MaxHit"):
                return -1
            total += dm["Formula"]["Data"].get("Percent", 1)
        best = max(best, total)
    return round(best, 4)


out = []
for r in kim:
    n = names.get(r["file"], {})
    out.append({
        "Id": "MelvorBase:" + r["id"], "File": os.path.splitext(r["file"])[0], "UniqueName": r["unique_name"],
        "Story": n.get("hikaye", ""), "Affixes": [r["affix1"], r["affix2"]],
        "Signature": ("MelvorBase:" + r["signature_attack"]) if r["signature_attack"] else "",
        "SignatureChance": float(r["signature_chance"] or 0) / 100.0,
        "SignaturePeak": peak(r["signature_attack"]) if r["signature_attack"] else 0,
        "Passive": ("MelvorBase:" + r["added_passive"]) if r["added_passive"] else "",
    })
data_dir = os.path.join(mod, "data")
os.makedirs(data_dir, exist_ok=True)
with open(os.path.join(data_dir, "elites.json"), "w", encoding="utf-8", newline="\n") as f:
    json.dump(out, f, ensure_ascii=False, indent=1)
print(len(out), "elites;", sum(1 for o in out if o["SignaturePeak"] < 0), "signatures left out")

if "--art" in sys.argv:
    from PIL import Image
    size = 384
    for tier in ("champion", "unique"):
        src = os.path.join(hs, "assets", tier)
        dst = os.path.join(mod, "art", tier)
        os.makedirs(dst, exist_ok=True)
        n = 0
        for r in kim:
            p = os.path.join(src, r["file"])
            if not os.path.exists(p):
                print("missing", tier, r["file"])
                continue
            im = Image.open(p).convert("RGBA").resize((size, size), Image.LANCZOS)
            im.save(os.path.join(dst, os.path.splitext(r["file"])[0] + ".webp"), "WEBP", quality=88, method=4)
            n += 1
        print(tier, n)
