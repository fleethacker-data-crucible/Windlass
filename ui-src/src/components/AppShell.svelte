<script lang="ts">
  import { onMount, type Component } from 'svelte';
  import { nav, type RouteName } from '$lib/nav.svelte';
  import { prefs } from '$lib/prefs.svelte';
  import Sidebar from './Sidebar.svelte';
  import StatusBar from './StatusBar.svelte';
  import HelpDialog from './HelpDialog.svelte';
  import Overview from '$views/Overview.svelte';
  import Chat from '$views/Chat.svelte';
  import Playbook from '$views/Playbook.svelte';
  import Logs from '$views/Logs.svelte';
  import SettingsView from '$views/Settings.svelte';

  const views: Record<RouteName, Component> = { overview: Overview, chat: Chat, playbook: Playbook, logs: Logs, settings: SettingsView };
  const View = $derived(views[nav.name]);

  // Under 1024 px the sidebar collapses by itself (Fleet UI section 5).
  let narrow = $state(typeof matchMedia !== 'undefined' && matchMedia('(max-width: 1023px)').matches);
  let help = $state(false);
  let goPending = false;

  onMount(() => {
    nav.start();
    const mq = matchMedia('(max-width: 1023px)');
    const onMq = () => (narrow = mq.matches);
    mq.addEventListener('change', onMq);
    return () => mq.removeEventListener('change', onMq);
  });

  function typing(t: EventTarget | null) {
    const el = t as HTMLElement | null;
    return !!el && (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA' || el.tagName === 'SELECT' || el.isContentEditable);
  }

  function onKey(e: KeyboardEvent) {
    if (typing(e.target) || e.ctrlKey || e.metaKey || e.altKey || document.querySelector('dialog[open]')) return;
    if (goPending) {
      goPending = false;
      const map: Record<string, RouteName> = { o: 'overview', c: 'chat', p: 'playbook', l: 'logs', s: 'settings' };
      const r = map[e.key.toLowerCase()];
      if (r) {
        e.preventDefault();
        nav.go(r);
      }
      return;
    }
    if (e.key === '?') {
      e.preventDefault();
      help = true;
    } else if (e.key === 'g') {
      goPending = true;
      setTimeout(() => (goPending = false), 1200);
    }
  }
</script>

<svelte:window onkeydown={onKey} />

<div class="flex h-full min-h-0 flex-col" data-testid="app-shell">
  <div class="flex min-h-0 flex-1">
    <Sidebar collapsed={prefs.sidebarCollapsed || narrow} />
    <main class="flex min-w-0 flex-1 flex-col bg-surface" id="main" data-route={nav.name}>
      <View />
    </main>
  </div>
  <StatusBar />
</div>
<HelpDialog bind:open={help} />
