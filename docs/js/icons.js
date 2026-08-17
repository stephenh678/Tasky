// Minimal inline SVG icon set (stroke-based, currentColor) - kept dependency-free rather than
// pulling in an icon font/library, matching the "build everything natively" constraint.
const ICONS = {
  menu: '<path d="M3 6h18M3 12h18M3 18h18"/>',
  sync: '<path d="M21 12a9 9 0 0 1-15.3 6.4L3 16M3 12a9 9 0 0 1 15.3-6.4L21 8"/><path d="M21 3v5h-5M3 21v-5h5"/>',
  info: '<circle cx="12" cy="12" r="9"/><path d="M12 16v-5M12 8h.01" stroke-linecap="round"/>',
  sun: '<circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4" stroke-linecap="round"/>',
  moon: '<path d="M20 14.5A8.5 8.5 0 1 1 9.5 4a7 7 0 0 0 10.5 10.5z"/>',
  logout: '<path d="M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4"/><path d="M16 17l5-5-5-5M21 12H9" stroke-linecap="round"/>',
  list: '<path d="M8 6h13M8 12h13M8 18h13" stroke-linecap="round"/><circle cx="3.5" cy="6" r="1"/><circle cx="3.5" cy="12" r="1"/><circle cx="3.5" cy="18" r="1"/>',
  repeat: '<path d="M17 1l4 4-4 4M3 11V9a4 4 0 0 1 4-4h14M7 23l-4-4 4-4M21 13v2a4 4 0 0 1-4 4H3" stroke-linecap="round"/>',
  check: '<circle cx="12" cy="12" r="9"/><path d="M8 12.5l2.5 2.5L16 9" stroke-linecap="round" stroke-linejoin="round"/>',
  trash: '<path d="M4 7h16M9 7V4h6v3M6 7l1 13a1 1 0 0 0 1 1h8a1 1 0 0 0 1-1l1-13" stroke-linecap="round" stroke-linejoin="round"/>',
  chevronLeft: '<path d="M15 18l-6-6 6-6" stroke-linecap="round" stroke-linejoin="round"/>',
  chevronRight: '<path d="M9 18l6-6-6-6" stroke-linecap="round" stroke-linejoin="round"/>',
  pin: '<path d="M12 2l1.5 5.5L19 9l-5 3.5.5 6-2.5-3-2.5 3 .5-6-5-3.5 5.5-1.5z" stroke-linejoin="round"/>',
  plus: '<path d="M12 5v14M5 12h14" stroke-linecap="round"/>',
  x: '<path d="M18 6L6 18M6 6l12 12" stroke-linecap="round"/>',
  back: '<path d="M19 12H5M12 19l-7-7 7-7" stroke-linecap="round" stroke-linejoin="round"/>',
  monitor: '<rect x="3" y="4" width="18" height="13" rx="2"/><path d="M8 21h8M12 17v4" stroke-linecap="round"/>',
};

export function icon(name, cls = '') {
  const body = ICONS[name] ?? '';
  return `<svg class="icon ${cls}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">${body}</svg>`;
}
