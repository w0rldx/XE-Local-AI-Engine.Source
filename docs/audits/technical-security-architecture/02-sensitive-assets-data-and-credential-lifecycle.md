# 02 — Sensitive Assets, Data, and Credential Lifecycle

## Review boundary

| Item | Value |
|---|---|
| Frozen application baseline | `7e64ed589e14eecc0e522e807d2e531a1095d19a` |
| Review date | 2026-07-28 |
| Scope | Representative sensitive assets, cryptographic roots and derived keys, authentication material, credential stores, databases, managed files, logs, process evidence, egress, deletion, and recovery boundaries |
| Evidence boundary | Repository implementation, tests, configuration, and documentation. No deployed data inventory, secret-manager record, key ceremony, access review, backup set, deletion sample, or third-party processing record was supplied. |

This is a representative, security-relevant inventory rather than an exhaustive enumeration of every transient value, database column, log field, model file, or external API payload.

## Asset and protection register

| Asset class | Representative content | Primary location while persisted | Baseline protection | Lifecycle / residual boundary |
|---|---|---|---|---|
| Node operator secret | 32-byte root secret supplied as `XE_NODE_SQLITE_KEY`, raw secret file, or Aspire parameter | Environment, `/run/secrets/node-sqlite-key`, desktop-generated `node.key`, or AppHost parameter configuration | Exact length validation; used only to derive separate keys; temporary byte arrays are zeroed after derivation. Desktop mode generates a per-installation key. B1 also tracks one shared development-only AppHost default; Aspire's sensitive flag masks presentation but does not make the committed value secret or unique. | Anyone with the source can recover data protected under the unchanged AppHost default. Confidential per-developer override/custody is not enforced or evidenced. Loss or change of a confidential deployment key can make selected encrypted fields and blobs unreadable and, on non-Windows through the wrapped Data Protection key ring, protected credentials unreadable. |
| Derived SQLite key | 32-byte HKDF-SHA256 result using node name and `c0re-node-sqlite\|v1\|...` context | Process memory | Distinct derivation context; zeroed on disposal | Protects selected fields/blobs, not the whole SQLite file. A wrong key may fail only when protected content is read. |
| Derived JWT signing key | 32-byte HKDF-SHA256 result using `c0re-node-jwt\|v1\|...` | Process memory | Distinct derivation context; HMAC-SHA256 JWT signing; zeroed on disposal | Changing the root input invalidates outstanding access tokens. No distributed key rollover is implemented for the local single-node posture. |
| Data Protection key-encryption key | 32-byte HKDF-SHA256 result using `c0re-node-dpkeyring\|v1\|...` on non-Windows | Process memory | Distinct derivation context; wraps new key-ring elements with AES-256-GCM | Wrong/missing secret fails on protected key use; recovery depends on the original root secret and key-ring files. |
| Data Protection key ring | XML key files under `<node-data>/dp-keys` | Node data filesystem | Stable application discriminator. Windows: current-user DPAPI. Non-Windows: new key elements use the derived AES-GCM wrapper and fail-closed resolution on decrypt failure. | Existing legacy plaintext key elements remain readable and are not proactively rewrapped. Key-ring loss can orphan protected credential files. |
| Local operator identity | Email/username, password hash, roles, setup flag, lockout state | Identity SQLite store | ASP.NET Core Identity hashing and lockout behavior; administrator role policy | The database is host-user accessible. No external identity provider, MFA, or access-review process is established. |
| Local access JWT | Subject, token id, issued/expiry time, name, roles | Browser application memory; request headers | HMAC-SHA256; default 15-minute lifetime; issuer/audience validation; cleared by client auth state transitions | Browser or host compromise can steal a live token. Repository evidence does not establish runtime session monitoring. |
| Local refresh token | 64 random bytes | Raw value in `node_rt` browser cookie; SHA-256 hash and status in identity database | `HttpOnly`, `Secure`, `SameSite=Strict`, auth-path-scoped cookie; default 14-day lifetime; rotation revokes prior active tokens; logout/password change revoke active tokens | Cookie theft within the user context can enable replay until rotation, revocation, or expiry. No device binding is implemented. |
| Central WorkerHub credentials | Worker access/refresh tokens, client node ID, binding metadata | `worker-credentials.enc` | Purpose-separated ASP.NET Core Data Protection; owner-oriented filesystem permissions; corrupted/unreadable file is cleared and re-pairing is required | Availability depends on Data Protection keys and external platform state. External token issuance/revocation controls were not reviewed. |
| Cloud provider configuration | Endpoint, API key or Entra identifiers/token configuration, deployment/model definitions | `cloud-credentials.enc` | Purpose-separated Data Protection; owner-oriented filesystem permissions; cryptographic/deserialization failure clears the stored configuration | Provider-side storage, access, retention, and revocation are external. Clearing the local file does not revoke a credential at the provider. |
| Hugging Face token | Optional download token | `hf-token.enc` | Purpose-separated Data Protection; owner-oriented permissions; unreadable token is cleared | Local deletion does not revoke the upstream token or remove previously downloaded models. |
| GitHub update session | User access token and login | `github-token.enc` | Purpose-separated Data Protection; owner-oriented permissions; token is not returned to React | Local clearing is not evidence of upstream revocation. Update provenance and signing limits are covered in Chapter 05. |
| Codex OAuth session | Access token, refresh token, account identifier | `codex-oauth-tokens.enc` | Purpose-separated Data Protection; owner-oriented permissions; unreadable token file is cleared | External account/session controls are outside the repository. Prompt/content sent through this provider crosses an external boundary. |
| Conversations and agent state | Prompts, responses, reasoning/tool data, metadata, run state, schedules, memories, workflow definitions | Node SQLite and managed blob stores | Selected content fields use application-level AES-256-GCM; context-bound AAD reduces row/column substitution | SQLite is not whole-file encrypted. Structural fields and intentionally searchable values remain plaintext. In-memory plaintext exists during use. |
| Knowledge-base material | Original files, extracted chunks, embeddings, indexes, model/vector identity | Encrypted document blobs plus SQLite metadata/chunks/vector tables | Original managed document bytes use AES-GCM blob protection; selected metadata fields may be encrypted | Chunk text and FTS/searchable content remain plaintext by design. Embeddings and metadata may disclose characteristics even without original files. |
| Uploaded chat files | Raw file bytes, extracted Markdown, filename/metadata | Encrypted managed blobs and SQLite records | AES-256-GCM with random nonce; AAD binds conversation ID, file ID, role-specific column name, and schema version | Extracted content exists in plaintext in memory and can be included in a model request. Filename/structural metadata is not uniformly encrypted. |
| Generated images and managed development artifacts | Image bytes, development artifacts and associated metadata | Node data managed file trees and SQLite | Managed blob protection uses the node AES-GCM key path; selected metadata uses field encryption | Exported or copied files inherit destination protection. Deleting catalog rows may not prove deletion from backups or external copies. |
| Selected folders and Development workspaces | Registered host path, alias/ID, repository identity, detached worktree, build/test output, patch evidence | Encrypted selected-folder path in SQLite; worktrees/runtime data under node data; registered repository remains on host | Opaque IDs at API boundary; encrypted persisted host path; canonicalization/identity checks; detached managed worktree; protected path and apply checks | Worktree and executed code are accessible to the host user. Repository code can read other host-user resources despite application path checks. |
| Development command-profile configuration and evidence binding | Profile ID/catalog version, build target, code-owned command snapshot, import provenance digest, artifact canonical-profile digest, artifact protocol version | `development_projects.command_profile_json` as plaintext SQLite `TEXT`; artifact digest/protocol metadata in SQLite | The canonical snapshot is intentionally plaintext non-secret operator/repository-selected configuration; artifacts store a separate 64-hex digest of that snapshot; artifact protocol version is independent | A database reader can inspect the command snapshot. The import can select only code-owned profiles, but backend fallback can occur without the React confirmation control. Plaintext storage must not be described as covered by selected-field encryption. |
| Local models and runtime binaries | GGUF/model weights, llama.cpp or stable-diffusion runtime assets, source-built/runtime downloads | Node model/runtime directories | Download/build validation varies by source; runtime supervisors validate launch/readiness | Models are untrusted executable inputs to native runtimes. Integrity, publisher authenticity, and vulnerability posture are not uniformly attested. |
| Logs and durable execution evidence | Application events, trace IDs, failures, run envelopes, approval records, scheduler/run state | Rolling log files and SQLite | Query-token redaction, sensitive telemetry content off by default, bounded rolling retention, selected metadata-only records | Arbitrary exception/tool/model text may still contain sensitive data. No centralized immutable retention or evidence-custody process was supplied. |
| Browser diagnostics | Redacted breadcrumbs, network metadata, environment/state snapshot, optional Developer Mode recording | Browser IndexedDB; manually exported zip | Known sensitive keys/token query values are redacted; local bounded retention; no automatic upload | Redaction is not a proof that arbitrary content is non-sensitive. Exported zip is not encrypted or signed by the feature. |
| Pre-migration database snapshot | Copy of the node-chat SQLite database | `<node-data>/backups` by default | `VACUUM INTO` preserves encrypted columns as ciphertext; three retained by default | Local, best-effort, migration-triggered only. Identity data, key ring, credentials, files, and models are not shown to be captured as one recoverable set. |

## Cryptographic hierarchy

```text
32-byte operator secret + node name
├── HKDF-SHA256 / c0re-node-sqlite|v1|...   → selected SQLite fields and managed blobs
├── HKDF-SHA256 / c0re-node-jwt|v1|...      → local access-token signing
└── HKDF-SHA256 / c0re-node-dpkeyring|v1|...→ non-Windows Data Protection key-ring wrapping
                                                        │
                                                        └── purpose-separated credential files
```

The three HKDF contexts are intentionally distinct. The hierarchy does not mean that every file is encrypted:

- node SQLite is plain SQLite with application-level field encryption;
- credentials protected by Data Protection depend on both the key ring and its platform/root-secret protection;
- models, runtimes, worktrees, logs, snapshots, and structural/search indexes follow their own storage posture; and
- plaintext necessarily exists in process memory when the application uses protected content.

### AES-GCM framing and binding

`AesGcmNodeAeadCipher` is the shared primitive: 256-bit key, 12-byte nonce, and 16-byte authentication tag. Field and managed-blob protectors generate a random nonce and store `nonce || ciphertext || tag`.

Associated data binds a payload to identifiers such as conversation, record/file, column role, and schema version. This makes simple ciphertext substitution into another protected row or role fail authentication. It does not prevent copying the entire database and key material, denial of service, plaintext capture in memory, or compromise of the host user.

Internal traceability:

- `XE-Local-AI-Engine.Client.Application/Services/Persistence/Implementation/NodeOperatorSecretProvider.cs`
- `XE-Local-AI-Engine.Client.Application/Services/Persistence/Implementation/NodeSqliteKeyHolder.cs`
- `XE-Local-AI-Engine.Client.Application/Services/Auth/Implementation/NodeJwtKeyProvider.cs`
- `XE-Local-AI-Engine.Client/Security/DataProtection/NodeDataProtectionKeyProvider.cs`
- `XE-Local-AI-Engine.Client/ConfigureServices.cs`
- `XE-Local-AI-Engine.Client.Persistence/Cryptography/AesGcmNodeAeadCipher.cs`
- `XE-Local-AI-Engine.Client.Persistence/Cryptography/NodePayloadProtector.cs`
- `XE-Local-AI-Engine.Client.Application/Services/DocumentIngestion/UploadedFileBlobProtector.cs`

Test support:

- `XE-Local-AI-Engine.Client.Persistence.Tests/NodeAeadCipherTests.cs`
- `XE-Local-AI-Engine.Client.Persistence.Tests/PersistenceEncryptionTests.cs`
- `XE-Local-AI-Engine.Client.Persistence.Tests/ConversationUploadedFileStoreTests.cs`
- `XE-Local-AI-Engine.Tests/Security/NodeDataProtectionKeyRingEncryptionTests.cs`

## Authentication lifecycle

| Stage | Baseline behavior | Evidence state and limitation |
|---|---|---|
| First-run setup | Anonymous setup endpoint creates the first administrator. A process-wide semaphore and serializable transaction recheck that setup is not already complete. | Implemented and test-supported. The endpoint still depends on the supported loopback boundary; no out-of-band bootstrap secret is used. |
| Login | ASP.NET Core Identity checks the password with lockout on failure. Login is under the auth rate-limit policy. | Implemented and integration-test-supported. Runtime rate-limit/lockout events were not supplied. |
| Access token | Signed HMAC-SHA256 JWT with subject, unique ID, issued time, name, and role claims; default lifetime 15 minutes. | Implemented and unit/integration-test-supported. Token theft within its lifetime remains possible. |
| Refresh | Raw 64-byte random token stays in the browser cookie; SHA-256 hash is stored. A successful refresh revokes the presented record and issues a new access/refresh pair. | Implemented and test-supported. This is rotation, not device binding or proof-of-possession. |
| Logout/password change | Active refresh tokens are revoked and the browser cookie is cleared. | Implemented and test-supported. Existing access JWTs remain valid until expiry unless another validation condition fails. |
| Credential corruption/key loss | Several local credential stores delete unreadable protected payloads and require sign-in/re-pairing. The non-Windows encrypted Data Protection key ring fails closed rather than silently regenerating after a decrypt failure. | Implemented and test-supported for key scenarios. This favors avoiding silent orphaning but can make the application unavailable until key material is recovered. |

Cookie attributes are `HttpOnly`, `Secure`, `SameSite=Strict`, `IsEssential`, and path-limited to `/api/local/v1/auth`. The React auth store keeps the access token in memory rather than browser local/session storage.

Internal traceability:

- `XE-Local-AI-Engine.Client/Endpoints/Auth/V1/NodeAuthCookie.cs`
- `XE-Local-AI-Engine.Client/Endpoints/Auth/V1/NodeAuthEndpoints.cs`
- `XE-Local-AI-Engine.Client.Application/Configuration/NodeAuthOptions.cs`
- `XE-Local-AI-Engine.Client.Application/Services/Auth/Implementation/NodeAuthService.cs`
- `XE-Local-AI-Engine.Client.Application/Services/Auth/Implementation/NodeTokenService.cs`
- `XE-Local-AI-Engine.Client.React/src/core/auth/stores/NodeAuthStore.tsx`
- `XE-Local-AI-Engine.Tests/Auth/NodeTokenServiceTests.cs`
- `XE-Local-AI-Engine.Tests/Auth/NodeAuthEndpointTests.cs`

## Credential-store lifecycle

Cloud, Hugging Face, GitHub, Codex OAuth, and WorkerHub credentials use dedicated Data Protection purposes and separate `.enc` files. On Windows the key ring is current-user DPAPI protected; on non-Windows new key-ring elements are wrapped with the operator-secret-derived key.

The stores generally:

1. validate and serialize a credential;
2. protect it with a purpose-specific `IDataProtector`;
3. write it under the node data root;
4. apply current-user/owner-only filesystem permissions (`0600` on Linux/macOS); and
5. delete or clear an unreadable cryptographic payload rather than return corrupted plaintext.

Important limits:

- purpose separation is cryptographic domain separation, not separate custody;
- a process running as the same user and able to use the key ring can access protected values;
- clearing the local file does not revoke an upstream token;
- key-ring loss can make every dependent store unreadable;
- repository behavior does not prove that file permissions are correct on a deployed host; and
- no periodic credential rotation or access-review evidence was supplied.

Internal traceability:

- `XE-Local-AI-Engine.Client.Application/Services/CloudProviders/Implementation/CloudCredentialStore.cs`
- `XE-Local-AI-Engine.Client.Application/Services/HuggingFace/HfTokenStore.cs`
- `XE-Local-AI-Engine.Client.Application/Services/AppUpdate/GitHubTokenStore.cs`
- `XE-Local-AI-Engine.Providers.CodexOAuth/Auth/CodexTokenStore.cs`
- `XE-Local-AI-Engine.Client.Application/Services/Auth/Implementation/TokenStore.cs`

## Data movement and egress

| Feature/path | Data that can cross the local-host boundary | Trigger/control | Review boundary |
|---|---|---|---|
| Local llama.cpp / image runtime | Prompts, context, and generated content remain between the host and local child process | Selection of a local provider/model | Child shares host user/network; native-runtime security is not equivalent to data egress prevention |
| Cloud AI provider | Prompt, system instructions, selected context/documents, model parameters, and returned content | Operator config/model selection; provider client; Development cloud authorization seam where applicable | Provider retention, training, logging, region, account policy, and incident handling were not reviewed |
| Codex OAuth | Conversation/request content, account metadata, and provider response | Operator sign-in and Codex provider selection | External OpenAI service boundary; local token protection does not govern provider-side data |
| Hugging Face | Token when configured, repository/model identifiers, download requests, model bytes | Operator model action or application download flow | Upstream authenticity/account controls and downloaded model behavior are separate risks |
| GitHub update/auth | Device/auth flow data, token, release metadata, downloaded release assets | Operator update sign-in/check/download | Release authenticity and signing limitations are in Chapter 05 |
| MCP server | Tool schema, arguments, selected context, output, and server-specific credentials | Server registration and tool invocation/approval policy | Each MCP server is a separate trust domain; exact destinations are deployment-specific |
| Central WorkerHub | Worker identity/tokens, assignments, status, events, and invocation data required by the platform contract | Pairing and outbound connection, off by default for auto-connect | Platform operations and retention were not reviewed |
| OTLP collector | Traces, metrics, logs, and optional sensitive AI telemetry if explicitly enabled | `OTEL_EXPORTER_OTLP_ENDPOINT`; separate sensitive-content option defaults off | Collector destination, filtering, retention, and access controls were not supplied |
| Browser diagnostic export | Redacted local snapshot and optional recording | Manual local zip download | No automatic upload, but later sharing/custody is outside the feature |

No statement in this table proves the absence of other deployment-specific egress. Network observation was not part of the evidence supplied.

## Retention, deletion, and recovery

The repository contains application deletion paths, optional conversation-retention behavior, credential clearing, managed-blob cleanup, browser diagnostic caps, rolling-log caps, and three retained pre-migration database snapshots by default.

Those mechanisms do not establish end-to-end erasure:

- retention is disabled by default for conversations;
- host filesystem remnants, logs, exported files, models, Development worktrees, browser exports, backups, and third-party copies have separate lifecycles;
- external token clearing does not prove provider-side revocation;
- no media-sanitization or deletion-verification procedure was supplied; and
- pre-migration snapshots are not a coordinated backup of the database, identity store, Data Protection keys, credential files, blobs, models, and configuration.

Recovery likewise depends on coordinated possession of ciphertext and the original key material. The repository does not define tested recovery-time/recovery-point objectives, secret escrow, periodic restore, or complete disaster-recovery evidence. See [Chapter 04](04-operations-resilience-and-response.md).

## Key lifecycle gaps

| Gap | Consequence | Evidence status | Owner / acceptance |
|---|---|---|---|
| No complete operator-secret rotation and re-encryption procedure | Rotation can invalidate JWTs and orphan selected database fields and blobs; on non-Windows it can also orphan protected credentials through the wrapped Data Protection key ring if not coordinated | Gap / not evidenced | Owner unassigned; no formal acceptance evidence |
| Shared tracked Aspire development operator-secret default | Source holders can derive the keys for development data created without an override; `secret: true` does not provide confidentiality or per-developer uniqueness | Gap in development default; desktop generates a separate per-installation `node.key` | Owner unassigned; no formal acceptance evidence |
| No documented escrow or recovery custody for the operator secret and Data Protection ring | Loss can cause durable data/credential unavailability | Gap / not evidenced | Owner unassigned; no formal acceptance evidence |
| Legacy plaintext Data Protection key elements are readable after upgrade rather than proactively rewrapped | Older installations can retain plaintext master-key elements until framework rotation replaces their use | Implemented compatibility behavior; remediation lifecycle not evidenced | Owner unassigned; no formal acceptance evidence |
| No deployed asset/data-flow inventory or third-party processing record was supplied | The representative inventory cannot prove completeness for a specific installation | Evidence gap | Owner unassigned; no formal acceptance evidence |
| No deletion or restore operating sample was supplied | Code-defined behavior does not demonstrate deployed erasure or recoverability | Evidence gap | Owner unassigned; no formal acceptance evidence |
