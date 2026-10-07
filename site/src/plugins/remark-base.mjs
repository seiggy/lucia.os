// Docs are authored with root-relative links (/docs/..., /screenshots/...).
// GitHub Pages serves the project under a base path, so prefix it here.
export default function remarkBase({ base = '' } = {}) {
  const prefix = base.replace(/\/$/, '');
  const fix = (url) => {
    if (!url || !url.startsWith('/') || url.startsWith('//') || url.startsWith(prefix + '/')) return url;
    let [path, hash = ''] = url.split('#');
    if (path.startsWith('/docs') && !path.endsWith('/') && !/\.[a-z0-9]+$/i.test(path)) path += '/';
    return prefix + path + (hash ? '#' + hash : '');
  };
  const walk = (node) => {
    if ((node.type === 'link' || node.type === 'image' || node.type === 'definition') && node.url) node.url = fix(node.url);
    if (node.children) node.children.forEach(walk);
  };
  return (tree) => walk(tree);
}
