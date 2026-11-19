// Applies the stored colour scheme before the bundle loads, so a dark choice never paints a white page first. It is
// Mantine's ColorSchemeScript logic for ThemeProvider's storage key and its "auto" default (the OS preference); the
// provider sets the same attribute again once it mounts. A classic script, loaded blocking from index.html; Biome
// lints an inline one under index.html's name. Pinned by FirstPaintColorScheme.test.ts.
try {
	const stored = localStorage.getItem("mantine-color-scheme");
	const prefersDark = matchMedia("(prefers-color-scheme: dark)").matches;
	const scheme = stored === "light" || stored === "dark" ? stored : prefersDark ? "dark" : "light";
	document.documentElement.setAttribute("data-mantine-color-scheme", scheme);
} catch {
	// Storage can throw (private mode, blocked site data); Mantine then picks the scheme when it mounts.
}
