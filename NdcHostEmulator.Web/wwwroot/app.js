// Convert Unicode Control Pictures back to original control bytes on copy
document.addEventListener('copy', function (e) {
    const selection = window.getSelection().toString();
    if (!selection) return;
    let raw = '';
    for (let i = 0; i < selection.length; i++) {
        const code = selection.charCodeAt(i);
        if (code >= 0x2400 && code <= 0x241F)
            raw += String.fromCharCode(code - 0x2400);
        else if (code === 0x2421)
            raw += String.fromCharCode(0x7F);
        else
            raw += selection[i];
    }
    e.clipboardData.setData('text/plain', raw);
    e.preventDefault();
});

window.scrollToBottom = function (elementId) {
    const el = document.getElementById(elementId);
    if (el) el.scrollIntoView({ behavior: 'smooth', block: 'end' });
};

window.initResizable = function (handleId, scrollId, paddingTargetSelector, minHeight, maxHeight) {
    const handle = document.getElementById(handleId);
    const scroll = document.getElementById(scrollId);
    if (!handle || !scroll) return;

    let startY = 0;
    let startHeight = 0;

    handle.addEventListener('mousedown', function (e) {
        startY = e.clientY;
        startHeight = scroll.offsetHeight;
        document.body.style.userSelect = 'none';
        document.body.style.cursor = 'ns-resize';

        function onMove(e) {
            const delta = startY - e.clientY; // drag up = increase height
            const newHeight = Math.min(maxHeight, Math.max(minHeight, startHeight + delta));
            scroll.style.height = newHeight + 'px';

            // keep padding-bottom in sync so content isn't hidden under panel
            const panel = handle.closest('[data-livelog-panel]');
            const totalPanelHeight = panel ? panel.offsetHeight : newHeight + 28;
            const target = document.querySelector(paddingTargetSelector);
            if (target) target.style.paddingBottom = (totalPanelHeight + 8) + 'px';
        }

        function onUp() {
            document.body.style.userSelect = '';
            document.body.style.cursor = '';
            document.removeEventListener('mousemove', onMove);
            document.removeEventListener('mouseup', onUp);
        }

        document.addEventListener('mousemove', onMove);
        document.addEventListener('mouseup', onUp);
        e.preventDefault();
    });
};
