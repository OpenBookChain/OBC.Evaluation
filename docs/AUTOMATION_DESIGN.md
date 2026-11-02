# Automated runs: design

Status: built for Claude Code and Copilot CLI. Antigravity (`agy`) is not built yet: it needs its headless flags and output format.

## Goal

Run the evaluation unattended with the CLI agents the study uses (Claude Code, Copilot CLI, Gemini CLI), started from the app, with no HTTP API or key. Each run must stay as clean and as comparable as a manual wizard run.

## What changes

The runner moves **inside the web app**. Today a separate process asks the app for the next run over `/api/agent/*` and posts the result back. Instead, a hosted background service calls `EvalService` directly (`NextAsync`, `StartAsync`, `SubmitAsync`, `AbandonAsync`), so there is one writer to the database and no key to manage.

| Today | After |
|---|---|
| `OBC.Evaluation.Runner` console app | `AgentRunService` (hosted service) in `OBC.Evaluation.Web` |
| `/api/agent/*` endpoints, `AgentApiKey` | removed |
| `runner.json` | `Evaluation:Agents` in `appsettings` (or `agents.json`, git-ignored) |
| progress in a terminal | **Agents** page: live state, log, Start / Pause / Stop |

`AgentProcess` (spawn, timeout, kill tree, output parsing) moves to `OBC.Evaluation.Core` unchanged in spirit. The prompt builder, reply checks and exports are untouched.

## What stays the same

- **One fresh process per run**, in an empty folder, so a run never sees another run's question or answer.
- **The same prompt as the manual flow**, with its SHA-256 recorded.
- **CSV setup:** the CSV is copied in and checked against the bundle hash first.
- **Every attempt is saved exactly:** prompt, reply, raw output, command line, time and measured usage.
- A failed or timed-out run is an **abandoned attempt** with the reason, and is retried as a new attempt, never overwritten.

## Agent adapters

Each CLI is a config entry: command, argument template, how the prompt is passed, how to read the result, and which environment variables isolate it. An adapter turns the CLI's output into `AgentResult` (reply, tokens, cost, turns, seconds).

| CLI | Headless call | Result and usage | Isolation | Status |
|---|---|---|---|---|
| Claude Code | `claude -p --output-format json --strict-mcp-config --mcp-config <file>` | one JSON object: `result`, `usage`, `total_cost_usd`, `num_turns` (parser exists) | empty `CLAUDE_CONFIG_DIR`, strict MCP config | `claude` isn't on this machine's PATH, so it needs a full path in config |
| Copilot CLI | `copilot -p <text> --allow-all-tools --no-ask-user --no-custom-instructions --output-format json --usage-output-file <file>` | JSONL events for the reply, plus a usage JSON file; memory is off in prompt mode | `--additional-mcp-config` for the setup's server, `--disable-builtin-mcps`, `--available-tools` / `--excluded-tools` to block web and GitHub tools | installed (1.0.99-2); **event format to confirm with a real run** |
| Gemini (`agy`, Antigravity CLI) | **unknown**: `agy` isn't on PATH here; its folder is `~/.gemini/antigravity-cli`. A classic `gemini` shim exists (`gemini.ps1`) | to find out from `agy --help` | to find out | **needs `agy --help` output or its install path** |

Rules for every adapter:
- The prompt goes in by stdin or a prompt file where the CLI allows it, so Windows quoting can't alter it.
- Web search, web fetch and memory are off. Only the setup's own data source is available (OBC MCP, plain Postgres MCP, or the CSV with file read and Python).
- The adapter records the exact command line and the CLI version in each attempt.

## Sign-in and isolation

Both MCP servers (OBC and plain Postgres) need a sign-in. Facts and assumptions, to be confirmed by the checks below:

- **Sign-ins are not shared between tools.** Each CLI keeps its own MCP tokens, and the desktop app's connectors are separate again. Plan on one sign-in per tool per MCP server.
- **Several processes of the same CLI can normally share one sign-in**, because they read the same stored tokens. Refreshing a token in parallel can race, which is another reason to run one at a time.
- **Isolation can remove the login.** An empty config directory (for example `CLAUDE_CONFIG_DIR`) also has no login and no MCP tokens. So each tool gets a dedicated **eval profile**: a directory with the sign-in and the two MCP servers and nothing else (no memory, skills, plugins or custom instructions). It is created once by hand, by running the CLI with that directory and signing in, then reused by every run.
- **Per-setup MCP config:** the `obc` setup lists only the OBC server, `plain` only the Postgres server, `csv` none. Run `plain` and `obc` with separate MCP configs so a model can't reach the other source.
- **Preflight check** before a session starts, per setup: a short probe run in the eval profile that confirms the right MCP tools are listed and callable, the CLI version, and the exact model. If the sign-in has expired, the session doesn't start and says which tool to sign in to again.
- **Mid-session expiry:** if a run fails because of authentication, the attempt is abandoned with that reason and the service pauses, rather than burning through the matrix with failures.

## Run control

- **Queue:** the next ready run for the chosen model, filtered by size and setup, using the existing `NextAsync` rules. Audits wait for their preparer; runs unlock only when the checklist phase is ready.
- **Start, Pause, Stop:** pause finishes the current run and stops. Stop kills the current process and abandons the attempt with the reason "stopped".
- **Pause points:** the service stops by itself when nothing is ready, for example before the change set (T7), and says what to tick on the checklist.
- **Safety:** a per-run timeout; stop after N failures in a row; an optional cost cap per session (from measured cost where the CLI reports it).
- **Concurrency:** one run at a time by default. An optional limit of one concurrent run per setup would be a later step; runs of the same setup stay serial.
- **Crash recovery:** on startup, open agent attempts left by a crash are abandoned (the app already has `AbandonStaleAgentAttemptsAsync`).
- **One model per session:** an agent model is its CLI plus model version plus reasoning setting. The Models page gets the adapter name and its settings.

## Usage figures

Agents report measured tokens, cost and time, so those are the figures the study uses. The model's self-reported usage in the prompt (added earlier) is redundant for agents. It stays only so manual and agent prompts remain identical. The wizard's optional manual token fields can stay or go.

## Open questions

1. **OBC sign-in:** does the OBC MCP server work in each CLI's eval profile after a one-time browser sign-in, and does that sign-in last through a long session? A token or API key would be better. This decides whether the `obc` setup can be automated.
2. **Plain Postgres MCP:** which server, and is it read-only? The prompt tells the agent the schema is the same as the OBC org's data.
3. **Model naming:** Copilot and Gemini can route to different models (`--model auto` in Copilot). The study should pin an exact model per run and refuse "auto".
4. **Claude Code on this machine:** the desktop app is present, but the `claude` CLI isn't on PATH. Install it, or point the config at its full path.
5. **Cost:** 145 runs per model per CLI. Do you want a default session cap?

## Build order

1. Move the process runner and parsers into Core; add the Copilot and Gemini adapters with tests on captured sample output.
2. Add `AgentRunService` and the Agents page (Start, Pause, Stop, live log).
3. Remove the HTTP API, the key and the standalone runner; update the README.
4. Run a 3-run trial per CLI, compare the saved records with a manual run of the same tasks, and check that prompts hash identically.
