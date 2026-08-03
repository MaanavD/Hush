const invoke = (...args) => window.__TAURI__.core.invoke(...args);

let current = null;
let prompts = [];

const ids = [
  "hotkey",
  "clean_hotkey",
  "microphone_name",
  "language",
  "transcription_model",
  "model_unload_timeout",
  "post_processing_enabled",
  "post_processing_model",
  "partials_in_overlay",
  "streaming_commit",
  "auto_submit_key",
  "sound_effects",
  "auto_start",
  "active_prompt"
];

function el(id) {
  return document.getElementById(id);
}

function setOptions(select, values, selected) {
  select.innerHTML = "";
  for (const value of values) {
    const option = document.createElement("option");
    option.value = value;
    option.textContent = value;
    if (value === selected) option.selected = true;
    select.appendChild(option);
  }
}

function readSettings() {
  const settings = { ...current.settings };
  for (const id of ids) {
    const element = el(id);
    if (!element) continue;
    if (element.type === "checkbox") settings[id] = element.checked;
    else settings[id] = element.value;
  }
  settings.custom_substitutions = [...document.querySelectorAll(".dictionary-row")].map((row) => ({
    match_text: row.querySelector("[data-kind='match']").value,
    replace_text: row.querySelector("[data-kind='replace']").value
  })).filter((rule) => rule.match_text.trim().length > 0);
  return settings;
}

function renderDictionary(rules) {
  const root = el("dictionary");
  root.innerHTML = "";
  for (const rule of rules) addDictionaryRow(rule.match_text, rule.replace_text);
}

function addDictionaryRow(matchText = "", replaceText = "") {
  const row = document.createElement("div");
  row.className = "dictionary-row";
  row.innerHTML = `
    <input data-kind="match" placeholder="heard" />
    <span>→</span>
    <input data-kind="replace" placeholder="type" />
    <button type="button" class="ghost-button">Remove</button>
  `;
  row.querySelector("[data-kind='match']").value = matchText;
  row.querySelector("[data-kind='replace']").value = replaceText;
  row.querySelector("button").addEventListener("click", () => row.remove());
  el("dictionary").appendChild(row);
}

async function load() {
  current = await invoke("get_settings");
  prompts = current.prompts;
  const settings = current.settings;

  el("hotkey").value = settings.hotkey;
  el("clean_hotkey").value = settings.clean_hotkey;
  setOptions(el("microphone_name"), current.microphones, settings.microphone_name);
  setOptions(el("language"), current.language_options, settings.language);
  setOptions(el("transcription_model"), current.transcription_model_options, settings.transcription_model);
  setOptions(el("model_unload_timeout"), current.model_unload_options, settings.model_unload_timeout);
  el("post_processing_enabled").checked = settings.post_processing_enabled;
  setOptions(el("post_processing_model"), current.post_processing_model_options, settings.post_processing_model);
  setOptions(el("auto_submit_key"), current.auto_submit_options, settings.auto_submit_key);
  el("partials_in_overlay").checked = settings.partials_in_overlay;
  el("streaming_commit").checked = settings.streaming_commit;
  el("sound_effects").checked = settings.sound_effects;
  el("auto_start").checked = settings.auto_start;
  el("active_prompt").value = settings.active_prompt;

  const promptSelect = el("prompt_preset");
  promptSelect.innerHTML = "";
  for (const prompt of prompts) {
    const option = document.createElement("option");
    option.value = prompt.prompt;
    option.textContent = `${prompt.name} — ${prompt.description}`;
    if (prompt.prompt === settings.active_prompt) option.selected = true;
    promptSelect.appendChild(option);
  }
  promptSelect.addEventListener("change", () => {
    el("active_prompt").value = promptSelect.value;
  });

  renderDictionary(settings.custom_substitutions || []);
}

window.addEventListener("DOMContentLoaded", async () => {
  await load();
  el("hide").addEventListener("click", () => invoke("hide_settings_window"));
  el("cancel").addEventListener("click", () => invoke("hide_settings_window"));
  el("refresh").addEventListener("click", async () => {
    const microphones = await invoke("refresh_microphones");
    setOptions(el("microphone_name"), microphones, el("microphone_name").value);
  });
  el("add-substitution").addEventListener("click", () => addDictionaryRow());
  el("save").addEventListener("click", async () => {
    await invoke("save_settings", { settings: readSettings() });
    await load();
  });
});
