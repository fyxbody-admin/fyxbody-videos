// The mobile header can wrap as labels, fonts or available width change.
// Keep its actual height on the shell so a fixed menu never covers account controls.
export function observeHeaderHeight(shell, Observer = globalThis.ResizeObserver) {
  const header = shell.querySelector('.topbar');
  if (!header) return () => {};
  const update = () => shell.style.setProperty('--topbar-height', `${Math.ceil(header.getBoundingClientRect().height)}px`);
  update();
  if (!Observer) return () => {};
  const observer = new Observer(update);
  observer.observe(header);
  return () => observer.disconnect();
}
