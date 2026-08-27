# Prompting an Agent

Agent prompts are not chat prompts. They are read once and then govern dozens of
autonomous turns with no chance to clarify. Optimize for unambiguous behavior
under repetition, not for a good first response.

Contents:
1. System vs. instance templates
2. Everything is a template, strictly rendered
3. The loop contract
4. Termination protocols
5. Boundaries
6. Worked examples in the prompt
7. Where to fix behavior

---

## 1. System vs. instance templates

Split the prompt in two, and keep the system message almost empty:

```yaml
system_template: |
  You are a helpful assistant that can interact with a computer shell to solve programming tasks.
instance_template: |
  <pr_description>
  {{task}}
  </pr_description>
  <instructions>
  ...everything else...
  </instructions>
```

One line of system prompt, and all the operational detail in the first user
message. This is deliberate:

- The system message is the most cacheable and least task-specific thing you
  have. Keeping it tiny and stable maximizes cache hits across tasks.
- Instructions land closer to the task they govern, which helps adherence.
- Swapping the instruction set per task type doesn't disturb the system message.

## 2. Everything is a template, strictly rendered

```python
Template(template, undefined=StrictUndefined).render(**self.get_template_vars())
```

`StrictUndefined` makes an undefined variable raise instead of rendering empty.
Without it, `{{ working_dir }}` when the variable is called `cwd` produces a
prompt with a blank where a path should be, and an agent that behaves oddly for
reasons nothing in the logs explains. Fail loudly.

Template variables are merged from every layer, with a defined precedence:

```python
recursive_merge(
    self.config.model_dump(),      # config values
    self.env.get_template_vars(),  # cwd, platform info, env vars
    self.model.get_template_vars(),# model name, etc.
    {"n_model_calls": ..., "model_cost": ..., "elapsed_seconds": ...},
    self.extra_template_vars,
    kwargs,
)
```

Exposing `n_model_calls`, `model_cost`, and `elapsed_seconds` lets prompts adapt
to budget state — for instance, warning the agent when it is near the step limit
so it wraps up rather than getting cut off mid-edit.

Platform facts get injected the same way, and used conditionally:

```jinja
<system_information>{{system}} {{release}} {{version}} {{machine}}</system_information>

{%- if system == "Darwin" -%}
<important>You are on MacOS. For all the below examples, you need to use `sed -i ''` instead of `sed -i`.</important>
{%- endif -%}
```

Platform-specific corrections belong in the prompt, conditionally, not in a
paragraph the model has to remember to apply.

## 3. The loop contract

State the interaction protocol explicitly. The model cannot see your loop.

```
You are operating in an environment where
1. You issue at least one command
2. The system executes the command(s) in a subshell
3. You see the result(s)
4. You write your next command(s)

Each response should include:
1. **Reasoning text** where you explain your analysis and plan
2. At least one tool call with your command

**CRITICAL REQUIREMENTS:**
- Your response SHOULD include reasoning text explaining what you're doing
- Your response MUST include AT LEAST ONE bash tool call. You can make MULTIPLE
  tool calls in a single response when the commands are independent
  (e.g., searching multiple files, reading different parts of the codebase).
- Directory or environment variable changes are not persistent. Every action is
  executed in a new subshell.
- However, you can prefix any action with `MY_ENV_VAR=MY_VALUE cd /path/to/dir && ...`
```

Note the SHOULD/MUST distinction, used precisely. Reasoning is encouraged; a tool
call is required, because a response without one stalls the loop and burns a step
for nothing.

The parallel-calls guidance includes *when* it applies ("when the commands are
independent") with an example. Without that qualifier, models batch dependent
commands and get confusing interleaved results.

## 4. Termination protocols

An agent must be able to say "I'm done" unambiguously. Relying on the model
simply not calling a tool is unreliable — it produces both premature stops and
agents that never stop.

Use a sentinel the model emits into the environment, detected by the harness:

```python
def _check_finished(self, output: dict):
    lines = output.get("output", "").lstrip().splitlines(keepends=True)
    if lines and lines[0].strip() == "COMPLETE_TASK_AND_SUBMIT_FINAL_OUTPUT" and output["returncode"] == 0:
        submission = "".join(lines[1:])
        raise Submitted({"role": "exit", "content": submission,
                         "extra": {"exit_status": "Submitted", "submission": submission}})
```

Three conditions, all required: the sentinel is on the **first** line, the return
code is **0**, and everything after is the payload. Checking only "the sentinel
appears somewhere" means an agent that `grep`s for the string in its own source
accidentally terminates.

The prompt side is correspondingly rigid:

```
Step 1: Create the patch file
Run `git diff -- path/to/file1 path/to/file2 > patch.txt` listing only the source
files you modified. Do NOT commit your changes.

Step 2: Verify your patch
Inspect patch.txt to confirm it only contains your intended changes.

Step 3: Submit (EXACT command required)
```bash
echo COMPLETE_TASK_AND_SUBMIT_FINAL_OUTPUT && cat patch.txt
```
If the command fails (nonzero exit status), it will not submit.

<CRITICAL>
- Creating/viewing the patch and submitting it MUST be separate commands (not combined with &&).
- If you modify patch.txt after verifying, you SHOULD verify again before submitting.
- You CANNOT continue working (reading, editing, testing) in any way on this task after submitting.
</CRITICAL>
```

What earns each line:

- **Separate verify and submit steps.** An agent that builds and submits in one
  command never inspects what it submitted.
- **"If the command fails, it will not submit"** — tells the model the failure
  semantics so it doesn't assume submission happened after an error.
- **"You CANNOT continue working after this"** stated twice. Without it, agents
  submit early "to check", then try to keep working, and lose the run.
- **The exact command is given literally.** Do not make the model construct the
  sentinel; any variation breaks the match.

## 5. Boundaries

State what must not be touched, in its own section, in the positive-and-negative
form:

```
## Important Boundaries
- MODIFY: Regular source code files in /testbed
- DO NOT MODIFY: Tests, configuration files (pyproject.toml, setup.cfg, etc.)
```

And again at submission time, since that is where violations actually surface:

```
Do not submit file creations or changes to any of the following files:
- test and reproduction files
- helper scripts, tests, or tools that you created
- installation, build, packaging, configuration, or setup scripts unless they are
  directly part of the issue you were fixing
- binary or compiled files
```

Restating a constraint at the point of enforcement is not redundancy. The first
statement is 200 turns back in the context; the second is where it matters.

## 6. Worked examples in the prompt

Show one correct response, concretely:

```
<example_response>
I need to understand the Builder-related code. Let me find relevant files and check the project structure.

[Makes multiple bash tool calls: {"command": "ls -la"}, {"command": "find src -name '*.java' | grep -i builder"}]
</example_response>
```

And a small command cookbook for the operations the model will need constantly:

```bash
# Create a file
cat <<'EOF' > newfile.py
hello = "world"
EOF

# Edit with sed
sed -i 's/old_string/new_string/g' filename.py   # all occurrences
sed -i '1,10s/old/new/g' filename.py             # lines 1-10 only

# View specific lines with numbers
nl -ba filename.py | sed -n '10,20p'
```

Note `<<'EOF'` with **quoted** delimiter — unquoted, the shell expands `$` and
backticks inside the heredoc and silently corrupts any file containing them. This
is exactly the kind of detail a cookbook is for: the model knows `sed`, but a
cookbook eliminates the variance.

## 7. Where to fix behavior

In descending order of preference:

1. **The environment.** `PAGER=cat` beats "do not use interactive commands."
2. **The observation.** A prescriptive elision warning beats a system-prompt rule
   about output size. It arrives exactly when relevant, in context.
3. **The error message.** Distinguishing truncation from malformation fixes more
   than a paragraph about response formatting.
4. **The instance template.** Task-specific rules, boundaries, workflow.
5. **The system prompt.** Last resort. It is the most cached, least specific, and
   most-distant-from-the-problem place to put anything.

Working the list top-down produces smaller prompts and better behavior than
accreting rules at the bottom.
