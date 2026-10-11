// Human label for a sandbox role ID from DevelopmentCapabilityService.BuildIsolation ("run_python", "mcp-stdio", ...).
// Lives in core because both `node-settings` (the High profile's refusal list) and `development` (the isolation panel)
// render role IDs, and neither feature owns the concept. An ID without a label falls back to the raw ID.
export function sandboxRoleLabel(t: (key: string, fallback: string) => string, role: string): string {
	return t(`pages.sandboxProfile.roles.${role}`, role);
}
