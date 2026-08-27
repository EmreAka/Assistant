# Implementing These Patterns in .NET

The patterns in this skill are language-neutral, but the idiomatic .NET
expression differs enough from the Python and TypeScript sources to be worth
spelling out. This is written for `Microsoft.Extensions.AI` (`IChatClient`) and
Microsoft Agent Framework.

> Verify current API surface against the official docs before relying on
> specific type or method names — the Agent Framework moves quickly. The
> patterns below are stable; the exact signatures may not be.

Contents:
1. The loop
2. Limits as a policy object
3. Exit as a message
4. Observation shaping
5. Configuration and templates
6. Sandboxing from .NET
7. Where MAF gives you these for free, and where it doesn't

---

## 1. The loop

The Python loop maps cleanly onto `IChatClient` plus a `List<ChatMessage>`:

```csharp
public async Task<AgentResult> RunAsync(string task, CancellationToken ct = default)
{
    var messages = new List<ChatMessage>
    {
        new(ChatRole.System, RenderTemplate(_options.SystemTemplate)),
        new(ChatRole.User,   RenderTemplate(_options.InstanceTemplate, new { task })),
    };

    while (true)
    {
        try
        {
            _limits.ThrowIfExceeded(_state);              // BEFORE the call
            var response = await _chatClient.GetResponseAsync(messages, _chatOptions, ct);
            _state.Record(response.Usage);
            messages.AddMessages(response);

            var actions = _parser.Parse(response);
            foreach (var action in actions)
            {
                var output = await _environment.ExecuteAsync(action, ct);
                messages.Add(_formatter.ToObservation(output));
            }
            _consecutiveFormatErrors = 0;
        }
        catch (AgentFormatException ex)
        {
            _state.AddCost(ex.BilledCost);                // still charge the failed call
            if (++_consecutiveFormatErrors >= _options.MaxConsecutiveFormatErrors)
            {
                messages.Add(ExitMessage(ExitStatus.RepeatedFormatError));
            }
            else
            {
                messages.Add(ex.RecoveryMessage);
            }
        }
        catch (AgentTerminatedException ex)
        {
            messages.Add(ex.ExitMessage);
        }
        finally
        {
            await _trajectoryStore.SaveAsync(messages, _state, ct);
        }

        if (messages[^1].IsExit()) break;
    }

    return AgentResult.From(messages[^1]);
}
```

The `finally`-block save is the piece most .NET implementations skip. Make the
trajectory store cheap (append-only JSONL, or a single overwrite of a small file)
so saving every step is affordable.

If you are streaming, accumulate with `GetStreamingResponseAsync` and only append
to `messages` once the turn completes — a half-streamed assistant message in the
history will confuse the next turn.

## 2. Limits as a policy object

Bundle the three limits so they are checked together and configured together:

```csharp
public sealed class AgentLimits
{
    public int StepLimit { get; init; } = 100;
    public decimal CostLimitUsd { get; init; } = 3.00m;
    public TimeSpan WallClockLimit { get; init; } = TimeSpan.FromMinutes(20);

    public void ThrowIfExceeded(AgentState state)
    {
        if (StepLimit > 0 && state.ModelCalls >= StepLimit)
            throw new AgentTerminatedException(ExitStatus.LimitsExceeded);
        if (CostLimitUsd > 0 && state.CostUsd >= CostLimitUsd)
            throw new AgentTerminatedException(ExitStatus.LimitsExceeded);
        if (WallClockLimit > TimeSpan.Zero && state.Elapsed >= WallClockLimit)
            throw new AgentTerminatedException(ExitStatus.TimeExceeded);
    }
}
```

Bind it with `IOptions<AgentLimits>` so limits are configurable per environment
without a rebuild — the values you want in a dev loop and in unattended
production are not the same.

`CancellationToken` is not a substitute for the wall-clock limit. A cancellation
token aborts; the wall-clock limit produces a clean `exit` message with a status,
which is what you want in the trajectory. Wire a
`CancellationTokenSource.CancelAfter` as a *backstop* above the wall-clock limit,
not as the primary mechanism.

## 3. Exit as a message

`ChatMessage` has no `exit` role, so carry it in `AdditionalProperties`:

```csharp
public static ChatMessage ExitMessage(ExitStatus status, string submission = "") =>
    new(ChatRole.Assistant, submission)
    {
        AdditionalProperties = new()
        {
            ["exit"] = true,
            ["exit_status"] = status.ToString(),
            ["submission"] = submission,
        }
    };

public static bool IsExit(this ChatMessage m) =>
    m.AdditionalProperties?.TryGetValue("exit", out var v) == true && v is true;
```

This keeps the "trajectory is the message list" property intact — the reason for
stopping is inside the serialized history, not in a sibling field.

## 4. Observation shaping

Head/tail elision, with the prescriptive warning:

```csharp
public ChatMessage ToObservation(CommandOutput output)
{
    const int Cap = 10_000, Half = 5_000;
    var text = output.Text;

    var body = text.Length < Cap
        ? $"<output>{text}</output>"
        : $"""
          <warning>
          The output of your last command was too long. Try a command that produces
          less output: use head, tail, or sed to view fewer lines; use a more
          selective grep pattern; or redirect to a file and search within it.
          </warning>
          <output_head>{text[..Half]}</output_head>
          <elided_chars>{text.Length - Cap} characters elided</elided_chars>
          <output_tail>{text[^Half..]}</output_tail>
          """;

    return new ChatMessage(ChatRole.User,
        $"<returncode>{output.ExitCode}</returncode>\n{body}");
}
```

Watch `text[..Half]` on strings containing surrogate pairs — slicing by UTF-16
code unit can split an emoji or a non-BMP character into invalid text. Use
`StringInfo`, or check `char.IsHighSurrogate(text[Half - 1])` and adjust by one.
The TypeScript source has a dedicated `truncateUtf16Safe` helper for exactly this
reason.

For structured tool results, prefer a `record` serialized with
`System.Text.Json` over hand-built strings — you get correct escaping for free,
which is the `| tojson` point from `context-management.md`.

## 5. Configuration and templates

.NET has no direct Jinja `StrictUndefined` equivalent, and this matters — the
silent-empty-variable bug is real. Options:

- **Scriban** with `TemplateContext { StrictVariables = true }` — the closest
  match, and the recommended default.
- **Raw string literals with interpolation** — compile-time safety, no runtime
  template layer, but no per-deployment prompt editing. Fine when prompts are
  owned by developers.
- **`IConfiguration` binding into a strongly-typed prompt options record**, then
  interpolate. Good middle ground: prompts live in `appsettings.json` and binding
  failures surface at startup rather than mid-run.

Whatever you pick, validate at startup with `IValidateOptions<T>` so a
missing prompt section fails fast rather than producing a subtly degraded agent.

## 6. Sandboxing from .NET

`Process` with `ProcessStartInfo` is the equivalent of `subprocess.run`, with two
things to get right:

- **Kill the process tree**, not the process: `process.Kill(entireProcessTree: true)`.
  This is the .NET equivalent of the process-group kill in `control-loop.md`.
- **Read stdout and stderr asynchronously** (`OutputDataReceived` /
  `BeginOutputReadLine`, or `ReadToEndAsync` on both concurrently). Reading them
  sequentially deadlocks when a child fills the other pipe's buffer — a classic
  and intermittent bug that will look like a random hang.

For real isolation, shell out to `docker exec` rather than trying to sandbox
in-process. `AppDomain` is gone and `AssemblyLoadContext` is not a security
boundary; the CLR has no equivalent of a container.

## 7. Where MAF gives you these for free, and where it doesn't

Broadly, Microsoft Agent Framework handles the plumbing: model abstraction over
providers, tool registration and invocation, graph-structured workflows, session
and thread state, and telemetry. That is real value and worth using.

What it does not decide for you — and what this skill is about:

| Pattern | Who owns it |
|---|---|
| Model/provider abstraction | Framework |
| Tool schema + invocation | Framework |
| Workflow graph, checkpointing | Framework |
| Session/thread persistence | Framework |
| **Cost / step / wall-clock limits** | **You** |
| **Observation shaping and elision** | **You** |
| **Compaction strategy and quality audit** | **You** |
| **Termination protocol** | **You** |
| **Untrusted-text wrapping** | **You** |
| **Environment shaping (`PAGER=cat` etc.)** | **You** |
| **Consecutive format-error handling** | **You** |

The framework gives you a loop that runs. It does not give you a loop that stops
at the right time, keeps its context clean, or refuses to follow instructions it
found in a scraped page. Those are the parts that determine whether the agent is
usable in production, and they are all yours.

A practical structure: implement the patterns above as middleware around
`IChatClient` (Microsoft.Extensions.AI supports delegating clients) so limits,
trajectory persistence, cache-control marking, and observation shaping compose
independently of whichever agent abstraction sits on top. That keeps them
portable if the framework layer changes underneath you.
