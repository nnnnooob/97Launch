'use strict';
let viewer;
let active = true;
let animate = true;
let skinRequest = 0;
let lastSkin = '';
const canvas = document.getElementById('player');
function renderState() {
  if (!viewer) return;
  viewer.animation.paused = !animate;
  viewer.renderPaused = !active || document.hidden;
  if (!viewer.renderPaused) viewer.render();
}
function resetPose() {
  if (!viewer) return;
  viewer.resetCameraPose();
  viewer.controls.target.set(0, 0, 0);
  viewer.controls.update();
  viewer.playerObject.rotation.set(0, -Math.PI / 3, 0);
  viewer.render();
}
window.resetPose = resetPose;
window.setActivity = function (state) {
  active = state.active;
  animate = state.animate;
  document.documentElement.classList.toggle('dark', state.dark);
  renderState();
};
window.setSkin = async function (skin) {
  if (!viewer || lastSkin === skin.image) return;
  lastSkin = skin.image;
  const request = ++skinRequest;
  const image = new Image();
  image.src = skin.image;
  try {
    await image.decode();
    if (request !== skinRequest) return;
    viewer.loadSkin(image, {model:skin.model});
    document.getElementById('error').style.display = 'none';
  } catch (_) {
    if (request !== skinRequest) return;
    await viewer.loadSkin(skin.fallback, {model:skin.fallbackModel});
  }
  resetPose();
};
try {
  viewer = new skinview3d.SkinViewer({canvas, width:Math.max(1,innerWidth),height:Math.max(1,innerHeight),
    skin:'steve.png', model:'default', fov:45, zoom:.82, enableControls:true});
  viewer.pixelRatio = Math.min(devicePixelRatio || 1, 1.5);
  viewer.controls.enablePan = false;
  viewer.controls.enableZoom = false;
  viewer.controls.rotateSpeed = .6;
  viewer.controls.minPolarAngle = Math.PI / 3;
  viewer.controls.maxPolarAngle = Math.PI * .65;
  viewer.animation = new skinview3d.WalkingAnimation();
  viewer.animation.speed = .65;
  viewer.animation.headBobbing = false;
  viewer.autoRotate = false;
  window.viewerReady = true;
  resetPose();
  new ResizeObserver(() => {
    if (innerWidth > 0 && innerHeight > 0) { viewer.setSize(innerWidth, innerHeight); if (active) viewer.render(); }
  }).observe(document.body);
  document.addEventListener('visibilitychange', renderState);
  canvas.addEventListener('keydown', event => {
    if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') {
      viewer.playerObject.rotation.y += event.key === 'ArrowLeft' ? -.15 : .15;
      viewer.render(); event.preventDefault();
    } else if (event.key === 'Home') { resetPose(); event.preventDefault(); }
  });
  canvas.addEventListener('webglcontextlost', () => { viewer.renderPaused = true; });
  window.addEventListener('pagehide', () => viewer.dispose(), {once:true});
  renderState();
} catch (_) {
  document.getElementById('error').textContent = '当前设备暂时无法显示 3D 皮肤';
  document.getElementById('error').style.display = 'block';
}
