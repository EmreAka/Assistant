# Context Management

The context window is the agent's working memory and its single largest cost
driver. Most "the agent got dumber over time" reports are context problems, not
model problems.

Contents:
1. Observation shape
2. Head/tail elision
3. Full output escape hatch
4. Compaction: structured sections
5. Compaction: quality audit
6. Compaction: re-distillation
7. TTL-based pruning
8. Prompt cache breakpoints
9. Aggregate budgets

---

## 1. Observation shape

Every observation should carry, at minimum: the return code, the output, and any
exception info. mini-swe-agent's CLI config emits JSON:

```jinja
{
  "returncode": {{ output.returncode }},
  "output": {{ output.output | tojson }}
  {%- if output.exception_info %}, "exception_info": {{ output.exception_info | tojson }}{% endif %}
}
```

and its benchmark config uses XML-ish tags instead:

```jinja
<returncode>{{output.returncode}}</returncode>
<output>{{ output.output }}</output>
```

Both work. What matters is that the return code is **always present and
structurally separate from the output text**. Agents that loop on a failing
command usually do so because success and failure looked identical in the
observation — the command printed nothing either way and the exit code was
invisible.

Note `| tojson` on the output. Command output contains quotes, backslashes, and
newlines; interpolating it raw into a JSON-shaped observation produces malformed
JSON that the model then tries to interpret. Escape properly for whatever format
you chose.

## 2. Head/tail elision

Never truncate the middle silently, and never truncate only the tail. Error
messages live at the end; the command being run lives at the beginning.

```jinja
{% if output.output | length < 10000 -%}
<output>{{ output.output }}</output>
{%- else -%}
<warning>
The output of your last command was too long.
Please try a different command that produces less output.
If you're looking at a file you can try use head, tail or sed to view a smaller
number of lines selectively.
If you're using grep or find and it produced too much output, you can use a more
selective search pattern.
If you really need to see something from the full command's output, you can
redirect output to a file and then search in that file.
</warning>
<output_head>{{ output.output[:5000] }}</output_head>
<elided_chars>{{ output.output | length - 10000 }} characters elided</elided_chars>
<output_tail>{{ output.output[-5000:] }}</output_tail>
{%- endif -%}
```

Three things this gets right:

- **The elision is stated with an exact character count.** The model knows
  something is missing and roughly how much, so it can decide whether to go
  looking. Silent truncation produces confident conclusions from partial data.
- **The warning is prescriptive.** It doesn't say "output too long"; it lists the
  specific commands to use instead (`head`, `tail`, `sed`, narrower `grep`,
  redirect-then-search). This is where you teach the model to behave, not in the
  system prompt.
- **Head and tail are separate tags**, so the model isn't tempted to read across
  the gap as continuous text.

10,000 chars total with 5,000 head / 5,000 tail is a reasonable default for a
200k-token context. Scale it down aggressively for smaller windows.

## 3. Full output escape hatch

Truncation is only safe if the full data remains reachable. OpenClaw writes full
output to a file and appends the path:

```typescript
export function formatFullOutputFooter(path: string): string {
  return `Full output: ${path}`;
}
```

One line, and it converts a lossy truncation into a lazy load. The model can
`grep` the file for exactly what it needs instead of either flying blind or
demanding you raise the cap.

## 4. Compaction: structured sections

When the conversation must be summarized to continue, do not ask for a free-form
summary. Require exact sections:

```typescript
const REQUIRED_SUMMARY_SECTIONS = [
  "## Decisions",
  "## Open TODOs",
  "## Constraints/Rules",
  "## Pending user asks",
  "## Exact identifiers",
] as const;
```

Each of these exists because free-form summaries reliably drop that category:

- **Decisions** — what was settled, so it isn't relitigated.
- **Open TODOs** — the work not yet done, which a narrative summary compresses to nothing.
- **Constraints/Rules** — "don't touch the tests", "use tabs", "this is Postgres not MySQL". The most damaging losses.
- **Pending user asks** — requests the agent hasn't answered yet. Summaries are written from the agent's perspective and drop these first.
- **Exact identifiers** — see below.

The instruction for the last section is deliberately absolute:

```
For ## Exact identifiers, preserve literal values exactly as seen
(IDs, URLs, file paths, ports, hashes, dates, times).
```

Summarizers paraphrase by default. Paraphrasing `/var/log/app-7f3a2b.log` into
"the application log file" destroys the only thing that made it useful.

Two more instructions worth carrying verbatim:

```
Do not omit unresolved asks from the user.
When prior compaction summaries are present, re-distill them with new messages
and remove stale duplicate detail.
```

And for multilingual sessions:

```
Write the summary body in the primary language used in the conversation.
Do not translate or alter code, file paths, identifiers, or error messages.
```

Without this, summarizers drift to English mid-session and the agent's subsequent
replies follow. The second clause matters as much as the first — a summarizer
told "write in Turkish" will happily translate an error message and destroy it.

## 5. Compaction: quality audit

This is the pattern most harnesses are missing, and it is the highest-value thing
in this file. **Verify the summary before you throw away the messages.**

```typescript
export function auditSummaryQuality(params: {
  summary: string;
  identifiers: string[];
  latestAsk: string | null;
  identifierPolicy?: "strict" | "off" | "custom";
}): { ok: boolean; reasons: string[] } {
  const reasons: string[] = [];
  const lines = new Set(normalizedSummaryLines(params.summary));

  for (const section of REQUIRED_SUMMARY_SECTIONS) {
    if (!lines.has(section)) reasons.push(`missing_section:${section}`);
  }

  if ((params.identifierPolicy ?? "strict") === "strict") {
    const missing = params.identifiers.filter(
      (id) => !summaryIncludesIdentifier(params.summary, id));
    if (missing.length > 0) reasons.push(`missing_identifiers:${missing.slice(0,3).join(",")}`);
  }

  if (!hasAskOverlap(params.summary, params.latestAsk)) {
    reasons.push("latest_user_ask_not_reflected");
  }

  return { ok: reasons.length === 0, reasons };
}
```

Three checks, all cheap and deterministic:

1. **Sections present** — string match on required headings.
2. **Identifiers survived** — extract candidate opaque identifiers from the
   pre-compaction messages, then assert each appears in the summary.
3. **Latest user ask is reflected** — tokenize the most recent user request,
   strip stop words, and require token overlap with the summary. Catches the
   summary that describes what the agent was doing while forgetting what it was
   asked to do.

Identifier extraction is a regex over the categories that must not be paraphrased:

```typescript
/([A-Fa-f0-9]{8,}                                   // hashes
 |https?:\/\/\S+                                     // URLs
 |\/[\w.-]{2,}(?:\/[\w.-]+)+                         // POSIX paths
 |[A-Za-z]:\\[\w\\.-]+                               // Windows paths
 |(?<![A-Za-z0-9._-])[A-Za-z0-9._-]+\.[A-Za-z0-9._/-]+:\d{1,5}  // host:port
 |\b\d{6,}\b)/g                                      // long numeric IDs
```

On failure: **retry the summarization once** (default `maxRetries: 1`) with the
audit reasons fed back in. If it fails again, fall back to a structured fallback
summary rather than accepting a bad one. Retaining the previous valid summary
beats accepting a lossy new one.

Round out the design with:
- **Preserve the last N turns verbatim** (default 3, cap 12) alongside the
  summary. Recent context is where the active work is.
- **Cap the summary itself** (16,000 chars) with an explicit truncation marker.
- **Cap operator-supplied custom instructions** (800 chars ≈ 200 tokens) — the
  comment in the source is exact: it "keeps summarization quality stable".

## 6. Compaction: re-distillation

On the second and later compactions, do not append the new summary to the old.
Feed the old summary back in as input to be re-distilled:

```typescript
const PREVIOUS_SUMMARY_REDISTILL_PREFIX =
  "Previous compaction summary to re-distill with the current conversation. " +
  "Prune stale, duplicate, or superseded details instead of preserving it verbatim.";
```

wrapped in `<previous-compaction-summary>` and prepended to the messages being
summarized. Concatenating summaries produces monotonic growth and, worse,
preserves decisions that were later reversed. Re-distilling lets superseded
detail actually die.

## 7. TTL-based pruning

A gentler alternative to full compaction: expire *old tool results* on a timer
while leaving the conversation intact.

```typescript
const CACHE_TTL_DEFAULT_PLACEHOLDER = "[Old tool result content cleared]";
const CACHE_TTL_IMAGE_MARKER = "[image removed during context pruning]";
let ttlMs = 5 * 60_000;  // default 5 minutes
```

Tool results are the bulk of context and the fastest to go stale — a directory
listing from twenty steps ago is noise. Replace the body with a placeholder and
keep the tool call itself, so the model still knows the action happened.

Refinements worth having: an allow/deny glob list so results from specific tools
are exempt (a search whose results are still being worked through) or always
pruned; and images cleared first, since they are the most expensive per unit of
lasting value.

## 8. Prompt cache breakpoints

For providers with explicit cache control, mark **only the final message** as a
cache breakpoint, clearing all previous marks:

```python
for i_entry, entry in enumerate(reversed(messages)):
    _clear_cache_control(entry)
    if i_entry == 0:
        _set_cache_control(entry)
```

The prefix up to that point is what gets cached. Since an agent appends
monotonically, marking the end each turn means every turn hits cache on
everything that came before. Leaving stale marks scattered through the history
fragments the cache and can push you over the provider's breakpoint limit.

The corollary constrains everything else in this file: **anything that mutates
earlier messages invalidates the cache from that point forward.** TTL pruning,
transcript rewriting, and compaction all trade cache hits for context space. Do
them on a schedule, not opportunistically, so you pay the invalidation once.

## 9. Aggregate budgets

Per-observation caps are necessary but not sufficient — fifty observations at
9,999 chars each will still overflow. Budget in aggregate:

```typescript
const AGGREGATE_TOOL_RESULT_CONTEXT_SHARE = 0.5;
const PROMPT_TOOL_RESULT_AGGREGATE_CAP_MULTIPLIER = 4;
```

Tool results get a bounded *share* of the context window (here, half), computed
against the actual window size of the model in use rather than a hardcoded
number. When the aggregate is exceeded, oldest results are trimmed first.

Compute limits from the model's real context window. Hardcoding a token count
means every model swap silently changes your agent's behavior.
