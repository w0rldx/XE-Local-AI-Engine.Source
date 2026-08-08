/**
 * dependency-cruiser configuration — architecture boundary gate for the React app.
 *
 * Policy: "green now, no new debt". Native error-severity rules fail immediately, while
 * `scripts/CheckDependencyBaseline.mjs` rejects any rule/from/to fingerprint absent from
 * the committed warn baseline. Paying debt down needs no config edit; broad warning promotion can wait
 * until the corresponding baseline reaches zero. See QUALITY-GATE.md.
 *
 * `.cjs` (not `.js`) because package.json declares `"type": "module"`.
 */
module.exports = {
  forbidden: [
    {
      name: "no-circular",
      comment:
        "Circular dependencies make modules impossible to reason about in isolation and break " +
        "tree-shaking. The codebase is currently free of cycles, so this is enforced as an error.",
      severity: "error",
      from: { pathNot: "node_modules" },
      to: { circular: true },
    },
    {
      name: "no-orphans",
      comment:
        "Orphan modules (imported by nothing, importing nothing) are usually dead code. " +
        "Informational only: generated clients, type declarations, and config files are legitimate orphans.",
      severity: "warn",
      from: {
        orphan: true,
        pathNot: [
          "\\.d\\.ts$",
          "(^|/)src/core/api/generated/",
          "src/routeTree\\.gen\\.ts$",
          "(^|/)[^/]+\\.(config|cjs|mjs)\\.(js|ts|cjs|mjs)$",
          "(^|/)[^/]+\\.config\\.(js|ts|cjs|mjs)$",
          "\\.cjs$",
        ],
      },
      to: {},
    },
    {
      name: "no-core-to-features",
      comment:
        "TRACKED DEBT (promote to error later): core/ is the shared foundation and must not depend on " +
        "feature/ modules. Known violation: HeaderBar -> features/about.",
      severity: "warn",
      from: { path: "^src/core" },
      to: { path: "^src/features" },
    },
    {
      name: "no-core-to-legacy",
      comment:
        "TRACKED DEBT (promote to error later): core/ must not reach into the legacy " +
        "data/components/modules/pages trees. Known violation: navigation/header reach into data/components/modules.",
      severity: "warn",
      from: { path: "^src/core" },
      to: { path: "^src/(data|components|modules|pages)" },
    },
    {
      name: "no-cross-feature",
      comment:
        "TRACKED DEBT (promote to error later): a feature should not import another feature's internals; " +
        "shared code belongs in core/. Known cross-feature edges: model-fit<->models, agents->chat/tools/skills, " +
        "mcp->tools, preview->chat/agents, etc.",
      severity: "warn",
      from: { path: "^src/features/([^/]+)/" },
      to: {
        path: "^src/features/([^/]+)/",
        pathNot: "^src/features/$1/",
      },
    },
    {
      name: "no-feature-to-routes",
      comment:
        "TRACKED DEBT (promote to error later): features should be route-agnostic; the routes/ tree wires them, " +
        "not the other way around.",
      severity: "warn",
      from: { path: "^src/features" },
      to: { path: "^src/routes" },
    },
    {
      name: "not-to-test",
      comment:
        "Production source must never import test files. Enforced as an error (codebase is clean).",
      severity: "error",
      from: { pathNot: "\\.(test|spec)\\.(ts|tsx|js|jsx)$" },
      to: { path: "\\.(test|spec)\\.(ts|tsx|js|jsx)$" },
    },
    {
      name: "not-to-dev-dep",
      comment:
        "Production source must not import devDependencies (they are absent from the production bundle). " +
        "Enforced as an error (codebase is clean).",
      severity: "error",
      from: {
        path: "^src",
        pathNot: "\\.(test|spec)\\.(ts|tsx|js|jsx)$|\\.d\\.ts$",
      },
      to: {
        dependencyTypes: ["npm-dev"],
        dependencyTypesNot: ["type-only"],
        pathNot: ["node_modules/@types/"],
      },
    },
  ],
  options: {
    tsConfig: { fileName: "tsconfig.json" },
    tsPreCompilationDeps: true,
    doNotFollow: { path: "node_modules" },
    exclude: {
      path:
        "(^|/)(node_modules|dist|coverage)/|src/core/api/generated/|src/routeTree\\.gen\\.ts$|\\.test\\.(ts|tsx)$",
    },
    enhancedResolveOptions: {
      extensions: [".ts", ".tsx", ".js", ".jsx", ".json"],
      mainFields: ["module", "main", "types", "typings"],
    },
  },
};
