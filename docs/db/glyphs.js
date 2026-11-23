// Our own line glyphs (24x24, stroke = currentColor). No game art is used anywhere on this site.
const P = {
  // weapons
  sword: '<path d="M14.5 3.5H20.5V9.5L9 21 3 15Z"/><path d="M6.5 13.5 10.5 17.5"/><path d="M4 20 2.5 21.5"/>',
  sword2h: '<path d="M16 2h6v6L10 20l-6-6Z"/><path d="M5 11l8 8"/><path d="M3 21l3-3"/><path d="M14 4l6 6"/>',
  dagger: '<path d="M18 3l3 3-9 9-3-3Z"/><path d="M8 11l5 5"/><path d="M8.5 15.5 4 20"/>',
  scimitar: '<path d="M5 19C12 17 18 11 20 3c-4 5-9 9-14 12"/><path d="M4.5 14.5l5 5"/><path d="M5 19l-2 2"/>',
  spear: '<path d="M3 21 16 8"/><path d="M16 8l1-5 4 0 0 4-5 1"/><path d="M6 15l3 3"/>',
  mallet: '<path d="M4 20 14 10"/><path d="M11 5l8 8 3-3-8-8Z"/>',
  claw: '<path d="M6 21c0-6 2-10 6-13"/><path d="M10 21c0-5 2-9 6-12"/><path d="M14 21c0-4 2-7 6-10"/><path d="M4 21h14"/>',
  whip: '<path d="M4 20c2-2 2-5 0-7"/><path d="M4 13c4-6 12-9 17-6-5 0-9 3-11 8"/>',
  bow: '<path d="M6 3c9 2 13 8 15 15"/><path d="M6 3 21 18"/><path d="M4 21l8-8"/><path d="M4 18v3h3"/>',
  crossbow: '<path d="M3 9c6-4 12-4 18 0"/><path d="M12 6v15"/><path d="M3 9l9 4 9-4"/><path d="M10 19h4"/>',
  javelin: '<path d="M3 21 19 5"/><path d="M19 5l2-2"/><path d="M16 4l4 4"/><path d="M5 17l2 2"/>',
  boomerang: '<path d="M4 20 10 4h4l-4 10 10 2v4Z"/>',
  hammer: '<path d="M5 20 15 10"/><path d="M12 4l8 8"/><path d="M14 3l7 7-2 2-7-7Z"/>',
  staff: '<path d="M6 21 17 7"/><circle cx="18.5" cy="5.5" r="2.5"/><path d="M14 4l1 2M21 9l-2-1"/>',
  wand: '<path d="M4 20 14 10"/><path d="M17 3l1 2.5 2.5 1-2.5 1L17 10l-1-2.5L13.5 6.5 16 5.5Z"/>',
  // armour
  helmet: '<path d="M4 15a8 8 0 0 1 16 0v4H4Z"/><path d="M12 7v12"/><path d="M8 15h8"/>',
  cape: '<path d="M8 3h8l4 18H4Z"/><path d="M8 3c0 3 8 3 8 0"/>',
  amulet: '<path d="M5 3c0 7 3 10 7 11 4-1 7-4 7-11"/><path d="M12 14l3 3-3 4-3-4Z"/>',
  torso: '<path d="M8 3 4 6v6h3v9h10v-9h3V6l-4-3c-1 2-7 2-8 0Z"/>',
  legs: '<path d="M6 3h12l1 18h-5l-2-11-2 11H5Z"/>',
  gloves: '<path d="M7 21v-6L4 11l2-2 2 2V4h2v6V3h2v7V4h2v7l2-3 2 1-3 7v5Z"/>',
  boots: '<path d="M7 3h6v10l7 4v4H5Z"/><path d="M5 17h15"/>',
  ring: '<circle cx="12" cy="14" r="6"/><path d="M10 5h4l1.5 2.5L12 9 8.5 7.5Z"/>',
  shield: '<path d="M12 3 20 6v6c0 5-4 8-8 9-4-1-8-4-8-9V6Z"/><path d="M12 3v18"/>',
  ammo: '<path d="M3 21 17 7"/><path d="M17 7l1-4 3 3-4 1"/><path d="M5 15l4 4M3 17l4 4"/>',
  // other kinds
  food: '<path d="M3 12c3-5 9-7 14-4l4-3v14l-4-3c-5 3-11 1-14-4Z"/><circle cx="13" cy="11" r="1"/>',
  potion: '<path d="M9 3h6"/><path d="M10 3v5L5 17a3 3 0 0 0 3 4h8a3 3 0 0 0 3-4l-5-9V3"/><path d="M7 15h10"/>',
  tool: '<path d="M3 21l9-9"/><path d="M8 6c4-4 9-3 12 0-4-1-7 0-9 3Z"/><path d="M11 9l4 4"/>',
  utility: '<circle cx="12" cy="12" r="3"/><path d="M12 2v3M12 19v3M2 12h3M19 12h3M5 5l2 2M17 17l2 2M5 19l2-2M17 7l2-2"/>',
  trap: '<path d="M3 17h18"/><path d="M5 17l2-8 3 5 2-8 2 8 3-5 2 8"/>',
  bait: '<path d="M6 18c-3-3 0-7 3-5s5-2 3-5 2-5 5-2"/><circle cx="18.5" cy="5.5" r="1"/>',
  compost: '<path d="M5 19C5 10 11 5 20 4c-1 9-6 15-15 15Z"/><path d="M5 19 13 11"/>',
  bones: '<path d="M7 17 17 7"/><circle cx="5.5" cy="16.5" r="2"/><circle cx="7.5" cy="18.5" r="2"/><circle cx="16.5" cy="5.5" r="2"/><circle cx="18.5" cy="7.5" r="2"/>',
  chest: '<path d="M3 10h18v10H3Z"/><path d="M3 10c0-4 3-6 9-6s9 2 9 6"/><path d="M11 12h2v3h-2Z"/>',
  material: '<path d="M12 3 20 8v8l-8 5-8-5V8Z"/><path d="M12 3v18M4 8l8 5 8-5"/>',
  // sources
  skull: '<path d="M5 11a7 7 0 0 1 14 0v4h-2v4H7v-4H5Z"/><circle cx="9" cy="12" r="1.5"/><circle cx="15" cy="12" r="1.5"/><path d="M11 19v-2M13 19v-2"/>',
  castle: '<path d="M4 21V9l2-1v-3h2v3h2V5h4v3h2V5h2v3l2 1v12Z"/><path d="M10 21v-5a2 2 0 0 1 4 0v5"/>',
  hand: '<path d="M8 13V5a1.5 1.5 0 0 1 3 0v6V4a1.5 1.5 0 0 1 3 0v7V6a1.5 1.5 0 0 1 3 0v8c0 4-3 7-6 7-2 0-4-1-5-3l-3-5a1.5 1.5 0 0 1 2.5-1.5Z"/>',
  axe: '<path d="M5 21 15 11"/><path d="M12 4c4 0 7 2 8 6l-4 1-5-5Z"/>',
  pick: '<path d="M4 20 14 10"/><path d="M5 7c5-4 10-4 14 0-4-2-7-1-9 1"/><path d="M15 6l3 3"/>',
  fish: '<path d="M3 12c3-5 9-7 14-4l4-3v14l-4-3c-5 3-11 1-14-4Z"/><circle cx="13" cy="11" r="1"/>',
  paw: '<circle cx="6" cy="10" r="2"/><circle cx="10" cy="6" r="2"/><circle cx="14" cy="6" r="2"/><circle cx="18" cy="10" r="2"/><path d="M8 18c0-4 2-6 4-6s4 2 4 6c0 2-2 3-4 2-2 1-4 0-4-2Z"/>',
  anvil: '<path d="M3 8h13c0 3 2 4 5 4v2H14l-1 3h3v3H7v-3h3l-1-3H6c-2 0-3-2-3-6Z"/>',
  coin: '<circle cx="12" cy="12" r="8"/><path d="M14.5 9.5c-.5-1-1.5-1.5-2.5-1.5-1.5 0-2.5 1-2.5 2s1 1.6 2.5 2c1.5.4 2.5 1 2.5 2s-1 2-2.5 2c-1 0-2-.5-2.5-1.5M12 6.5v11"/>',
  scroll: '<path d="M7 3h11a2 2 0 0 1 0 4h-1v12a2 2 0 0 1-2 2H6a2 2 0 0 1 0-4h1Z"/><path d="M10 9h4M10 13h4"/>',
  up: '<path d="M12 20V5"/><path d="M6 11l6-6 6 6"/>',
  star: '<path d="M12 3l2.7 5.6 6.3.9-4.5 4.4 1 6.1L12 17l-5.5 3 1-6.1L3 9.5l6.3-.9Z"/>',
  leaf: '<path d="M5 19C5 10 11 5 20 4c-1 9-6 15-15 15Z"/><path d="M5 19 13 11"/>',
  sprout: '<path d="M12 21v-8"/><path d="M12 13c0-4-3-6-7-6 0 4 3 6 7 6Z"/><path d="M12 11c0-4 3-6 7-6 0 4-3 6-7 6Z"/>',
  fire: '<path d="M12 21c-4 0-7-3-7-7 0-5 5-6 5-11 3 2 4 5 4 7 1-1 2-2 2-4 2 2 3 5 3 8 0 4-3 7-7 7Z"/>',
  run: '<circle cx="14" cy="4" r="2"/><path d="M9 21l3-6 3 3v3M6 12l3-4 5 1 2 4 3 1"/><path d="M12 15l-2-4"/>',
  // ui
  search: '<circle cx="11" cy="11" r="7"/><path d="M20 20l-4-4"/>',
  filter: '<path d="M3 5h18l-7 8v6l-4 2v-8Z"/>',
  close: '<path d="M6 6l12 12M18 6 6 18"/>',
  link: '<path d="M10 14a4 4 0 0 0 6 0l3-3a4 4 0 0 0-6-6l-1 1"/><path d="M14 10a4 4 0 0 0-6 0l-3 3a4 4 0 0 0 6 6l1-1"/>',
  compare: '<path d="M8 3v18M16 3v18"/><path d="M3 8h5M16 16h5"/>',
  book: '<path d="M4 4h7a3 3 0 0 1 3 3v14a2 2 0 0 0-2-2H4Z"/><path d="M20 4h-6M20 4v15h-6"/>',
  all: '<rect x="4" y="4" width="7" height="7" rx="1"/><rect x="13" y="4" width="7" height="7" rx="1"/><rect x="4" y="13" width="7" height="7" rx="1"/><rect x="13" y="13" width="7" height="7" rx="1"/>',
  back: '<path d="M15 5l-7 7 7 7"/>',
};

const WEAPON = {
  'Sword': 'sword', '2H Sword': 'sword2h', 'Greatsword': 'sword2h', 'Katana': 'sword', 'Twin Blades': 'dagger',
  'Dagger': 'dagger', 'Scimitar': 'scimitar', 'Spear': 'spear', 'Mallet': 'mallet', 'Morningstar': 'mallet',
  'Claw': 'claw', 'Whip': 'whip', 'Bow': 'bow', 'Crossbow': 'crossbow', '2H Crossbow': 'crossbow',
  'Javelin': 'javelin', 'Boomerang': 'boomerang', 'Thrown Hammer': 'hammer', 'Throw Hammer': 'hammer',
  'Staff': 'staff', 'Wand': 'wand', 'Melee': 'hand',
};
const SLOT = {
  Helmet: 'helmet', Cape: 'cape', Amulet: 'amulet', Torso: 'torso', Legs: 'legs', Gloves: 'gloves',
  Boots: 'boots', Ring: 'ring', Offhand: 'shield', Ammo: 'ammo', Weapon: 'sword',
};
const CAT = {
  Food: 'food', Potion: 'potion', Tool: 'tool', Utility: 'utility', Trap: 'trap', Bait: 'bait', Compost: 'compost',
  Bones: 'bones', Openable: 'chest', Material: 'material', Ammo: 'ammo',
};

export function svg(name, extra = '') {
  return `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"${extra}>${P[name] || P.material}</svg>`;
}

export function glyphOf(item) {
  if (item.wtype) return WEAPON[item.wtype] || 'sword';
  if (item.slot) return SLOT[item.slot] || 'material';
  return CAT[item.cat] || 'material';
}

export const has = (name) => name in P;
