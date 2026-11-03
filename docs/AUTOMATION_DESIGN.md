# Agent runner

## Model profiles

An agent-mode model stores its CLI name and setup-specific launch configuration in `Models.AgentCli` and `Models.AgentConfigJson`. Each setup (`obc`, `plain`, or `csv`) defines the command, arguments, prompt transport, output format, environment overrides, and optional MCP config. The runner reads the selected model profile directly; there is no separate `agents.json` requirement.

Authentication stays with the CLI's normal local sign-in. The evaluation app does not need an OpenAI or Claude API key for CLI runs, and credentials are not copied into the database.

## Session flow

The Agent runner page selects a model and optional size, setup, run count, and cost cap. It can check the configured setups, start a session, pause after the current run, or stop immediately. Session progress and saved/failed counts update while each CLI runs.

For every task, the service:

1. Gets the next available run and opens an attempt using the normal evaluation prompt.
2. Creates a fresh temporary working directory. For CSV tasks, it copies only the hash-checked CSV into that directory. For connector tasks, the model profile supplies the setup-specific MCP configuration.
3. Starts a new CLI process for that one task. No conversation is shared between tasks.
4. Reads the selected CLI's output format and submits the final reply, duration, and available usage fields to the evaluation service.
5. Deletes the temporary working files. Failed starts and stopped sessions are recorded as abandoned attempts, so the run can be retried.

The output readers support Claude JSON/stream JSON, Copilot JSONL, Codex JSONL, and plain text. Codex CLI supplies token and turn usage, but not per-run USD cost. A cost cap therefore only stops sessions when the selected CLI reports cost.

## Operational safeguards

- The session uses one fresh process per task to prevent an agent from seeing earlier questions or answers.
- CSV files are checked against the bundle's recorded hash before they are copied to the agent's working directory.
- A run that uses an unapproved skill reported by its CLI is discarded.
- Connector setups must call a data tool before a successful result is saved.
- The session stops after three consecutive failed runs, on an explicit stop request, or when a supported cost cap is reached.
- If the app exits during a run, stale attempts are abandoned and the run becomes available again.
