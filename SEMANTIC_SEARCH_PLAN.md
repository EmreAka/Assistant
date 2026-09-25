# Plan: Hybrid Chat-History Search (full-text + semantic)

> **Outcome (2026-09-25):** hybrid was built, then full-text was **removed** after the Step 6 logs showed it mostly returned noise (common words like "this" or "time" matched unrelated turns). Search is now **semantic only**; the RRF merge was removed with it. The search query is the **current message only** (`RecentMessageMemoryLimit = 0`).

**Goal:** when the agent looks up past chat turns, find them by **meaning** as well as by words, and merge both result lists into one.

**Builds on:** `EMBEDDINGS_PLAN.md` (turns already have a `vector(768)` embedding filled in by `ChatTurnEmbeddingJob`).

**Rules carried over:** ZDR stays on, so one input per embedding call. No vector index.

---

## How search works today

1. `AgentService` registers a `TextSearchProvider` (`SearchTime = BeforeAIInvoke`).
2. Before **every** model call, it takes the last few user messages (`RecentMessageMemoryLimit = 4`) as the query and calls `SearchChatTurnsAsync`.
3. That calls `ChatTurnService.SearchTurnsAsync(chatId, query, 3)`:
   - builds a `tsquery` like `hiking:* | kaçkar:*`
   - runs a PostgreSQL full-text match on `search_vector`, ranked by `ts_rank_cd`
   - returns the top 3
4. The results get injected into the prompt as "Relevant past chat turns".

**Weakness:** only matching words are found. "Where did I go climbing?" won't find a turn about "hiking in Kaçkar".

## What changes

Only **step 3** changes. `SearchTurnsAsync` keeps its signature, so `AgentService` and `TextSearchProvider` are untouched.

```
query ─┬─► full-text search  ─► top 10 ─┐
       │                                ├─► merge (RRF) ─► top 3
       └─► embed query ─► vector search ─► top 10 ─┘
```

---

## Key concepts

### Why both, not just semantic?
They cover each other's gaps:

| | Full-text | Semantic |
|---|---|---|
| Exact names, numbers, rare words ("IBAN", "Kaçkar") | ✅ Strong | ⚠️ Can miss |
| Same meaning, different words | ❌ Misses | ✅ Strong |
| The newest turns (not embedded yet, up to 19) | ✅ Finds them | ❌ Can't see them |
| Nothing relevant exists | Returns nothing ✅ | **Still returns the "closest" turns** ⚠️ |

The last row matters: vector search **always** returns something, even if it's unrelated. That's why we need a distance cutoff (see Step 3).

### Why RRF (Reciprocal Rank Fusion) for merging?
The two scores aren't comparable. `ts_rank_cd` might be 0.1 or 2.5, while cosine distance is 0 to 2 and lower is better. You can't just add them.

RRF ignores the scores and only uses **positions (ranks)**:

```
score(turn) = Σ  1 / (k + rank)      k = 60 (standard value), rank starts at 1
```

- A turn ranked #1 in full-text and #3 in semantic: `1/61 + 1/63 ≈ 0.0323`
- A turn ranked #1 in only one list: `1/61 ≈ 0.0164`

So a turn that **both** searches like beats a turn only one search likes. It's simple, has no tuning, and is the standard way to do hybrid search.

---

## Steps

### Step 1: Query embedding
Add a second method to `IChatTurnEmbeddingService`:
```csharp
Task<Vector> EmbedQueryAsync(string text, CancellationToken cancellationToken);
```
It's the same as `EmbedDocumentAsync`, but with the **query** prefix: `task: search result | query: {text}`.
Stored turns use the document prefix and searches use the query prefix. That's the asymmetric setup from the start.

### Step 2: Config
Add to `EmbeddingOptions` and the `Embeddings` section:
```json
"SearchCandidates": 10,
"MaxCosineDistance": 0.5
```
- `SearchCandidates`: how many results each search contributes before merging.
- `MaxCosineDistance`: semantic matches farther than this are dropped. **0.5 is a placeholder** that gets tuned in Step 6.

### Step 3: Vector search query
A new private method in `ChatTurnService`:
```csharp
dbContext.ChatTurns
    .Where(x => x.TelegramUser.ChatId == chatId && x.Embedding != null)
    .Select(x => new { turn = x, Distance = x.Embedding!.CosineDistance(queryVector) })
    .Where(x => x.Distance <= MaxCosineDistance)
    .OrderBy(x => x.Distance)
    .Take(SearchCandidates)
```
`CosineDistance` comes from `Pgvector.EntityFrameworkCore` and becomes the `<=>` operator in SQL. With no index this is an exact scan, which is fine at your size.

### Step 4: Hybrid `SearchTurnsAsync`
1. Run full-text search, taking `SearchCandidates` results instead of `maxResults`.
2. Embed the query and run the vector search.
3. Merge with RRF and take the top `maxResults` (3).
4. Return `ChatTurnSearchResult`, with `Score` = the RRF score.

**Fallback (important):** if the embedding call fails (timeout, API down), log a warning and return **full-text results only**. Search must never break a chat reply. It's the same idea as the try/catch blocks in `ChatCommand`.

(`DbContext` can't run two queries at the same time, so the two searches run one after the other. That's fine.)

### Step 5: RRF as a small pure function
Keep the merge logic in a `static` method that takes two ranked ID lists and returns the merged order. For each turn, also return **which list(s) it came from and its rank in each**, so Step 6 can log them. No database, no API calls, so it's **easy to unit test** (and those tests work with no in-memory database issues):
- a turn in both lists ranks above turns in only one
- ordering within one list is preserved
- empty inputs work (semantic failed → full-text only, and the reverse)

### Step 6: Log and tune
For each search, log the final top results (debug level) with:
- **Source:** `fulltext`, `semantic`, or `both`
- **Rank in each list**, plus the cosine **distance** for semantic hits
- The query and turn IDs, so you can check afterward whether a hit was actually relevant

Chat normally for a while, then look at the logs and answer two questions:

1. **What should `MaxCosineDistance` be?**
   - relevant hits: probably distance < 0.3–0.4
   - unrelated noise: higher
   - set the cutoff between the two

2. **Is full-text search worth keeping?** Look at the results whose source is `fulltext` only:
   - mostly useful (exact names, numbers, things semantic missed) → keep hybrid
   - mostly noise (matched a common word like "bugün") → remove full-text and go semantic only

   Removing it is easy (drop one query and the merge). Don't decide until the logs show it.

You can only do this with real data, so it's a separate step.

### Step 7: Verify by hand
Ask something using **different words** from the past message, like "tırmanışa nereye gitmiştim?" for a hiking turn. The turn should show up in "Relevant past chat turns", and it wouldn't have before.

---

## Costs and trade-offs
- **An extra API call per message:** the query is embedded before every model call. It's about 20–100 tokens, which is basically free, but it adds some latency (a few hundred ms). A possible improvement for later: start the embedding call while full-text search runs.
- **Newest turns** (up to 19 not yet embedded) are only findable through full-text. Hybrid search covers exactly this gap.

## Out of scope
- An HNSW index (not needed at this size)
- Re-ranking with an LLM
- Using the vectors for memory consolidation or deduplication
