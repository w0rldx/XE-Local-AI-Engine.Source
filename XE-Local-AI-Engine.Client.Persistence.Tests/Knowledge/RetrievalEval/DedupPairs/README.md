# Memory dedup calibration data

Two data files that calibrate the semantic memory dedup threshold per embedding model. `MemoryDedupPairSetTests`
validates both on every gate run; `Live/MemoryDedupCalibrationLiveTests` embeds them with a real model (opt-in).

## Provenance

Every text was written for this repository. Nothing is copied from a dataset, a website, a model output or another
project, so the files carry the repository's licence. The texts are memory `behavior` strings: one short, generalizable
instruction each, in the shape the memory extraction prompt asks for, with no personal data and no secrets.

## `pairs.json`: calibration pairs

72 pairs, 12 per kind and language (`en`, `de`). No pair mixes languages. Fields: `id`, `language`, `kind`,
`lessonId`, `a`, `b`.

| Kind | Rule |
|---|---|
| `duplicate` | `a` and `b` state the same lesson in different words. Merging them loses nothing. `lessonId` names that lesson. |
| `near-miss` | Same topic, different lesson: a flipped preference, another tool, another value or another scope of applicability. Merging them would be wrong because it drops one of two lessons. `lessonId` names the lesson of `a`. |
| `unrelated` | Two lessons from different topics. `lessonId` names the lesson of `a`. |

## `heldout.json`: set-level replay scenarios

16 scenarios (8 `en`, 8 `de`), each with three `existing` memories and five `candidates` in proposal order. Each item
has a `lessonId`, a `scope` (a `MemoryScope` name) and a `behavior`; each candidate also has a `kind`. Every scenario
contains exactly one candidate of each kind:

| Candidate kind | Expected |
|---|---|
| `duplicate` | A paraphrase of an existing memory in the same scope: a duplicate. |
| `near-miss` | Same scope and topic as an existing memory, different lesson: kept. |
| `cross-scope` | A paraphrase of an existing memory filed under another scope: kept, because dedup only compares within one scope. |
| `new` | A lesson none of the existing memories holds: kept. |
| `in-batch-duplicate` | A later paraphrase of the `new` candidate in the same scope: a duplicate, found by comparing against candidates accepted earlier in the same run. |

`expectedDuplicateIndexes` lists the zero-based candidate positions the production decision should flag:
`MemorySemanticDeduplicator.ClassifyCandidates` flags a candidate when its cosine to a same-scope existing memory, or
to a same-scope candidate accepted earlier in the run, is at least the threshold. A flagged candidate is never a
comparison target.

## Authoring rules (checked in review; the structural half by `MemoryDedupPairSetTests`)

1. Each `duplicate` pair states one lesson; each `near-miss` pair would be wrong to merge.
2. Held-out lessons are disjoint from the calibration lessons: no `lessonId` and no text appears in both files, and
   no held-out lesson restates a calibration lesson under another id.
3. `expectedDuplicateIndexes` equals what the production decision returns when two items match exactly when they share
   `scope` and `lessonId`; the unit test replays that through `ClassifyCandidates`.
4. German reads as natural German written for German readers, not as a translation.
5. Every text is unique (ignoring case) across both files.

Changing a file after a calibration run voids that run: the live report records the sha256 of both files.

## What the live run computes

Per model, on the raw `behavior` strings (no profile prefix, as production dedup embeds them):

1. `t` = the upper bound of the 95% percentile-bootstrap interval (2,000 resamples, seed 20261117) of the 0.95
   quantile of all `near-miss` and `unrelated` cosines, rounded up to the next 0.005, with no cap. Quantiles are the
   linear-interpolation estimate (type 7).
2. Calibration recall: the share of `duplicate` pairs with cosine at least `t`.
3. Every held-out scenario replays through `ClassifyCandidates` at `t`; precision and recall of the flagged indexes are
   pooled over all scenarios.
4. The model passes when `t` is at most 1, calibration recall and held-out recall are at least 0.5 and held-out
   precision is at least 0.9. AUC (duplicate against near-miss) is reported as a description only.

The run writes `dedup-calibration.json` and `dedup-calibration.md`. It fails only when the run itself is invalid (a
server that does not start, a short or malformed embedding response); a model that does not pass is a reported
outcome, not a test failure.
