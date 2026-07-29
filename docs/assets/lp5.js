/*
 * lp5.js - build Logi Options+ `.lp5` profiles in the browser.
 *
 * A direct port of tools/lp5kit/lp5kit.py. No dependencies: the ZIP writer
 * and the PNG rendering are both done here, so the page works offline and
 * from a plain GitHub Pages host.
 *
 * Everything the two implementations must agree on lives in specs.json.
 */

/* ------------------------------------------------------------------ *
 * ZIP writer
 * ------------------------------------------------------------------ */

const CRC_TABLE = (() => {
  const t = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    t[n] = c >>> 0;
  }
  return t;
})();

function crc32(buf) {
  let c = 0xffffffff;
  for (let i = 0; i < buf.length; i++) c = CRC_TABLE[(c ^ buf[i]) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

async function deflateRaw(bytes) {
  // Real .lp5 files are deflate-compressed. Use the platform compressor when
  // it exists and fall back to STORE, which is equally valid ZIP.
  if (typeof CompressionStream === "undefined") return null;
  try {
    const cs = new CompressionStream("deflate-raw");
    const stream = new Blob([bytes]).stream().pipeThrough(cs);
    return new Uint8Array(await new Response(stream).arrayBuffer());
  } catch {
    return null;
  }
}

const utf8 = (s) => new TextEncoder().encode(s);

/** Build a ZIP archive. `files` is [{name, data:Uint8Array}]. */
export async function makeZip(files) {
  const chunks = [];
  const central = [];
  let offset = 0;

  for (const file of files) {
    const nameBytes = utf8(file.name);
    const raw = file.data;
    const crc = crc32(raw);

    let method = 0;
    let payload = raw;
    const packed = await deflateRaw(raw);
    if (packed && packed.length < raw.length) {
      method = 8;
      payload = packed;
    }

    const local = new DataView(new ArrayBuffer(30));
    local.setUint32(0, 0x04034b50, true);
    local.setUint16(4, 20, true); // version needed
    local.setUint16(6, 0x0800, true); // UTF-8 names
    local.setUint16(8, method, true);
    local.setUint16(10, 0, true); // mod time
    local.setUint16(12, 0x2821, true); // mod date (fixed, for reproducibility)
    local.setUint32(14, crc, true);
    local.setUint32(18, payload.length, true);
    local.setUint32(22, raw.length, true);
    local.setUint16(26, nameBytes.length, true);
    local.setUint16(28, 0, true);

    chunks.push(new Uint8Array(local.buffer), nameBytes, payload);

    const cen = new DataView(new ArrayBuffer(46));
    cen.setUint32(0, 0x02014b50, true);
    cen.setUint16(4, 20, true);
    cen.setUint16(6, 20, true);
    cen.setUint16(8, 0x0800, true);
    cen.setUint16(10, method, true);
    cen.setUint16(12, 0, true);
    cen.setUint16(14, 0x2821, true);
    cen.setUint32(16, crc, true);
    cen.setUint32(20, payload.length, true);
    cen.setUint32(24, raw.length, true);
    cen.setUint16(28, nameBytes.length, true);
    cen.setUint16(30, 0, true);
    cen.setUint16(32, 0, true);
    cen.setUint16(34, 0, true);
    cen.setUint16(36, 0, true);
    cen.setUint32(38, 0, true);
    cen.setUint32(42, offset, true);
    central.push(new Uint8Array(cen.buffer), nameBytes);

    offset += 30 + nameBytes.length + payload.length;
  }

  const centralSize = central.reduce((n, c) => n + c.length, 0);
  const end = new DataView(new ArrayBuffer(22));
  end.setUint32(0, 0x06054b50, true);
  end.setUint16(8, files.length, true);
  end.setUint16(10, files.length, true);
  end.setUint32(12, centralSize, true);
  end.setUint32(16, offset, true);

  return new Blob([...chunks, ...central, new Uint8Array(end.buffer)], {
    type: "application/zip",
  });
}

/* ------------------------------------------------------------------ *
 * Keyboard encoding - mirrors lp5kit.py
 * ------------------------------------------------------------------ */

const KK_SEP = "#¤%&+?";
export const LCIDS = { "en-GB": 2057, "en-US": 1033 };
export const KEY_LAYOUTS = {
  "en-GB": "com.apple.keylayout.British",
  "en-US": "com.apple.keylayout.US",
};

const FLAG_FUNCTION = 0x800000;
const FLAG_NUMPAD = 0x200000;

const LETTERS = { A: 0, S: 1, D: 2, F: 3, H: 4, G: 5, Z: 6, X: 7, C: 8, V: 9,
  B: 11, Q: 12, W: 13, E: 14, R: 15, Y: 16, T: 17, O: 31, U: 32, I: 34,
  P: 35, L: 37, J: 38, K: 40, N: 45, M: 46 };
const DIGITS = { 0: 29, 1: 18, 2: 19, 3: 20, 4: 21, 5: 23, 6: 22, 7: 26, 8: 28, 9: 25 };
const FNKEYS = { 1: 122, 2: 120, 3: 99, 4: 118, 5: 96, 6: 97, 7: 98, 8: 100,
  9: 101, 10: 109, 11: 103, 12: 111 };

/** key name -> [macOS virtual keycode, character, always-on mask bits] */
export const KEY_TABLE = {};
for (const [ch, code] of Object.entries(LETTERS)) KEY_TABLE["Key" + ch] = [code, ch.toLowerCase(), 0];
for (const [ch, code] of Object.entries(DIGITS)) KEY_TABLE["Key" + ch] = [code, ch, 0];
for (const [n, code] of Object.entries(FNKEYS))
  KEY_TABLE["F" + n] = [code, String.fromCharCode(0xf704 + Number(n) - 1), FLAG_FUNCTION];
Object.assign(KEY_TABLE, {
  Return: [36, "\r", 0],
  Tab: [48, "\t", 0],
  Space: [49, " ", 0],
  Escape: [53, "", 0],
  Backspace: [51, "\b", 0],
  Delete: [117, "", FLAG_FUNCTION],
  ArrowUp: [126, "", FLAG_FUNCTION | FLAG_NUMPAD],
  ArrowDown: [125, "", FLAG_FUNCTION | FLAG_NUMPAD],
  ArrowLeft: [123, "", FLAG_FUNCTION | FLAG_NUMPAD],
  ArrowRight: [124, "", FLAG_FUNCTION | FLAG_NUMPAD],
  Minus: [27, "-", 0],
  Equals: [24, "=", 0],
  Period: [47, ".", 0],
  Comma: [43, ",", 0],
  Oem2: [44, "/", 0],
  Oem4: [33, "[", 0],
  Oem5: [50, "`", 0],
  Oem6: [30, "]", 0],
  Oem7: [42, "\\", 0],
});

const KEY_LABELS = {
  Return: "Return", Tab: "Tab", Space: "Space", Escape: "Escape",
  Backspace: "Backspace", Delete: "Delete", ArrowUp: "ArrowUp",
  ArrowDown: "ArrowDown", ArrowLeft: "ArrowLeft", ArrowRight: "ArrowRight",
};

const MOD_MASKS = {
  cmd: 0x100000 | 0x000008,
  ctrl: 0x040000 | 0x000001,
  alt: 0x080000 | 0x000020,
  shift: 0x020000 | 0x000002,
};
const MOD_ORDER_LOGICAL = ["cmd", "ctrl", "alt", "shift"];
const MOD_ORDER_LABEL = ["cmd", "alt", "ctrl", "shift"];
const MOD_LOGICAL = { cmd: "ControlOrCommand", ctrl: "Control", alt: "AltOrOption", shift: "Shift" };
const MOD_LABEL = { cmd: "Cmd", ctrl: "Ctrl", alt: "Opt", shift: "Shift" };

function ctrlChar(key) {
  if (key.length === 4 && key.startsWith("Key") && /[A-Za-z]/.test(key[3]))
    return String.fromCharCode(key.charCodeAt(3) - 64);
  return null;
}

export function encodeKey(key, mods = [], label = null, locale = "en-GB") {
  const entry = KEY_TABLE[key];
  if (!entry) throw new Error(`unknown key: ${key}`);
  const [code, baseChar, extra] = entry;
  mods = mods.map((m) => m.toLowerCase());

  const logical = MOD_ORDER_LOGICAL.filter((m) => mods.includes(m))
    .map((m) => MOD_LOGICAL[m]).concat(key).join("+");

  if (label === null) {
    let pretty = KEY_LABELS[key];
    if (pretty === undefined)
      pretty = key.startsWith("Key") && key.length === 4 ? key[3].toUpperCase() : baseChar;
    label = MOD_ORDER_LABEL.filter((m) => mods.includes(m))
      .map((m) => MOD_LABEL[m]).concat(pretty).join("+");
  }

  let mask = extra;
  for (const m of mods) mask |= MOD_MASKS[m] || 0;

  let char = baseChar;
  if (mods.includes("ctrl") && ctrlChar(key)) char = ctrlChar(key);

  const lcid = LCIDS[locale] ?? LCIDS["en-GB"];
  const layout = KEY_LAYOUTS[locale] ?? KEY_LAYOUTS["en-GB"];
  return `${logical}___${lcid}___${label}___mac-${code}${KK_SEP}${mask}${KK_SEP}${char}${KK_SEP}${layout}`;
}

/* ------------------------------------------------------------------ *
 * Profile assembly
 * ------------------------------------------------------------------ */

const T = {
  profile: "Loupedeck.Service.ApplicationProfile, LoupedeckService",
  layout: "Loupedeck.Service.Devices.Loupedeck7Devices.ProfileLayout7, LoupedeckService",
  mode: "Loupedeck.Service.Devices.Loupedeck7Devices.ProfileLayoutMode7, LoupedeckService",
  workspace: "Loupedeck.Service.Devices.Loupedeck7Devices.ProfileLayoutWorkspace7, LoupedeckService",
  page: "Loupedeck.Service.Devices.Loupedeck7Devices.ProfileLayoutPage7, LoupedeckService",
  control: "Loupedeck.Service.Devices.Loupedeck7Devices.ProfileLayoutControl7, LoupedeckService",
  appInfo: "Loupedeck.Service.SupportedApplicationInfo, LoupedeckService",
  appMode: "Loupedeck.Service.ApplicationMode, LoupedeckService",
  cmd: "Loupedeck.Service.ApplicationProfileCommand, LoupedeckService",
  macro: "Loupedeck.Service.ApplicationProfileMacroCommand, LoupedeckService",
  adj: "Loupedeck.Service.ApplicationProfileMacroAdjustment, LoupedeckService",
  step: "Loupedeck.Service.MacroActionEditorCommand, LoupedeckService",
  params: "Loupedeck.ActionEditorActionParameters, PluginApi",
  strDict: "Loupedeck.StringDictionaryNoCase, PluginApi",
  dictNC: "Loupedeck.DictionaryNoCase`1[[System.String, System.Private.CoreLib]], PluginApi",
  dictSS:
    "System.Collections.Generic.Dictionary`2[[System.String, System.Private.CoreLib]," +
    "[System.String, System.Private.CoreLib]], System.Private.CoreLib",
  iconImage: "Loupedeck.Service.ActionIconImageItem, LoupedeckShared",
  iconText: "Loupedeck.Service.ActionIconTextItem, LoupedeckShared",
};

const TEMPLATE_KEY = "$@Generic___@KeyboardKey";
export const DEVICES = {
  keypad: { type: "Loupedeck70", press: 9, rotate: 0 },
  dialpad: { type: "Loupedeck71", press: 4, rotate: 2 },
};

const guid = () =>
  (crypto.randomUUID
    ? crypto.randomUUID().replace(/-/g, "")
    : [...crypto.getRandomValues(new Uint8Array(16))]
        .map((b) => b.toString(16).padStart(2, "0")).join("")
  ).toUpperCase();

function shortId(n = 21) {
  const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
  return [...crypto.getRandomValues(new Uint8Array(n))]
    .map((b) => alphabet[b % alphabet.length]).join("");
}

export function hexToArgb(hex) {
  let v = (hex || "#000000").replace("#", "");
  if (v.length === 6) v = "FF" + v;
  return parseInt(v, 16) >>> 0;
}

/** Draw a key face and return PNG bytes. Mirrors _render_icon_png in Python. */
export async function renderKeyPng(title, glyph, colorHex, size = 116) {
  const canvas = document.createElement("canvas");
  canvas.width = canvas.height = size;
  const ctx = canvas.getContext("2d");
  ctx.fillStyle = colorHex || "#000000";
  ctx.fillRect(0, 0, size, size);
  ctx.textAlign = "center";
  ctx.fillStyle = "#ffffff";

  let y = 14;
  if (glyph) {
    ctx.font = `600 ${Math.round(size * 0.34)}px -apple-system, "Segoe UI Symbol", sans-serif`;
    ctx.textBaseline = "top";
    ctx.fillText(glyph, size / 2, y, size - 8);
    y += Math.round(size * 0.42);
  } else {
    y = Math.round(size * 0.28);
  }

  ctx.font = `500 ${Math.round(size * 0.145)}px -apple-system, "Helvetica Neue", sans-serif`;
  const words = String(title || "").split(/\s+/).filter(Boolean);
  const lines = [];
  let cur = "";
  for (const w of words) {
    const trial = cur ? `${cur} ${w}` : w;
    if (ctx.measureText(trial).width <= size - 12 || !cur) cur = trial;
    else { lines.push(cur); cur = w; }
  }
  if (cur) lines.push(cur);
  for (const line of lines.slice(0, 3)) {
    ctx.fillText(line, size / 2, y, size - 6);
    y += Math.round(size * 0.17);
  }

  const blob = await new Promise((res) => canvas.toBlob(res, "image/png"));
  return new Uint8Array(await blob.arrayBuffer());
}

function compileSteps(steps, locale, editor) {
  const actions = [];
  for (const step of steps || []) {
    if (step.text !== undefined) {
      actions.push(`$@Generic___@TypeText___${step.text}`);
    } else if (step.key) {
      const id = shortId();
      editor.push({
        $type: T.step,
        name: id,
        templateName: TEMPLATE_KEY,
        actionParameters: {
          $type: T.dictSS,
          keyboardKey: encodeKey(step.key, step.mods || [], step.label ?? null, locale),
        },
      });
      actions.push(id);
    }
  }
  return actions;
}

/**
 * Build a `.lp5` from a spec object (the same shape used in specs.json).
 * Returns {blob, filename}.
 */
export async function buildLp5(spec, opts = {}) {
  const locale = opts.locale || "en-GB";
  const dev = DEVICES[spec.device];
  if (!dev) throw new Error(`unknown device: ${spec.device}`);

  const profileGuid = guid();
  const mode = "System";
  const profileActions = [];
  const macroCommands = [];
  const macroAdjustments = [];
  const icons = new Map(); // actionName -> {title, glyph, color}

  const addSingleKey = (step, title) => {
    const name = `$@Generic___@ProfileAction___${guid()}`;
    profileActions.push({
      $type: T.cmd,
      isCommand: true,
      name,
      templateActionName: TEMPLATE_KEY,
      actionParameters: {
        $type: T.params,
        parameters: {
          $type: T.strDict,
          keyboardKey: encodeKey(step.key, step.mods || [], step.label ?? null, locale),
        },
        count: 1,
      },
      displayName: title,
      description:
        "Activate a keyboard shortcut with a single press or hold down for " +
        "continuous use like a keyboard key",
      groupName: "",
      superGroupName: "@macro",
      isProfileAction: true,
      isMultiState: false,
      isResetCommand: false,
      adjustmentName: null,
      states: null,
    });
    return name;
  };

  const addMacro = (steps, title) => {
    const g = guid();
    const editor = [];
    const actions = compileSteps(steps, locale, editor);
    macroCommands.push({
      $type: T.macro,
      isCommand: true,
      name: g,
      displayName: title,
      description: "",
      groupName: "",
      superGroupName: "@macro",
      supportedOs: "All",
      supportedModes: [mode],
      showAsSingleAction: false,
      actionEditorCommands: editor,
      isMultiState: false,
      actions,
    });
    return `$@Generic___@Macro___${g}`;
  };

  const addAdjustment = (dial) => {
    const g = guid();
    const editor = [];
    const left = compileSteps(dial.left, locale, editor);
    const right = compileSteps(dial.right, locale, editor);
    const before = dial.press && dial.press.length
      ? compileSteps(dial.press, locale, editor) : [""];
    macroAdjustments.push({
      $type: T.adj,
      isCommand: false,
      name: g,
      displayName: dial.title,
      description: "",
      groupName: "",
      superGroupName: "@macro",
      supportedOs: "All",
      supportedModes: [],
      showAsSingleAction: false,
      actionEditorCommands: editor,
      isMultiState: false,
      actionsBefore: before,
      actionsLeft: left,
      actionsRight: right,
      actionsReset: [],
      clickRateLimit: 1,
    });
    return `$@Generic___@MacroAdjustment___${g}`;
  };

  const pressControls = [];
  const preview = [];
  for (let i = 0; i < dev.press; i++) {
    const b = (spec.buttons || [])[i];
    let action = null;
    if (b && b.steps && b.steps.length) {
      action =
        b.steps.length === 1 && b.steps[0].key
          ? addSingleKey(b.steps[0], b.title)
          : addMacro(b.steps, b.title);
      icons.set(action, { title: b.title, glyph: b.glyph, color: b.color });
      preview.push({ controlId: i, action, button: b });
    }
    pressControls.push({ $type: T.control, controlId: i, pressAction: action, rotateAction: null });
  }

  const rotateControls = [];
  for (let i = 0; i < dev.rotate; i++) {
    const d = (spec.dials || [])[i];
    let action = null;
    if (d && (d.left?.length || d.right?.length || d.press?.length)) {
      action = addAdjustment(d);
      icons.set(action, { title: d.title, glyph: d.glyph, color: d.color });
    }
    rotateControls.push({ $type: T.control, controlId: i, pressAction: null, rotateAction: action });
  }

  const workspace = {
    $type: T.workspace,
    name: guid(),
    displayName: "Workspace 1",
    description: "",
    pressPages: [{ $type: T.page, name: guid(), displayName: "Page (1)", description: "", controls: pressControls }],
    rotatePages: rotateControls.length
      ? [{ $type: T.page, name: guid(), displayName: "Dial Page", description: "", controls: rotateControls }]
      : [],
  };

  const profileInfo = {
    $type: T.profile,
    name: profileGuid,
    profileFlags: "None",
    displayName: spec.name,
    description: spec.description || "",
    deviceType: dev.type,
    applicationName: "@_defaultmac",
    nativePluginName: "DefaultMac",
    hasNativePlugin: true,
    additionalNativePluginNames: [],
    lastModifiedTimeUtc: new Date().toISOString().replace("Z", "000Z"),
    profileSettings: { $type: T.dictNC },
    actionImages90: null,
    actionImages60: null,
    wheelImages: null,
    actionColors: null,
    layout: {
      $type: T.layout,
      deviceType: dev.type,
      profileFlags: "None",
      layoutModes: [{
        $type: T.mode,
        deviceType: dev.type,
        modeName: mode,
        parentModeName: null,
        actions: null,
        dynamicButtonPages: null,
        dynamicEncoderPages: null,
        workspaces: [workspace],
        homeWorkspaceName: workspace.name,
      }],
      folderPages: [],
    },
    macroCommands,
    macroAdjustments,
    profileCommands: [],
    profileAdjustments: [],
    conversionHistory: null,
    packageName: guid(),
    packageVersion: "1.0.0.0",
    profileActions,
  };

  const applicationInfo = {
    $type: T.appInfo,
    name: "@_defaultmac",
    displayName: "System plugin",
    description: null,
    deviceType: dev.type,
    nativePluginName: "DefaultMac",
    hasNativePlugin: true,
    processOrBundleName: null,
    modes: [{ $type: T.appMode, name: mode, parentModeName: null, displayName: mode }],
    defaultProfileName: profileGuid,
    isEnabled: true,
  };

  const files = [
    { name: "ProfileInfo.json", data: utf8(JSON.stringify(profileInfo, null, 4)) },
    { name: "ApplicationInfo.json", data: utf8(JSON.stringify(applicationInfo, null, 4)) },
    { name: "metadata/AdvancedInfo.json", data: utf8(JSON.stringify({ additionalPluginNames: [] }, null, 4)) },
    {
      name: "metadata/LoupedeckPackage.yaml",
      data: utf8(
        `type: Profile5\nname: ${profileGuid}\ndisplayName: ${spec.name}\nversion: 1.0.0.0\n`
      ),
    },
    { name: "ApplicationIcon.png", data: await renderKeyPng(spec.name, null, "#14161A", 250) },
  ];

  for (const [action, meta] of icons) {
    files.push({
      name: `ActionIcons/${action}.ict`,
      data: utf8(JSON.stringify(buildIct(meta), null, 4)),
    });
  }

  const buttonPages = [];
  for (const p of preview) {
    const png = await renderKeyPng(p.button.title, p.button.glyph, p.button.color, 116);
    buttonPages.push({
      controlId: p.controlId,
      actionName: p.action,
      displayName: p.button.title,
      description: "",
      image: bytesToBase64(png),
    });
  }
  files.push({
    name: "metadata/ProfilePreview.json",
    data: utf8(JSON.stringify({ buttonPages }, null, 4)),
  });

  const suffix = spec.device === "dialpad" ? "dialpad" : "keypad";
  return {
    blob: await makeZip(files),
    filename: `${spec.name} Profile_${suffix}.lp5`,
  };
}

function buildIct(meta) {
  const items = [];
  const textColor = 0xffffffff;
  if (meta.glyph) {
    items.push({
      $type: T.iconText,
      text: meta.glyph,
      textColor,
      fontSize: 14,
      fontName: "Brown Logitech Pan Light",
      isVisible: true,
      itemType: "Text",
      area: { x: 0, y: 8, width: 100, height: 30, isFullScreen: false },
    });
  }
  items.push({
    $type: T.iconText,
    text: meta.title,
    textColor,
    fontSize: 6,
    fontName: "Brown Logitech Pan Light",
    isVisible: true,
    itemType: "Text",
    area: meta.glyph
      ? { x: 0, y: 72, width: 100, height: 24, isFullScreen: false }
      : { x: 0, y: 34, width: 100, height: 32, isFullScreen: false },
  });
  return { backgroundColor: hexToArgb(meta.color), items };
}

function bytesToBase64(bytes) {
  let s = "";
  for (let i = 0; i < bytes.length; i += 0x8000)
    s += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
  return btoa(s);
}
