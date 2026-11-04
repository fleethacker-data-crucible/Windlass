// Whether the model picker is open. Opened from the Overview, the Chat page and the status when no model is loaded.
class Picker {
  open = $state(false);
  show() {
    this.open = true;
  }
}
export const picker = new Picker();
