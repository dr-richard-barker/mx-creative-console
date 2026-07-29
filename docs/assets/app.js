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

const state = {
  specs: null,
  spec: null,
  // Which control the editor is pointing at: a press button or a dial.
  selected: { kind: "button", i: 0 },
  locale: "en-GB",
};

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

/*
 * Device geometry, traced from Logitech's labelled hardware diagram.
 * Values are percentages of the device body; circles derive their height from
 * the body's aspect ratio so they stay round.
 *
 * Control-id mapping is inferred from the profiles Logitech ships:
 *   press 0,1  the top-left pair   (Undo / Redo by default; always a pair -
 *              Back/Forward, Cmd+[ / Cmd+], Escape/Return)
 *   press 2    bottom-left         (Escape by default)
 *   press 3    bottom-right        (Show Actions Ring; a profile switch in
 *              every shipped profile)
 *   rotate 0   the ROLLER, top right - the default profile binds it to
 *              Volume, and System Volume is the roller's hardware default
 *   rotate 1   the big centre dial - default Scroll, matching the dial's
 *              "Contextual + Vertical Scroll" hardware label
 */
const DIALPAD_ASPECT = 386 / 327;
const DIALPAD_GEOMETRY = {
  buttons: [
    { left: 2.0, top: 7.3, w: 12.0, hardware: "Undo" },
    { left: 16.8, top: 6.7, w: 12.0, hardware: "Redo" },
    { left: 2.0, top: 80.1, w: 13.0, hardware: "Escape" },
    { left: 65.0, top: 80.1, w: 13.0, hardware: "Show Actions Ring" },
  ],
  dials: [
    { left: 53.8, top: 7.7, w: 19.4, h: 12.8, roller: true, hardware: "Roller — System Volume" },
    { left: 24.8, top: 33.4, w: 40.4, main: true, hardware: "Dial — Contextual + Vertical Scroll" },
  ],
};

const hasSteps = (b) => !!(b && b.steps?.length);
const dialBound = (d) => !!(d && (d.left?.length || d.right?.length || d.press?.length));

function keyFace(button, opts = {}) {
  if (!hasSteps(button)) {
    return el("div", { className: "key empty" }, opts.placeholder ?? "");
  }
  const node = el("div", { className: "key" });
  node.style.background = button.color || "#24262c";
  if (button.glyph) node.append(el("span", { className: "glyph" }, button.glyph));
  node.append(el("span", { className: "label" }, button.title || ""));
  return node;
}

/** One absolutely-positioned control on the dialpad body. */
function dialpadCtrl(geo, { title, glyph, color, bound, extraClass = "" }) {
  const node = el("div", {
    className: `ctrl ${geo.roller ? "roller" : "round"} ${extraClass} ${bound ? "" : "unassigned"}`.trim(),
  });
  node.style.left = `${geo.left}%`;
  node.style.top = `${geo.top}%`;
  node.style.width = `${geo.w}%`;
  node.style.height = geo.h ? `${geo.h}%` : `${geo.w * DIALPAD_ASPECT}%`;
  if (bound && color) node.style.background = color;
  node.title = geo.hardware;
  if (glyph) node.append(el("span", { className: "g" }, glyph));
  if (title) node.append(el("span", { className: "t" }, title));
  return node;
}

/**
 * Render a device body for `spec`. `interactive` wires up selection; the
 * profile cards use the same renderer without it so previews and the builder
 * can never drift apart.
 */
function deviceNode(spec, interactive = false) {
  const dev = DEVICES[spec.device];
  const buttons = spec.buttons || [];
  const dials = spec.dials || [];

  if (spec.device !== "dialpad") {
    const body = el("div", { className: "device device-keypad" });
    const grid = el("div", { className: "grid-keypad" });
    grid.style.width = "100%";
    for (let i = 0; i < dev.press; i++) {
      const face = keyFace(buttons[i], { placeholder: String(i + 1) });
      if (interactive) makeSelectable(face, "button", i);
      grid.append(face);
    }
    body.append(grid);
    return body;
  }

  const body = el("div", { className: "device device-dialpad" });
  DIALPAD_GEOMETRY.buttons.forEach((geo, i) => {
    const b = buttons[i];
    const node = dialpadCtrl(geo, {
      title: b?.title, glyph: b?.glyph, color: b?.color, bound: hasSteps(b),
    });
    if (interactive) makeSelectable(node, "button", i);
    body.append(node);
  });
  DIALPAD_GEOMETRY.dials.forEach((geo, i) => {
    const d = dials[i];
    const node = dialpadCtrl(geo, {
      title: d?.title,
      glyph: dialBound(d) ? (geo.roller ? "↕" : "↻") : null,
      color: d?.color,
      bound: dialBound(d),
      extraClass: geo.main ? "dial-main" : "",
    });
    if (interactive) makeSelectable(node, "dial", i);
    body.append(node);
  });
  return body;
}

function devicePreview(spec) {
  return deviceNode(spec, false);
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

function blankButton() {
  return { title: "", glyph: "", color: "#2A2E35", steps: [], note: "" };
}
function blankDial() {
  return { title: "", color: "#2A2E35", left: [], right: [], press: [], note: "" };
}

function loadPreset(value) {
  if (value.startsWith("blank")) {
    const device = value.endsWith("dialpad") ? "dialpad" : "keypad";
    const dev = DEVICES[device];
    state.spec = {
      name: "My Profile",
      device,
      description: "",
      buttons: Array.from({ length: dev.press }, blankButton),
      dials: Array.from({ length: dev.rotate }, blankDial),
    };
  } else {
    state.spec = clone(state.specs.profiles[Number(value)]);
  }

  // Presets only define the controls they use; pad so every physical control
  // on the device is editable, including unassigned ones.
  const dev = DEVICES[state.spec.device];
  state.spec.buttons = state.spec.buttons || [];
  state.spec.dials = state.spec.dials || [];
  while (state.spec.buttons.length < dev.press) state.spec.buttons.push(blankButton());
  while (state.spec.dials.length < dev.rotate) state.spec.dials.push(blankDial());

  state.selected = { kind: "button", i: 0 };
}

/* ---- selection ---- */

const isSelected = (kind, i) =>
  state.selected.kind === kind && state.selected.i === i;

function select(kind, i) {
  state.selected = { kind, i };
  renderBuilder();
}

const activateOnKey = (fn) => (e) => {
  if (e.key === "Enter" || e.key === " ") {
    e.preventDefault();
    fn();
  }
};

function makeSelectable(node, kind, i) {
  node.setAttribute("role", "button");
  node.tabIndex = 0;
  node.setAttribute("aria-current", String(isSelected(kind, i)));
  const pick = () => select(kind, i);
  node.addEventListener("click", pick);
  node.addEventListener("keydown", activateOnKey(pick));
}

const HINTS = {
  keypad: "9 LCD keys, top-left to bottom-right. Click one to edit it.",
  dialpad:
    "Laid out like the hardware: the Undo/Redo pair, the roller, the big dial, "
    + "and the two corner buttons. Click any of them to edit.",
};

function renderBuilder() {
  $("#device").replaceChildren(deviceNode(state.spec, true));
  $("#grid-hint").textContent = HINTS[state.spec.device] || "";
  renderEditor();
}

/* Repaint the device only, so the editor keeps focus while you type. */
function refreshFaces() {
  $("#device").replaceChildren(deviceNode(state.spec, true));
}

/* ---- editor ---- */

function renderEditor() {
  const host = $("#editor");
  host.replaceChildren();
  host.append(field("Profile name", inputText(state.spec.name, (v) => {
    state.spec.name = v;
  })));

  if (state.selected.kind === "dial") renderDialEditor(host);
  else renderButtonEditor(host);
}

function renderButtonEditor(host) {
  const b = state.spec.buttons[state.selected.i];

  host.append(el("h3", { style: "margin:6px 0 0" }, `Key ${state.selected.i + 1}`));
  host.append(field("Label", inputText(b.title, (v) => {
    b.title = v; refreshFaces();
  })));

  const faceRow = el("div", { className: "row-inline" });
  const glyphInput = inputText(b.glyph || "", (v) => { b.glyph = v; refreshFaces(); });
  glyphInput.style.maxWidth = "90px";
  glyphInput.placeholder = "◉";
  const colorInput = el("input", { type: "color", value: normaliseHex(b.color) });
  colorInput.addEventListener("input", () => { b.color = colorInput.value; refreshFaces(); });
  faceRow.append(glyphInput, colorInput,
    el("span", { style: "font-size:12.5px;color:var(--muted)" }, "glyph and colour"));
  host.append(field("Face", faceRow));

  host.append(field("Actions", stepsEditor(b, "steps")));
  if (b.note) {
    host.append(el("p", { style: "font-size:13px;color:var(--muted);margin:0" }, b.note));
  }
  const enc = encodedPreview(b.steps);
  if (enc) host.append(enc);
}

const DIAL_SLOTS = [
  ["left", "Turn anticlockwise"],
  ["right", "Turn clockwise"],
  ["press", "Press"],
];

function renderDialEditor(host) {
  const d = state.spec.dials[state.selected.i];

  host.append(el("h3", { style: "margin:6px 0 0" }, `Dial ${state.selected.i + 1}`));
  host.append(field("Label", inputText(d.title, (v) => {
    d.title = v; refreshFaces();
  })));

  const colorInput = el("input", { type: "color", value: normaliseHex(d.color) });
  colorInput.addEventListener("input", () => { d.color = colorInput.value; refreshFaces(); });
  host.append(field("Colour", colorInput));

  for (const [key, label] of DIAL_SLOTS) {
    d[key] = d[key] || [];
    const slot = el("div", { className: "slot" });
    slot.append(el("h4", {}, label));
    slot.append(stepsEditor(d, key));
    const enc = encodedPreview(d[key]);
    if (enc) slot.append(enc);
    host.append(slot);
  }

  if (d.note) {
    host.append(el("p", { style: "font-size:13px;color:var(--muted);margin:0" }, d.note));
  }
}

function encodedPreview(steps) {
  const text = (steps || [])
    .map((s) => (s.text !== undefined ? `type: ${s.text}` : safeEncode(s)))
    .join("\n");
  return text ? el("div", { className: "encoded" }, text) : null;
}

function safeEncode(step) {
  try {
    return encodeKey(step.key, step.mods || [], null, state.locale);
  } catch (err) {
    return `(cannot encode: ${err.message})`;
  }
}

/**
 * Editor for one ordered list of steps. `owner[key]` is the array, so this
 * serves both a button's `steps` and a dial's left/right/press slots.
 */
function stepsEditor(owner, key) {
  const wrap = el("div", { className: "steps" });
  owner[key] = owner[key] || [];
  const steps = owner[key];

  steps.forEach((step, idx) => {
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
      const select_ = el("select");
      for (const [group, keys] of Object.entries(KEY_GROUPS)) {
        const og = el("optgroup", { label: group });
        for (const k of keys) {
          og.append(el("option", { value: k },
            KEY_PRETTY[k] || (/^Key[A-Z0-9]$/.test(k) ? k.slice(3) : k)));
        }
        select_.append(og);
      }
      select_.value = step.key || "Return";
      select_.addEventListener("change", () => { step.key = select_.value; renderEditor(); });
      body.append(select_);

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
      steps.splice(idx, 1);
      refreshFaces();
      renderEditor();
    });
    row.append(del);
    wrap.append(row);
  });

  const add = el("div", { className: "row-inline" });
  const addKey = el("button", { type: "button" }, "+ keystroke");
  addKey.addEventListener("click", () => {
    steps.push({ key: "Return", mods: [] });
    refreshFaces(); renderEditor();
  });
  const addText = el("button", { type: "button" }, "+ text");
  addText.addEventListener("click", () => {
    steps.push({ text: "" });
    refreshFaces(); renderEditor();
  });
  add.append(addKey, addText);
  wrap.append(add);
  return wrap;
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
