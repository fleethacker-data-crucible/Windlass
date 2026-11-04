// Per-viewer conveniences kept in localStorage, always inside try/catch: the app renders right without storage.
// Keys start with the lower-case app name (Fleet UI section 9).
export type Theme = 'system' | 'dark' | 'light';

function read(key: string): string | null {
  try {
    return localStorage.getItem(key);
  } catch {
    return null;
  }
}
function write(key: string, value: string | null) {
  try {
    if (value == null) localStorage.removeItem(key);
    else localStorage.setItem(key, value);
  } catch {
    /* storage unavailable */
  }
}

export function applyTheme(theme: Theme) {
  const root = document.documentElement;
  if (theme === 'system') root.removeAttribute('data-theme');
  else root.setAttribute('data-theme', theme);
}

class Prefs {
  theme = $state<Theme>((read('windlass-theme') as Theme) === 'dark' ? 'dark' : read('windlass-theme') === 'light' ? 'light' : 'system');
  sidebarCollapsed = $state(read('windlass-sidebar') === 'collapsed');
  /** The chat's last settings, so a long reply can be repeated with the same limits. */
  maxTokens = $state(Number(read('windlass-max-tokens')) || 256);

  setTheme(t: Theme) {
    this.theme = t;
    write('windlass-theme', t === 'system' ? null : t);
    applyTheme(t);
  }
  setSidebar(collapsed: boolean) {
    this.sidebarCollapsed = collapsed;
    write('windlass-sidebar', collapsed ? 'collapsed' : null);
  }
  setMaxTokens(n: number) {
    this.maxTokens = n;
    write('windlass-max-tokens', String(n));
  }
}

export const prefs = new Prefs();

/** The API key lives for this tab only (sessionStorage); it is never written to localStorage. */
export const keyStore = {
  load: () => {
    try {
      return sessionStorage.getItem('windlass-api-key') ?? '';
    } catch {
      return '';
    }
  },
  save: (key: string) => {
    try {
      if (key) sessionStorage.setItem('windlass-api-key', key);
      else sessionStorage.removeItem('windlass-api-key');
    } catch {
      /* storage unavailable: the key is asked for again after a reload */
    }
  }
};
