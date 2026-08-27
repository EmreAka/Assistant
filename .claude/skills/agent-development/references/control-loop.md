# The Control Loop

Contents:
1. The loop
2. Exit as a message
3. Limits and where to check them
4. Format errors and consecutive-failure counting
5. Statelessness vs. persistent sessions
6. Timeouts and process groups
7. Trajectory persistence
8. Subagents

---

## 1. The loop

The whole agent, from mini-swe-agent's `DefaultAgent.run()`:

```python
def run(self, task: str = "", **kwargs) -> dict:
    self.messages = []
    self.add_messages(
        format_message(role="system", content=render(self.config.system_template)),
        format_message(role="user",   content=render(self.config.instance_template)),
    )
    while True:
        try:
            self.step()
            self.n_consecutive_format_errors = 0     # reset on any clean step
        except FormatError as e:
            self.cost += e.messages[0].get("extra", {}).get("cost", 0.0)
            self.n_consecutive_format_errors += 1
            if 0 < self.config.max_consecutive_format_errors <= self.n_consecutive_format_errors:
                self.add_messages(*e.messages, exit_message("RepeatedFormatError"))
            else:
                self.add_messages(*e.messages)
        except InterruptAgentFlow as e:
            self.add_messages(*e.messages)
        except Exception as e:
            self.handle_uncaught_exception(e)
            raise
        finally:
            self.save(self.config.output_path)
        if self.messages[-1].get("role") == "exit":
            break
    return self.messages[-1].get("extra", {})
```

Read the control flow carefully — the structure carries several decisions:

- **Recoverable problems are converted into messages, not exceptions that escape.**
  `FormatError` and `InterruptAgentFlow` both carry the messages that should be
  appended so the model can see what went wrong and try again. The exception is
  a transport for a message, not an error signal.
- **Unrecoverable problems still write a message before re-raising.** The
  uncaught-exception handler appends an `exit` message with the traceback, *then*
  re-raises. You get both a clean trajectory and a real stack trace.
- **`finally: save()`** — the trajectory is persisted after every step including
  the one that crashed. This is what makes post-mortem debugging possible.
- **The loop condition is a property of the messages**, checked after the try
  block, not a boolean the body sets.

`step()` is two lines: `execute_actions(query())`. Keeping these separate lets
you override either half — subclass and wrap `query()` to add a hook, or
`execute_actions()` to add approval gating, without touching the loop.

## 2. Exit as a message

```python
{
    "role": "exit",
    "content": submission_text,
    "extra": {"exit_status": "Submitted", "submission": submission_text},
}
```

Standard exit statuses worth having: `Submitted`, `LimitsExceeded`,
`TimeExceeded`, `RepeatedFormatError`, and the exception class name for
uncaught crashes.

Why this rather than a return value: when you have 500 trajectories from a
benchmark run and want to know why the failures failed, `exit_status` is one
field away in every file, and the messages leading to it are right there in the
same array. A return code forces you to correlate two artifacts.

## 3. Limits and where to check them

```python
def query(self) -> dict:
    if 0 < self.config.step_limit <= self.n_calls or 0 < self.config.cost_limit <= self.cost:
        raise LimitsExceeded(exit_message("LimitsExceeded"))
    if 0 < self.config.wall_time_limit_seconds <= int(time.time() - self._start_time):
        raise TimeExceeded(exit_message("TimeExceeded"))
    self.n_calls += 1
    message = self.model.query(self.messages)
    self.cost += message.get("extra", {}).get("cost", 0.0)
    ...
```

Three independent limits, all checked **before** the call:

- **Step limit** — bounds the number of model calls. 250 for a SWE-bench-style
  task; 30–50 for a focused extraction or research task.
- **Cost limit** — bounds spend. Defaults to $3.00 per task. This is the one that
  saves you at 3am.
- **Wall-clock limit** — bounds latency. Distinct from step limit because a single
  step can hang. Set it when the agent runs unattended.

The `0 < limit <= value` idiom means `0` disables the limit, which is why the
comparison is written that way rather than as a plain `>=`.

Note the cost bookkeeping in the format-error branch of `run()`:

```python
except FormatError as e:
    # The call was billed before parsing failed, so query() never got to charge it.
    self.cost += e.messages[0].get("extra", {}).get("cost", 0.0)
```

A malformed response still costs money. If you only charge on the success path,
an agent stuck in a format-error loop burns budget invisibly. This is a two-line
fix for a genuinely nasty failure mode.

## 4. Format errors and consecutive-failure counting

Count failures **consecutively**, and reset the counter on any clean step:

```python
self.step()
self.n_consecutive_format_errors = 0
```

Cumulative counting is wrong: a long-running agent that recovers cleanly ten
times over 200 steps is healthy, not failing. What you actually want to detect is
*being stuck* — N failures in a row with no progress between them. Default N = 3.

The parse itself insists on exactly one action:

```python
actions = [a.strip() for a in re.findall(action_regex, content, re.DOTALL)]
if len(actions) != 1:
    raise FormatError({... "content": render(format_error_template,
                                             actions=actions, error=f"Expected exactly 1 action, found {len(actions)}.")})
```

Reporting the *count* found, not just "parse failed", matters: zero actions and
five actions are different mistakes with different fixes, and the model can only
correct if it knows which one it made.

Distinguish truncation from malformation. The response was cut off at the token
limit, which surfaces as zero parsed actions but needs the opposite advice:

```jinja
{% if finish_reason == "length" or (finish_reason == "tool_calls" and not has_tool_calls) -%}
Your previous response reached the output token limit (finish_reason={{ finish_reason }})
before you produced a tool call, so it was cut off. Respond more concisely and finish
with exactly one bash tool call. If you need to think more, do so briefly.
{%- else -%}
Tool call error:
<error>{{error}}</error>
...
{%- endif %}
```

Telling a truncated model "your format was wrong" makes it rewrite the format and
get truncated again. Telling it "you ran out of room, be shorter" fixes it.

## 5. Statelessness vs. persistent sessions

mini-swe-agent executes every action in a fresh subprocess. No persistent shell.

```python
result = _run(command, cwd, os.environ | self.config.env, timeout)
```

What this buys:

- Sandboxing is a one-line swap: `subprocess.run` → `docker exec`. No session to
  attach, no PTY to manage.
- Parallel execution across hundreds of containers is trivial.
- No class of bugs around hung processes, prompt detection, interleaved output,
  or `cd` state drifting away from what the model thinks it is.

What it costs: no long-running processes, no REPLs, no background servers, and
`cd`/`export` don't persist.

The cost is paid **in the prompt**, explicitly:

```
- Directory or environment variable changes are not persistent.
  Every action is executed in a new subshell.
- However, you can prefix any action with `MY_ENV_VAR=MY_VALUE cd /path/to/dir && ...`
  or write/load environment variables from files
```

Either choice is defensible. What is not defensible is being stateless without
telling the model, which produces an agent that appears to work and then
mysteriously operates on the wrong directory.

If you need statefulness — a dev server, a REPL, an interactive debugger — a
persistent session is the right call, but budget for the session-management bugs.
The middle path both codebases use is a stateless default with an escape hatch
(`nohup ... &`, or a named background process managed by an explicit tool).

## 6. Timeouts and process groups

```python
def _run(command, cwd, env, timeout):
    """Like subprocess.run, but kills the whole process group on timeout
    so no children are orphaned."""
    process = subprocess.Popen(command, shell=True, text=True, cwd=cwd, env=env, ...)
```

A naive `subprocess.run(timeout=...)` kills the shell and leaves its children
running. Over a few hundred agent steps that is a machine full of orphaned test
runners. Start the child in its own process group and signal the group.

Default timeout: 30s for interactive work, 60s for build/test-heavy environments.
A timeout should produce a normal observation with a nonzero return code and an
`exception_info` field — not an exception that ends the run. The model should get
the chance to try something faster.

## 7. Trajectory persistence

```python
{
  "info": {
    "model_stats": {"instance_cost": ..., "api_calls": ...},
    "config": {"agent": {...}, "agent_type": "module.ClassName"},
    "exit_status": "...",
    "submission": "...",
  },
  "messages": [...],
  "trajectory_format": "mini-swe-agent-1.1",
}
```

Points worth copying:

- **The full config is embedded in the trajectory.** Six months later you can
  tell exactly which prompt, limits, and model produced this run. Without it,
  archived trajectories are uninterpretable.
- **A `trajectory_format` version string.** You will change the schema. Tooling
  that reads old runs needs to know which shape it is looking at.
- **The agent class name**, so a trajectory identifies the code that made it.
- **`messages` is verbatim** — the same array sent to the model, so a trajectory
  is directly replayable and directly usable as training data.

## 8. Subagents

Delegating to a subagent is the main tool for keeping context bounded on large
tasks: the child does the exploration and returns a summary, and the parent never
sees the intermediate mess.

Things OpenClaw's spawn machinery makes explicit, each corresponding to a real
failure:

- **Depth limits.** Without them, a subagent that can spawn subagents will.
- **Ownership and cleanup.** A parent that dies must not leave orphaned children
  holding containers.
- **A bootstrap delivery contract.** The child gets a defined starting context,
  not an ad-hoc string, so its behavior is reproducible.
- **Heartbeats.** Long-running children need liveness signals or you cannot
  distinguish "working" from "hung".
- **A completion contract.** The child returns a structured terminal status; the
  parent decides what to surface. Don't let the child's raw output flow straight
  back into the parent's context — that defeats the purpose of delegating.

Use a subagent when the subtask has a **narrow, summarizable result** and a
**wide, disposable exploration**. If the parent needs all the intermediate detail
anyway, a subagent adds coordination cost and buys nothing.
