// Provider tags the local-models catalog stamps on its entries (backend `LocalModelProviders`). Only the ones the
// client actually branches on live here; they sit in core/ because several features — the chat picker, the model
// store page, the landing page — have to agree on the same string.

/**
 * The single multiplexer provider serving every operator-registered external OpenAI-compatible endpoint.
 *
 * One tag covers all connections, which is why the per-connection identity travels separately (in
 * `externalConnectionId`). Entries carrying it are catalog registrations, not files in this node's model store.
 */
export const EXTERNAL_PROVIDER = "external";
