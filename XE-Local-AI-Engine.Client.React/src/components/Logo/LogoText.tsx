interface LogoTextProperties {
	className?: string;
}

// Inline SVG (not <img>) so the wordmark inherits `currentColor`; color is set to
// the Mantine primary so the logo always matches the active theme's primary color.
export function LogoText({ className = "object-contain h-full w-full" }: LogoTextProperties) {
	return (
		<svg
			className={className}
			xmlns="http://www.w3.org/2000/svg"
			width="818"
			height="108"
			viewBox="0 0 818 108"
			fill="currentColor"
			role="img"
			aria-label="AI Engine"
			style={{ color: "var(--mantine-primary-color-filled)" }}
		>
			<path
				d="M 0 97 L 43 9 L 63 9 L 106 97 L 88 97 L 78 76 L 28 76 L 18 97 Z M 36 62 L 53 27 L 70 62 Z"
				fillRule="evenodd"
			/>
			<rect x="124" y="9" width="16" height="88" />
			<path d="M 225 9 L 306 9 L 306 24 L 225 24 L 225 46 L 299 46 L 299 60 L 225 60 L 225 82 L 306 82 L 306 97 L 225 97 L 210 82 L 210 24 Z" />
			<rect x="326" y="9" width="16" height="88" />
			<rect x="402" y="9" width="16" height="88" />
			<polygon points="342,9 358,9 402,97 386,97" />
			<polygon points="440,28 455,13 455,93 440,78" />
			<polygon points="458,9 528,9 546,22 546,24 531,24 522,24 464,24 455,33 440,33 440,27" />
			<polygon points="440,79 455,73 464,82 522,82 531,73 546,73 546,84 528,97 458,97 440,84" />
			<rect x="531" y="46" width="15" height="37" />
			<rect x="496" y="46" width="50" height="14" />
			<rect x="568" y="9" width="16" height="88" />
			<rect x="606" y="9" width="16" height="88" />
			<rect x="682" y="9" width="16" height="88" />
			<polygon points="622,9 638,9 682,97 666,97" />
			<path d="M 737 9 L 818 9 L 818 24 L 737 24 L 737 46 L 811 46 L 811 60 L 737 60 L 737 82 L 818 82 L 818 97 L 737 97 L 722 82 L 722 24 Z" />
		</svg>
	);
}
