# Blameless postmortem template

Use this template after any customer-visible incident or any incident that paged someone outside working hours.

## Principles

The review is blameless: it asks how the system allowed a mistake, not who made it. People who were on call are
invited to describe what they saw and what they believed at the time, without being judged by hindsight.

## Deadline

The written postmortem is due within five business days of the incident being resolved. The incident commander
owns the document, even when someone else writes most of it.

## Sections

1. Summary in three sentences that a non-engineer can follow.
2. Impact: duration, affected customers, and data loss if any.
3. Timeline, with every entry in UTC.
4. Contributing factors, found by asking "why" repeatedly until the answer is a process or a design decision.
5. Action items.

## Action items

Every action item has exactly one owner and a due date. Items without an owner are not accepted in the review
meeting. Follow-up is tracked in the weekly operations review until every item is closed or explicitly dropped.
