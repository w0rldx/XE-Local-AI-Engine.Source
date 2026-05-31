import { z } from "zod";

// Mirrors the backend McpTransportKind enum (Stdio=0, Http=1). The wire contract carries the string form.
// Stdio launches a local process by command/args/env/cwd; Http connects to an already-running loopback-only
// server by URL (see dynamic tool-catalog plan §2.1 / §0.1).
export type McpTransportKind = "Stdio" | "Http";

export const mcpTransportKinds: readonly McpTransportKind[] = ["Stdio", "Http"];

// A single stdio environment variable. The form edits env as an ordered list of key/value pairs; the API
// layer projects it to the wire map<string,string>. Modeled as a list (not a Record) so the editor can keep
// blank/duplicate rows while the user types without losing focus or collapsing keys.
export interface McpEnvEntry {
	readonly key: string;
	readonly value: string;
}

// Domain view-model for a registered MCP server. Secret-bearing fields (description, args, env) are encrypted
// at rest on the node; here they are plaintext for editing. Timestamps are epoch milliseconds (long on the
// wire). A server is disabled on register (locked P4 decision 3) — enabled is toggled explicitly by the user.
export interface McpServerRegistration {
	readonly id: string;
	readonly name: string;
	readonly description: string;
	readonly transportKind: McpTransportKind;
	readonly command: string | null;
	readonly arguments: readonly string[];
	readonly workingDirectory: string | null;
	readonly env: readonly McpEnvEntry[];
	readonly url: string | null;
	readonly enabled: boolean;
	readonly version: number;
	readonly createdAtUtc: number;
	readonly updatedAtUtc: number;
}

// Form values are narrower than the persisted entity: identity/version/timestamps are backend-managed. The
// enabled flag is NOT edited in the create/update form — enabling is a separate, deliberate action (the
// strict default gate), surfaced as its own row toggle, so registering never auto-connects/launches.
export interface McpServerFormValues {
	name: string;
	description: string;
	transportKind: McpTransportKind;
	command: string;
	arguments: string[];
	workingDirectory: string;
	env: McpEnvEntry[];
	url: string;
}

const transportKindSchema = z.enum(["Stdio", "Http"]);

const envEntrySchema = z.object({
	key: z.string(),
	value: z.string(),
});

// Loopback-only HTTP URL guard (locked P4 decision 1). A node may only reach a loopback MCP server by URL;
// pointing it at a remote host is out of scope and rejected client-side (the backend re-validates).
const loopbackHosts = new Set(["127.0.0.1", "localhost", "::1", "[::1]"]);

function isLoopbackUrl(value: string): boolean {
	try {
		const parsed = new URL(value);
		if (parsed.protocol !== "http:" && parsed.protocol !== "https:") {
			return false;
		}
		return loopbackHosts.has(parsed.hostname.toLowerCase());
	} catch {
		return false;
	}
}

// Zod schema validating the form before submit. Name is required (non-empty after trim). Transport-specific
// required fields are enforced conditionally: Stdio needs a non-empty Command; Http needs a loopback URL.
// env keys must be non-empty when the row carries a value (a value with no key can never be applied).
export const mcpServerFormSchema = z
	.object({
		name: z.string().trim().min(1).max(120),
		description: z.string().max(2000),
		transportKind: transportKindSchema,
		command: z.string(),
		arguments: z.array(z.string()),
		workingDirectory: z.string(),
		env: z.array(envEntrySchema),
		url: z.string(),
	})
	.superRefine((value, ctx) => {
		if (value.transportKind === "Stdio") {
			if (value.command.trim().length === 0) {
				ctx.addIssue({ code: "custom", message: "Command is required for stdio transport", path: ["command"] });
			}
		} else {
			const trimmedUrl = value.url.trim();
			if (trimmedUrl.length === 0) {
				ctx.addIssue({ code: "custom", message: "URL is required for HTTP transport", path: ["url"] });
			} else if (!isLoopbackUrl(trimmedUrl)) {
				ctx.addIssue({ code: "custom", message: "URL must point at a loopback host", path: ["url"] });
			}
		}

		for (const [index, entry] of value.env.entries()) {
			if (entry.value.trim().length > 0 && entry.key.trim().length === 0) {
				ctx.addIssue({ code: "custom", message: "Environment key is required", path: ["env", index, "key"] });
			}
		}
	});

export type McpServerFormSchema = z.infer<typeof mcpServerFormSchema>;

export { isLoopbackUrl };
