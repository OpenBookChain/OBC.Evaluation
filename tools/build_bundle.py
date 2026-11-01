"""Build the frozen evaluation bundle from the study's source files.

The bundle is everything a run needs and nothing it must not see:
    bundle/bundle.json       tasks, run matrix, checklist, data-set hashes, skill hashes
    bundle/skills/*.md       the exact skill texts pasted into each run
    bundle/answer-key.json   the answers, sealed: the app shows only its SHA-256 until scoring

Sources come from OBC.SimulationDataGenerator/evaluation (answer files, change sets, skills) and
the financial-data-answering skill (family B and the direct family D questions). The output is
byte-identical on rerun, so bundle.json's SHA-256 identifies exactly what was run.

Usage:
    python tools/build_bundle.py --source ../OBC.SimulationDataGenerator/evaluation --qa-skill path/to/SKILL.md
"""

import argparse
import hashlib
import json
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SETUPS = ["obc", "plain", "csv"]
QA_SKILL = "financial-data-answering"

# Run matrix (SCENARIOS_V2_DRAFT.md): family B at 500 and 5,000, with a CSV sample at 5,000;
# family C at both sizes, planted errors in C2 at 5,000; family D re-audits at both sizes and
# the direct questions at 5,000.
B_SIZES = [500, 5000]
CSV_SAMPLE_5000 = {"B9", "B1", "B7", "B12", "B13"}
C_SIZES = [500, 5000]
PLANTED_SIZE = 5000
D_DIRECT_SIZES = [5000]


def sha256_bytes(data):
    return hashlib.sha256(data).hexdigest()


def sha256_file(path):
    return sha256_bytes(Path(path).read_bytes())


def strip_frontmatter(text):
    """The paste text is the skill body; the frontmatter only matters to skill loaders."""
    if text.startswith("---\n"):
        end = text.index("\n---\n", 4)
        return text[end + 5:].lstrip("\n")
    return text


def write_text(path, text):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(text.encode("utf-8"))


def git_commit(path):
    try:
        return subprocess.check_output(["git", "-C", str(path), "rev-parse", "--short", "HEAD"], text=True).strip()
    except (OSError, subprocess.CalledProcessError):
        return None


def skills(source, qa_skill, out):
    found = {}
    for role in ("prepare", "audit"):
        for setup in SETUPS:
            name = f"{role}-{setup}"
            found[name] = (source / "skills" / "dist" / name / "SKILL.md").read_text(encoding="utf-8")
    found[QA_SKILL] = Path(qa_skill).read_text(encoding="utf-8")
    listed = []
    for name in sorted(found):
        body = strip_frontmatter(found[name].replace("\r\n", "\n"))
        file = f"skills/{name}.md"
        write_text(out / file, body)
        listed.append({"name": name, "file": file, "sha256": sha256_bytes(body.encode("utf-8"))})
    return listed


def task(**fields):
    fields.setdefault("heldOut", False)
    fields.setdefault("dependsOn", None)
    return fields


def family_b(size, answers, key):
    conventions = answers["family_b"]["conventions"].strip()
    tasks = []
    for template, instances in answers["family_b"]["templates"].items():
        for inst in instances:
            tid = f"{inst['id']}@{size}"
            setups = SETUPS if size == 500 or template in CSV_SAMPLE_5000 else ["obc", "plain"]
            tasks.append(task(
                id=tid, family="B", kind="qa", phase="T4", size=size, template=template,
                title=f"{inst['id']}: {inst['question'][:90]}",
                request=f"{conventions}\n\nQuestion: {inst['question']}",
                setups=setups, dataFile="base.csv", heldOut=not inst["id"].endswith(".1")))
            key[tid] = {"answer_type": inst["answer_type"], "params": inst["params"], "answer": inst["answer"]}
    return tasks


def family_c(size, answers, expected, key):
    tasks = []
    for rid, report in answers["family_c"]["reports"].items():
        prep = f"{rid}-prepare@{size}"
        tasks.append(task(
            id=prep, family="C", kind="prepare", phase="T4", size=size, template=rid,
            title=f"{rid} preparer: {report['title']}", request=report["request"],
            setups=SETUPS, dataFile="base.csv"))
        key[prep] = {"line_items": report["line_items"]}
        tasks.append(task(
            id=f"{rid}-audit@{size}", family="C", kind="audit", phase="T4", size=size, template=rid,
            title=f"{rid} auditor: {report['title']}", request="Audit the report and working papers below.",
            setups=SETUPS, dataFile="base.csv", dependsOn=prep))
        key[f"{rid}-audit@{size}"] = {"rule": "true verdict per claim: supported if the preparer's value matches the line item, else unsupported",
                                      "line_items": report["line_items"]}
        if expected:
            tasks.append(task(
                id=f"{rid}-reaudit@{size}", family="D", kind="reaudit", phase="T7", size=size, template=rid,
                title=f"{rid} re-audit after the change set: {report['title']}",
                request="Audit the report and working papers below.",
                setups=SETUPS, dataFile="post.csv", dependsOn=prep))
            key[f"{rid}-reaudit@{size}"] = {"rule": expected["family_c_reaudit"]["rule"],
                                            "changed_claims": expected["family_c_reaudit"]["changed_claims"].get(rid, []),
                                            "post_values": expected["family_c_reaudit"]["post_values"].get(rid)}
    planted = answers["family_c"]["planted_errors"]
    if size == PLANTED_SIZE:
        rid = planted["report"]
        tid = f"{rid}-planted-audit@{size}"
        tasks.append(task(
            id=tid, family="C", kind="planted-audit", phase="T4", size=size, template=rid,
            title=f"{rid} auditor on a copy with 3 planted errors",
            request="Audit the report and working papers below.",
            setups=SETUPS, dataFile="base.csv", dependsOn=f"{rid}-prepare@{size}",
            plantSpec=planted["errors"]))
        key[tid] = {"planted_errors": planted["errors"]}
    return tasks


def family_d(size, expected, key):
    tasks = []
    for qid, instances in expected["family_d"].items():
        for i, inst in enumerate(instances, start=1):
            tid = f"{qid}.{i}@{size}"
            tasks.append(task(
                id=tid, family="D", kind="qa", phase="T7", size=size, template=qid,
                title=f"{qid}.{i}: {inst['question'][:90]}", request=f"Question: {inst['question']}",
                setups=SETUPS, dataFile="post.csv", heldOut=i > 1))
            key[tid] = {k: v for k, v in inst.items() if k != "question"}
    return tasks


def checklist(size, answers, expected):
    dc = answers["data_check"]
    items = [
        ("T1", "Load the base data",
         f"Import base.csv into a new org (CSV import, process, approve, publish), sync FinAI, and load the same file "
         f"into plain Postgres. Expect {dc['journals']} journals and {dc['lines']} lines."),
        ("T2", "Close August 2025 to April 2026",
         "Apply close.zip on the change-set page, then sync FinAI. Expect 9 closed periods, version 1 each; May to July open."),
        ("T3", "Family A data check",
         f"In OBC, plain Postgres and the CSV: {dc['journals']} journals, {dc['lines']} lines, debits {dc['total_debits']:.2f}, "
         f"income {dc['total_income']:.2f}, receivables {dc['receivables_balance']:.2f}, payables {dc['payables_balance']:.2f}; "
         "get_sync_status shows dataComplete. Record the values."),
        ("T4", "Baseline runs", "Families B and C. Every run in the T4 matrix is saved before the change set."),
    ]
    if expected:
        post = expected["data_check_post"]
        items += [
            ("T5", "Apply the change set",
             "Apply changeset.zip with 'continue on errors', save the change-set result log, sync FinAI, and reload "
             "plain Postgres from post.csv. Step 7 must be refused."),
            ("T6", "Family A data check after the change set",
             f"{post['journals']} live journals, {post['lines']} lines, debits {post['total_debits']:.2f}, "
             f"income {post['total_income']:.2f}, {post['voided_journals']} voided journals."),
            ("T7", "Family D runs", "Re-audits of the T4 reports and the direct history questions."),
        ]
    items.append(("T8", "Score and export", "Unseal the answer key, score, and export the evidence."))
    return [{"id": f"{step}@{size}", "size": size, "step": step, "title": title, "details": details}
            for step, title, details in items]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", required=True, help="OBC.SimulationDataGenerator/evaluation")
    parser.add_argument("--qa-skill", required=True, help="financial-data-answering SKILL.md")
    parser.add_argument("--out", default=str(ROOT / "bundle"))
    args = parser.parse_args()
    source, out = Path(args.source).resolve(), Path(args.out)

    key, tasks, datasets, steps = {}, [], [], []
    for size in sorted(set(B_SIZES + C_SIZES)):
        folder = source / "data" / f"retailer-{size}"
        answers = json.loads((folder / "answers.json").read_text(encoding="utf-8"))
        expected_path = folder / "expected.json"
        expected = json.loads(expected_path.read_text(encoding="utf-8")) if expected_path.exists() else None
        files = {"base.csv": answers["sha256"]}
        if expected:
            files["post.csv"] = expected["post_sha256"]
            files["changeset.zip"] = expected["changeset_sha256"]
            files["close.zip"] = expected["close"]["sha256"]
        datasets.append({"size": size, "folder": f"retailer-{size}", "files": files})
        if size in B_SIZES:
            tasks += family_b(size, answers, key)
        if size in C_SIZES:
            tasks += family_c(size, answers, expected, key)
        if expected and size in D_DIRECT_SIZES:
            tasks += family_d(size, expected, key)
        steps += checklist(size, answers, expected)

    key_text = json.dumps({"tasks": key}, indent=2, sort_keys=True, ensure_ascii=False) + "\n"
    write_text(out / "answer-key.json", key_text)
    bundle = {
        "schema": 1,
        "name": "obc-evaluation-round-1",
        "source": {"repo": "OBC.SimulationDataGenerator", "commit": git_commit(source)},
        "setups": SETUPS,
        "datasets": datasets,
        "skills": skills(source, args.qa_skill, out),
        "qaSkill": QA_SKILL,
        "tasks": tasks,
        "checklist": steps,
        "answerKey": {"file": "answer-key.json", "sha256": sha256_bytes(key_text.encode("utf-8"))},
    }
    text = json.dumps(bundle, indent=2, ensure_ascii=False) + "\n"
    write_text(out / "bundle.json", text)
    runs = sum(len(t["setups"]) for t in tasks if not t["heldOut"])
    print(f"bundle: {len(tasks)} tasks, {runs} runs per model in the matrix, sha256 {sha256_bytes(text.encode('utf-8'))}")


if __name__ == "__main__":
    main()
