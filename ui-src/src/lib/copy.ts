import { toast } from './toast.svelte';

export async function copyText(text: string, what = 'Copied'): Promise<void> {
  try {
    await navigator.clipboard.writeText(text);
    toast.success(`${what} to the clipboard.`);
  } catch {
    toast.error('The browser did not allow copying. Select the text and press Ctrl+C.');
  }
}
