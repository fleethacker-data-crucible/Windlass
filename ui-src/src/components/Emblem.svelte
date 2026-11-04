<script lang="ts">
  // The full emblem (transparent PNGs): ivory wordmark on dark, dark-blue wordmark on light. Follows the chosen theme,
  // or the system one when the theme is left on "Follow the system".
  import { onMount } from 'svelte';
  import { prefs } from '$lib/prefs.svelte';
  let { size = 160 }: { size?: number } = $props();
  let systemDark = $state(typeof matchMedia === 'undefined' ? true : !matchMedia('(prefers-color-scheme: light)').matches);
  onMount(() => {
    const mq = matchMedia('(prefers-color-scheme: light)');
    const on = () => (systemDark = !mq.matches);
    mq.addEventListener('change', on);
    return () => mq.removeEventListener('change', on);
  });
  const dark = $derived(prefs.theme === 'system' ? systemDark : prefs.theme === 'dark');
</script>

<img src={dark ? './assets/emblem-dark.png' : './assets/emblem-light.png'} alt="" width={size} height={size} class="object-contain" />
