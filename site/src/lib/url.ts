const base = import.meta.env.BASE_URL.replace(/\/$/, '');

export const u = (path = '/') => base + (path.startsWith('/') ? path : '/' + path);

export const repo = 'https://github.com/seiggy/lucia.os';

export const sections = [
  { id: 'getting-started', label: 'Getting started' },
  { id: 'architecture', label: 'Architecture' },
  { id: 'reference', label: 'Reference' },
] as const;
