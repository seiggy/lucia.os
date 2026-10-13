import {themes as prismThemes} from 'prism-react-renderer';
import type {Config} from '@docusaurus/types';
import type * as Preset from '@docusaurus/preset-classic';

// The landing page lives in ../site (Astro) at /lucia.os/. This site is mounted under /lucia.os/docs/ by the Pages workflow.
const config: Config = {
  title: 'Lucia docs',
  tagline: 'Who it is for, what it does, and why it works the way it does.',
  favicon: 'img/favicon.svg',
  future: {v4: true},

  url: 'https://seiggy.github.io',
  baseUrl: '/lucia.os/docs/',
  organizationName: 'seiggy',
  projectName: 'lucia.os',
  trailingSlash: true,
  onBrokenLinks: 'throw',
  markdown: {
    mermaid: true,
    hooks: {onBrokenMarkdownLinks: 'throw'},
  },
  themes: ['@docusaurus/theme-mermaid'],

  presets: [
    [
      'classic',
      {
        docs: {
          routeBasePath: '/',
          sidebarPath: './sidebars.ts',
          editUrl: 'https://github.com/seiggy/lucia.os/edit/main/docs-site/',
        },
        blog: false,
        theme: {customCss: './src/css/custom.css'},
      } satisfies Preset.Options,
    ],
  ],

  themeConfig: {
    colorMode: {respectPrefersColorScheme: true},
    navbar: {
      title: 'Lucia docs',
      logo: {alt: 'Lucia', src: 'img/logo.svg'},
      items: [
        {type: 'docSidebar', sidebarId: 'guide', position: 'left', label: 'Using Lucia'},
        {type: 'docSidebar', sidebarId: 'deploy', position: 'left', label: 'Deploying'},
        {type: 'docSidebar', sidebarId: 'code', position: 'left', label: 'Building Lucia'},
        {href: 'https://seiggy.github.io/lucia.os/', label: 'Lucia home', position: 'right'},
        {href: 'https://github.com/seiggy/lucia.os', label: 'GitHub', position: 'right'},
      ],
    },
    footer: {
      style: 'dark',
      copyright: 'Lucia is a self-hosted homelab platform. A documentation agent keeps these docs current when code changes.',
    },
    prism: {
      theme: prismThemes.github,
      darkTheme: prismThemes.dracula,
      additionalLanguages: ['csharp', 'bash', 'powershell', 'yaml', 'json', 'docker', 'python', 'diff'],
    },
    mermaid: {theme: {light: 'neutral', dark: 'dark'}},
  } satisfies Preset.ThemeConfig,
};

export default config;
