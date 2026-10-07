// Wrap portal screenshots in docs with a provenance caption, like Shot.astro on the landing page.
const routes = { home: '#/', devices: '#/devices', apps: '#/apps', updates: '#/updates', models: '#/ai/models', domains: '#/settings/domains', jobs: '#/ai/jobs', playground: '#/ai' };
const esc = (s = '') => s.replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/</g, '&lt;');

export default function remarkShots({ captured = '2026-10-07' } = {}) {
  return (tree) => {
    const walk = (node) => {
      if (!node.children) return;
      node.children = node.children.map((child) => {
        const img = child.type === 'paragraph' && child.children.length === 1 ? child.children[0] : null;
        const m = img?.type === 'image' && /\/screenshots\/([\w-]+)\.png$/.exec(img.url);
        if (!m) { walk(child); return child; }
        const route = routes[m[1]] ?? '';
        return {
          type: 'html',
          value: `<figure class="shot"><img src="${esc(img.url)}" alt="${esc(img.alt)}" width="1440" height="900" loading="lazy" decoding="async" /><figcaption>Captured from lucia.homelab.seiggy.com/${esc(route)} on ${captured}. Unretouched.</figcaption></figure>`,
        };
      });
    };
    walk(tree);
  };
}