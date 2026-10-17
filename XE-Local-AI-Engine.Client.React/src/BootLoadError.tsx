// Shown when the translation bundle failed to load. Plain English and inline styles on purpose: without the bundle
// there is nothing to translate with, and the Mantine theme provider lives inside the app this replaces.
export function BootLoadError({ onRetry }: { onRetry: () => void }) {
	return (
		<main role="alert" style={{ fontFamily: "system-ui, sans-serif", margin: "4rem auto", maxWidth: 480, padding: 16 }}>
			<h1 style={{ fontSize: "1.25rem" }}>The interface could not load</h1>
			<p>Part of the app failed to download. Check that the node is still running, then try again.</p>
			<button type="button" onClick={onRetry}>
				Retry
			</button>
		</main>
	);
}
