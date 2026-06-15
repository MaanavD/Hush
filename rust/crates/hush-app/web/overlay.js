const overlay = document.getElementById("overlay");
const title = document.getElementById("title");
const detail = document.getElementById("detail");
const mode = document.getElementById("mode");
const hint = document.getElementById("hint");
const canvas = document.getElementById("wave");
const ctx = canvas.getContext("2d");

let state = {
  kind: "ready",
  title: "Ready",
  detail: "Focus a text field, then hold a hotkey.",
  mode: "raw",
  accent: "#9b7feb",
  intensity: 0.18,
  showWave: true
};
let started = performance.now();

function applyState(next) {
  state = { ...state, ...next };
  overlay.classList.toggle("clean", state.mode === "clean");
  overlay.classList.toggle("error", state.kind === "error");
  title.textContent = state.title || "Hush";
  detail.textContent = state.detail || "";
  mode.textContent = state.mode === "clean" ? "CLEAN" : "RAW";
  hint.textContent = state.hint || "Ctrl+H raw · Ctrl+Alt+H clean";
}

function hexToRgb(hex) {
  const clean = hex.replace("#", "");
  const value = Number.parseInt(clean, 16);
  return [(value >> 16) & 255, (value >> 8) & 255, value & 255];
}

function drawWave(now) {
  const width = canvas.width;
  const height = canvas.height;
  const t = (now - started) / 1000;
  const [r, g, b] = hexToRgb(state.accent || "#9b7feb");
  const level = state.kind === "listening" ? 0.95 : state.kind === "finalizing" ? 0.70 : 0.25;
  const intensity = Math.max(state.intensity ?? 0.2, level);

  ctx.clearRect(0, 0, width, height);
  ctx.fillStyle = "rgba(238, 238, 248, 0.025)";
  roundRect(ctx, 0, 0, width, height, 18);
  ctx.fill();

  ctx.strokeStyle = "rgba(238, 238, 248, 0.10)";
  ctx.lineWidth = 1;
  ctx.beginPath();
  ctx.moveTo(0, height / 2);
  ctx.lineTo(width, height / 2);
  ctx.stroke();

  const layers = [
    { color: `rgba(${r}, ${g}, ${b}, 0.96)`, width: 4.2, freq: 2.8, speed: 3.0, phase: 0, amp: 1.0 },
    { color: state.mode === "clean" ? "rgba(251, 146, 60, 0.64)" : "rgba(251, 113, 133, 0.62)", width: 2.6, freq: 4.5, speed: -4.4, phase: 1.0, amp: 0.66 },
    { color: state.mode === "clean" ? "rgba(250, 204, 21, 0.45)" : "rgba(129, 140, 248, 0.48)", width: 2.0, freq: 2.1, speed: 2.2, phase: 2.2, amp: 0.48 }
  ];

  for (const layer of layers) {
    ctx.beginPath();
    for (let i = 0; i <= 96; i++) {
      const x = (i / 96) * width;
      const p = i / 96;
      const envelope = Math.pow(Math.sin(Math.PI * p), 0.72);
      const primary = Math.sin(p * Math.PI * 2 * layer.freq + t * layer.speed + layer.phase);
      const secondary = Math.sin(p * Math.PI * 2 * layer.freq * 1.9 - t * layer.speed * 0.42) * 0.30;
      const tertiary = Math.sin(p * Math.PI * 2 * layer.freq * 2.7 + t * layer.speed * 0.24) * 0.12;
      const y = height / 2 + Math.tanh(primary + secondary + tertiary) * envelope * height * 0.34 * intensity * layer.amp;
      if (i === 0) ctx.moveTo(x, y);
      else ctx.lineTo(x, y);
    }
    ctx.strokeStyle = layer.color;
    ctx.lineWidth = layer.width;
    ctx.lineCap = "round";
    ctx.lineJoin = "round";
    ctx.stroke();
  }

  requestAnimationFrame(drawWave);
}

function roundRect(context, x, y, width, height, radius) {
  context.beginPath();
  context.moveTo(x + radius, y);
  context.arcTo(x + width, y, x + width, y + height, radius);
  context.arcTo(x + width, y + height, x, y + height, radius);
  context.arcTo(x, y + height, x, y, radius);
  context.arcTo(x, y, x + width, y, radius);
  context.closePath();
}

window.addEventListener("DOMContentLoaded", async () => {
  applyState(state);
  requestAnimationFrame(drawWave);
  const tauri = window.__TAURI__;
  if (tauri?.event?.listen) {
    await tauri.event.listen("overlay-state", (event) => applyState(event.payload));
  }
});
