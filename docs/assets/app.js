import { buildLp5, encodeKey, KEY_TABLE, DEVICES } from "./lp5.js";

const $ = (sel, root = document) => root.querySelector(sel);
const el = (tag, props = {}, ...kids) => {
  const n = Object.assign(document.createElement(tag), props);
  for (const k of kids.flat()) n.append(k?.nodeType ? k : document.createTextNode(k));
  return n;
};

/* ---------------- theme ---------------- */

const root = document.documentElement;
const stored = localStorage.getItem("theme");
if (stored) root.dataset.theme = stored;
$("#theme-toggle").addEventListener("click", () => {
  const now = root.dataset.theme
    || (matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light");
  const next = now === "dark" ? "light" : "dark";
  root.dataset.theme = next;
  localStorage.setItem("theme", next);
});

/* ---------------- data ---------------- */

const state = { specs: null, spec: null, selected: 0, locale: "en-GB" };

const MOD_LIST = [
  ["cmd", "Cmd"],
  ["ctrl", "Ctrl"],
  ["alt", "Opt"],
  ["shift", "Shift"],
];

// Keys worth offering in a dropdown, grouped so the list stays navigable.
const KEY_GROUPS = {
  Letters: Object.keys(KEY_TABLE).filter((k) => /^Key[A-Z]$/.test(k)).sort(),
  Digits: Object.keys(KEY_TABLE).filter((k) => /^Key[0-9]$/.test(k)).sort(),
  Editing: ["Return", "Tab", "Space", "Escape", "Backspace", "Delete"],
  Arrows: ["ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight"],
  Punctuation: ["Minus", "Equals", "Period", "Comma", "Oem2", "Oem4", "Oem5", "Oem6", "Oem7"],
  Function: Object.keys(KEY_TABLE).filter((k) => /^F\d+$/.test(k))
    .sort((a, b) => +a.slice(1) - +b.slice(1)),
};

const KEY_PRETTY = {
  Oem2: "Oem2  /", Oem4: "Oem4  [", Oem5: "Oem5  `", Oem6: "Oem6  ]", Oem7: "Oem7  \\",
  Minus: "Minus  -", Equals: "Equals  =", Period: "Period  .", Comma: "Comma  ,",
};

const clone = (o) => JSON.parse(JSON.stringify(o));

async function load() {
  const res = await fetch("assets/specs.json");
  if (!res.ok) throw new Error(`could not load specs.json (${res.status})`);
  state.specs = await res.json();
  renderCards();
  initBuilder();
}

/* ---------------- previews ---------------- */

function keyFace(button, opts = {}) {
  if (!button || !button.steps?.length) {
    return el("div", { className: "key empty" }, opts.placeholder ?? "");
  }
  const node = el("div", { className: "key" });
  node.style.background = button.color || "#24262c";
  if (button.glyph) node.append(el("span", { className: "glyph" }, button.glyph));
  node.append(el("span", { className: "label" }, button.title || ""));
  return node;
}

function devicePreview(spec) {
  const dev = DEVICES[spec.device];
  const grid = el("div", {
    className: spec.device === "dialpad" ? "grid-dialpad" : "grid-keypad",
  });
  for (let i = 0; i < dev.press; i++) grid.append(keyFace((spec.buttons || [])[i]));
  return grid;
}

/* ---------------- ready-made profile cards ---------------- */

function renderCards() {
  const host = $("#profile-cards");
  host.replaceChildren();

  // Group keypad + dialpad variants of the same product together.
  const groups = new Map();
  for (const spec of state.specs.profiles) {
    if (!groups.has(spec.name)) groups.set(spec.name, []);
    groups.get(spec.name).push(spec);
  }

  for (const [name, specs] of groups) {
    const card = el("div", { className: "card" });
    card.append(el("h3", {}, name));
    card.append(el("p", {}, specs[0].description || ""));

    const previews = el("div", { className: "previews" });
    for (const spec of specs) {
      const col = el("div");
      col.append(devicePreview(spec));
      col.append(el("p", {
        style: "font-size:11.5px;color:var(--muted);margin:6px 0 0;text-align:center",
      }, spec.device));
      previews.append(col);
    }
    card.append(previews);

    const dl = el("div", { className: "dl" });
    for (const spec of specs) {
      const btn = el("button", { type: "button" }, `Download ${spec.device}`);
      btn.addEventListener("click", () => download(spec, btn));
      dl.append(btn);
    }
    card.append(dl);
    host.append(card);
  }
}

async function download(spec, btn) {
  const original = btn.textContent;
  btn.disabled = true;
  btn.textContent = "Building…";
  try {
    const { blob, filename } = await buildLp5(spec, { locale: state.locale });
    const url = URL.createObjectURL(blob);
    const a = Object.assign(document.createElement("a"), { href: url, download: filename });
    document.body.append(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 2000);
    btn.textContent = "Downloaded";
  } catch (err) {
    console.error(err);
    btn.textContent = "Failed";
    alert(`Could not build the profile:\n\n${err.message}`);
  } finally {
    setTimeout(() => { btn.textContent = original; btn.disabled = false; }, 1400);
  }
}

/* ---------------- builder ---------------- */

function initBuilder() {
  const preset = $("#preset");
  state.specs.profiles.forEach((spec, i) => {
    preset.append(el("option", { value: String(i) }, `${spec.name} — ${spec.device}`));
  });
  preset.append(el("option", { value: "blank-keypad" }, "Blank — keypad"));
  preset.append(el("option", { value: "blank-dialpad" }, "Blank — dialpad"));

  preset.addEventListener("change", () => {
    loadPreset(preset.value);
    renderBuilder();
  });
  $("#locale").addEventListener("change", (e) => {
    state.locale = e.target.value;
    renderEditor();
  });
  $("#download").addEventListener("click", (e) => download(state.spec, e.target));

  loadPreset("0");
  renderBuilder();
}

function loadPreset(value) {
  if (value.startsWith("blank")) {
    const device = value.endsWith("dialpad") ? "dialpad" : "keypad";
    const dev = DEVICES[device];
    state.spec = {
      name: "My Profile",
      device,
      description: "",
      buttons: Array.from({ length: dev.press }, () => ({
        title: "", glyph: "", color: "#2A2E35", steps: [], note: "",
      })),
      dials: Array.from({ length: dev.rotate }, () => ({
        title: "", color: "#2A2E35", left: [], right: [], press: [], note: "",
      })),
    };
  } else {
    state.spec = clone(state.specs.profiles[Number(value)]);
    const dev = DEVICES[state.spec.device];
    state.spec.buttons = state.spec.buttons || [];
    while (state.spec.buttons.length < dev.press) {
      state.spec.buttons.push({ title: "", glyph: "", color: "#2A2E35", steps: [] });
    }
  }
  state.selected = 0;
}

function renderBuilder() {
  const grid = $("#device-grid");
  const dev = DEVICES[state.spec.device];
  grid.className = state.spec.device === "dialpad" ? "grid-dialpad" : "grid-keypad";
  grid.replaceChildren();

  for (let i = 0; i < dev.press; i++) {
    const b = state.spec.buttons[i];
    const face = keyFace(b, { placeholder: String(i) });
    face.setAttribute("role", "button");
    face.tabIndex = 0;
    face.setAttribute("aria-current", String(i === state.selected));
    const pick = () => { state.selected = i; renderBuilder(); };
    face.addEventListener("click", pick);
    face.addEventListener("keydown", (e) => {
      if (e.key === "Enter" || e.key === " ") { e.preventDefault(); pick(); }
    });
    grid.append(face);
  }

  $("#grid-hint").textContent =
    state.spec.device === "dialpad"
      ? "4 buttons. Dial actions are kept from the preset and included in the download."
      : "9 keys, top-left to bottom-right. Click one to edit it.";

  renderEditor();
}

function renderEditor() {
  const host = $("#editor");
  const b = state.spec.buttons[state.selected];
  host.replaceChildren();

  host.append(
    field("Profile name", inputText(state.spec.name, (v) => {
      state.spec.name = v;
    })),
  );

  host.append(el("h3", { style: "margin:6px 0 0" }, `Key ${state.selected + 1}`));

  host.append(field("Label", inputText(b.title, (v) => {
    b.title = v; refreshFace();
  })));

  const glyphRow = el("div", { className: "row-inline" });
  const glyphInput = inputText(b.glyph || "", (v) => { b.glyph = v; refreshFace(); });
  glyphInput.style.maxWidth = "90px";
  glyphInput.placeholder = "◉";
  const colorInput = el("input", { type: "color", value: normaliseHex(b.color) });
  colorInput.addEventListener("input", () => { b.color = colorInput.value; refreshFace(); });
  glyphRow.append(glyphInput, colorInput,
    el("span", { style: "font-size:12.5px;color:var(--muted)" }, "glyph and colour"));
  host.append(field("Face", glyphRow));

  host.append(field("Actions", stepsEditor(b)));

  if (b.note) {
    host.append(el("p", { style: "font-size:13px;color:var(--muted);margin:0" }, b.note));
  }

  const encoded = (b.steps || [])
    .map((s) => (s.text !== undefined
      ? `type: ${s.text}`
      : safeEncode(s)))
    .join("\n");
  if (encoded) host.append(el("div", { className: "encoded" }, encoded));
}

function safeEncode(step) {
  try {
    return encodeKey(step.key, step.mods || [], null, state.locale);
  } catch (err) {
    return `(cannot encode: ${err.message})`;
  }
}

function stepsEditor(button) {
  const wrap = el("div", { className: "steps" });
  button.steps = button.steps || [];

  button.steps.forEach((step, idx) => {
    const row = el("div", { className: "step" });
    const body = el("div", { className: "step-body" });

    const kind = el("select");
    kind.append(el("option", { value: "key" }, "Keystroke"));
    kind.append(el("option", { value: "text" }, "Type text"));
    kind.value = step.text !== undefined ? "text" : "key";
    kind.addEventListener("change", () => {
      if (kind.value === "text") { delete step.key; delete step.mods; step.text = ""; }
      else { delete step.text; step.key = "Return"; step.mods = []; }
      renderEditor();
    });
    body.append(kind);

    if (step.text !== undefined) {
      body.append(inputText(step.text, (v) => { step.text = v; renderEditor(); },
        "/compact"));
    } else {
      const select = el("select");
      for (const [group, keys] of Object.entries(KEY_GROUPS)) {
        const og = el("optgroup", { label: group });
        for (const k of keys) {
          og.append(el("option", { value: k },
            KEY_PRETTY[k] || (/^Key[A-Z0-9]$/.test(k) ? k.slice(3) : k)));
        }
        select.append(og);
      }
      select.value = step.key || "Return";
      select.addEventListener("change", () => { step.key = select.value; renderEditor(); });
      body.append(select);

      const mods = el("div", { className: "mods" });
      step.mods = step.mods || [];
      for (const [id, label] of MOD_LIST) {
        const cb = el("input", { type: "checkbox", checked: step.mods.includes(id) });
        cb.addEventListener("change", () => {
          step.mods = cb.checked
            ? [...step.mods, id]
            : step.mods.filter((m) => m !== id);
          renderEditor();
        });
        mods.append(el("label", { className: "chk" }, cb, label));
      }
      body.append(mods);
    }

    row.append(body);
    const del = el("button", { type: "button", title: "Remove this step" }, "✕");
    del.addEventListener("click", () => {
      button.steps.splice(idx, 1);
      refreshFace();
      renderEditor();
    });
    row.append(del);
    wrap.append(row);
  });

  const add = el("div", { className: "row-inline" });
  const addKey = el("button", { type: "button" }, "+ keystroke");
  addKey.addEventListener("click", () => {
    button.steps.push({ key: "Return", mods: [] });
    refreshFace(); renderEditor();
  });
  const addText = el("button", { type: "button" }, "+ text");
  addText.addEventListener("click", () => {
    button.steps.push({ text: "" });
    refreshFace(); renderEditor();
  });
  add.append(addKey, addText);
  wrap.append(add);
  return wrap;
}

/* Repaint just the device grid, keeping the editor's focus intact. */
function refreshFace() {
  const grid = $("#device-grid");
  const dev = DEVICES[state.spec.device];
  [...grid.children].forEach((node, i) => {
    if (i >= dev.press) return;
    const fresh = keyFace(state.spec.buttons[i], { placeholder: String(i) });
    fresh.setAttribute("role", "button");
    fresh.tabIndex = 0;
    fresh.setAttribute("aria-current", String(i === state.selected));
    fresh.addEventListener("click", () => { state.selected = i; renderBuilder(); });
    node.replaceWith(fresh);
  });
}

/* ---------------- small helpers ---------------- */

function field(label, control) {
  const row = el("div", { className: "row" });
  row.append(el("label", {}, label), control);
  return row;
}

function inputText(value, onInput, placeholder = "") {
  const input = el("input", { type: "text", value: value ?? "", placeholder });
  let timer;
  input.addEventListener("input", () => {
    clearTimeout(timer);
    timer = setTimeout(() => onInput(input.value), 120);
  });
  return input;
}

function normaliseHex(v) {
  const m = /^#?([0-9a-f]{6})$/i.exec(String(v || ""));
  return m ? `#${m[1]}` : "#2a2e35";
}

load().catch((err) => {
  console.error(err);
  document.querySelector("#profile-cards").append(
    el("p", { style: "color:#b3261e" }, `Failed to load: ${err.message}`),
  );
});
