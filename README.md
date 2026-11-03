# OBC evaluation

The tool that runs the OBC evaluation study, and keeps a record of it that can be published as evidence. The study asks whether an AI answers financial questions more accurately, with a better audit trail, through OBC's MCP server than through a plain Postgres MCP server or an attached CSV. The plan is `evaluation/EVALUATION_PLAN.md` in OBC.SimulationDataGenerator.

The tool:
- holds the frozen inputs (questions, report requests, skills) as one hashed **bundle**;
- creates the **run matrix** for every model: each task in each setup, 145 runs per model;
- tracks the **checklist** per data size (load, close periods, data check, baseline runs, change set, history runs, export) and unlocks runs only when their phase is ready;
- runs each model **one by one in a wizard** (chat apps such as ChatGPT, Gemini or Claude.ai) or with the **agent runner** inside the app (Claude Code, Copilot CLI or any CLI agent);
- saves every attempt exactly: the prompt and reply with their SHA-256, the model version, the time and, for agents, tokens and cost;
- **exports** everything as an evidence package for the public repo, and **imports** a teammate's package, so several people can run different models and end up with one record.

Answers stay sealed. Neither the wizard nor the agent runner shows them. Only the answer key's SHA-256 is shown, so it can be published later and checked against what was fixed before the runs.

## Layout

| Path | What it is |
|---|---|
| `bundle/` | The frozen inputs: `bundle.json` (tasks, matrix, checklist, data-set hashes), `skills/` (the exact skill texts) and `answer-key.json` (sealed). |
| `tools/build_bundle.py` | Builds the bundle from OBC.SimulationDataGenerator. Byte-identical on rerun. |
| `src/OBC.Evaluation.Core` | Bundle loading, the prompt builder, the reply checks, and the agent launcher and output readers. |
| `src/OBC.Evaluation.Web` | The app: dashboard, checklist, models, run wizard, agent runner, runs, export and import. SQLite in `state/`. |
| `docs/AUTOMATION_DESIGN.md` | How the in-app agent runner uses model profiles. |
| `tests/` | Unit and end-to-end tests. |

## Install

You need:
- the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0);
- git, with access to this repo and to OBC.SimulationDataGenerator;
- for the agent flow, the CLI tools you will run (Codex CLI, Claude Code, or Copilot CLI), signed in;
- for the OBC and plain setups, access to an OBC org and a Postgres database, each with its MCP server.

Python 3 is only needed to rebuild the bundle. It uses no packages.

Clone both repos into the same folder. The app reads the data files from the sibling repo.

```bash
git clone https://github.com/OpenBookChain/OBC.Evaluation.git
```

```bash
git clone https://github.com/OpenBookChain/OBC.SimulationDataGenerator.git
```

Then start the app from `OBC.Evaluation`, with your name, so your exports say who made them:

```bash
dotnet run --project src/OBC.Evaluation.Web -- --Evaluation:Operator=YourName
```

Open http://localhost:5234. The database and its folder are created on first start. Check that the setup is sound:

```bash
dotnet test
```

Stop the app first: while it runs, it locks the files the build must replace.

### Settings

These are under `Evaluation` in `src/OBC.Evaluation.Web/appsettings.json`. Paths are relative to the repo root.

| Setting | Default | What it is |
|---|---|---|
| `BundlePath` | `bundle` | The frozen inputs. |
| `DatabasePath` | `state/evaluation.db` | The study's record on this machine. |
| `DataRoot` | `../OBC.SimulationDataGenerator/evaluation/data` | The data sets. |
| `Operator` | none | Your name, shown on your exports and on your log entries in a teammate's database. |

### Backups

`state/` is the study's record, and it isn't in git. The database runs in write-ahead mode, so recent results can sit in `evaluation.db-wal`, and a copy of `evaluation.db` alone can miss them. Stop the app and copy the whole `state/` folder, or keep the latest evidence export: it holds every result, and it can be imported into an empty database.

### The bundle

The bundle is committed, so a normal install doesn't build it. One person rebuilds it, only before the first run, and commits it:

```bash
python tools/build_bundle.py --source ../OBC.SimulationDataGenerator/evaluation --qa-skill <path to financial-data-answering SKILL.md>
```

Every attempt records the bundle's SHA-256, and a rebuilt bundle with different content makes those results incomparable.

## One prompt for both flows

A run's prompt is the skill text, then `## Request` and the question or report request. For an audit it adds the preparer's REPORT and WORKING PAPERS blocks, and for the CSV setup a `Data file:` line. The wizard shows this text to paste, and the agent runner sends the same text to the agent. So a chat-app run and an agent run of the same task get the same words, and the prompt's SHA-256 proves it.

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

The runner is part of the app. Add or edit an agent-mode model on the **Models** page and choose its CLI. The model profile stores the CLI command and setup-specific configuration; no separate runner config or API key is needed. Sign in to the CLI using its normal account flow.

On the **Agent runner** page, choose a model, size, setup, run limit and optional cost cap. **Check setup** probes the configured setups. **Start**, **Pause after this run** and **Stop now** control the session, with progress and results shown on the page.

Each model profile stores the exact model version and launch settings for OBC, plain Postgres and CSV. Codex profiles use the OBC MCP endpoint, the plain Postgres MCP endpoint, or no MCP for CSV. Claude profiles receive the corresponding per-setup MCP config. Codex JSONL output provides the reply, turns, tool calls and token usage; per-run USD cost is unavailable from Codex CLI output.

Each run is a **fresh agent process** in an empty folder with only that setup's data source. The CSV setup gets only the CSV, checked against its hash. One process per run is deliberate: an agent that answers many questions in one conversation sees its earlier questions and answers, which a manual run never does.

A session stops when nothing is ready (for example before the change set, when you tick the checklist), after 3 failed runs in a row, at the optional cost cap, or when you stop it. A failed or stopped run is saved as an abandoned attempt with the reason and offered again. A run is also thrown away if the tool reports it loaded a skill not listed in `allowSkills`.

What is recorded depends on the tool. Claude Code reports tokens, cost and turns. Copilot CLI reports the model it used, MCP servers, skills and premium requests, but no token counts. Time is always measured.

## Evidence

The dashboard's **Export evidence** downloads a zip with:
- the bundle as run;
- the models and the checklist with its evidence;
- every run and attempt: prompt, reply, raw agent output and metadata;
- the planted-error copies and the event log;
- `manifest.json` with the SHA-256 of every file.

The answer key isn't in the export. Publish it with the export once all runs are done.

## Working as a team

Each person runs the app on their own machine with their own database, and owns the models of one vendor. The evidence export is also the sync file: you share your zip, and your teammates import it on the dashboard under **Team sync**. There is no server to run.

### Set up once

1. **Same code and bundle.** Everyone pulls the same commit, and nobody rebuilds the bundle on their own. The sidebar shows the bundle's hash; compare it. An import from a different bundle is refused.
2. **Split the models.** Each model has one owner, and only the owner adds and runs it:

   | Person | Vendor | Models |
   |---|---|---|
   | A | Anthropic | e.g. `claude-opus-5-5`, in Claude Code and Claude.ai |
   | B | OpenAI | e.g. the exact GPT version, in ChatGPT |
   | C | Google | e.g. the exact Gemini version, in the Gemini app |

3. **One OBC org and one Postgres database per person.** Each person does T1–T3, T5 and T6 on their own data and ticks their own checklist. Don't share an org: T5 applies the change set, and every T4 baseline run has to be saved before it, so one person's T5 would change the data under everyone else's baseline runs.
4. **A private shared folder** for the zips, such as a Teams, OneDrive or Drive folder. Don't commit them here: they hold every prompt and reply.

### Sync

Do this at the end of each working session, and whenever you want to see where the team stands.

1. **Export:** on the dashboard, press **Export evidence**. Put the zip in the shared folder. A new zip replaces your earlier one, since it holds everything.
2. **Import:** under **Team sync**, choose each teammate's latest zip.
3. **See their progress:** a teammate's model arrives inactive, so it stays out of your wizard and runner. Tick **Active** on the Models page to see it on the dashboard. Don't run it.

Any order works, and so does importing the same file twice: it adds no results, only a line in the event log.

### What an import does

The ids differ between machines, so rows are matched on what they are:

| Row | Matched on |
|---|---|
| Model | name, exact version and app |
| Run | model, task, setup and repeat |
| Attempt | run, start time and prompt hash |

- **Results are only added.** An attempt already in your database is left alone. An audit's link to its preparer and a planted copy's link to its source are re-pointed at the ids on your machine.
- **Bad files are refused whole.** If a file doesn't match the manifest's hash, or the bundle differs, nothing is written.
- **The checklist isn't applied.** A teammate's T1 tick says their data is loaded, not yours, so it must not unlock your runs. Their ticks and evidence arrive as log entries labelled with their name.
- **Open attempts wait.** An attempt still in progress in the export is left out, and arrives with a later export.
- **Skips travel one way.** A run skipped on another machine becomes skipped here, and un-skipping it there doesn't undo that. Un-skip it here too.

### If the same run was done twice

If two people run the same cell of the same model, the import keeps both attempts and lists the run under **Run on both machines**. The attempt with the higher number counts, as with any retry, so this is how the scored result can change. Agree which attempt to use, and retry if needed. A different name, version or app makes a different model, so agree on the exact spelling before anyone starts.

### The final record

One person imports everyone's final zips and makes the export to publish. Keep each person's final zip beside it: the merged export's `checklist.json` shows only that person's own ticks, while the other two appear in its `events.json`.

The import's hash check catches damaged or accidentally changed files. It doesn't prove who made a zip, because anyone who edits a file can also edit the manifest. For published evidence, record each final zip's SHA-256 when it is shared.

### Keep the answers sealed

`bundle/answer-key.json` and the `answers.json` and `expected.json` files in OBC.SimulationDataGenerator are in everyone's clone. The app never shows them, so sealing depends on people too: nobody opens them until all runs are in.

## Not built yet

- Scoring: the plan's tolerance and verdict rules, run against the sealed answer key, once all runs are in.
- Importing the change-set result log as evidence for family D.

## Troubleshooting

| Problem | Cause and fix |
|---|---|
| `dotnet test` or a build says a file is locked by `OBC.Evaluation.Web` | The app is running. Stop it first. |
| An import says the bundle differs | Someone is on another commit or rebuilt the bundle. Get everyone on the same one. Results from different bundles can't be merged. |
| An import says the zip doesn't match its manifest | The file was changed or damaged after export. Ask for a fresh export. |
| A teammate's model isn't on the dashboard | It arrives inactive. Tick **Active** on the Models page. |
| Runs stay blocked after an import | Your checklist gates your runs, and an import doesn't tick it. Do the steps on your own data. |
| The app can't find a data file | OBC.SimulationDataGenerator isn't next to this repo. Clone it there, or set `DataRoot`. |
