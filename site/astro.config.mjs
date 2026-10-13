import { defineConfig } from 'astro/config';
import sitemap from '@astrojs/sitemap';

// The docs are a separate Docusaurus site (../docs-site), mounted under /docs/ when the Pages workflow publishes.
const base = '/lucia.os';

export default defineConfig({
  site: 'https://seiggy.github.io',
  base,
  trailingSlash: 'always',
  integrations: [sitemap({ filter: page => !page.includes('/docs/') })],
});
