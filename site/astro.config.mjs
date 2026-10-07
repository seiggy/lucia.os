import { defineConfig } from 'astro/config';
import sitemap from '@astrojs/sitemap';
import { unified } from '@astrojs/markdown-remark';
import remarkBase from './src/plugins/remark-base.mjs';
import remarkCallouts from './src/plugins/remark-callouts.mjs';
import remarkShots from './src/plugins/remark-shots.mjs';

const base = '/lucia.os';

export default defineConfig({
  site: 'https://seiggy.github.io',
  base,
  trailingSlash: 'always',
  integrations: [sitemap()],
  markdown: {
    processor: unified({ remarkPlugins: [[remarkBase, { base }], remarkCallouts, remarkShots] }),
    shikiConfig: {
      themes: { light: 'github-light', dark: 'github-dark-dimmed' },
      defaultColor: false,
      wrap: false,
    },
  },
});
