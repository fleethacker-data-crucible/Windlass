<script lang="ts">
  import { nav, ROUTES, type RouteName } from '$lib/nav.svelte';
  import { prefs } from '$lib/prefs.svelte';
  import { session } from '$lib/session.svelte';
  import { confirm } from '$lib/confirm.svelte';
  import BrandMark from './BrandMark.svelte';
  import Gauge from '@lucide/svelte/icons/gauge';
  import MessageSquare from '@lucide/svelte/icons/message-square';
  import NotebookText from '@lucide/svelte/icons/notebook-text';
  import ScrollText from '@lucide/svelte/icons/scroll-text';
  import Settings from '@lucide/svelte/icons/settings';
  import Power from '@lucide/svelte/icons/power';
  import PanelLeftClose from '@lucide/svelte/icons/panel-left-close';
  import PanelLeftOpen from '@lucide/svelte/icons/panel-left-open';

  let { collapsed }: { collapsed: boolean } = $props();

  const items: Record<RouteName, { label: string; icon: typeof Gauge }> = {
    overview: { label: 'Overview', icon: Gauge },
    chat: { label: 'Chat', icon: MessageSquare },
    playbook: { label: 'Playbook', icon: NotebookText },
    logs: { label: 'Log', icon: ScrollText },
    settings: { label: 'Settings', icon: Settings }
  };

  async function endSession() {
    const owns = session.status?.autoStop.ownsLife;
    const ok = await confirm({
      title: 'End session?',
      description: `Windlass stops, and any reply it is writing stops with it. Programs that use its API, such as Fairlead, lose the model until you start it again with Launch-Windlass.bat.${owns ? '' : ' Closing this tab alone does not stop it.'}`,
      confirmLabel: 'End session',
      destructive: true
    });
    if (ok) await session.endSession();
  }
</script>

<aside class="flex h-full shrink-0 flex-col border-r border-border bg-sidebar transition-[width] duration-150 {collapsed ? 'w-[var(--fl-sidebar-collapsed)]' : 'w-[var(--fl-sidebar-width)]'}" aria-label="Windlass navigation">
  <div class="flex h-14 items-center {collapsed ? 'justify-center' : 'px-4'}"><BrandMark size={34} withText={!collapsed} /></div>
  <nav class="fl-nav min-h-0 flex-1 overflow-y-auto py-2" aria-label="Sections">
    <ul class="grid gap-0.5">
      {#each ROUTES as r (r)}
        {@const it = items[r]}
        <li>
          <button class="fl-nav-item {collapsed ? 'justify-center px-0' : ''}" aria-current={nav.name === r ? 'page' : undefined} title={collapsed ? it.label : undefined} aria-label={collapsed ? it.label : undefined} onclick={() => nav.go(r)} data-testid={`nav-${r}`}>
            <it.icon aria-hidden="true" />{#if !collapsed}<span>{it.label}</span>{/if}
          </button>
        </li>
      {/each}
    </ul>
  </nav>
  <div class="grid gap-0.5 border-t border-border py-2">
    <button class="fl-nav-item {collapsed ? 'justify-center px-0' : ''}" onclick={endSession} title={collapsed ? 'End session' : undefined} aria-label={collapsed ? 'End session' : undefined} data-testid="nav-end"><Power aria-hidden="true" />{#if !collapsed}<span>End session</span>{/if}</button>
    <button class="fl-nav-item max-[1023px]:hidden {collapsed ? 'justify-center px-0' : ''}" onclick={() => prefs.setSidebar(!collapsed)} aria-label={collapsed ? 'Expand the sidebar' : 'Collapse the sidebar'} aria-pressed={collapsed} data-testid="nav-collapse">
      {#if collapsed}<PanelLeftOpen aria-hidden="true" />{:else}<PanelLeftClose aria-hidden="true" /><span>Collapse</span>{/if}
    </button>
  </div>
</aside>
