window.gba = (function () {
  let ctx;
  let imageData;
  let u32;

  function initCanvas(id, width, height, scale) {
    const canvas = document.getElementById(id);
    if (!canvas) return;
    canvas.width = width;
    canvas.height = height;
    canvas.style.width = `${width * scale}px`;
    canvas.style.height = `${height * scale}px`;
    ctx = canvas.getContext('2d', { alpha: false });
    imageData = ctx.createImageData(width, height);
    u32 = new Uint32Array(imageData.data.buffer);
  }

  function presentFrame(pixels) {
    if (!ctx || !imageData || !u32) return;
    u32.set(pixels);
    ctx.putImageData(imageData, 0, 0);
  }

  function startRaf(dotNetRef) {
    function tick() {
      dotNetRef.invokeMethodAsync('OnRaf');
      window.requestAnimationFrame(tick);
    }

    window.requestAnimationFrame(tick);
  }

  return {
    initCanvas,
    presentFrame,
    startRaf
  };
})();
