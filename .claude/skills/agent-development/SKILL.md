---
name: agent-development
description: "Battle-tested patterns for designing, building, debugging, and hardening LLM agent harnesses — the control loop, context and compaction strategy, tool/action contracts, termination protocols, prompt-injection defense, and sandbox permissions. Distilled from reading the mini-swe-agent and OpenClaw source. Use this skill whenever the user is building or fixing anything that runs an LLM in a loop with tools: agent harnesses, agentic workflows, coding agents, extraction/scraping pipelines with retry-and-repair, multi-agent orchestration, subagent spawning, MCP tool servers, or \"my agent keeps looping / blowing up context / losing track of the task\". Trigger it even when the user frames the work as a normal engineering task (\"add retries to my pipeline\", \"my LLM workflow costs too much\", \"how do I summarize old messages\") — if there is a model-in-a-loop, these patterns apply. Applies across Python, TypeScript, and .NET / Microsoft Agent Framework."
---

# Agent Development

Building a good agent is mostly not about picking a framework. It is about a
handful of decisions in the control loop, the shape of what you put in the
context window, and what happens when things go wrong. Frameworks paper over
these decisions; the failure modes come back anyway.

The patterns here are extracted from two codebases that sit at opposite ends of
the complexity range and, notably, converge on the same answers:

- **mini-swe-agent** (SWE-agent team) — ~100-line loop, deliberately minimal, scores >74% on SWE-bench verified.
- **OpenClaw** — a large production harness with sandboxing, subagents, compaction, plugins, and a real threat model.

Where they agree, treat it as settled. Where they differ, the difference is
usually scale, and this skill says which side of the line you are on.

## How to use this skill

Start with the **Core Model** and **Design Checklist** below — that is usually
enough to make the right call. Load a reference file when you are actually
writing that part:

| File | Read it when |
|---|---|
| `references/control-loop.md` | Writing or debugging the loop itself: stepping, exits, limits, format errors, trajectory persistence |
| `references/context-management.md` | Observations are too big, costs are climbing, or long sessions lose the plot; compaction and pruning |
| `references/tools-and-actions.md` | Deciding tool-calling vs. bash, writing tool contracts, sandbox and permission design |
| `references/prompting.md` | Writing system/instance templates, termination protocols, error-recovery nudges |
| `references/security.md` | Any untrusted text reaches the prompt: scraped pages, filenames, user config, tool output |
| `references/dotnet.md` | Implementing in C# / .NET, especially on Microsoft Agent Framework or `IChatClient` |

`scripts/minimal_agent.py` is a complete, runnable ~150-line agent implementing
every pattern in this skill. Read it as the canonical worked example, or fork it
as a starting point.

## Core model

An agent is this, and nothing more:

```
messages = [system, task]
loop:
    response = model(messages)          # may raise: limits, format
    actions  = parse(response)
    outputs  = [env.execute(a) for a in actions]
    messages += [response, *observations(outputs)]
    if terminated(messages): break
```

Everything else — memory, planning, multi-agent, tools — is a decision about
**what goes into `messages`** and **what `env.execute` is allowed to do**. Keep
that framing and most "agent architecture" questions collapse into one of those
two.

Three properties are worth defending hard, because both codebases pay real costs
to preserve them:

**1. The trajectory is the message list.** Not a parallel log, not a
reconstruction. If `messages` is exactly what you send to the model and exactly
what you persist, then debugging, replay, evals, and fine-tuning data are all the
same artifact. The moment you keep agent state outside `messages`, you have two
sources of truth and they will drift.

**2. Termination is a message, not a flag.** Append a message with
`role: "exit"` carrying `exit_status` and the final result, then break on it.
This means the reason for stopping is in the trajectory, visible in every replay,
rather than in a return code somebody has to correlate.

**3. Limits are checked before the model call, not after.** Step count, cost, and
wall-clock all get checked at the top of `query()`. Checking after means you have
already paid for the call that broke the budget, and in a runaway loop you pay
for it many times.

## Design checklist

Walk this before writing code, and again when an agent misbehaves. Most agent
bugs are a missing row here, not a bad prompt.

**Loop and termination**
- [ ] Explicit step, cost, and wall-clock limits, enforced before each model call
- [ ] A deliberate termination protocol the model can trigger (see `prompting.md`) — not "the model stops calling tools"
- [ ] Format errors counted *consecutively* and reset on any clean step, with an exit after N in a row
- [ ] Trajectory saved in a `finally` block, so a crash still leaves you the evidence

**Context**
- [ ] A hard per-observation size cap with head+tail elision, never a silent middle truncation
- [ ] Elision notices that tell the model *what to do differently* next time
- [ ] Full output written somewhere addressable, with the path in the observation
- [ ] A compaction strategy with required sections and a post-compaction quality check
- [ ] Prompt-cache breakpoints placed so the stable prefix stays stable

**Actions**
- [ ] Deliberate choice of tool-calling vs. text-parsed actions, with a reason
- [ ] Stateless-per-action or explicitly-stateful, stated in the prompt either way
- [ ] Environment configured so CLI output is model-friendly (`PAGER=cat`, progress bars off)
- [ ] Timeouts that kill the whole process group, not just the parent

**Safety**
- [ ] Every untrusted string wrapped as data, not instructions, before it hits the prompt
- [ ] Control and format characters stripped from interpolated values
- [ ] Allow/deny policy for tools with traceable provenance for each decision
- [ ] Blast radius bounded by a sandbox, not by prompt instructions

## Rules of thumb

**Prefer deleting scaffolding to adding it.** mini-swe-agent's whole thesis is
that the SWE-agent team's original custom tooling (file viewer, lint-checked
editor, scoped search) became unnecessary as models improved. Before you build a
tool, check whether the model can already do it with what it has. Every tool is
permanent surface area: schema, docs, tests, failure modes, tokens in every
request.

**Make the environment friendly instead of writing a tool.** Setting
`PAGER=cat`, `TQDM_DISABLE=1`, `PIP_PROGRESS_BAR=off` eliminates a whole class of
garbage observations for free. This is a much better return on effort than a tool
that wraps and cleans output.

**Fail loudly on template errors.** Render prompt templates with strict undefined
semantics (Jinja's `StrictUndefined`, or equivalent) so a typo'd variable raises
instead of silently rendering empty. A silently-empty prompt section is one of the
hardest agent bugs to spot, because the agent still *sort of* works.

**When the model does something dumb, fix the observation before the prompt.**
The system prompt is the last place to add a rule. Usually the model did
something dumb because the observation it saw was confusing, truncated in the
middle, or missing a return code. Fix the input; the behavior follows.

**Test the recovery paths, not the happy path.** The happy path works on the
first try with a good model. What determines whether your agent is usable is what
happens on a malformed response, a 10MB stdout, a timeout, a rate limit, and a
context overflow. Those are the tests worth writing.

## Common failure modes and their fix

| Symptom | Actual cause | Fix |
|---|---|---|
| Agent loops on the same command | Observation doesn't distinguish success from failure | Include return code in every observation; make failures visually distinct |
| Context explodes mid-run | One unbounded tool output | Per-observation cap with head/tail elision + full output to file |
| Agent "forgets" a constraint after a while | Compaction dropped it | Required `## Constraints/Rules` section + quality audit (`context-management.md`) |
| Costs spike unpredictably | No pre-call budget check | Enforce cost limit before `query()`, not after |
| Agent stops early, task half-done | Termination is implicit (model just stopped calling tools) | Explicit submission sentinel the model must emit |
| Malformed responses spiral | Format errors counted cumulatively, or not at all | Count consecutively, reset on clean step, exit at N |
| Works locally, breaks in container | Prompt assumed shell state that doesn't persist | State the statelessness in the prompt; set `BASH_ENV` if you need rc files |
| Agent follows instructions from a scraped page | Untrusted text interpolated as prompt authority | Wrap in a data block, strip control chars (`security.md`) |
| Summary loses file paths and IDs | Summarizer paraphrased identifiers | Extract identifiers pre-compaction, assert their survival post-compaction |

## Output expectations

When helping someone build an agent, produce **working code**, not an
architecture sketch. Both source codebases are small enough to read precisely
because they resisted abstraction; mirror that. Specifically:

- Show the actual loop, not a diagram of it.
- Name the limits with concrete default values, and say why those values.
- Include the failure branches inline. An agent snippet without error handling is
  misleading, because the error handling *is* the agent.
- When you deviate from a pattern here, say which one and why the user's case
  differs. These patterns are defaults with reasons, not laws.
