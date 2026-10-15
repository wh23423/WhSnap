/* ============================================================================
   WhSnap · Focal · icons.js
   一套自绘描边图标（24×24 网格，1.6 描边，圆头圆角），无第三方依赖。
   用法：<svg class="f-i"><use href="#i-crop"/></svg>
   规则：只允许 currentColor；不允许双色图标；不允许填充图标（状态点除外）。
   ========================================================================== */
(function () {
  var P = {
    /* 品牌标记：取景框 + 中心点 */
    brand: '<path d="M4 9V6.5A2.5 2.5 0 0 1 6.5 4H9M15 4h2.5A2.5 2.5 0 0 1 20 6.5V9M20 15v2.5a2.5 2.5 0 0 1-2.5 2.5H15M9 20H6.5A2.5 2.5 0 0 1 4 17.5V15"/><circle cx="12" cy="12" r="3"/>',

    /* 截图 / 区域 */
    crop: '<path d="M6 3v13.5A1.5 1.5 0 0 0 7.5 18H21"/><path d="M3 6h13.5A1.5 1.5 0 0 1 18 7.5V21"/>',
    capture: '<path d="M4 8V6a2 2 0 0 1 2-2h2M16 4h2a2 2 0 0 1 2 2v2M20 16v2a2 2 0 0 1-2 2h-2M8 20H6a2 2 0 0 1-2-2v-2"/><path d="M3 12h18" stroke-dasharray="2 3"/>',

    /* 通用动作 */
    check: '<path d="M4.5 12.5l4.5 4.5L19.5 6.5"/>',
    'check-circle': '<circle cx="12" cy="12" r="8.5"/><path d="M8.2 12.3l2.6 2.6 5-5.6"/>',
    x: '<path d="M6.5 6.5l11 11M17.5 6.5l-11 11"/>',
    copy: '<rect x="9" y="9" width="11" height="11" rx="2"/><path d="M15 5.5V5a2 2 0 0 0-2-2H6a2 2 0 0 0-2 2v7a2 2 0 0 0 2 2h.5"/>',
    save: '<path d="M12 3.5v11M7.8 10.3L12 14.5l4.2-4.2"/><path d="M4.5 19.5h15"/>',
    'save-as': '<path d="M5 5.5A1.5 1.5 0 0 1 6.5 4h8L19 8.5v10a1.5 1.5 0 0 1-1.5 1.5H6.5A1.5 1.5 0 0 1 5 18.5z"/><path d="M9 4v5h6"/>',
    upload: '<path d="M12 20V9.5M8 13l4-4 4 4"/><path d="M4 4.5h16"/>',
    download: '<path d="M12 3.5v11M8 10.5l4 4 4-4"/><path d="M4 19.5h16"/>',
    refresh: '<path d="M20.5 12a8.5 8.5 0 1 1-2.6-6.1"/><path d="M21 4.5V10h-5.5"/>',
    trash: '<path d="M4 7h16M9.5 7V4.5h5V7M6.5 7l.9 12.6A1.5 1.5 0 0 0 8.9 21h6.2a1.5 1.5 0 0 0 1.5-1.4L17.5 7"/><path d="M10.5 11v6M13.5 11v6"/>',
    undo: '<path d="M9 6L4 11l5 5"/><path d="M4 11h10.5a5.5 5.5 0 0 1 0 11H9"/>',
    redo: '<path d="M15 6l5 5-5 5"/><path d="M20 11H9.5a5.5 5.5 0 0 0 0 11H15"/>',
    plus: '<path d="M12 5v14M5 12h14"/>',
    minus: '<path d="M5 12h14"/>',
    search: '<circle cx="11" cy="11" r="6.5"/><path d="M15.8 15.8L21 21"/>',
    gear: '<path d="M4 7.5h9M17.5 7.5H20M4 16.5h3M11.5 16.5H20"/><circle cx="15" cy="7.5" r="2.5"/><circle cx="9" cy="16.5" r="2.5"/>',
    keyboard: '<rect x="3" y="6.5" width="18" height="11" rx="2.5"/><path d="M7 10h.01M11 10h.01M15 10h.01M8 14h8"/>',
    info: '<circle cx="12" cy="12" r="8.5"/><path d="M12 11.2v5"/><path d="M12 8.2h.01"/>',
    warning: '<path d="M10.3 4.3 2.8 17.3A2 2 0 0 0 4.5 20.3h15a2 2 0 0 0 1.7-3L13.7 4.3a2 2 0 0 0-3.4 0Z"/><path d="M12 9.5v4.2M12 17h.01"/>',
    external: '<path d="M14 4.5h5.5V10"/><path d="M19.5 4.5 11 13"/><path d="M18 14.5v4.5a1.5 1.5 0 0 1-1.5 1.5h-11A1.5 1.5 0 0 1 4 19V8a1.5 1.5 0 0 1 1.5-1.5H10"/>',
    chevronRight: '<path d="M9.5 6l6 6-6 6"/>',
    chevronDown: '<path d="M7 10l5 5 5-5"/>',
    power: '<path d="M12 3.5v8"/><path d="M7.6 6.6a7 7 0 1 0 8.8 0"/>',

    /* 遮罩 / 量测 */
    magnifier: '<circle cx="11" cy="11" r="6.5"/><path d="M15.8 15.8L21 21"/>',
    crosshair: '<circle cx="12" cy="12" r="7.5"/><path d="M12 2.5v4M12 17.5v4M2.5 12h4M17.5 12h4"/>',
    pointer: '<path d="M6 3.5l12.2 8.4-5.4 1.2 2.9 6.1-2.3 1.1-2.9-6.1-3.6 3.3z"/>',
    eyedropper: '<path d="M17.6 3.4l3 3-2.1 2.1-3-3z"/><path d="M15.5 5.5 5.4 15.6 4 20l4.4-1.4L18.5 8.5"/>',
    ruler: '<path d="M3.6 14.4 14.4 3.6a1.5 1.5 0 0 1 2.1 0l3.9 3.9a1.5 1.5 0 0 1 0 2.1L9.6 20.4a1.5 1.5 0 0 1-2.1 0l-3.9-3.9a1.5 1.5 0 0 1 0-2.1Z"/><path d="M7.5 10.5l2 2M10.5 7.5l2 2M13.5 4.5l2 2"/>',
    grid: '<rect x="4" y="4" width="7" height="7" rx="1.5"/><rect x="13" y="4" width="7" height="7" rx="1.5"/><rect x="4" y="13" width="7" height="7" rx="1.5"/><rect x="13" y="13" width="7" height="7" rx="1.5"/>',
    monitor: '<rect x="3" y="4.5" width="18" height="12" rx="2.5"/><path d="M9 20h6M12 16.5V20"/>',
    layers: '<path d="M12 3.5 21 8.5l-9 5-9-5z"/><path d="M3.5 13.2 12 17.9l8.5-4.7"/>',
    history: '<circle cx="12" cy="12" r="8.5"/><path d="M12 7.2V12l3.4 2"/>',
    palette: '<path d="M12 20.5a8.5 8.5 0 1 1 8.5-8.5c0 2.4-2 3.4-3.6 3.4h-2a2 2 0 0 0-1.4 3.4 2 2 0 0 1-1.5 1.7Z"/><path d="M8 10h.01M12 7.6h.01M15.8 10h.01"/>',
    image: '<rect x="3.5" y="4.5" width="17" height="15" rx="2.5"/><circle cx="9" cy="10" r="1.6"/><path d="M4.5 17.5 10 12l4 4 2.5-2 3 3.5"/>',
    film: '<rect x="3.5" y="5" width="17" height="14" rx="2.5"/><path d="M8 5v14M16 5v14"/>',

    /* 标注工具 */
    rect: '<rect x="4" y="6.5" width="16" height="11" rx="1.5"/>',
    ellipse: '<ellipse cx="12" cy="12" rx="8" ry="5.8"/>',
    line: '<path d="M5 19L19 5"/>',
    arrow: '<path d="M4.5 19.5 19 5"/><path d="M19 5h-6.5M19 5v6.5"/>',
    pencil: '<path d="M4 20l.9-3.8L15.6 5.5l2.9 2.9L7.8 19.1z"/><path d="M14 7.1l2.9 2.9"/>',
    marker: '<path d="M9.5 14.5 16 8l3.4 3.4L12.9 18H9.5z"/><path d="M4 20.5h9"/>',
    text: '<path d="M5 7V5h14v2M12 5v14M9.2 19h5.6"/>',
    mosaic: '<rect x="4" y="4" width="5" height="5" rx="1"/><rect x="15" y="4" width="5" height="5" rx="1"/><rect x="9.5" y="9.5" width="5" height="5" rx="1"/><rect x="4" y="15" width="5" height="5" rx="1"/><rect x="15" y="15" width="5" height="5" rx="1"/>',
    blur: '<path d="M12 3.2s6 6.6 6 10.4a6 6 0 0 1-12 0C6 9.8 12 3.2 12 3.2Z"/><path d="M9.6 13.6a2.6 2.6 0 0 0 2.6 2.6"/>',
    eraser: '<path d="M8.8 20.5H20"/><path d="M5.2 16.3l7.1-7.1 5.6 5.6-6.2 6.2H7.9l-2.7-2.7a1.5 1.5 0 0 1 0-2Z"/>',
    badge: '<rect x="4" y="4" width="16" height="16" rx="4.5"/><path d="M10.3 9.6 12 8.4v7.2"/>',
    highlight: '<rect x="3.5" y="9" width="17" height="6" rx="3"/><path d="M7 19h10"/>',
    select: '<path d="M6 3.5l12.2 8.4-5.4 1.2 2.9 6.1-2.3 1.1-2.9-6.1-3.6 3.3z"/><path d="M18.5 17.5l3 3" stroke-dasharray="2 2.4"/>',

    /* 贴图窗口操作 */
    rotate: '<path d="M21 12a9 9 0 1 1-3-6.7"/><path d="M21 3.5V9h-5.5"/>',
    flipH: '<path d="M12 3.5v17" stroke-dasharray="2 3"/><path d="M9.2 8 5 12l4.2 4z"/><path d="M14.8 8 19 12l-4.2 4z"/>',
    flipV: '<path d="M3.5 12h17" stroke-dasharray="2 3"/><path d="M8 9.2 12 5l4 4.2z"/><path d="M8 14.8 12 19l4-4.2z"/>',
    droplet: '<path d="M12 3.2s6 6.6 6 10.4a6 6 0 0 1-12 0C6 9.8 12 3.2 12 3.2Z"/><path d="M12 20.6a5.9 5.9 0 0 0 5.9-5.9c0-2.4-2-5.4-3.7-7.6L12 4.7Z" fill="currentColor" stroke="none" opacity=".55"/>',
    pin: '<path d="M8.5 3.5h7v1.8l-1.2 1.1v4.4l3.2 3.1H6.5l3.2-3.1V6.4L8.5 5.3z"/><path d="M12 14v6.5"/>',
    window: '<rect x="3.5" y="4.5" width="17" height="15" rx="2.5"/><path d="M3.5 9h17"/>',
    eye: '<path d="M2.8 12S6.6 6 12 6s9.2 6 9.2 6-3.8 6-9.2 6-9.2-6-9.2-6Z"/><circle cx="12" cy="12" r="2.6"/>',
    'eye-off': '<path d="M4 4l16 16"/><path d="M9.6 6.5A9.6 9.6 0 0 1 12 6c5.4 0 9.2 6 9.2 6a17 17 0 0 1-3 3.4M6.4 8.2A17.5 17.5 0 0 0 2.8 12S6.6 18 12 18a9.4 9.4 0 0 0 3-.5"/>',
    clipboard: '<rect x="5.5" y="4.5" width="13" height="16" rx="2.5"/><path d="M9.2 4.5V3.4A1.4 1.4 0 0 1 10.6 2h2.8a1.4 1.4 0 0 1 1.4 1.4v1.1z"/>',
    folder: '<path d="M3.5 7.5A2 2 0 0 1 5.5 5.5h3.6l2 2.4h7.4a2 2 0 0 1 2 2v7.6a2 2 0 0 1-2 2H5.5a2 2 0 0 1-2-2z"/>',
    tray: '<path d="M4 14.5h4l1.4 2.4h5.2L16 14.5h4"/><path d="M4 14.5 6.4 5.6A1.5 1.5 0 0 1 7.8 4.5h8.4a1.5 1.5 0 0 1 1.4 1.1L20 14.5v4a1.5 1.5 0 0 1-1.5 1.5h-13A1.5 1.5 0 0 1 4 18.5z"/>',
    lock: '<rect x="4.5" y="10.5" width="15" height="10" rx="2.5"/><path d="M8 10.5V8a4 4 0 0 1 8 0v2.5"/>',
    speed: '<path d="M4 17.5a9 9 0 1 1 16 0"/><path d="M12 13.5 16.5 9"/><circle cx="12" cy="15" r="1.6"/>'
  };

  var sprite = '<svg width="0" height="0" style="position:absolute" aria-hidden="true" focusable="false">';
  for (var k in P) {
    if (!Object.prototype.hasOwnProperty.call(P, k)) continue;
    sprite += '<symbol id="i-' + k + '" viewBox="0 0 24 24">' + P[k] + '</symbol>';
  }
  sprite += '</svg>';

  function inject() {
    var host = document.createElement('div');
    host.innerHTML = sprite;
    document.body.insertBefore(host.firstChild, document.body.firstChild);
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', inject);
  else inject();
})();
