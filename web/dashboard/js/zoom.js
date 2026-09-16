/*
  Digital zoom for a live view.

  Asked for by someone who moved to another system partly because this one had none. Every camera
  benefits: a fixed camera has no other way to look closer, and even a PTZ camera is often quicker
  to zoom here than to move the lens and put it back.

  Deliberately done with a CSS transform on the element that is already showing the picture. The
  MJPEG stream keeps arriving at its own size and nothing extra is asked of the server, so zooming
  costs one repaint on the machine doing the looking. A server-side crop would mean a second
  transcode per viewer, which is the cost this release spent its time removing.

  Zooming shows the pixels that are there, larger. It cannot invent detail the camera did not send,
  so the indicator states the factor rather than implying a better picture.
*/

const MAX_SCALE = 8;
const MIN_SCALE = 1;
const WHEEL_STEP = 1.15;

/**
 * Makes an image zoomable and pannable inside its container.
 * Returns a handle with reset(), and a detach() for when the view goes away.
 */
export function attachZoom(container, image, { onChange } = {}) {
  let scale = 1;
  let offsetX = 0;
  let offsetY = 0;

  let dragging = false;
  let dragStartX = 0;
  let dragStartY = 0;

  // Pinch state, tracked by pointer id so a stray third touch cannot corrupt it.
  const pointers = new Map();
  let pinchDistance = 0;
  let pinchScale = 1;

  container.style.overflow = 'hidden';
  container.style.touchAction = 'none';
  image.style.transformOrigin = '0 0';
  image.style.willChange = 'transform';

  function clamp() {
    scale = Math.min(MAX_SCALE, Math.max(MIN_SCALE, scale));

    // At 1x the picture sits exactly in its box. Beyond that, panning is limited to the part of
    // the image that has been pushed outside, so it can never be dragged off into empty space.
    const width = container.clientWidth;
    const height = container.clientHeight;
    const maxX = Math.max(0, width * (scale - 1));
    const maxY = Math.max(0, height * (scale - 1));

    offsetX = Math.min(0, Math.max(-maxX, offsetX));
    offsetY = Math.min(0, Math.max(-maxY, offsetY));
  }

  function apply() {
    clamp();
    image.style.transform = `translate(${offsetX}px, ${offsetY}px) scale(${scale})`;
    image.style.cursor = scale > 1 ? (dragging ? 'grabbing' : 'grab') : '';
    onChange?.(scale);
  }

  /** Zooms about a point in container coordinates, so what is under the pointer stays there. */
  function zoomAt(factor, pointX, pointY) {
    const previous = scale;
    scale = Math.min(MAX_SCALE, Math.max(MIN_SCALE, scale * factor));
    if (scale === previous) return;

    const ratio = scale / previous;
    offsetX = pointX - (pointX - offsetX) * ratio;
    offsetY = pointY - (pointY - offsetY) * ratio;

    if (scale === 1) { offsetX = 0; offsetY = 0; }
    apply();
  }

  function localPoint(event) {
    const box = container.getBoundingClientRect();
    return { x: event.clientX - box.left, y: event.clientY - box.top };
  }

  const onWheel = (event) => {
    event.preventDefault();
    const { x, y } = localPoint(event);
    zoomAt(event.deltaY < 0 ? WHEEL_STEP : 1 / WHEEL_STEP, x, y);
  };

  const onPointerDown = (event) => {
    pointers.set(event.pointerId, event);

    if (pointers.size === 2) {
      const [a, b] = [...pointers.values()];
      pinchDistance = Math.hypot(a.clientX - b.clientX, a.clientY - b.clientY);
      pinchScale = scale;
      dragging = false;
      return;
    }

    if (scale <= 1) return;
    dragging = true;
    dragStartX = event.clientX - offsetX;
    dragStartY = event.clientY - offsetY;
    container.setPointerCapture?.(event.pointerId);
    apply();
  };

  const onPointerMove = (event) => {
    if (!pointers.has(event.pointerId)) return;
    pointers.set(event.pointerId, event);

    if (pointers.size === 2 && pinchDistance > 0) {
      const [a, b] = [...pointers.values()];
      const distance = Math.hypot(a.clientX - b.clientX, a.clientY - b.clientY);
      const box = container.getBoundingClientRect();
      const midX = (a.clientX + b.clientX) / 2 - box.left;
      const midY = (a.clientY + b.clientY) / 2 - box.top;

      const target = pinchScale * (distance / pinchDistance);
      zoomAt(target / scale, midX, midY);
      return;
    }

    if (!dragging) return;
    offsetX = event.clientX - dragStartX;
    offsetY = event.clientY - dragStartY;
    apply();
  };

  const onPointerUp = (event) => {
    pointers.delete(event.pointerId);
    if (pointers.size < 2) pinchDistance = 0;
    if (pointers.size === 0) dragging = false;
    container.releasePointerCapture?.(event.pointerId);
    apply();
  };

  // Double click is the fastest way back to the whole picture, and the way people already expect
  // to reset a zoomed image.
  const onDoubleClick = (event) => {
    if (scale > 1) { reset(); return; }
    const { x, y } = localPoint(event);
    zoomAt(2, x, y);
  };

  function reset() {
    scale = 1;
    offsetX = 0;
    offsetY = 0;
    apply();
  }

  function zoomBy(factor) {
    zoomAt(factor, container.clientWidth / 2, container.clientHeight / 2);
  }

  container.addEventListener('wheel', onWheel, { passive: false });
  container.addEventListener('pointerdown', onPointerDown);
  container.addEventListener('pointermove', onPointerMove);
  container.addEventListener('pointerup', onPointerUp);
  container.addEventListener('pointercancel', onPointerUp);
  container.addEventListener('dblclick', onDoubleClick);

  apply();

  return {
    reset,
    zoomIn: () => zoomBy(1.5),
    zoomOut: () => zoomBy(1 / 1.5),
    get scale() { return scale; },
    detach() {
      container.removeEventListener('wheel', onWheel);
      container.removeEventListener('pointerdown', onPointerDown);
      container.removeEventListener('pointermove', onPointerMove);
      container.removeEventListener('pointerup', onPointerUp);
      container.removeEventListener('pointercancel', onPointerUp);
      container.removeEventListener('dblclick', onDoubleClick);
      image.style.transform = '';
    },
  };
}
