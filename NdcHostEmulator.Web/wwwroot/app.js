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
