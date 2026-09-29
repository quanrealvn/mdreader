// table-tool.js — the grid on /markdown-table-generator.
//
// The Markdown in the editor is the one source of truth. The grid reads it, writes it back
// on every keystroke, and never holds state the text does not. That is what makes the two
// halves agree: paste a table into the editor and the grid picks it up; type in a cell and
// the editor shows the pipe table it produced.
//
// The grid is only rebuilt when the text changed underneath it (a paste, a file, a typed
// edit), never when the grid itself wrote the text. Rebuilding on its own write would take
// the focus out of the cell being typed in.

/** Column alignments, in the order the alignment control offers them. */
const ALIGNMENTS = [
  { value: "left", label: "Left" },
  { value: "center", label: "Centre" },
  { value: "right", label: "Right" },
];

const DEFAULT_MODEL = {
  headers: ["Column", "Column"],
  rows: [["", ""]],
  align: ["left", "left"],
};

/**
 * @param {HTMLElement} builder the container to reveal and fill
 * @param {HTMLTextAreaElement} input the editor holding the Markdown
 * @param {() => void} onWrite called after the grid has written new text into the editor
 */
export function initTableBuilder(builder, input, onWrite) {
  const grid = builder.querySelector("#mdr-tool-grid");
  if (!grid) return;

  let model = parse(input.value) ?? structuredClone(DEFAULT_MODEL);
  let writing = false;

  // The box is already laid out at its finished height (site.css, :root[data-js]); this
  // only makes its contents visible, so nothing below it moves.
  builder.classList.add("is-ready");

  function publish() {
    writing = true;
    input.value = format(model);
    writing = false;
    onWrite();
  }

  function draw(focus) {
    grid.replaceChildren(buildHead(), buildBody());
    if (focus) {
      const cell = grid.querySelector(focus);
      if (cell instanceof HTMLInputElement) cell.focus();
    }
  }

  function buildHead() {
    const head = document.createElement("thead");
    const labels = document.createElement("tr");
    const controls = document.createElement("tr");

    model.headers.forEach((text, column) => {
      const cell = document.createElement("th");
      cell.appendChild(cellInput(text, `Heading of column ${column + 1}`, (value) => {
        model.headers[column] = value;
        publish();
      }));
      labels.appendChild(cell);

      const control = document.createElement("th");
      control.appendChild(alignmentSelect(column));
      controls.appendChild(control);
    });

    head.append(labels, controls);
    return head;
  }

  function alignmentSelect(column) {
    const select = document.createElement("select");
    select.className = "mdr-tool-align";
    select.setAttribute("aria-label", `Alignment of column ${column + 1}`);
    for (const option of ALIGNMENTS) {
      const element = document.createElement("option");
      element.value = option.value;
      element.textContent = option.label;
      if (model.align[column] === option.value) element.selected = true;
      select.appendChild(element);
    }
    select.addEventListener("change", () => {
      model.align[column] = select.value;
      publish();
    });
    return select;
  }

  function buildBody() {
    const body = document.createElement("tbody");
    model.rows.forEach((row, rowIndex) => {
      const tr = document.createElement("tr");
      row.forEach((text, column) => {
        const td = document.createElement("td");
        td.appendChild(cellInput(text, `Row ${rowIndex + 1}, column ${column + 1}`, (value) => {
          model.rows[rowIndex][column] = value;
          publish();
        }));
        tr.appendChild(td);
      });
      body.appendChild(tr);
    });
    return body;
  }

  function cellInput(value, label, onInput) {
    const element = document.createElement("input");
    element.type = "text";
    element.value = value;
    element.setAttribute("aria-label", label);
    element.addEventListener("input", () => onInput(element.value));
    return element;
  }

  builder.addEventListener("click", (event) => {
    const action = event.target instanceof Element ? event.target.closest("[data-grid]") : null;
    if (!action) return;

    const columns = model.headers.length;
    switch (action.getAttribute("data-grid")) {
      case "add-row":
        model.rows.push(new Array(columns).fill(""));
        break;
      case "remove-row":
        if (model.rows.length > 1) model.rows.pop();
        break;
      case "add-column":
        model.headers.push("Column");
        model.align.push("left");
        for (const row of model.rows) row.push("");
        break;
      case "remove-column":
        if (columns > 1) {
          model.headers.pop();
          model.align.pop();
          for (const row of model.rows) row.pop();
        }
        break;
      default:
        return;
    }

    publish();
    draw(null);
  });

  input.addEventListener("input", () => {
    if (writing) return; // the grid's own write, already reflected in the model
    const parsed = parse(input.value);
    if (!parsed) return; // not a table any more: leave the grid as it was
    model = parsed;
    draw(null);
  });

  // Drawn from the text, and the text is left exactly as it was: rewriting it here would
  // reformat someone's table before they touched anything, and would cost a render request
  // on every visit to the page.
  draw(null);
}

/**
 * Reads the first pipe table out of `text`. Returns null when there is not one, which is
 * the signal to leave the grid alone rather than to empty it.
 * @param {string} text
 */
export function parse(text) {
  const lines = text.split(/\r\n|\r|\n/);
  for (let i = 0; i < lines.length - 1; i++) {
    const headers = splitRow(lines[i]);
    const delimiters = splitRow(lines[i + 1]);
    if (!headers || !delimiters || headers.length !== delimiters.length) continue;
    if (!delimiters.every(isDelimiter)) continue;

    const rows = [];
    for (let j = i + 2; j < lines.length; j++) {
      const row = splitRow(lines[j]);
      if (!row) break;
      rows.push(fit(row, headers.length));
    }

    return {
      headers,
      align: delimiters.map(alignmentOf),
      rows: rows.length > 0 ? rows : [new Array(headers.length).fill("")],
    };
  }
  return null;
}

/** Turns a model back into Markdown, with the columns padded so the source stays readable. */
export function format(model) {
  const widths = model.headers.map((header, column) => {
    const cells = [escapeCell(header), ...model.rows.map((row) => escapeCell(row[column] ?? ""))];
    return Math.max(3, ...cells.map((cell) => cell.length));
  });

  const line = (cells) =>
    "| " + cells.map((cell, column) => cell.padEnd(widths[column], " ")).join(" | ") + " |";

  const dashes = model.align.map((alignment, column) => {
    const width = widths[column];
    switch (alignment) {
      case "center":
        return ":" + "-".repeat(Math.max(1, width - 2)) + ":";
      case "right":
        return "-".repeat(Math.max(1, width - 1)) + ":";
      default:
        // Left is the default, so it is written the way people write it: no colon.
        return "-".repeat(width);
    }
  });

  return [
    line(model.headers.map(escapeCell)),
    line(dashes),
    ...model.rows.map((row) => line(model.headers.map((_, column) => escapeCell(row[column] ?? "")))),
  ].join("\n") + "\n";
}

/** A pipe inside a cell would start the next one, so it is escaped on the way out. */
function escapeCell(text) {
  return String(text).replace(/\|/g, "\\|").trim();
}

/** Splits a table row on unescaped pipes, or returns null if the line is not one. */
function splitRow(line) {
  if (typeof line !== "string") return null;
  const trimmed = line.trim();
  if (!trimmed.includes("|")) return null;

  const cells = [];
  let current = "";
  for (let i = 0; i < trimmed.length; i++) {
    const c = trimmed[i];
    if (c === "\\" && trimmed[i + 1] === "|") {
      current += "|";
      i++;
    } else if (c === "|") {
      cells.push(current);
      current = "";
    } else {
      current += c;
    }
  }
  cells.push(current);

  // The outer pipes are optional; when they are there they leave an empty cell at each end.
  if (cells.length > 1 && cells[0].trim() === "" && trimmed.startsWith("|")) cells.shift();
  if (cells.length > 1 && cells[cells.length - 1].trim() === "" && trimmed.endsWith("|")) cells.pop();

  return cells.length === 0 ? null : cells.map((cell) => cell.trim());
}

function isDelimiter(cell) {
  return /^:?-+:?$/.test(cell);
}

function alignmentOf(cell) {
  const left = cell.startsWith(":");
  const right = cell.endsWith(":");
  if (left && right) return "center";
  if (right) return "right";
  return "left";
}

function fit(row, width) {
  const cells = row.slice(0, width);
  while (cells.length < width) cells.push("");
  return cells;
}
