// Labels and order for the top-level docs entries, applied by the docs plugin's sidebarItemsGenerator.
// They live here instead of _category_.json files, which would show up in GitHub's docs/ listings, and not in
// sidebars.js because Docusaurus reads every export of that file as a sidebar.
export const SECTIONS = {
  'user-guide': { label: 'User guide', position: 1 },
  wiki: { label: 'Wiki', position: 2, link: { type: 'doc', id: 'Home' } },
  adr: { label: 'ADRs', position: 3 },
  runbooks: { label: 'Runbooks', position: 4 },
  security: { label: 'Security', position: 5 },
  'agentic-support': { label: 'Agentic support', position: 6 },
  troubleshooting: { label: 'Troubleshooting', position: 7 },
};
