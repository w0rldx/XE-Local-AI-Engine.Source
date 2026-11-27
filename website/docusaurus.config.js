import rawImgToImage, { applyAuthoredWidths } from './src/remark/raw-img-to-image.mjs';
import rewriteExternalLinks from './src/remark/rewrite-external-links.mjs';
import { BASE_URL, BRANCH, DOCS_DIR, INCLUDE, REPO_URL, ROUTE_BASE } from './src/lib/scope.mjs';
import { SECTIONS } from './src/lib/sidebar-sections.js';

const sectionLabels = Object.fromEntries(Object.entries(SECTIONS).map(([dir, { label }]) => [dir, label]));

/** @type {import('@docusaurus/types').Config} */
export default {
  title: 'XE Local AI Engine',
  tagline: 'Documentation',
  url: 'https://w0rldx.github.io',
  baseUrl: BASE_URL,
  trailingSlash: false,
  onBrokenLinks: 'throw',
  onBrokenAnchors: 'throw',
  onDuplicateRoutes: 'throw',
  future: { v4: true },
  markdown: { format: 'detect', hooks: { onBrokenMarkdownLinks: 'throw' } },
  presets: [
    [
      'classic',
      {
        docs: {
          path: DOCS_DIR,
          routeBasePath: ROUTE_BASE,
          include: INCLUDE,
          numberPrefixParser: false,
          // raw <img> must become image nodes before links are rewritten, so out-of-set images are caught too
          beforeDefaultRemarkPlugins: [rawImgToImage, rewriteExternalLinks],
          remarkPlugins: [applyAuthoredWidths],
          sidebarPath: './sidebars.js',
          async sidebarItemsGenerator({ defaultSidebarItemsGenerator, categoriesMetadata, ...args }) {
            const items = await defaultSidebarItemsGenerator({
              ...args,
              categoriesMetadata: { ...categoriesMetadata, ...SECTIONS },
            });
            // top-level loose pages are docs, not categories, so their labels are set here
            return items.map((item) =>
              item.type === 'doc' && sectionLabels[item.id] ? { ...item, label: sectionLabels[item.id] } : item,
            );
          },
          editUrl: ({ docPath }) => `${REPO_URL}/edit/${BRANCH}/docs/${docPath}`,
        },
        blog: false,
        theme: {},
      },
    ],
  ],
  themes: [
    [
      '@easyops-cn/docusaurus-search-local',
      { docsRouteBasePath: ROUTE_BASE, docsDir: DOCS_DIR, indexBlog: false, hashed: true },
    ],
  ],
  themeConfig: {
    announcementBar: { id: 'dev', content: 'Development documentation, built from <code>develop</code>', isCloseable: false },
    navbar: {
      title: 'XE Local AI Engine',
      items: [
        { to: '/docs/user-guide', label: 'User guide', position: 'left' },
        { to: '/docs/wiki/Home', label: 'Wiki', position: 'left' },
        { href: REPO_URL, label: 'GitHub', position: 'right' },
      ],
    },
    footer: {
      links: [
        {
          title: 'Project',
          items: [
            { label: 'GitHub repository', href: REPO_URL },
            { label: 'License (Apache-2.0)', href: `${REPO_URL}/blob/${BRANCH}/LICENSE` },
            { label: 'Third-party notices', to: '/third-party-notices' },
          ],
        },
      ],
    },
  },
};
