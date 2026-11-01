"""What the log records, and what it deliberately doesn't.

Every hook the game documents, classified: logged, derivable from what is logged,
or skipped with a reason. Generated from `sts2.xml` and the mod's own source, so it
cannot drift from either.

    python tools/hook_coverage.py          # writes hook_coverage.md
    python tools/hook_coverage.py --check  # fails if anything is unclassified

Why it exists: "log everything, refine later" only works if later knows what was
never in the file. An unclassified hook is a question nobody has answered yet, and
this makes that visible instead of assumed.
"""
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CODE = ROOT / "SlayMyStatsCode"
XML = Path(r"E:\SteamLibrary\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64\sts2.xml")
OUT = ROOT / "hook_coverage.md"

PATCHED = re.compile(r"\[HarmonyPatch\(typeof\(Hook\),\s*nameof\(Hook\.(\w+)\)\)\]")

# First match wins. Anything unmatched is reported as unclassified.
RULES = [
    # Decisions and pass-through adjustments: not facts, and each one is already
    # reflected by the event that reports its result.
    (r"^(Should|Modify|TryModify|AfterModifying|IterateCombatHookListeners)",
     "skipped", "a decision or an adjusted value, not a fact; its result is reported by the event it modifies"),
    (r"^Before(?!Attack|Combat|HandDraw)",
     "skipped", "pre-hook: the matching After hook records the outcome"),
    # Damage, block and HP: the results are logged, the arithmetic is not.
    (r"^(ModifyDamage|ModifyUnblockedDamageTarget|ModifyBlock|ModifyHpLost|"
     r"AfterModifyingDamageAmount|AfterModifyingBlockAmount|AfterModifyingHpLost|"
     r"AfterModifyingPowerAmount|ModifyPowerAmount)",
     "skipped", "arithmetic on damage, block or powers; the result is in DamageResult, the HP deltas and the power events"),
    # Run-level things the .run file already holds.
    (r"^(AfterActEntered|AfterRoomEntered|AfterMapGenerated|ModifyGeneratedMap|ModifyNextEvent|"
     r"AfterItemPurchased|AfterRewardTaken|AfterGoldGained|AfterRestSite|ModifyRestSite|"
     r"ModifyRewards|BeforeCombatRewardOffered|ModifyCardReward|TryModifyCardRewardOptions|"
     r"ModifyMerchant|ShouldAllow|ShouldGenerateTreasure|ShouldProcurePotion|ShouldForcePotionReward|"
     r"ShouldProceedToNextMapPoint|ShouldRefillMerchantEntry|ShouldDisableRemainingRestSiteOptions|"
     r"ModifyExtraRestSiteHealText|ModifyUnknownMapPointRoomTypes|ModifyOddsIncreaseForUnrolledRoomType|"
     r"ModifyShuffleOrder|ModifyCardBeingAddedToDeck|ShouldAddToDeck|ModifyCardRewardUpgradeOdds)",
     "skipped", "run-level and already in the .run file (floors, gold, picks, rest choices); a second copy could only disagree"),
    # Combat hooks whose effect is visible in another logged event.
    (r"^(AfterFlush|BeforeFlush|ShouldFlush|AfterHandEmptied|ModifyHandDraw|AfterModifyingHandDraw|"
     r"ShouldDraw|AfterPreventingDraw|AfterModifyingCardPlayCount|ModifyCardPlayCount|"
     r"ModifyAttackHitCount|ModifyKeywordsInCombat|ModifyEnergyCostInCombat|ModifyStarCost|"
     r"ModifyMaxEnergy|ShouldPayExcessEnergyCostWithStars|ShouldGainStars|ShouldEtherealTrigger|"
     r"AfterModifyingOrbPassiveTriggerCount|ModifyOrbPassiveTriggerCount|ModifyOrbValue|"
     r"ModifySummonAmount|ShouldClearBlock|AfterPreventingBlockClear|ShouldDie|"
     r"ShouldStopCombatFromEnding|ShouldCreatureBeRemovedFromCombatAfterDeath|"
     r"ShouldAllowHitting|ShouldAllowTargeting|ShouldAllowFreeTravel|ShouldAllowAncient|"
     r"ShouldPlayerResetEnergy|ShouldTakeExtraTurn|ShouldPowerBeRemovedOnDeath|"
     r"AfterModifyingEnergyGain|ModifyEnergyGain|AfterAutoPrePlayPhaseEntered|"
     r"AfterAutoPostPlayPhaseEntered|BeforeCardAutoPlayed|ModifyCardPlayResultLocation|"
     r"BeforeCardRemoved|BeforePotionUsed|AfterCreatureAddedToCombat|AfterOstyRevived|"
     r"AfterCardGeneratedForCombat|AfterShuffle|AfterFlush|AfterBlockGained)" ,
     "derived", "visible through a logged event or the next snapshot; logging it too would be a second opinion"),
]

# Hooks that are logged and need no rule to explain them.
LOGGED_NOTE = "logged"


def hooks():
    """{name: summary} for every documented Hook method."""
    tree = ET.parse(XML)
    contracts, found = {}, {}
    for m in tree.getroot().findall(".//member"):
        name = m.get("name") or ""
        text = " ".join("".join(m.itertext()).split())
        if name.startswith("M:MegaCrit.Sts2.Core.Models.AbstractModel."):
            contracts[name.split("(")[0].rsplit(".", 1)[-1]] = text
        elif name.startswith("M:MegaCrit.Sts2.Core.Hooks.Hook."):
            short = name.split("(")[0].rsplit(".", 1)[-1]
            found[short] = text
    return {k: contracts.get(k, v) for k, v in found.items()}


def patched():
    names = set()
    for f in sorted(CODE.glob("*.cs")):
        names.update(PATCHED.findall(f.read_text(encoding="utf-8")))
    return names


def classify(name, is_patched):
    if is_patched:
        return "logged", LOGGED_NOTE
    for pattern, status, reason in RULES:
        if re.match(pattern, name):
            return status, reason
    return "unclassified", "nobody has decided yet"


def main(argv):
    all_hooks, live = hooks(), patched()
    rows = [(n, *classify(n, n in live)) for n in sorted(all_hooks)]
    counts = {}
    for _, status, _ in rows:
        counts[status] = counts.get(status, 0) + 1
    missing_patch = sorted(live - set(all_hooks))

    lines = ["# Hook coverage", "",
             "Every hook the game documents, and what the log does with it. Generated by",
             "`tools/hook_coverage.py` from `sts2.xml` and the mod's own patches -- do not edit by hand.",
             "",
             f"- **logged**: {counts.get('logged', 0)} (a patch records it)",
             f"- **derived**: {counts.get('derived', 0)} (reconstructable from what is logged)",
             f"- **skipped**: {counts.get('skipped', 0)} (with a reason)",
             f"- **unclassified**: {counts.get('unclassified', 0)} (undecided -- the gap to look at)",
             "",
             "| hook | what the log does |", "|---|---|"]
    for name, status, reason in rows:
        lines.append(f"| `{name}` | **{status}** -- {reason} |")
    if missing_patch:
        lines += ["", f"Patched hooks with no documentation: {', '.join(missing_patch)}"]
    OUT.write_text("\n".join(lines) + "\n", encoding="utf-8")

    print(f"{len(rows)} hooks: " + ", ".join(f"{k} {v}" for k, v in sorted(counts.items())))
    print(f"wrote {OUT.name}")
    if "--check" in argv:
        unclassified = [n for n, s, _ in rows if s == "unclassified"]
        if unclassified:
            print("unclassified: " + ", ".join(unclassified))
            return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
