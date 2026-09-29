// tasks.js — tickable task-list checkboxes (§7.2 `taskToggle`).
//
// Only the checkboxes the renderer produced carry `data-line` (the sanitizer strips a
// forged one), so a click on one of those is a request to edit that source line. The box
// is left showing the state the click gave it — the re-render the host sends back is the
// source of truth and will correct it if the write didn't happen.

import { send } from "./bridge.js";

// Version of the render currently on screen; travels with the message so the host can
// drop a click that was made on an outdated page.
let renderVersion = 0;

export function setTaskVersion(version) {
  renderVersion = version | 0;
}

function onClick(event) {
  const box = event.target;
  if (
    !box ||
    box.tagName !== "INPUT" ||
    box.type !== "checkbox" ||
    typeof box.getAttribute !== "function"
  ) {
    return;
  }

  const attribute = box.getAttribute("data-line");
  if (attribute === null) return;

  const line = Number.parseInt(attribute, 10);
  if (!Number.isInteger(line) || line < 1) {
    event.preventDefault();
    return;
  }

  // `checked` already holds the new state here: the activation behavior runs before the
  // click event is dispatched.
  send({ type: "taskToggle", line, checked: box.checked, version: renderVersion });
}

// One delegated listener on the container, so it survives every innerHTML replacement.
export function initTasks(container) {
  container.addEventListener("click", onClick);
}
