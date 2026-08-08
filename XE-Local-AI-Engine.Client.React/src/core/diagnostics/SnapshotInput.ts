// Assemble the breadcrumb-derived portion of a snapshot.
//
// The snapshot bundler calls `buildSnapshotInput(kind, error?)` to get the redacted breadcrumbs, the
// network log (network breadcrumbs flattened into `NetworkEntry[]`), and the env. It then adds the
// redacted store `state` and (DevMode) `rrweb` segment before persisting.

import { getAll } from "@/core/diagnostics/BreadcrumbBuffer";
import { collectEnv } from "@/core/diagnostics/Env";
import type { NetworkEntry, SnapshotError, SnapshotInput, SnapshotKind } from "@/core/diagnostics/Types";

/** Build the buffer-derived snapshot input. State + rrweb are filled by the snapshot bundler. */
export function buildSnapshotInput(kind: SnapshotKind, error?: SnapshotError): SnapshotInput {
	const breadcrumbs = getAll();
	const network: NetworkEntry[] = [];
	for (const crumb of breadcrumbs) {
		if (crumb.category === "network") {
			network.push(crumb.entry);
		}
	}

	return {
		kind,
		breadcrumbs,
		network,
		env: collectEnv(),
		...(error === undefined ? {} : { error }),
	};
}
