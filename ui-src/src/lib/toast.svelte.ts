export type ToastKind = 'info' | 'success' | 'error';
export interface Toast {
  id: number;
  kind: ToastKind;
  text: string;
}

class Toasts {
  items = $state<Toast[]>([]);
  private n = 0;
  push(kind: ToastKind, text: string) {
    const id = ++this.n;
    this.items = [...this.items, { id, kind, text }].slice(-4);
    // Errors stay until dismissed; everything else goes after 5 s (Fleet UI section 6).
    if (kind !== 'error') setTimeout(() => this.dismiss(id), 5000);
  }
  dismiss(id: number) {
    this.items = this.items.filter((t) => t.id !== id);
  }
}
export const toasts = new Toasts();
export const toast = {
  info: (t: string) => toasts.push('info', t),
  success: (t: string) => toasts.push('success', t),
  error: (t: string) => toasts.push('error', t)
};
