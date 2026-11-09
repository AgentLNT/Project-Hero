"""Rebuild the stage-4 readout from native case metrics; no combat results are calculated here."""
from pathlib import Path
from collections import defaultdict, Counter
import csv
import hashlib
import json
import gzip
import statistics
import zipfile
import argparse
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parent
REPO = ROOT.parents[2]


def read_csv(path):
    with path.open(encoding="utf-8-sig", newline="") as source:
        return list(csv.DictReader(source))


def save_csv(name, rows):
    assert rows
    with (ROOT / name).open("w", encoding="utf-8-sig", newline="") as target:
        output = csv.DictWriter(target, fieldnames=list(rows[0]))
        output.writeheader()
        output.writerows(rows)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def main(corrected=False):
    global ROOT
    if corrected:
        ROOT = ROOT / "corrected-preview-v2"
    expected_cases, expected_pairs = (96, 16) if corrected else (304, 64)
    policy_version = "tactical-public-kernel-v2" if corrected else "tactical-public-kernel-v1"
    matrix = read_csv(ROOT / "matrix.csv")
    assert len(matrix) == expected_cases and len({s["case"] for s in matrix}) == expected_cases
    rows = []
    followups = []
    for planned in matrix:
        prefix = ROOT / "cases" / planned["case"]
        row, = read_csv(Path(str(prefix) + ".metrics.csv"))
        dto = json.loads(Path(str(prefix) + ".input.json").read_text(encoding="utf-8-sig"))
        assert row["case"] == planned["case"] and row["definitionHash"] == dto["definitionHash"]
        assert row["sourceFingerprint"] == dto["sourceFingerprint"]
        layouts = ["ProbeOpen", "ProbeWall", "HeavyQuickOpen", "HeavyQuickWall", "TeamSweepOpen", "TeamSweepWall", "HoldoutDiagonal", "HoldoutStaggered"]
        policies = ["Legacy", "SustainedOffense", "SeekInterrupt", "CautiousDefense"]
        assert layouts[dto["layout"]] == row["layout"] == planned["layout"]
        assert policies[dto["bluntPolicy"]] == row["bluntPolicy"] == planned["bluntPolicy"]
        assert policies[dto["opponentPolicy"]] == row["opponentPolicy"] == planned["opponentPolicy"]
        for key in ("recovery", "seed", "windowTicks", "maxTicks"):
            assert dto[key] == int(row[key]) == int(planned[key])
        for key in ("opponentFirst", "swapSpawns"):
            assert dto[key] == (row[key] == "True") == (planned[key] == "True")
        assert dto["sceneVersion"] == "recovery_policy_long_matches.1" and dto["policyVersion"] == policy_version
        assert 0 <= int(row["endTick"]) < int(row["maxTicks"])
        if row["outcome"] == "timeout": assert int(row["endTick"]) + 1 == int(row["maxTicks"])
        for suffix in (".heroReplay", ".plans.csv", ".decisions.csv.gz", ".candidates.csv.gz", ".events.txt.gz"):
            assert Path(str(prefix) + suffix).is_file(), (prefix, suffix)
        assert not Path(str(prefix) + ".failure.txt").exists()
        for side in ("a", "b"):
            value = lambda key: int(row[f"{side}_{key}"])
            assert value("HealthQ10") + value("DamageReceivedQ10") == (2 if row["team"] == "True" else 1) * 24576
            assert value("IncomeRequested") == value("IncomeCredited") + value("IncomeOverflow")
            assert value("IncomeCredited") + value("Refunded") - value("Reserved") - value("CycleExpired") - value("BattleEndAvailableCleared") == value("FinalAvailable")
            assert value("WindowBusyTicks") <= value("WindowTicks")
            other = "b" if side == "a" else "a"
            assert value("IncomeRequested") == 2 * int(row[f"{other}_DamageReceivedQ10"]) + 3 * value("DamageReceivedQ10")
            reservation_loss = value("Reserved") - value("Consumed") - value("Refunded") - value("FinalReserved")
            assert reservation_loss >= 0
            row[f"{side}_UnconsumedUnrefundedReservationLoss"] = str(reservation_loss)
            # The raw exporter counts every terminated attack before its expected impact,
            # including Legacy's manual deletion of not-yet-started drafts. Split phases here.
            row[f"{side}_CancelledAttackBeforeImpact"] = row[f"{side}_CancelledWindups"]
            for name in ("CancelledBeforeStart", "CancelledWindups", "ControlCancelledWindups", "KilledDuringWindup", "ManualCancelledAttacks",
                         "NoLegalOrdinaryDecisionTicks", "LegalButNoPositiveDecisionTicks"):
                row[f"{side}_{name}"] = "0"
        plans = read_csv(Path(str(prefix) + ".plans.csv"))
        assert row["outcome"] in {"victory", "defeat", "draw", "timeout"}
        for plan in plans:
            if plan["type"] != "1" or int(plan["terminated"]) < 0 or int(plan["terminated"]) >= int(plan["impact"]):
                continue
            side = "a" if ((int(plan["owner"]) % 2 == 1) != (row["opponentFirst"] == "True")) else "b"
            def increment(name):
                key = f"{side}_{name}"
                row[key] = str(int(row[key]) + 1)
            if int(plan["locked"]) < 0:
                increment("CancelledBeforeStart")
            else:
                increment("CancelledWindups")
                if plan["terminalReason"] == "ACTION_TERMINATION_INTERRUPTED_BY_CONTROL": increment("ControlCancelledWindups")
                if plan["terminalReason"] == "ACTION_TERMINATION_OWNER_DIED": increment("KilledDuringWindup")
            if plan["terminalReason"] == "ACTION_TERMINATION_CANCELLED_BY_COMMAND": increment("ManualCancelledAttacks")
        for plan in plans:
            if plan["type"] != "1" or plan["terminalReason"] != "ACTION_TERMINATION_INTERRUPTED_BY_CLASH":
                continue
            later = sorted((p for p in plans if p["owner"] == plan["owner"] and p["plan"] != plan["plan"] and p["type"] == "1"
                            and int(p["locked"]) >= 0 and int(p["start"]) >= int(plan["impact"])), key=lambda p: int(p["start"]))
            next_plan = later[0] if later else None
            if next_plan is not None:
                assert int(next_plan["start"]) >= int(plan["end"]), (row["case"], plan, next_plan)
            followups.append({"case": row["case"], "block": row["block"], "recovery": row["recovery"], "owner": plan["owner"],
                              "clashPlan": plan["plan"], "clashImpact": plan["impact"], "recoveryEnd": plan["end"],
                              "sourceWindow": plan["window"], "nextAttackStart": next_plan["start"] if next_plan else -1,
                              "nextScheduledImpact": next_plan["impact"] if next_plan else -1,
                              "nextExecutedImpact": next_plan["executedImpact"] if next_plan else -1,
                              "nextSourceWindow": next_plan["window"] if next_plan else -1,
                              "gapAfterRecoveryEnd": int(next_plan["start"]) - int(plan["end"]) if next_plan else -1})
        for side in ("a", "b"):
            assert int(row[f"{side}_CancelledBeforeStart"]) + int(row[f"{side}_CancelledWindups"]) == int(row[f"{side}_CancelledAttackBeforeImpact"])
        with gzip.open(Path(str(prefix) + ".decisions.csv.gz"), "rt", encoding="utf-8", newline="") as choices:
            for choice in csv.DictReader(choices):
                if choice["waitReason"] == "WAIT_NO_POSITIVE_CANDIDATE":
                    side = choice["side"].lower()
                    key = f"{side}_" + ("NoLegalOrdinaryDecisionTicks" if int(choice["eligible"]) == 0 else "LegalButNoPositiveDecisionTicks")
                    row[key] = str(int(row[key]) + 1)
        rows.append(row)
    assert len(list((ROOT / "cases").glob("*.input.json"))) == expected_cases
    assert len({r["sourceFingerprint"] for r in rows}) == 1
    frozen = json.loads((ROOT / "frozen-existing-logic.json").read_text(encoding="utf-8"))
    frozen_hash = sha("".join(f"{p}:{h}\n" for p, h in frozen.items()).encode())
    assert all(r["frozenLogicFingerprint"] == frozen_hash for r in rows)
    sources = sorted([*Path(REPO / "Assets/Scripts").rglob("*.cs"), *Path(REPO / "Assets/Tests").rglob("*.cs")], key=lambda p: p.relative_to(REPO).as_posix())
    source_hash = sha("".join(f"{p.relative_to(REPO).as_posix()}:{sha(p.read_bytes())}\n" for p in sources).encode())
    source_origin = "live workspace"
    if source_hash != rows[0]["sourceFingerprint"]:
        # Later policy corrections must not relabel the sealed V1 experiment.
        with zipfile.ZipFile(ROOT / "source-archive.zip") as archive:
            assert all(sha(archive.read(p)) == h for p, h in frozen.items())
            paths = sorted(p for p in archive.namelist() if p.endswith(".cs")
                           and p.startswith(("Assets/Scripts/", "Assets/Tests/")))
            source_hash = sha("".join(f"{p}:{sha(archive.read(p))}\n" for p in paths).encode())
        source_origin = f"sealed source-archive.zip ({policy_version}); current workspace has later changes"
    else:
        assert all(sha((REPO / p).read_bytes()) == h for p, h in frozen.items())
    assert all(r["sourceFingerprint"] == source_hash for r in rows)
    save_csv("summary.csv", rows)
    if followups:
        save_csv("post-clash-followups.csv", followups)

    groups = defaultdict(list)
    for row in rows:
        key = tuple(row[c] for c in ("layout", "bluntPolicy", "opponentPolicy", "seed", "windowTicks", "recovery"))
        groups[key].append(row)
    grouped = []
    for key, items in sorted(groups.items()):
        assert len(items) == 4
        outcomes = Counter(r["outcome"] for r in items)
        margins = [(int(r["a_HealthQ10"]) - int(r["b_HealthQ10"])) / 1024 for r in items]
        g = dict(zip(("layout", "bluntPolicy", "opponentPolicy", "seed", "windowTicks", "recovery"), key))
        g.update(n=4, aWins=outcomes["victory"], bWins=outcomes["defeat"], timeout=outcomes["timeout"], draw=outcomes["draw"],
                 meanHealthMargin=statistics.mean(margins), minHealthMargin=min(margins), maxHealthMargin=max(margins),
                 meanEndTick=statistics.mean(int(r["endTick"]) for r in items),
                 aAttackStarts=sum(int(r["a_AttackStarts"]) for r in items), bAttackStarts=sum(int(r["b_AttackStarts"]) for r in items),
                 aClashes=sum(int(r["a_Clashes"]) for r in items),
                 aPostClashLossPoints=sum(int(r["a_PostClashRecoveryLossQ10"]) for r in items) / 1024,
                 bPostClashLossPoints=sum(int(r["b_PostClashRecoveryLossQ10"]) for r in items) / 1024,
                 rejectedCommands=sum(int(r["commandRejections"]) for r in items))
        grouped.append(g)
    save_csv("by-scenario-policy.csv", grouped)

    blocks = defaultdict(dict)
    for r in rows:
        blocks[r["block"]][int(r["recovery"])] = r
    pairs = []
    delta_fields = ["AttackStarts", "AttackImpacts", "CancelledWindups", "ControlCancelledWindups", "CancelledBeforeStart", "Clashes", "PostClashRecoveryHits", "PostClashRecoveryLossQ10",
                    "IncomeCredited", "IncomeOverflow", "Consumed", "CycleExpired", "WindowBusyTicks", "WindowNoPositiveTicks", "GuardStarts", "MoveStarts"]
    for block, variants in sorted(blocks.items()):
        a, b = variants[31], variants[40]
        p = {key: a[key] for key in ("block", "layout", "holdout", "bluntPolicy", "opponentPolicy", "opponentFirst", "swapSpawns", "seed", "windowTicks")}
        p.update(outcome31=a["outcome"], outcome40=b["outcome"], outcomeChanged=a["outcome"] != b["outcome"],
                 aHealth31=int(a["a_HealthQ10"]) / 1024, aHealth40=int(b["a_HealthQ10"]) / 1024,
                 bHealth31=int(a["b_HealthQ10"]) / 1024, bHealth40=int(b["b_HealthQ10"]) / 1024,
                 deltaHealthMarginPoints=(int(b["a_HealthQ10"]) - int(b["b_HealthQ10"]) - int(a["a_HealthQ10"]) + int(a["b_HealthQ10"])) / 1024,
                 endTick31=int(a["endTick"]), endTick40=int(b["endTick"]), deltaEndTick=int(b["endTick"]) - int(a["endTick"]),
                 rejected31=int(a["commandRejections"]), rejected40=int(b["commandRejections"]))
        for side in ("a", "b"):
            for f in delta_fields:
                p[f"delta_{side}_{f}"] = int(b[f"{side}_{f}"]) - int(a[f"{side}_{f}"])
        pairs.append(p)
    assert len(pairs) == expected_pairs
    save_csv("paired-31-vs-40.csv", pairs)

    observations = {"cases": len(rows), "recordedTicks": sum(int(r["endTick"]) + 1 for r in rows),
                    "matched31vs40Blocks": len(pairs), "outcomeChanges": sum(p["outcomeChanged"] for p in pairs),
                    "equalHealthMarginBlocks": sum(p["deltaHealthMarginPoints"] == 0 for p in pairs),
                    "healthMarginImprovesAt40": sum(p["deltaHealthMarginPoints"] > 0 for p in pairs),
                    "healthMarginWorsensAt40": sum(p["deltaHealthMarginPoints"] < 0 for p in pairs),
                    "sameOutcomeAndHealthButDifferentEndTick": sum(not p["outcomeChanged"] and p["deltaHealthMarginPoints"] == 0 and p["deltaEndTick"] != 0 for p in pairs),
                    "frozenExistingLogicFiles": len(frozen), "frozenLogicFingerprint": frozen_hash, "sourceFingerprint": source_hash,
                    "verifiedSourceOrigin": source_origin, "policyVersion": policy_version,
                    "commandRejectionsByOpponentPolicy": {}, "endResultsByLayout": {}}
    observations["pairedStrata"] = {}
    def stratum(pair):
        if pair["holdout"] == "True": return "holdout"
        if pair["opponentPolicy"] == "Legacy": return "legacyOpponent"
        if pair["windowTicks"] != "180": return "windowSensitivity"
        return "core"
    for kind in ("core", "legacyOpponent", "holdout", "windowSensitivity"):
        subset = [p for p in pairs if stratum(p) == kind]
        observations["pairedStrata"][kind] = {"n": len(subset), "outcomeChanges": sum(p["outcomeChanged"] for p in subset),
            "healthMarginImprovesAt40": sum(p["deltaHealthMarginPoints"] > 0 for p in subset),
            "healthMarginWorsensAt40": sum(p["deltaHealthMarginPoints"] < 0 for p in subset),
            "equalHealthMargin": sum(p["deltaHealthMarginPoints"] == 0 for p in subset)}
    for policy in sorted({r["opponentPolicy"] for r in rows}):
        subset = [r for r in rows if r["opponentPolicy"] == policy]
        observations["commandRejectionsByOpponentPolicy"][policy] = {"n": len(subset), "rejections": sum(int(r["commandRejections"]) for r in subset)}
    for layout in sorted({r["layout"] for r in rows}):
        subset = [r for r in rows if r["layout"] == layout]
        observations["endResultsByLayout"][layout] = dict(Counter(r["outcome"] for r in subset))
    (ROOT / "analysis.json").write_text(json.dumps(observations, ensure_ascii=False, indent=2), encoding="utf-8")

    lines = [f"# 阶段四数据读出：{policy_version}", "", "以下来自原生规则决策器对战汇总；每组均包含独立交换的双先手、双出生位置。HP 差为钝击方减对手，单位为生命点。2026-11-04 用户纠正后的解释：敌方命中自身之后我方尽早命中，沿用普通交互结果；不以目标动作阶段设独立成功门槛。现有候选尚无该专门时机，本批不构成动作卡预设验收。胜负和超时必须结合具体策略及地图阅读。", "",
             "## 匹配的 31/40 对照", "", f"共 {len(pairs)} 个固定输入比较块；其中 {observations['outcomeChanges']} 块终局类别改变，{observations['equalHealthMarginBlocks']} 块最终生命差相同。完整逐块变化见 paired-31-vs-40.csv。", "",
             "## 按场景与策略分组", "", "| 场景 | 钝击策略 / 对手策略 | 种子 / 窗口 | 后摇 | 钝击胜 / 对手胜 / 超时 | 平均 HP 差（范围） | 结束 Tick 均值 |", "|---|---|---|---:|---|---|---:|"]
    for g in grouped:
        lines.append(f"| {g['layout']} | {g['bluntPolicy']} / {g['opponentPolicy']} | {g['seed']} / {g['windowTicks']} | {g['recovery']} | {g['aWins']} / {g['bWins']} / {g['timeout']} | {g['meanHealthMargin']:g} ({g['minHealthMargin']:g}～{g['maxHealthMargin']:g}) | {g['meanEndTick']:g} |")
    lines += ["", "## 有变化的匹配块", "", "下表保留具体先手、站位及种子，便于复现。正的生命差变化表示 40 Tick 时钝击方最终生命差更高；不自动解释为更平衡。", "",
              "| 输入块 | 31 → 40 结果 | HP 差变化 | 结束 Tick 变化 |", "|---|---|---:|---:|"]
    for p in pairs:
        if p["outcomeChanged"] or p["deltaHealthMarginPoints"] != 0 or p["deltaEndTick"] != 0:
            lines.append(f"| {p['block']} | {p['outcome31']} → {p['outcome40']} | {p['deltaHealthMarginPoints']:g} | {p['deltaEndTick']} |")
    lines += ["", "## 统计口径", "", "- 真实伤害取提交前后生命变化，名义直接伤害另列；后摇生命损失是总生命损失的子集，不再加到总收益。", "- 资源请求量、实际入账、溢出、预留、消费、退款、周期过期及战斗结束清空分别记录，逐场核对生命与可用余额守恒。", "- 原始导出中的目标动作阶段字段只标记普通命中发生时的状态，不形成特殊收益、独立命中类别或成功门槛；按真实接触、伤害及应对评价。", "- 连续拼刀为同一单位连续攻击效果中以拼刀结算的最长序列；非拼刀攻击效果会清零。", "- 汇总中的 CancelledAttackBeforeImpact 保留原生导出的命中前取消总数；根据 plans.csv 的 Locked 边界，另分 CancelledBeforeStart 与 CancelledWindups，控制及死亡原因单列。不能把旧 AI 撤销草稿当成控制收益。", "- 新策略的 WAIT_NO_POSITIVE_CANDIDATE 另按是否有合法候选分组；Legacy 没有新策略评分明细，其 EligibleCandidates/ResourceBlockedDecisionTicks 的零值不能解释为无合法候选或无资源问题。旧策略拒绝以权威事件为准。", "- 原始决策和全部候选见压缩 CSV；事件和回放保存完整时序。窗口占用包含 Guard、移动和后摇，不能把全部等待解释为空转。", "- 本轮只解释其登记的载荷/地图/策略组合；V1 为封存的旧预演基线，V2 为修复预演后的 96 场回归，不能合并成同一策略版本的平衡样本。未覆盖所有策略×地图组合，也没有人工试玩。", ""]
    (ROOT / "analysis-report.md").write_text("\n".join(lines), encoding="utf-8")
    print(json.dumps(observations, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--corrected", action="store_true")
    main(parser.parse_args().corrected)
