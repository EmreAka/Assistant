# Tools and Actions

Contents:
1. Tool-calling vs. text-parsed actions
2. What deserves to be a tool
3. Environment shaping
4. Tool result contracts
5. Sandbox and permission policy
6. Skills as progressive disclosure

---

## 1. Tool-calling vs. text-parsed actions

mini-swe-agent supports both and has **changed its recommendation**, which is
worth knowing precisely because the older position is widely quoted:

```python
"""Parse actions & format observations without toolcalls.
This was the method used for mini-swe-agent v1.0 and the original SWE-agent.
As of mini-swe-agent v2.0, we strongly recommend to use toolcalls instead.
"""
```

The current default configs use a `bash` tool with `parallel_tool_calls: true`.
So the famous "no tool calling at all" property was a v1.0 design that the
authors have since moved away from, while keeping bash as the *only* tool.

Decide deliberately:

**Use provider tool-calling when** (the default): you control which models run,
you want structured arguments and provider-side validation, you want parallel
independent calls in one turn, or you want the provider's own training on tool
use working for you.

**Use text-parsed actions when**: you must support models without a tool-calling
API (base models, some local models); you are benchmarking models against each
other and don't want each provider's tool-call implementation as a confound; or
you are collecting RL/fine-tuning data and want the trajectory to be plain text
with no schema to serialize.

Both codebases converge on the deeper point, which is orthogonal to the wire
format: **the bash tool alone covers almost everything.** Every CLI on the box is
a capability you didn't have to define, document, or maintain. `gh pr create`
already exists.

## 2. What deserves to be a tool

Ask which of two things a proposed tool is:

- **A capability the model cannot otherwise reach** — your database, your
  internal API, a browser, a sandboxed filesystem in another process. These
  should be real tools with typed schemas and validation.
- **An interface making the model's life easier** — a file viewer, a search
  wrapper, a "run tests" convenience over `pytest`. These were essential in 2024
  and are mostly obsolete now. Each costs tokens in every request and adds a
  failure mode.

The SWE-agent team's own trajectory is the evidence: their 2024 paper argued the
Agent-Computer Interface was the dominant lever, and they built a windowed file
viewer, a lint-checked editor, and scoped search. mini-swe-agent removed all of
it and scores >74% on SWE-bench verified. The scaffolding was compensating for
model weakness that no longer exists.

Where a dedicated tool still earns its place over raw bash:

- **File editing at scale.** A `str_replace`-style tool with exact-match
  verification catches a class of silent corruption that `sed` does not. Worth it
  for long files.
- **Anything needing structured validation before side effects** — payments,
  deletions, writes to production.
- **Anything needing separate permissioning.** Bash is a single all-or-nothing
  capability; you cannot grant "read files" while denying "network access" if
  everything goes through one bash tool.

## 3. Environment shaping

The cheapest quality win in this entire skill. Configure the environment so
programs produce model-friendly output:

```yaml
environment:
  cwd: "/testbed"
  timeout: 60
  interpreter: ["bash", "-c"]
  env:
    PAGER: cat
    MANPAGER: cat
    LESS: -R
    PIP_PROGRESS_BAR: "off"
    TQDM_DISABLE: "1"
    # bash -c is non-login so it won't source ~/.bashrc; point BASH_ENV at it so the
    # image's `conda activate testbed` runs (else commands use the base env).
    BASH_ENV: /root/.bashrc
```

- `PAGER`/`MANPAGER=cat` — otherwise `git log` or `man` blocks forever waiting
  for a keypress that will never come. This single line prevents a whole category
  of hangs.
- `LESS=-R` — belt and braces for anything that reaches `less` anyway.
- `PIP_PROGRESS_BAR=off`, `TQDM_DISABLE=1` — progress bars emit thousands of
  carriage-return-separated redraws. Pure token waste, and they blow the output
  cap on their own.
- `BASH_ENV` — the comment in the source captures a real bug: `bash -c` is
  non-login and non-interactive, so it does not read `.bashrc`, so the image's
  environment activation never runs, so every command silently uses the wrong
  Python. Symptom: "works in my shell, fails in the agent."

Before writing a tool to clean up output, check whether an environment variable
already does it.

## 4. Tool result contracts

Whatever the tool, the result should be predictable in shape. Minimum:

- A **status/return code**, structurally separate from the output text.
- The **output**, escaped correctly for the observation format.
- **Exception info** when the tool itself failed, distinct from the tool
  succeeding and the command inside it failing. These are different situations
  and the model must be able to tell them apart.
- On truncation, a **pointer to the full result** (`Full output: <path>`).

For edit tools, return a **diff** rather than the resulting file:

```typescript
export type EditToolDetails =
  | { changed: false }
  | { changed: true; diff: string; patch: string; firstChangedLine?: number };
```

Note `changed: false` as an explicit state. An edit that matched nothing is not
an error and not a success — it means the model's assumption about the file was
wrong, and it needs to know that specifically rather than inferring it from an
unchanged file.

## 5. Sandbox and permission policy

Prompt instructions are not a security boundary. Enforce in the environment.

OpenClaw resolves tool permissions across three scopes with **traceable
provenance** — each resolved rule records where it came from:

```typescript
function buildSource(params: { scope: "agent" | "global" | "default"; key: string }): SandboxToolPolicySource {
  return { source: params.scope, key: params.key };
}
```

so a decision reports `agents.entries.*.tools.sandbox.tools.allow` rather than
just "denied". Without provenance, debugging a permission problem in a layered
config is guesswork.

The shape is `allow` / `alsoAllow` / `deny`, glob-matched:

- `allow` — replaces the default allowlist entirely.
- `alsoAllow` — extends it. The distinction matters; without `alsoAllow`, adding
  one tool means restating every default and silently drifting from upstream
  defaults as they change.
- `deny` — always wins.

For blast radius, container isolation with explicit mounts is the workable
default: a read-only skills mount, an explicit workspace mount, a network mode
you chose deliberately, and sanitized environment variables so host secrets don't
leak into the sandbox by accident.

## 6. Skills as progressive disclosure

Both codebases use the same trick for large bodies of instructions: expose only
name + description + location up front, and let the agent read the full file when
relevant.

```typescript
const lines = [
  "The following skills provide specialized instructions for specific tasks.",
  "Use the read tool to load a skill's file when the task matches its description.",
  "If a skill's <version> differs from a previous turn, re-read its SKILL.md before using it.",
  "When a skill file references a relative path, resolve it against the skill directory",
  "<available_skills>",
];
for (const skill of skills) {
  lines.push("  <skill>");
  lines.push(`    <name>${escapeXml(skill.name)}</name>`);
  lines.push(`    <description>${escapeXml(skill.description)}</description>`);
  lines.push(`    <location>${escapeXml(skill.filePath)}</location>`);
  if (skill.promptVersion) lines.push(`    <version>${escapeXml(skill.promptVersion)}</version>`);
  lines.push("  </skill>");
}
```

Three details:

- **`<version>` is a content hash**, and the prompt instructs re-reading when it
  changes. This solves cache staleness for free — edit a skill mid-session and
  the agent picks it up without a restart.
- **Explicit relative-path resolution guidance.** Agents habitually resolve paths
  against the working directory instead of the skill directory. Say it once here
  rather than debugging it forever.
- **`escapeXml` on every interpolated field.** A skill description containing
  `<` would otherwise break the block structure — the same concern as
  `security.md`, applied to your own content.

Use this pattern for anything large and conditionally relevant: house style
guides, API references, runbooks, domain rules. It scales to hundreds of
documents at a cost of roughly one line each in the always-loaded prompt.
