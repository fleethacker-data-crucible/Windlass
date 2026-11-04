// A promise-based confirm dialog; destructive actions always go through it (Fleet UI section 6).
export interface ConfirmRequest {
  title: string;
  description: string;
  confirmLabel: string;
  destructive?: boolean;
}

class Confirm {
  request = $state<ConfirmRequest | null>(null);
  private resolve: ((ok: boolean) => void) | null = null;
  ask(r: ConfirmRequest): Promise<boolean> {
    this.resolve?.(false);
    this.request = r;
    return new Promise((res) => (this.resolve = res));
  }
  answer(ok: boolean) {
    this.request = null;
    this.resolve?.(ok);
    this.resolve = null;
  }
}
export const confirmHost = new Confirm();
export const confirm = (r: ConfirmRequest) => confirmHost.ask(r);
