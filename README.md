# OBC evaluation

The tool that runs the OBC evaluation study, and keeps a record of it that can be published as evidence. The study asks whether an AI answers financial questions more accurately, with a better audit trail, through OBC's MCP server than through a plain Postgres MCP server or an attached CSV. The plan is `evaluation/EVALUATION_PLAN.md` in OBC.SimulationDataGenerator.

The tool:
- holds the frozen inputs (questions, report requests, skills) as one hashed **bundle**;
- creates the **run matrix** for every model: each task in each setup, 145 runs per model;
- tracks the **checklist** per data size (load, close periods, data check, baseline runs, change set, history runs, export) and unlocks runs only when their phase is ready;
- runs each model **one by one in a wizard** (chat apps such as ChatGPT, Gemini or Claude.ai) or with the **agent runner** (Claude Code, Copilot CLI or any CLI agent);
- saves every attempt exactly: the prompt and reply with their SHA-256, the model version, the time and, for agents, tokens and cost;
- **exports** everything as an evidence package for the public repo.

Answers stay sealed. Neither the wizard nor the agent API shows them. Only the answer key's SHA-256 is shown, so it can be published later and checked against what was fixed before the runs.

## Layout

| Path | What it is |
|---|---|
| `bundle/` | The frozen inputs: `bundle.json` (tasks, matrix, checklist, data-set hashes), `skills/` (the exact skill texts) and `answer-key.json` (sealed). |
| `tools/build_bundle.py` | Builds the bundle from OBC.SimulationDataGenerator. Byte-identical on rerun. |
| `src/OBC.Evaluation.Core` | Bundle loading, the prompt builder and the reply checks, shared by the app and the runner. |
| `src/OBC.Evaluation.Web` | The app: dashboard, checklist, models, run wizard, runs, agent API and export. SQLite in `state/`. |
| `src/OBC.Evaluation.Runner` | The agent runner. |
| `tests/` | Unit and end-to-end tests. |

## Start

```bash
python tools/build_bundle.py --source ../OBC.SimulationDataGenerator/evaluation --qa-skill <path to financial-data-answering SKILL.md>
dotnet run --project src/OBC.Evaluation.Web
```

Open http://localhost:5234. The app expects OBC.SimulationDataGenerator next to this repo for the data files (`Evaluation:DataRoot` in `appsettings.json`). The database is `state/evaluation.db`. Back it up: it is the study's record.

Rebuild the bundle only before the first run. Every attempt records the bundle's SHA-256, and a rebuilt bundle with different content makes those results incomparable.

## One prompt for both flows

A run's prompt is the skill text, then `## Request` and the question or report request. For an audit it adds the preparer's REPORT and WORKING PAPERS blocks, and for the CSV setup a `Data file:` line. The wizard shows this text to paste, and the agent API sends the same text to the agent. So a chat-app run and an agent run of the same task get the same words, and the prompt's SHA-256 proves it.

## Manual flow (chat apps)

1. **Models:** add the model with its exact version and app, in manual mode. It gets its 145 runs.
2. **Checklist:** tick T1–T3 for a size once the org is loaded, the periods are closed and the data check passes. Write down the evidence.
3. **Run wizard:** choose the model, and filter by setup to keep one connector on.
4. For each run:
   1. Open a new chat with memory, history and web search off, and only that setup's connector (or attach the CSV from the link).
   2. Start the run, copy the prompt, and send it.
   3. Paste the final reply and save.

The reply's structure is checked straight away. A broken paste can be retried as a new attempt; nothing is overwritten. Audits unlock when their preparer run is saved. They get its report and working papers automatically, so nothing is copied between chats by hand.

## Agent flow

Set `Evaluation:AgentApiKey` (user secrets or `Evaluation__AgentApiKey`), add the model in agent mode, copy `runner.example.json` to `runner.json`, and run:

```bash
dotnet run --project src/OBC.Evaluation.Runner -- --config runner.json --max 1
```

The runner asks the app for the next run, starts a **fresh agent process** in an empty folder with only that setup's data source, and posts the reply back. It records the agent's token use, cost and turns too. The CSV setup gets only the CSV, checked against its hash.

One process per run is deliberate. An agent that answers many questions in one conversation sees its earlier questions and answers, which a manual run never does. Give the agent a clean profile: no memory, no user-level instructions or skills, and only the MCP server of that setup (for Claude Code, `--strict-mcp-config` and an empty `CLAUDE_CONFIG_DIR`).

## Evidence

The dashboard's **Export evidence** downloads a zip with:
- the bundle as run;
- the models and the checklist with its evidence;
- every run and attempt: prompt, reply, raw agent output and metadata;
- the planted-error copies and the event log;
- `manifest.json` with the SHA-256 of every file.

The answer key isn't in the export. Publish it with the export once all runs are done.

## Not built yet

- Scoring: the plan's tolerance and verdict rules, run against the sealed answer key, once all runs are in.
- Importing the change-set result log as evidence for family D.
