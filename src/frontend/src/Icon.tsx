const paths = {
  home: <><path d="m3 10 9-7 9 7v10a1 1 0 0 1-1 1h-5v-7H9v7H4a1 1 0 0 1-1-1Z" /></>,
  devices: <><rect x="3" y="3" width="12" height="15" rx="2" /><path d="M7 7h4M7 11h4" /><rect x="17" y="10" width="5" height="11" rx="1" /></>,
  tasks: <><rect x="5" y="4" width="14" height="17" rx="2" /><path d="M9 3h6v3H9zm0 8 2 2 4-4M9 17h6" /></>,
  settings: <><path d="m9 4 1-2h4l1 2 2 1 2-.2 2 3-1 2v3l1 2-2 3-2-.2-2 1-1 2h-4l-1-2-2-1-2 .2-2-3 1-2v-3l-1-2 2-3 2 .2Z" /><circle cx="12" cy="11.5" r="3" /></>,
  spark: <><rect x="4" y="4" width="16" height="16" rx="4" /><path d="M8 8h8v8H8zM9 1v3m6-3v3M9 20v3m6-3v3M1 9h3m-3 6h3m16-6h3m-3 6h3" /></>,
  server: <><rect x="4" y="3" width="16" height="18" rx="3" /><path d="M4 9h16M4 15h16M8 6h.01M8 12h.01M8 18h.01M12 6h4M12 12h4M12 18h4" /></>,
  media: <><rect x="3" y="4" width="18" height="13" rx="3" /><path d="M8 21h8m-4-4v4m-2-10 5 3-5 3Z" /></>,
  drive: <><rect x="4" y="5" width="16" height="14" rx="4" /><path d="M4 14h16M15 17h2" /><circle cx="12" cy="10" r="2" /></>,
  check: <path d="m5 12 4 4L19 6" />,
  attention: <><circle cx="12" cy="12" r="9" /><path d="M12 7v6m0 4h.01" /></>,
  unknown: <><circle cx="12" cy="12" r="9" /><path d="M9.5 8.5a2.5 2.5 0 0 1 5 .5c0 2-2.5 2-2.5 4m0 4h.01" /></>,
  clock: <><circle cx="12" cy="12" r="9" /><path d="M12 7v5l3 2" /></>,
  arrow: <path d="M5 12h14m-5-5 5 5-5 5" />,
  back: <path d="M19 12H5m5-5-5 5 5 5" />,
  chevron: <path d="m9 5 7 7-7 7" />,
  sun: <><circle cx="12" cy="12" r="4" /><path d="M12 2v2m0 16v2M2 12h2m16 0h2M5 5l1.5 1.5m11 11L19 19M5 19l1.5-1.5m11-11L19 5" /></>,
  moon: <path d="M20.5 14A9 9 0 0 1 10 3.5 9 9 0 1 0 20.5 14Z" />,
  system: <><rect x="2" y="3" width="20" height="14" rx="2" /><path d="M8 21h8m-4-4v4" /></>,
  shield: <><path d="m12 2 8 3v6c0 5-8 10-8 10s-8-5-8-10V5Z" /><path d="m8 11 3 3 5-6" /></>,
  refresh: <><path d="M20 4v5h-5M4 20v-5h5" /><path d="M4.5 9a8 8 0 0 1 13-4L20 9M4 15l2.5 4a8 8 0 0 0 13-4" /></>,
  offline: <><path d="M2 2 20 20M8.5 8.5a10 10 0 0 1 11 3M5 11a10 10 0 0 1 1-.8M8 15a6 6 0 0 1 8 0M12 19h.01M3 7a15 15 0 0 1 1.7-1M9 4.4A15 15 0 0 1 21 7" /></>,
  close: <path d="m6 6 12 12M6 18 18 6" />,
  user: <><circle cx="12" cy="8" r="4" /><path d="M4 22v-3a8 8 0 0 1 16 0v3" /></>,
  copy: <><rect x="8" y="8" width="13" height="13" rx="2" /><path d="M16 8V3H3v13h5" /></>,
  stop: <rect x="5" y="5" width="14" height="14" rx="2" />,
  down: <path d="m6 9 6 6 6-6" />,
  search: <><circle cx="10.5" cy="10.5" r="6.5" /><path d="m16 16 5 5" /></>,
} as const

export type IconName = keyof typeof paths

export function Icon({ name, className = '' }: { name: IconName; className?: string }) {
  return <svg className={className} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.75" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">{paths[name]}</svg>
}
