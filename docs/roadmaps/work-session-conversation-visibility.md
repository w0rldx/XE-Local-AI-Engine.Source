# Work-session conversation visibility

- **Decision:** none filed — recorded here in place of a standalone issue (operator ruling, External Integrations, 2026-09-03)
- **Status authority:** this living page
- **Last verified against the tree:** 2026-09-04
- **Overall state:** Chat-list leak closed by the S0 `NodeConversation.Kind` discriminator. No other leak surface found; two open questions remain below.

This page carries the External Integrations S0 "R2 — live check and ticket" item (2026-09-03), which was written to
be filed as a standalone issue and instead is tracked here by operator decision. Its text, verbatim except that the
`file:line` references are reduced to symbols per `AGENTS.md`:

> **Work-session owned conversations appear in the chat list.** `WorkSessionService.CreateAsync` creates the
> session's transcript as an ordinary `NodeConversation`, and the chat list filters only `purged`/`archived`
> (`NodeChatReadModel`), so every work session adds an untitled chat the operator did not start and cannot explain.
> Confirmed live on develop @d883a70b (see below). The external-integrations S0 slice adds a
> `NodeConversation.Kind` discriminator that backfills existing work-session conversations and filters them out of
> the list, which closes the leak for the chat list specifically. This ticket tracks the rest: whether any other
> surface (platform sync, export, search) still exposes them, and whether a pre-S0 database needs an
> operator-visible note that some chats will disappear from the list after upgrading.

## The leak

`WorkSessionService.CreateAsync` creates every work session's transcript as an ordinary `NodeConversation`, and before
the change below the chat-list queries filtered only `purged`/`archived`, so every work session added an untitled
chat the operator did not start and could not explain. Confirmed live on develop `@d883a70b`: `POST work-sessions`
followed by `GET chat/conversations` returned the new session's `conversationId` in the list, and the chat page
rendered it as an ordinary empty chat.

## What closed it

The External Integrations S0 slice (merged develop `@2f951c65`) adds a `NodeConversationKind` discriminator
(`XE-Local-AI-Engine.Client.Persistence/Entities/NodeConversationKind.cs`: `Chat`, `WorkSession`, `Integration`),
persisted as the `kind` column added by the `AddIntegrationFoundation` migration
(`XE-Local-AI-Engine.Client.Persistence/Migrations/20260903104044_AddIntegrationFoundation.cs`). `WorkSessionService.
CreateAsync` now passes `NodeConversationKind.WorkSession`, and the two chat-list queries in
`NodeChatReadModel` — `ListActiveConversationsAsync` and `ListAllConversationsAsync` — both filter `c.kind = 'chat'`.
Re-run after the change: the same work session's conversation id is absent from `GET chat/conversations`, while
`GET chat/conversations/{id}` still returns 200. This closes the leak for the chat list specifically.

The migration also backfills existing rows in the same `Up()`, immediately after adding the column: every
`conversations` row whose id appears in `agent_work_sessions` is set to `kind = 'work-session'`. So a database that
already had work sessions before this migration is reclassified automatically — no separate data-migration step is
needed for that part.

## What is still open

- **Other surfaces.** Every other reader of the `conversations` table was checked: the by-id reads/writes (the
  caller already holds the id — `NodeChatReadModel.ReadConversationAsync`, `NodeChatPersistenceSql.
  ReadConversationRowAsync`, `TouchConversationAsync`, every by-id command in `NodeChatConversationCommands`),
  retention/purge (deliberately unfiltered, so a closed session's transcript still ages out —
  `NodeRetentionStore.ListExpiredConversationCandidatesAsync`, `ConversationFootprintPurge.DeleteAsync`,
  `RetentionSweeperService`), and the one-shot `NodeChatTitleEncryptionBackfillService`. As of this verification the
  repository has **no conversation export feature and no conversation search feature** — a grep for `FROM
  conversations` outside those already-accounted-for files and the two chat-list queries returns nothing else. If
  either is ever added, it must filter on `Kind` the same way the chat list does, or this leak reopens on that
  surface.
- **Platform sync.** `EnsureConversationAsync` mirrors a remote-platform conversation into a local row and always
  takes the `'chat'` kind default, on the premise that a platform-mirrored conversation is by definition a chat. It
  is a writer, not a list reader, so it is not itself a leak surface — but nothing enforces that premise, and it
  would need re-checking if platform-side work sessions or integrations ever start mirroring conversations locally.
- **Operator-visible upgrade note.** The migration's automatic backfill (above) means no data is lost or
  misclassified on upgrade. What is not addressed is whether an operator should be told in release notes that some
  conversations that used to appear in their chat list — every pre-upgrade work session — will disappear from it
  after upgrading, even though the underlying data is untouched and still reachable by id.

## Updating this page

1. Verify each claim against the current tree — grep `FROM conversations` for any new reader, and check whether a
   conversation export or search feature has since been added.
2. Record the verification date above.
3. Mark this page resolved once the export/search and upgrade-note questions are settled, or file the standalone
   issue at that point if the answer needs external tracking.
