// A tiny hash router: #/overview, #/chat, #/playbook, #/logs, #/settings.
export const ROUTES = ['overview', 'chat', 'playbook', 'logs', 'settings'] as const;
export type RouteName = (typeof ROUTES)[number];

function parse(): RouteName {
  const name = (typeof location !== 'undefined' ? location.hash : '').replace(/^#\/?/, '') as RouteName;
  return (ROUTES as readonly string[]).includes(name) ? name : 'overview';
}

class Nav {
  name = $state<RouteName>(parse());
  start() {
    window.addEventListener('hashchange', () => (this.name = parse()));
  }
  go(name: RouteName) {
    if (location.hash !== `#/${name}`) location.hash = `#/${name}`;
    this.name = name;
  }
}
export const nav = new Nav();
