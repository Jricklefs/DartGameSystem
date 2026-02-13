/**
 * DartsMob Settings Page
 */

const DART_DETECT_URL = 'http://192.168.0.158:8000';
const DART_SENSOR_URL = 'http://192.168.0.158:8001';  // DartSensor for camera snapshots
const DART_GAME_URL = window.location.origin;

// Default backgrounds
const DEFAULT_BACKGROUNDS = [
    '/images/backgrounds/speakeasy-1.jpg',
    '/images/backgrounds/speakeasy-2.jpg',
    '/images/backgrounds/speakeasy-3.jpg',
    '/images/backgrounds/speakeasy-4.jpg',
    '/images/backgrounds/speakeasy-5.jpg'
];

// NSFW backgrounds (loaded dynamically from /images/backgrounds/nsfw/)
let nsfwBackgrounds = [];
let nsfwMode = false;
let nsfwShow = false;

// Dartboard segment order (clockwise starting from 20 at top)
const SEGMENT_ORDER = [20, 1, 18, 4, 13, 6, 10, 15, 2, 17, 3, 19, 7, 16, 8, 11, 14, 9, 12, 5];
// Draw calibration overlay on canvas using stored calibration data
function drawCalibrationOverlay(canvas, calibrationDataJson, baseImage) {
    const ctx = canvas.getContext('2d');
    canvas.width = baseImage.naturalWidth || baseImage.width;
    canvas.height = baseImage.naturalHeight || baseImage.height;
    
    // Draw base image
    ctx.drawImage(baseImage, 0, 0, canvas.width, canvas.height);
    
    if (!calibrationDataJson) return;
    
    let cal;
    try {
        cal = typeof calibrationDataJson === 'string' ? JSON.parse(calibrationDataJson) : calibrationDataJson;
    } catch(e) { console.error('Failed to parse calibration data:', e); return; }
    
    const center = cal.center;
    if (!center) return;
    
    const cx = center[0], cy = center[1];
    
    // Helper: draw OpenCV ellipse
    function drawEllipse(ell, color, lineWidth) {
        if (!ell) return;
        const [ecx, ecy] = ell[0];
        const [w, h] = ell[1];
        const angleDeg = ell[2];
        ctx.save();
        ctx.translate(ecx, ecy);
        ctx.rotate(angleDeg * Math.PI / 180);
        ctx.beginPath();
        ctx.ellipse(0, 0, w/2, h/2, 0, 0, 2 * Math.PI);
        ctx.strokeStyle = color;
        ctx.lineWidth = lineWidth;
        ctx.stroke();
        ctx.restore();
    }
    
    // Draw rings
    drawEllipse(cal.outer_double_ellipse, 'rgba(255,255,0,0.8)', 2);
    drawEllipse(cal.inner_double_ellipse, 'rgba(255,255,0,0.5)', 1);
    drawEllipse(cal.outer_triple_ellipse, 'rgba(255,255,0,0.8)', 2);
    drawEllipse(cal.inner_triple_ellipse, 'rgba(255,255,0,0.5)', 1);
    drawEllipse(cal.bull_ellipse, 'rgba(255,255,0,0.6)', 1);
    drawEllipse(cal.bullseye_ellipse, 'rgba(255,255,0,0.6)', 1);
    
    // Draw segment lines
    const segAngles = cal.segment_angles;
    const seg20Idx = cal.segment_20_index || 0;
    
    if (segAngles && segAngles.length >= 20) {
        // Get outer double radius for line length
        let maxR = 300;
        if (cal.outer_double_ellipse) {
            maxR = Math.max(cal.outer_double_ellipse[1][0], cal.outer_double_ellipse[1][1]) / 2 + 10;
        }
        // Bull radius for inner endpoint
        let bullR = 20;
        if (cal.bull_ellipse) {
            bullR = Math.max(cal.bull_ellipse[1][0], cal.bull_ellipse[1][1]) / 2;
        }
        
        for (let i = 0; i < 20; i++) {
            const angle = segAngles[i];
            const dx = Math.cos(angle);
            const dy = Math.sin(angle);
            
            const x1 = cx + bullR * dx;
            const y1 = cy + bullR * dy;
            const x2 = cx + maxR * dx;
            const y2 = cy + maxR * dy;
            
            ctx.beginPath();
            ctx.moveTo(x1, y1);
            ctx.lineTo(x2, y2);
            ctx.strokeStyle = 'rgba(255,255,255,0.7)';
            ctx.lineWidth = 2;
            ctx.stroke();
            
            // Label: segment between boundary i and i+1
            let nextAngle = segAngles[(i + 1) % 20];
            let a1 = angle, a2 = nextAngle;
            if (Math.abs(a2 - a1) > Math.PI) {
                if (a2 < a1) a2 += 2 * Math.PI;
                else a1 += 2 * Math.PI;
            }
            const midAngle = (a1 + a2) / 2;
            const segNum = SEGMENT_ORDER[(i + seg20Idx) % 20];
            const labelR = maxR * 1.08;
            const lx = cx + labelR * Math.cos(midAngle);
            const ly = cy + labelR * Math.sin(midAngle);
            
            ctx.font = 'bold 16px Arial';
            ctx.textAlign = 'center';
            ctx.textBaseline = 'middle';
            // Black outline
            ctx.strokeStyle = 'black';
            ctx.lineWidth = 3;
            ctx.strokeText(String(segNum), lx, ly);
            // Green for 20, white for others
            ctx.fillStyle = segNum === 20 ? '#00ff00' : '#ffffff';
            ctx.fillText(String(segNum), lx, ly);
        }
    }
    
    // Center dot
    ctx.beginPath();
    ctx.arc(cx, cy, 3, 0, 2 * Math.PI);
    ctx.fillStyle = 'cyan';
    ctx.fill();
}

// Fetch live snapshot and draw calibration overlay
async function showLiveCalibration(camIndex) {
    const stored = storedCalibrations[`cam${camIndex}`];
    const canvas = document.getElementById('calibration-canvas');
    const img = document.getElementById('camera-base-img');
    const loading = document.getElementById('main-camera-loading');
    const offline = document.getElementById('main-camera-offline');
    
    if (!canvas) {
        console.warn('No calibration-canvas element found');
        return false;
    }
    
    // Fetch live snapshot from DartSensor
    try {
        const snapRes = await fetch(`${DART_SENSOR_URL}/cameras/${camIndex}/snapshot`, {
            signal: AbortSignal.timeout(3000)
        });
        if (!snapRes.ok) throw new Error('Snapshot failed');
        
        const blob = await snapRes.blob();
        const snapUrl = URL.createObjectURL(blob);
        
        const baseImg = new Image();
        return new Promise((resolve) => {
            const timeout = setTimeout(() => {
                URL.revokeObjectURL(snapUrl);
                resolve(false);
            }, 5000);
            baseImg.onload = () => {
                clearTimeout(timeout);
                canvas.style.display = 'block';
                canvas.style.position = 'relative';
                canvas.style.width = '100%';
                canvas.style.height = 'auto';
                canvas.style.pointerEvents = 'auto';
                canvas.style.zIndex = '15';
                if (img) img.style.display = 'none';
                loading.classList.add('hidden');
                offline.classList.add('hidden');
                const calData = stored ? stored.calibrationData : null;
                drawCalibrationOverlay(canvas, calData, baseImg);
                URL.revokeObjectURL(snapUrl);
                resolve(true);
            };
            baseImg.onerror = () => {
                clearTimeout(timeout);
                URL.revokeObjectURL(snapUrl);
                resolve(false);
            };
            baseImg.src = snapUrl;
        });
    } catch(e) {
        console.warn('Live snapshot failed, falling back to stored overlay:', e);
        return false;
    }
}


// State
let selectedBackgrounds = [];
let customBackgrounds = [];
let storedCalibrations = {};  // From database
let selectedCamera = 0;
let mark20Mode = false;
let calibrationViewMode = 'combined';  // 'overlay' or 'combined'
let lastCameraSnapshot = null;  // Store the base camera image

// Segment numbers are now drawn by DartDetectionAI in the overlay image
// No need for canvas overlay - removed to avoid duplicate labels

// ============================================================================
// Initialization
// ============================================================================

document.addEventListener('DOMContentLoaded', () => {
    initTabs();
    initBackground();
    loadThemeSettings();
    initCalibration();
    initSystemStatus();
    initEventListeners();
    initImprovementLoop();
});



// ============================================================================
// NSFW Background Mode
// ============================================================================

async function loadNsfwBackgrounds() {
    try {
        // Fetch list of NSFW images from server
        const resp = await fetch('/api/backgrounds/nsfw');
        if (resp.ok) {
            nsfwBackgrounds = await resp.json();
            console.log(`Loaded ${nsfwBackgrounds.length} NSFW backgrounds`);
        }
    } catch (e) {
        console.error('Failed to load NSFW backgrounds:', e);
        nsfwBackgrounds = [];
    }
}

function toggleNsfwShow(show) {
    nsfwShow = show;
    renderBackgroundGallery();
}

function toggleNsfwMode(enabled) {
    nsfwMode = enabled;
    
    // Save to theme settings
    const theme = JSON.parse(localStorage.getItem('dartsmob-theme') || '{}');
    theme.nsfwMode = enabled;
    localStorage.setItem('dartsmob-theme', JSON.stringify(theme));
    
    // Switch backgrounds
    if (enabled && nsfwBackgrounds.length > 0) {
        selectedBackgrounds = [...nsfwBackgrounds];
    } else {
        selectedBackgrounds = [...DEFAULT_BACKGROUNDS];
    }
    
    // Apply first background
    if (selectedBackgrounds.length > 0) {
        document.getElementById('background-layer').style.backgroundImage = 
            `url('${selectedBackgrounds[0]}')`;
    }
    
    // Refresh background grid
    renderBackgroundGrid();
}

// ============================================================================
// Tab Navigation
// ============================================================================

function initTabs() {
    const tabs = document.querySelectorAll('.nav-tab-sm');
    const sections = document.querySelectorAll('.settings-section');
    
    tabs.forEach(tab => {
        tab.addEventListener('click', () => {
            const target = tab.dataset.tab;
            
            tabs.forEach(t => t.classList.remove('active'));
            tab.classList.add('active');
            
            sections.forEach(s => {
                s.classList.toggle('active', s.id === `tab-${target}`);
            });
        });
    });
}

// ============================================================================
// Background Management
// ============================================================================

function initBackground() {
    const theme = JSON.parse(localStorage.getItem('dartsmob-theme') || '{}');
    selectedBackgrounds = theme.backgrounds || [...DEFAULT_BACKGROUNDS];
    customBackgrounds = theme.customBackgrounds || [];
    nsfwMode = false; // always unchecked on load
    
    // Load NSFW backgrounds
    loadNsfwBackgrounds().then(() => {
        // Set checkbox state
        const nsfwCheckbox = document.getElementById('nsfw-enable');
        if (nsfwCheckbox) {
            nsfwCheckbox.checked = false;
        }
        renderBackgroundGallery();
    });
    
    if (selectedBackgrounds.length > 0) {
        document.getElementById('background-layer').style.backgroundImage = 
            `url('${selectedBackgrounds[0]}')`;
    }
    
    const opacity = theme.overlayOpacity ?? 70;
    document.getElementById('background-overlay').style.background = 
        `rgba(0, 0, 0, ${opacity / 100})`;
}

function loadThemeSettings() {
    const theme = JSON.parse(localStorage.getItem('dartsmob-theme') || '{}');
    
    document.getElementById('theme-app-name').value = theme.appName || 'DartsMob';
    document.getElementById('theme-tagline').value = theme.tagline || "The Family's Game";
    
    selectedBackgrounds = theme.backgrounds || [...DEFAULT_BACKGROUNDS];
    customBackgrounds = theme.customBackgrounds || [];
    renderBackgroundGallery();
    
    document.getElementById('slideshow-toggle').checked = theme.slideshow !== false;
    document.getElementById('slideshow-speed').value = theme.slideshowSpeed || 30000;
    
    const opacity = theme.overlayOpacity ?? 70;
    document.getElementById('overlay-opacity').value = opacity;
    document.getElementById('opacity-value').textContent = `${opacity}%`;
}

function renderBackgroundGallery() {
    const gallery = document.getElementById('background-gallery');
    
    // === Standard Backgrounds Section ===
    const allSfw = [...DEFAULT_BACKGROUNDS, ...customBackgrounds];
    let html = '<div style="margin-bottom: 25px;">';
    html += '<div style="display: flex; align-items: center; justify-content: space-between; margin-bottom: 0;">';
    html += '<span style="color: var(--paper); font-size: 1.1rem;">Standard Backgrounds</span>';
    html += '<label class="btn btn-secondary" style="font-size: 0.85rem; padding: 5px 16px; margin: 0; cursor: pointer; border: 1px solid var(--gold-dark); background: transparent; color: var(--gold);">Upload Background<input type="file" id="bg-upload" accept="image/*" hidden onchange="handleBackgroundUpload(event)"></label>';
    html += '</div>';
    html += '<div style="height: 2px; background: var(--gold); margin: 8px 0 12px 0;"></div>';
    html += '<div style="display: flex; flex-wrap: wrap; gap: 10px;">';
    html += allSfw.map(bg => `
        <div class="bg-thumb ${selectedBackgrounds.includes(bg) ? 'selected' : ''}"
            style="background-image: url('${bg}'); width: 120px; height: 75px; flex-shrink: 0; border: 2px solid var(--gold-dark); border-radius: 8px; background-size: cover; background-position: center; cursor: pointer;"
            onclick="toggleBackground('${bg}')">
            ${customBackgrounds.includes(bg) ? '<span class="custom-badge">Custom</span>' : ''}
        </div>
    `).join('');
    html += '</div>';
    html += '</div>';
    
    // === NSFW Backgrounds Section ===
    html += '<div>';
    html += '<div style="display: flex; align-items: center; justify-content: space-between; margin-bottom: 0;">';
    html += '<div style="display: flex; align-items: center; gap: 20px;">';
    html += '<span style="color: var(--paper); font-size: 1.1rem;">NSFW Backgrounds</span>';
    html += '<label style="display: flex; align-items: center; gap: 6px; cursor: pointer; color: var(--paper-muted); font-size: 0.9rem;"><input type="checkbox" id="nsfw-show" onchange="toggleNsfwShow(this.checked)" style="accent-color: var(--gold);"> Show</label>';
    html += '<label style="display: flex; align-items: center; gap: 6px; cursor: pointer; color: var(--paper-muted); font-size: 0.9rem;"><input type="checkbox" id="nsfw-enable" ' + (nsfwMode ? 'checked' : '') + ' onchange="toggleNsfwMode(this.checked)" style="accent-color: var(--gold);"> Enable</label>';
    html += '</div>';
    html += '<label class="btn btn-secondary" style="font-size: 0.85rem; padding: 5px 16px; margin: 0; cursor: pointer; border: 1px solid var(--gold-dark); background: transparent; color: var(--gold);">Upload NSFW<input type="file" id="nsfw-upload" accept="image/*" hidden onchange="uploadNsfwImage(this)"></label>';
    html += '</div>';
    html += '<div style="height: 2px; background: var(--gold); margin: 8px 0 12px 0;"></div>';
    
    if (nsfwBackgrounds.length === 0) {
        html += '<div style="color: #666; font-style: italic; padding: 10px 0;">No NSFW images uploaded</div>';
    } else {
        html += '<div style="display: flex; flex-wrap: wrap; gap: 10px;">';
        html += nsfwBackgrounds.map(bg => {
            const filename = bg.split('/').pop();
            const blurStyle = nsfwShow ? '' : 'filter: blur(20px);';
            return `
                <div class="bg-thumb ${selectedBackgrounds.includes(bg) ? 'selected' : ''}" style="position: relative; width: 120px; height: 75px; flex-shrink: 0; overflow: hidden; border: 2px solid var(--gold-dark); border-radius: 8px;">
                    <div style="position: absolute; inset: 0; background-image: url('${bg}'); background-size: cover; background-position: center; border-radius: 6px; ${blurStyle} transition: filter 0.3s;"></div>
                    <div style="position: absolute; inset: 0; border-radius: 6px; cursor: pointer;" onclick="toggleBackground('${bg}')"></div>
                    <button onclick="event.stopPropagation(); deleteNsfwImage('${filename}')" 
                        style="position: absolute; top: 2px; right: 2px; background: rgba(139,0,0,0.8); border: none; color: white; border-radius: 50%; width: 22px; height: 22px; cursor: pointer; font-size: 0.65rem; z-index: 2; line-height: 22px;">🗑️</button>
                </div>
            `;
        }).join('');
        html += '</div>';
    }
    html += '<div style="height: 2px; background: var(--gold); margin: 12px 0 0 0;"></div>';
    html += '</div>';
    
    gallery.innerHTML = html;
}



function toggleBackground(bg) {
    if (selectedBackgrounds.includes(bg)) {
        selectedBackgrounds = selectedBackgrounds.filter(b => b !== bg);
    } else {
        selectedBackgrounds.push(bg);
    }
    renderBackgroundGallery();
}

function renderBackgroundGrid() {
    renderBackgroundGallery();
}

async function uploadNsfwImage(input) {
    const file = input.files[0];
    if (!file) return;
    
    const formData = new FormData();
    formData.append('file', file);
    
    try {
        const resp = await fetch('/api/backgrounds/nsfw/upload', {
            method: 'POST',
            body: formData
        });
        
        if (!resp.ok) {
            const err = await resp.text();
            alert('Upload failed: ' + err);
            return;
        }
        
        const result = await resp.json();
        nsfwBackgrounds.push(result.path);
        
        if (nsfwMode) {
            selectedBackgrounds.push(result.path);
        }
        
        renderBackgroundGallery();
    } catch (e) {
        console.error('NSFW upload failed:', e);
        alert('Upload failed');
    }
    
    input.value = '';
}

async function deleteNsfwImage(filename) {
    if (!confirm('Delete this image?')) return;
    
    try {
        const resp = await fetch(`/api/backgrounds/nsfw/${encodeURIComponent(filename)}`, {
            method: 'DELETE'
        });
        
        if (!resp.ok) {
            alert('Delete failed');
            return;
        }
        
        const path = `/images/backgrounds/nsfw/${filename}`;
        nsfwBackgrounds = nsfwBackgrounds.filter(bg => bg !== path);
        selectedBackgrounds = selectedBackgrounds.filter(bg => bg !== path);
        renderBackgroundGallery();
    } catch (e) {
        console.error('NSFW delete failed:', e);
        alert('Delete failed');
    }
}

// ============================================================================
// Calibration
// ============================================================================

async function initCalibration() {
    // Set up camera button listeners
    document.querySelectorAll('.cam-btn-sm').forEach(btn => {
        btn.addEventListener('click', () => {
            selectCamera(parseInt(btn.dataset.cam));
        });
    });
    
    // Set up view toggle buttons
    document.querySelectorAll('.view-toggle-btn').forEach(btn => {
        btn.addEventListener('click', () => {
            document.querySelectorAll('.view-toggle-btn').forEach(b => b.classList.remove('active'));
            btn.classList.add('active');
            calibrationViewMode = btn.dataset.view;
            updateCalibrationView();
        });
    });
    
    // Set up image click for Mark 20
    const mainImg = document.getElementById('camera-base-img');
    mainImg.addEventListener('click', handleImageClick);
    
    // Load stored calibrations from database (not live cameras)
    await loadStoredCalibrations();
    
    // Show first camera's stored calibration
    selectCamera(0);
}

// Update the calibration view based on mode
function updateCalibrationView() {
    const img = document.getElementById('camera-base-img');
    
    // The overlay image already contains both camera + overlay drawn on it
    // So we don't need separate images - just show the overlay
    if (img.src) {
        img.style.display = 'block';
        img.style.position = 'absolute';
        img.style.zIndex = '15';
    }
}

async function loadStoredCalibrations() {
    try {
        const res = await fetch(`${DART_GAME_URL}/api/calibrations`, {
            signal: AbortSignal.timeout(3000)
        });
        
        if (res.ok) {
            const calibrations = await res.json();
            console.log('[CALIBRATION] Loaded calibrations:', calibrations);
            storedCalibrations = {};
            calibrations.forEach(c => {
                storedCalibrations[c.cameraId] = c;
            });
            console.log('[CALIBRATION] storedCalibrations:', storedCalibrations);
            
            // Update all camera button indicators
            for (let i = 0; i < 3; i++) {
                updateCameraIndicator(i);
            }
        }
    } catch (e) {
        console.error('Failed to load stored calibrations:', e);
    }
}

function updateCameraIndicator(camIndex) {
    const indicator = document.getElementById(`cam-ind-${camIndex}`);
    if (!indicator) return;
    
    const stored = storedCalibrations[`cam${camIndex}`];
    
    indicator.classList.remove('calibrated', 'not-calibrated', 'offline');
    
    if (stored) {
        indicator.classList.add('calibrated');
        indicator.title = `Stored: ${Math.round(stored.quality * 100)}%`;
    } else {
        indicator.classList.add('not-calibrated');
        indicator.title = 'Not calibrated';
    }
}

async function selectCamera(camIndex) {
    selectedCamera = camIndex;
    
    // Stop focus stream if active
    if (focusStreamActive) {
        stopFocusStream();
        document.getElementById('focus-btn').textContent = '🔍 Focus';
        document.getElementById('focus-btn').classList.remove('btn-active');
        document.getElementById('cam-focus-label').style.display = 'none';
    }
    
    // Update button states
    document.querySelectorAll('.cam-btn-sm').forEach(btn => {
        btn.classList.toggle('active', parseInt(btn.dataset.cam) === camIndex);
    });
    
    const img = document.getElementById('camera-base-img');
    const baseImg = document.getElementById('camera-base-img');
    const loading = document.getElementById('main-camera-loading');
    const offline = document.getElementById('main-camera-offline');
    const qualityLabel = document.getElementById('cam-quality-label');
    
    const stored = storedCalibrations[`cam${camIndex}`];
    
    console.log('[CALIBRATION] selectCamera:', camIndex, 'stored:', stored);
    
    // Show live camera feed with stored calibration overlay
    if (stored) {
        // Try live snapshot + overlay first
        const liveOk = await showLiveCalibration(camIndex);
        if (!liveOk && stored.overlayImagePath) {
            // Fallback to stored overlay image
            console.log('[CALIBRATION] Falling back to stored overlay:', stored.overlayImagePath);
            const cacheBuster = stored.overlayImagePath.includes('?') ? '&' : '?';
            img.src = stored.overlayImagePath + cacheBuster + 't=' + Date.now();
            img.style.display = 'block';
            img.classList.add('loaded');
            loading.classList.add('hidden');
            offline.classList.add('hidden');
        }
        if (stored.calibrationImagePath) {
            lastCameraSnapshot = stored.calibrationImagePath;
        } else {
            lastCameraSnapshot = null;
        }
        updateCalibrationView();
        
        // Show 20-angle info if Mark 20 was used
        const angleInfo = stored.twentyAngle ? ` (20 at ${Math.round(stored.twentyAngle)}°)` : '';
        
        const modelInfo = stored.calibrationModel ? ` [${stored.calibrationModel}]` : '';
        qualityLabel.textContent = `✅ Stored: ${Math.round(stored.quality * 100)}%${angleInfo}${modelInfo}`;
        qualityLabel.className = 'cam-quality-label calibrated';
    } else {
        // No stored calibration - show placeholder
        img.classList.remove('loaded');
        img.style.display = 'none';
        lastCameraSnapshot = null;
        loading.classList.add('hidden');
        offline.classList.remove('hidden');
        offline.querySelector('span').textContent = '📷 No calibration stored - click Calibrate';
        
        qualityLabel.textContent = '❌ Not Calibrated';
        qualityLabel.className = 'cam-quality-label failed';
    }
}

async function refreshCurrentCamera() {
    // Get live snapshot from camera (for preview before calibrating)
    const img = document.getElementById('camera-base-img');
    const baseImg = document.getElementById('camera-base-img');
    const loading = document.getElementById('main-camera-loading');
    const offline = document.getElementById('main-camera-offline');
    const qualityLabel = document.getElementById('cam-quality-label');
    
    img.classList.remove('loaded');
    img.src = '';  // Clear existing image immediately
    baseImg.style.display = 'none';
    lastCameraSnapshot = null;
    loading.classList.remove('hidden');
    loading.querySelector('span').textContent = '📷 Loading live view...';
    offline.classList.add('hidden');
    
    try {
        const res = await fetch(`${DART_SENSOR_URL}/cameras/${selectedCamera}/snapshot`, {
            signal: AbortSignal.timeout(5000)
        });
        
        if (res.ok) {
            const data = await res.json();
            const imgData = `data:image/jpeg;base64,${data.image}`;
            img.src = imgData;
            img.classList.add('loaded');
            loading.classList.add('hidden');
            
            // Store snapshot for combined view
            lastCameraSnapshot = imgData;
            updateCalibrationView();
            
            qualityLabel.textContent = '📷 Live Preview';
            qualityLabel.className = 'cam-quality-label';
        } else {
            throw new Error('Camera unavailable');
        }
    } catch (e) {
        console.error(`Camera ${selectedCamera} error:`, e);
        // Clear everything when camera is offline
        img.src = '';
        img.classList.remove('loaded');
        baseImg.style.display = 'none';
        lastCameraSnapshot = null;
        loading.classList.add('hidden');
        offline.classList.remove('hidden');
        offline.querySelector('span').textContent = '❌ Camera Offline - Check DartSensor';
        qualityLabel.textContent = '❌ Camera Offline';
        qualityLabel.className = 'cam-quality-label failed';
    }
}

// ============================================================================
// Focus Measurement (Live Streaming Mode)
// ============================================================================

let focusStreamActive = false;
let focusStreamInterval = null;
let focusCenterX = null;  // Custom center point (null = auto-detect/image center)
let focusCenterY = null;

async function toggleFocusMode() {
    const btn = document.getElementById('focus-btn');
    const focusLabel = document.getElementById('cam-focus-label');
    const img = document.getElementById('camera-base-img');
    
    if (focusStreamActive) {
        // Stop streaming
        stopFocusStream();
        btn.textContent = '🔍 Focus';
        btn.classList.remove('btn-active');
        focusLabel.style.display = 'none';
        img.style.cursor = 'default';
        // Remove click listener
        img.removeEventListener('click', handleFocusCenterClick);
    } else {
        // Start streaming
        focusStreamActive = true;
        focusCenterX = null;  // Reset center
        focusCenterY = null;
        btn.textContent = '⏹ Stop Focus';
        btn.classList.add('btn-active');
        focusLabel.style.display = 'inline';
        focusLabel.textContent = 'Click image to set center, or wait...';
        img.style.cursor = 'crosshair';
        // Add click listener for center selection
        img.addEventListener('click', handleFocusCenterClick);
        
        // Start live focus loop
        await runFocusStream();
    }
}

function handleFocusCenterClick(e) {
    const img = e.target;
    const rect = img.getBoundingClientRect();
    
    // Calculate click position relative to image
    const clickX = e.clientX - rect.left;
    const clickY = e.clientY - rect.top;
    
    // Scale to actual image dimensions
    const scaleX = img.naturalWidth / rect.width;
    const scaleY = img.naturalHeight / rect.height;
    
    focusCenterX = Math.round(clickX * scaleX);
    focusCenterY = Math.round(clickY * scaleY);
    
    const focusLabel = document.getElementById('cam-focus-label');
    focusLabel.textContent = `📍 Center: (${focusCenterX}, ${focusCenterY}) - Measuring...`;
    
    console.log(`Focus center set to: (${focusCenterX}, ${focusCenterY})`);
}

function stopFocusStream() {
    focusStreamActive = false;
    if (focusStreamInterval) {
        clearTimeout(focusStreamInterval);
        focusStreamInterval = null;
    }
}

async function runFocusStream() {
    const focusLabel = document.getElementById('cam-focus-label');
    const img = document.getElementById('camera-base-img');
    
    while (focusStreamActive) {
        try {
            // Get fresh snapshot (returns JSON with base64 image)
            const snapRes = await fetch(`${DART_SENSOR_URL}/cameras/${selectedCamera}/snapshot`);
            if (!snapRes.ok) throw new Error('Camera offline');
            
            const snapData = await snapRes.json();
            const base64Image = snapData.image;
            
            // Update preview image
            img.src = `data:image/jpeg;base64,${base64Image}`;
            
            // Build focus request with optional center
            const focusRequest = {
                camera_id: `cam${selectedCamera}`,
                image: base64Image
            };
            
            // Add custom center if set by user click
            if (focusCenterX !== null && focusCenterY !== null) {
                focusRequest.center_x = focusCenterX;
                focusRequest.center_y = focusCenterY;
            }
            
            // Send to focus endpoint
            const focusRes = await fetch(`${DART_DETECT_URL}/v1/focus`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(focusRequest)
            });
            
            if (!focusRes.ok) throw new Error('Focus API error');
            
            const data = await focusRes.json();
            
            // Update label with score and quality
            const emoji = data.quality === 'excellent' ? '🟢' : 
                          data.quality === 'good' ? '🟡' :
                          data.quality === 'fair' ? '🟠' : '🔴';
            
            focusLabel.textContent = `${emoji} Focus: ${data.score}/100 (${data.quality})`;
            focusLabel.className = `cam-focus-label focus-${data.quality}`;
            
        } catch (err) {
            console.error('Focus stream error:', err);
            focusLabel.textContent = `❌ ${err.message}`;
            focusLabel.className = 'cam-focus-label focus-poor';
        }
        
        // Wait before next frame (aim for ~2-3 fps)
        await new Promise(r => setTimeout(r, 400));
    }
}

// Helper: Convert blob to base64
function blobToBase64(blob) {
    return new Promise((resolve, reject) => {
        const reader = new FileReader();
        reader.onloadend = () => resolve(reader.result.split(',')[1]);
        reader.onerror = reject;
        reader.readAsDataURL(blob);
    });
}

// Get current calibration model from the dropdown
function getCurrentCalibrationModel() {
    const select = document.getElementById('calibration-model-select');
    return select ? select.value : 'default';
}

async function calibrateCurrentCamera() {
    const btn = document.getElementById('calibrate-btn');
    const qualityLabel = document.getElementById('cam-quality-label');
    const img = document.getElementById('camera-base-img');
    const loading = document.getElementById('main-camera-loading');
    
    btn.disabled = true;
    btn.textContent = '⏳ Calibrating...';
    qualityLabel.textContent = 'Calibrating...';
    qualityLabel.className = 'cam-quality-label';
    loading.classList.remove('hidden');
    loading.querySelector('span').textContent = '🎯 Running calibration...';
    
    try {
        // Get fresh snapshot
        const snapRes = await fetch(`${DART_SENSOR_URL}/cameras/${selectedCamera}/snapshot`);
        if (!snapRes.ok) throw new Error('Could not get camera snapshot');
        
        const snapData = await snapRes.json();
        
        // Send calibration request to DartDetect
        const calRes = await fetch(`${DART_DETECT_URL}/v1/calibrate`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                cameras: [{
                    camera_id: `cam${selectedCamera}`,
                    image: snapData.image
                }]
            })
        });
        
        if (!calRes.ok) throw new Error('Calibration request failed');
        
        const result = await calRes.json();
        const camResult = result.results?.[0];
        
        if (camResult?.success) {
            // Save to database
            const saveRes = await fetch(`${DART_GAME_URL}/api/calibrations`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    cameraId: `cam${selectedCamera}`,
                    calibrationImage: snapData.image,
                    overlayImage: camResult.overlay_image,
                    quality: camResult.quality,
                    calibrationModel: getCurrentCalibrationModel(),
                    calibrationData: JSON.stringify(camResult.calibration_data)
                })
            });
            
            if (saveRes.ok) {
                const saved = await saveRes.json();
                storedCalibrations[`cam${selectedCamera}`] = saved;
            }
            
            // Show overlay image
            if (camResult.overlay_image) {
                img.src = `data:image/png;base64,${camResult.overlay_image}`;
                img.classList.add('loaded');
            }
            
            loading.classList.add('hidden');
            const modelName = getCurrentCalibrationModel();
            qualityLabel.textContent = `✅ Stored: ${Math.round(camResult.quality * 100)}% (${modelName})`;
            qualityLabel.className = 'cam-quality-label calibrated';
            
            updateCameraIndicator(selectedCamera);
            
        } else {
            throw new Error(camResult?.error || 'Calibration failed');
        }
        
    } catch (e) {
        console.error('Calibration error:', e);
        loading.classList.add('hidden');
        qualityLabel.textContent = `❌ Failed: ${e.message}`;
        qualityLabel.className = 'cam-quality-label failed';
    } finally {
        btn.disabled = false;
        btn.textContent = '🎯 Calibrate';
    }
}

// ============================================================================
// Mark 20 Feature
// ============================================================================

function toggleMark20Mode() {
    mark20Mode = !mark20Mode;
    const btn = document.getElementById('mark20-btn');
    const img = document.getElementById('camera-base-img');
    const preview = document.querySelector('.camera-preview-full');
    
    if (mark20Mode) {
        btn.classList.add('active');
        btn.textContent = '🎯 Click on 20...';
        preview.classList.add('mark20-mode');
    } else {
        btn.classList.remove('active');
        btn.textContent = '🎯 Mark 20';
        preview.classList.remove('mark20-mode');
    }
}

async function handleImageClick(e) {
    if (!mark20Mode) return;
    
    const img = e.target;
    const rect = img.getBoundingClientRect();
    
    // Get click position as normalized 0-1 coordinates
    const x = (e.clientX - rect.left) / rect.width;
    const y = (e.clientY - rect.top) / rect.height;
    
    console.log(`Mark 20 clicked at: ${x.toFixed(3)}, ${y.toFixed(3)}`);
    
    // Send to API
    try {
        const res = await fetch(`${DART_GAME_URL}/api/calibrations/cam${selectedCamera}/mark20`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ cameraId: `cam${selectedCamera}`, x, y })
        });
        
        if (res.ok) {
            const result = await res.json();
            
            // Store the result (angle saved in DB, used by DartSensorService)
            storedCalibrations[`cam${selectedCamera}`] = result;
            
            const qualityLabel = document.getElementById('cam-quality-label');
            qualityLabel.textContent = `✅ 20 marked at ${Math.round(result.twentyAngle)}°`;
            
            // Exit mark 20 mode
            toggleMark20Mode();
        } else {
            const err = await res.json();
            alert(`Failed: ${err.error || 'Unknown error'}`);
        }
    } catch (e) {
        console.error('Mark 20 error:', e);
        alert('Failed to mark 20');
    }
    
    toggleMark20Mode();
}

// ============================================================================
// System Status
// ============================================================================

async function initSystemStatus() {
    // DartGame API
    try {
        const res = await fetch(`${DART_GAME_URL}/health`);
        setStatus('sys-game-api', res.ok);
    } catch (e) {
        setStatus('sys-game-api', false);
    }
    
    // DartDetect API
    try {
        const res = await fetch(`${DART_DETECT_URL}/health`, { mode: 'cors' });
        setStatus('sys-detect-api', res.ok);
    } catch (e) {
        setStatus('sys-detect-api', false);
    }
    
    // Database
    try {
        const res = await fetch(`${DART_GAME_URL}/api/players`);
        setStatus('sys-database', res.ok);
    } catch (e) {
        setStatus('sys-database', false);
    }
    
    // SignalR
    setStatus('sys-signalr', true, 'Available');
}

function setStatus(id, online, text) {
    const el = document.getElementById(id);
    if (!el) return;
    el.textContent = text || (online ? '🟢 Online' : '🔴 Offline');
    el.className = `status-value ${online ? 'status-online' : 'status-offline'}`;
}



// ============================================================================
// Rotate 20 - Shift segment alignment by 1 position
// ============================================================================

async function rotate20() {
    const camId = `cam${selectedCamera}`;
    const stored = storedCalibrations[camId];
    
    if (!stored || !stored.calibrationData) {
        alert('No calibration data to rotate. Calibrate first.');
        return;
    }
    
    const btn = document.getElementById('rotate20-btn');
    btn.disabled = true;
    btn.textContent = '⏳ Rotating...';
    
    try {
        // Call API to rotate the 20 position by 1 segment (18 degrees)
        const res = await fetch(`${DART_DETECT_URL}/v1/calibrations/${camId}/rotate20`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' }
        });
        
        if (res.ok) {
            const data = await res.json();
            console.log('Rotate 20 result:', data);
            
            // Reload calibrations and refresh view
            await loadStoredCalibrations();
            await selectCamera(selectedCamera);
            
            // Show new angle
            const qualityLabel = document.getElementById('cam-quality-label');
            qualityLabel.textContent = `✅ Rotated! 20 now at ${Math.round(data.twentyAngle || 0)}°`;
        } else {
            const err = await res.json();
            alert(`Rotate failed: ${err.error || err.detail || 'Unknown error'}`);
        }
    } catch (e) {
        console.error('Rotate 20 error:', e);
        alert(`Rotate failed: ${e.message}`);
    }
    
    btn.disabled = false;
    btn.textContent = '🔄 Rotate 20';
}

// ============================================================================
// Event Listeners
// ============================================================================


// ============================================================================
// Live Overlay Mode - Show calibration overlay on live camera feed
// ============================================================================

let liveOverlayActive = false;
let liveOverlayInterval = null;

async function toggleLiveOverlay() {
    const btn = document.getElementById('live-btn');
    const img = document.getElementById('camera-base-img');
    const canvas = document.getElementById('calibration-canvas');
    const loading = document.getElementById('main-camera-loading');
    
    if (liveOverlayActive) {
        // Stop live overlay
        stopLiveOverlay();
        btn.classList.remove('active');
        btn.textContent = '📡 Live';
        return;
    }
    
    // Start live overlay
    liveOverlayActive = true;
    btn.classList.add('active');
    btn.textContent = '⏹ Stop';
    
    // Get stored calibration overlay for current camera
    const calData = storedCalibrations[`cam${selectedCamera}`];
    if (!calData || !calData.overlayImagePath) {
        alert('No calibration stored for this camera. Calibrate first.');
        stopLiveOverlay();
        btn.classList.remove('active');
        btn.textContent = '📡 Live';
        return;
    }
    
    // Set up canvas to show overlay on top of live feed
    const ctx = canvas.getContext('2d');
    canvas.style.display = 'block';
    canvas.style.position = 'absolute';
    canvas.style.top = '0';
    canvas.style.left = '0';
    canvas.style.pointerEvents = 'none';
    canvas.style.opacity = '0.7';
    
    // Load calibration overlay image
    const overlayImg = new Image();
    overlayImg.src = calData.overlayImagePath;
    
    await new Promise(resolve => {
        overlayImg.onload = resolve;
    });
    
    // Start streaming live camera underneath
    async function updateLiveFrame() {
        if (!liveOverlayActive) return;
        
        try {
            const res = await fetch(`${DART_SENSOR_URL}/cameras/${selectedCamera}/snapshot`, {
                signal: AbortSignal.timeout(3000)
            });
            
            if (res.ok && liveOverlayActive) {
                const data = await res.json();
                img.src = `data:image/jpeg;base64,${data.image}`;
                img.classList.add('loaded');
                loading.classList.add('hidden');
                
                // Resize canvas to match image
                canvas.width = img.naturalWidth || img.width;
                canvas.height = img.naturalHeight || img.height;
                canvas.style.width = img.clientWidth + 'px';
                canvas.style.height = img.clientHeight + 'px';
                
                // Draw overlay
                ctx.clearRect(0, 0, canvas.width, canvas.height);
                ctx.drawImage(overlayImg, 0, 0, canvas.width, canvas.height);
            }
        } catch (e) {
            console.error('Live overlay error:', e);
        }
        
        if (liveOverlayActive) {
            liveOverlayInterval = setTimeout(updateLiveFrame, 200);  // 5 FPS
        }
    }
    
    loading.classList.remove('hidden');
    loading.querySelector('span').textContent = '📡 Live + Overlay...';
    updateLiveFrame();
}

function stopLiveOverlay() {
    liveOverlayActive = false;
    if (liveOverlayInterval) {
        clearTimeout(liveOverlayInterval);
        liveOverlayInterval = null;
    }
    const canvas = document.getElementById('calibration-canvas');
    canvas.style.display = 'none';
}


// ==================== REFRESH CAMERA ====================
async function refreshCameraWithOverlay() {
    const camIndex = selectedCamera;
    const img = document.getElementById('camera-base-img');
    const canvas = document.getElementById('calibration-canvas');
    const ctx = canvas.getContext('2d');
    const loading = document.getElementById('main-camera-loading');
    const offline = document.getElementById('main-camera-offline');
    const refreshBtn = document.getElementById('refresh-btn');
    
    if (refreshBtn) {
        refreshBtn.disabled = true;
        refreshBtn.textContent = '⏳ Loading...';
    }
    
    loading.classList.remove('hidden');
    offline.classList.add('hidden');
    img.style.display = 'none';
    
    try {
        // Get fresh frame from camera
        const snapResp = await fetch(`${DART_SENSOR_URL}/cameras/${camIndex}/snapshot`);
        if (!snapResp.ok) throw new Error('Failed to get camera snapshot');
        
        const snapData = await snapResp.json();
        const frameBase64 = snapData.image;
        
        // Load the frame
        const frameImg = new Image();
        frameImg.onload = async () => {
            // Set canvas size to match image
            canvas.width = frameImg.naturalWidth;
            canvas.height = frameImg.naturalHeight;
            
            // Draw the camera frame
            ctx.drawImage(frameImg, 0, 0);
            
            // Now draw calibration overlay on top if we have one
            const stored = storedCalibrations[`cam${camIndex}`];
            if (stored && stored.overlayImagePath) {
                const overlayImg = new Image();
                overlayImg.onload = () => {
                    // Draw overlay at 70% opacity
                    ctx.globalAlpha = 0.7;
                    ctx.drawImage(overlayImg, 0, 0, canvas.width, canvas.height);
                    ctx.globalAlpha = 1.0;
                    
                    loading.classList.add('hidden');
                    if (refreshBtn) {
                        refreshBtn.disabled = false;
                        refreshBtn.textContent = '🔄 Refresh';
                    }
                };
                overlayImg.onerror = () => {
                    console.warn('Failed to load overlay image');
                    loading.classList.add('hidden');
                    if (refreshBtn) {
                        refreshBtn.disabled = false;
                        refreshBtn.textContent = '🔄 Refresh';
                    }
                };
                overlayImg.src = stored.overlayImagePath;
            } else {
                // No calibration, just show the frame
                loading.classList.add('hidden');
                if (refreshBtn) {
                    refreshBtn.disabled = false;
                    refreshBtn.textContent = '🔄 Refresh';
                }
            }
        };
        
        frameImg.onerror = () => {
            throw new Error('Failed to load frame image');
        };
        
        frameImg.src = `data:image/jpeg;base64,${frameBase64}`;
        
    } catch (err) {
        console.error('Refresh failed:', err);
        loading.classList.add('hidden');
        offline.classList.remove('hidden');
        offline.querySelector('span').textContent = '❌ ' + err.message;
        if (refreshBtn) {
            refreshBtn.disabled = false;
            refreshBtn.textContent = '🔄 Refresh';
        }
    }
}

function initEventListeners() {
    // Live button - show live camera feed with calibration overlay
    document.getElementById('refresh-btn')?.addEventListener('click', refreshCameraWithOverlay);
    
    // Refresh button
    document.getElementById('refresh-btn')?.addEventListener('click', refreshCurrentCamera);
    
    // Focus button
    document.getElementById('focus-btn')?.addEventListener('click', toggleFocusMode);
    
    // Calibrate button
    document.getElementById('calibrate-btn')?.addEventListener('click', calibrateCurrentCamera);
    
    // Mark 20 button
    document.getElementById('mark20-btn')?.addEventListener('click', toggleMark20Mode);
    
    // Rotate 20 button
    document.getElementById('rotate20-btn')?.addEventListener('click', rotate20);
    
    // Overlay opacity
    document.getElementById('overlay-opacity')?.addEventListener('input', (e) => {
        const value = e.target.value;
        document.getElementById('opacity-value').textContent = `${value}%`;
        document.getElementById('background-overlay').style.background = 
            `rgba(0, 0, 0, ${value / 100})`;
    });
    
    // Save theme
    document.getElementById('save-theme-btn')?.addEventListener('click', saveTheme);
    
    // Reset theme
    document.getElementById('reset-theme-btn')?.addEventListener('click', resetTheme);
    
    // Upload background
    document.getElementById('bg-upload')?.addEventListener('change', handleBackgroundUpload);
}

function saveTheme() {
    const theme = {
        appName: document.getElementById('theme-app-name').value,
        tagline: document.getElementById('theme-tagline').value,
        backgrounds: selectedBackgrounds,
        customBackgrounds: customBackgrounds,
        slideshow: document.getElementById('slideshow-toggle').checked,
        slideshowSpeed: parseInt(document.getElementById('slideshow-speed').value),
        overlayOpacity: parseInt(document.getElementById('overlay-opacity').value)
    };
    
    localStorage.setItem('dartsmob-theme', JSON.stringify(theme));
    alert('✅ Theme saved!');
}

function resetTheme() {
    if (confirm('Reset all theme settings to defaults?')) {
        localStorage.removeItem('dartsmob-theme');
        location.reload();
    }
}

function handleBackgroundUpload(e) {
    const file = e.target.files[0];
    if (!file) return;
    
    const reader = new FileReader();
    reader.onload = (event) => {
        const dataUrl = event.target.result;
        customBackgrounds.push(dataUrl);
        selectedBackgrounds.push(dataUrl);
        renderBackgroundGallery();
    };
    reader.readAsDataURL(file);
}

// ==========================================================================
// Audio Settings
// ==========================================================================

function initAudioSettings() {
    // Load saved settings
    const savedMode = localStorage.getItem('audio-mode') || 'off';
    const savedVolume = localStorage.getItem('audio-volume') || '80';
    
    // Set radio button
    const radioBtn = document.querySelector(`input[name="audio-mode"][value="${savedMode}"]`);
    if (radioBtn) radioBtn.checked = true;
    
    // Set volume slider
    const volumeSlider = document.getElementById('audio-volume');
    const volumeValue = document.getElementById('volume-value');
    if (volumeSlider) {
        volumeSlider.value = savedVolume;
        if (volumeValue) volumeValue.textContent = `${savedVolume}%`;
        
        volumeSlider.addEventListener('input', () => {
            if (volumeValue) volumeValue.textContent = `${volumeSlider.value}%`;
        });
    }
    
    // Save button
    document.getElementById('save-audio-btn')?.addEventListener('click', saveAudioSettings);
    
    // Test button
    document.getElementById('test-audio-btn')?.addEventListener('click', testAudio);
}

function saveAudioSettings() {
    const mode = document.querySelector('input[name="audio-mode"]:checked')?.value || 'off';
    const volume = document.getElementById('audio-volume')?.value || '80';
    
    localStorage.setItem('audio-mode', mode);
    localStorage.setItem('audio-volume', volume);
    
    alert('✅ Audio settings saved! Refresh the game page to apply.');
}

function testAudio() {
    const mode = document.querySelector('input[name="audio-mode"]:checked')?.value || 'off';
    const volume = parseInt(document.getElementById('audio-volume')?.value || '80') / 100;
    
    if (mode === 'off') {
        alert('Audio is turned off. Select a mode first.');
        return;
    }
    
    if (mode === 'tts') {
        if (!window.speechSynthesis) {
            alert('Browser TTS not supported on this device.');
            return;
        }
        const utterance = new SpeechSynthesisUtterance('Triple 20');
        utterance.volume = volume;
        speechSynthesis.speak(utterance);
    } else if (mode === 'files') {
        const audio = new Audio('/audio/triple-20.mp3');
        audio.volume = volume;
        audio.play().catch(err => {
            alert('Could not play audio file. Make sure /audio/triple-20.mp3 exists.');
        });
    }
}

// Add to init
const originalInit = window.onload;
window.onload = function() {
    if (originalInit) originalInit();
    initAudioSettings();
};

// ==========================================================================
// Logs Management
// ==========================================================================

async function loadLogs() {
    const source = document.getElementById('log-source-filter')?.value || '';
    const level = document.getElementById('log-level-filter')?.value || '';
    const limit = document.getElementById('log-limit')?.value || '100';
    
    const logContent = document.getElementById('log-content');
    const logCount = document.getElementById('log-count');
    
    try {
        let url = `/api/logs?limit=${limit}`;
        if (source) url += `&source=${source}`;
        if (level) url += `&level=${level}`;
        
        const response = await fetch(url);
        if (!response.ok) throw new Error('Failed to load logs');
        
        const logs = await response.json();
        
        if (logs.length === 0) {
            logContent.innerHTML = '<span style="color: #666;">No logs found</span>';
            logCount.textContent = '0 entries';
            return;
        }
        
        // Format logs with colors
        const formatted = logs.map(log => {
            const ts = new Date(log.timestamp).toLocaleString();
            const levelColor = {
                'DEBUG': '#888',
                'INFO': '#4a9eff',
                'WARN': '#ffa500',
                'ERROR': '#ff4444'
            }[log.level] || '#ccc';
            
            const sourceColor = {
                'DartDetect': '#9b59b6',
                'DartSensor': '#3498db',
                'DartGame': '#2ecc71',
                'UI': '#e67e22'
            }[log.source] || '#ccc';
            
            let line = `<span style="color:#666">[${ts}]</span> `;
            line += `<span style="color:${sourceColor}">[${log.source}]</span> `;
            line += `<span style="color:${levelColor}">[${log.level}]</span> `;
            if (log.category) line += `<span style="color:#888">[${log.category}]</span> `;
            line += `<span style="color:#eee">${escapeHtml(log.message)}</span>`;
            if (log.data) line += `\n  <span style="color:#666">Data: ${escapeHtml(log.data)}</span>`;
            
            return line;
        }).join('\n');
        
        logContent.innerHTML = formatted;
        logCount.textContent = `${logs.length} entries`;
        
    } catch (err) {
        logContent.innerHTML = `<span style="color:#ff4444">Error loading logs: ${err.message}</span>`;
    }
}

function escapeHtml(text) {
    const div = document.createElement('div');
    div.textContent = text;
    return div.innerHTML;
}

async function downloadLogs() {
    const limit = document.getElementById('log-limit')?.value || '1000';
    window.location.href = `/api/logs/download?limit=${limit}`;
}

async function clearLogs() {
    if (!confirm('Are you sure you want to delete ALL logs? This cannot be undone.')) {
        return;
    }
    
    try {
        const response = await fetch('/api/logs', { method: 'DELETE' });
        if (!response.ok) throw new Error('Failed to clear logs');
        
        const result = await response.json();
        alert(`Cleared ${result.deleted} log entries`);
        loadLogs();
    } catch (err) {
        alert(`Error: ${err.message}`);
    }
}

function initLogs() {
    document.getElementById('refresh-logs-btn')?.addEventListener('click', loadLogs);
    document.getElementById('download-logs-btn')?.addEventListener('click', downloadLogs);
    document.getElementById('clear-logs-btn')?.addEventListener('click', clearLogs);
    
    // Logging toggle handler
    const loggingToggle = document.getElementById('logging-enabled');
    if (loggingToggle) {
        // Load initial state
        fetch('/api/logs/status')
            .then(r => r.json())
            .then(data => {
                loggingToggle.checked = data.enabled;
                // Also sync benchmark toggle if on same page
                const benchmarkToggle = document.getElementById('benchmark-enabled');
                if (benchmarkToggle) {
                    benchmarkToggle.checked = data.enabled;
                }
            })
            .catch(() => {});
        
        // Handle toggle change
        loggingToggle.addEventListener('change', async () => {
            try {
                const response = await fetch('/api/logs/status', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ enabled: loggingToggle.checked })
                });
                const data = await response.json();
                console.log(`Logging ${data.enabled ? 'enabled' : 'disabled'}`);
                
                // Also toggle benchmark when logging is toggled
                const benchmarkEndpoint = loggingToggle.checked 
                    ? `${DART_DETECT_URL}/v1/benchmark/enable`
                    : `${DART_DETECT_URL}/v1/benchmark/disable`;
                fetch(benchmarkEndpoint, { method: 'POST' }).catch(() => {});
                
                // Sync benchmark toggle if visible
                const benchmarkToggle = document.getElementById('benchmark-enabled');
                if (benchmarkToggle) {
                    benchmarkToggle.checked = loggingToggle.checked;
                }
            } catch (e) {
                console.error('Failed to toggle logging:', e);
            }
        });
    }
    
    // Filter change handlers
    document.getElementById('log-source-filter')?.addEventListener('change', loadLogs);
    document.getElementById('log-level-filter')?.addEventListener('change', loadLogs);
    document.getElementById('log-limit')?.addEventListener('change', loadLogs);
    
    // Load initial logs when tab is opened
    document.querySelector('[data-tab="logs"]')?.addEventListener('click', () => {
        setTimeout(loadLogs, 100);
    });
}

// Add to init chain
const originalInit2 = window.onload;
window.onload = function() {
    if (originalInit2) originalInit2();
    initLogs();
    initAccuracy();
};


// ========== ACCURACY TAB ==========

async function loadBenchmarkStatus() {
    try {
        const response = await fetch(`${DART_DETECT_URL}/v1/benchmark/status`);
        const data = await response.json();
        
        const toggle = document.getElementById('benchmark-enabled');
        if (toggle) {
            toggle.checked = data.enabled;
        }
        
        return data;
    } catch (e) {
        console.error('Failed to load benchmark status:', e);
        return null;
    }
}

async function loadBenchmarkGames() {
    const gamesList = document.getElementById('games-list');
    if (!gamesList) return;
    
    try {
        const response = await fetch(`${DART_DETECT_URL}/v1/benchmark/games`);
        const data = await response.json();
        
        if (!data.games || data.games.length === 0) {
            gamesList.innerHTML = `
                <div class="game-item" style="background: #0a0a0a; border: 1px solid #333; border-radius: 6px; padding: 10px; color: var(--paper-muted);">
                    No benchmark data yet. Enable logging and play a game.
                </div>
            `;
            document.getElementById('overall-accuracy').textContent = '--';
            document.getElementById('total-darts-logged').textContent = '0';
            document.getElementById('total-corrections').textContent = '0';
            return;
        }
        
        // Calculate overall stats
        let totalDarts = 0;
        let totalCorrections = 0;
        for (const game of data.games) {
            totalDarts += game.total_darts;
            totalCorrections += game.corrections;
        }
        const overallAccuracy = totalDarts > 0 
            ? (((totalDarts - totalCorrections) / totalDarts) * 100).toFixed(1) + '%'
            : '--';
        
        document.getElementById('overall-accuracy').textContent = overallAccuracy;
        document.getElementById('total-darts-logged').textContent = totalDarts;
        document.getElementById('total-corrections').textContent = totalCorrections;
        
        // Render games list
        gamesList.innerHTML = data.games.map(game => `
            <div class="game-item" style="background: #0a0a0a; border: 1px solid #333; border-radius: 6px; padding: 12px; margin-bottom: 8px; cursor: pointer;"
                 onclick="showGameDetails('${game.board_id}', '${game.game_id}')">
                <div style="display: flex; justify-content: space-between; align-items: center;">
                    <div>
                        <span style="color: var(--gold); font-weight: bold;">${game.game_id}</span>
                        <span style="color: var(--paper-muted); margin-left: 10px;">Board: ${game.board_id}</span>
                    </div>
                    <div style="text-align: right;">
                        <span style="color: ${game.accuracy >= 90 ? '#4caf50' : game.accuracy >= 70 ? '#ff9800' : '#f44336'}; font-size: 1.2rem; font-weight: bold;">
                            ${game.accuracy}%
                        </span>
                        <span style="color: var(--paper-muted); display: block; font-size: 0.85rem;">
                            ${game.total_darts} darts, ${game.corrections} corrections
                        </span>
                    </div>
                </div>
            </div>
        `).join('');
        
    } catch (e) {
        console.error('Failed to load benchmark games:', e);
        gamesList.innerHTML = `
            <div class="game-item" style="background: #0a0a0a; border: 1px solid #333; border-radius: 6px; padding: 10px; color: #ff6b6b;">
                Error loading games: ${e.message}
            </div>
        `;
    }
}

async function showGameDetails(boardId, gameId) {
    const modal = document.getElementById('game-details-modal');
    const content = document.getElementById('game-details-content');
    
    if (!modal || !content) return;
    
    modal.style.display = 'flex';
    content.innerHTML = '<p>Loading...</p>';
    
    try {
        const response = await fetch(`${DART_DETECT_URL}/v1/benchmark/games/${boardId}/${gameId}/darts`);
        const data = await response.json();
        
        if (!data.darts || data.darts.length === 0) {
            content.innerHTML = '<p>No darts found for this game.</p>';
            return;
        }
        
        // Group by round
        const byRound = {};
        for (const dart of data.darts) {
            if (!byRound[dart.round]) byRound[dart.round] = [];
            byRound[dart.round].push(dart);
        }
        
        let html = `<h3 style="color: var(--gold);">Game: ${gameId}</h3>`;
        
        for (const [round, darts] of Object.entries(byRound)) {
            html += `<h4 style="color: var(--paper); margin-top: 15px;">${round}</h4>`;
            html += `<div style="display: flex; gap: 10px; flex-wrap: wrap;">`;
            
            for (const dart of darts) {
                const meta = dart.metadata || {};
                const final = meta.final_result || {};
                const correction = dart.correction;
                
                const detected = final.segment ? `${final.multiplier > 1 ? (final.multiplier === 2 ? 'D' : 'T') : ''}${final.segment}` : '?';
                const corrected = correction ? `${correction.corrected.multiplier > 1 ? (correction.corrected.multiplier === 2 ? 'D' : 'T') : ''}${correction.corrected.segment}` : null;
                
                const isCorrect = !correction;
                const borderColor = isCorrect ? '#4caf50' : '#f44336';
                
                // Find cam0 debug image
                const debugImg = dart.images?.find(i => i.includes('cam0_debug')) || dart.images?.[0];
                const imgUrl = debugImg ? `${DART_DETECT_URL}/v1/benchmark/image/${boardId}/${gameId}/${dart.round}/${dart.dart}/${debugImg}` : '';
                
                html += `
                    <div style="background: #1a1a1a; border: 2px solid ${borderColor}; border-radius: 8px; padding: 10px; width: 180px;">
                        ${imgUrl ? `<img src="${imgUrl}" style="width: 100%; border-radius: 4px; margin-bottom: 8px;">` : ''}
                        <div style="text-align: center;">
                            <span style="font-size: 1.5rem; color: var(--paper);">${dart.dart}</span>
                            <div style="margin-top: 5px;">
                                <span style="color: ${isCorrect ? '#4caf50' : '#ff9800'};">${detected}</span>
                                ${corrected ? `<span style="color: #f44336;"> → ${corrected}</span>` : ''}
                            </div>
                        </div>
                    </div>
                `;
            }
            html += `</div>`;
        }
        
        content.innerHTML = html;
        
    } catch (e) {
        console.error('Failed to load game details:', e);
        content.innerHTML = `<p style="color: #ff6b6b;">Error: ${e.message}</p>`;
    }
}



// === Stereo Calibration ===

async function initStereoCalibration() {
    // Check current status
    try {
        const response = await fetch(`${DART_DETECT_URL}/v1/stereo/status`);
        const data = await response.json();
        
        const modeSelect = document.getElementById('triangulation-mode');
        const statusSpan = document.getElementById('stereo-status');
        const panel = document.getElementById('stereo-calibration-panel');
        
        if (modeSelect) {
            modeSelect.value = data.mode;
        }
        
        if (statusSpan) {
            if (data.stereo_available) {
                statusSpan.textContent = `✅ Stereo calibrated (${data.cameras_calibrated.length} cameras)`;
                statusSpan.style.color = '#4ade80';
            } else {
                statusSpan.textContent = '⚠️ Stereo not calibrated';
                statusSpan.style.color = '#fbbf24';
            }
        }
        
        // Show/hide panel based on mode
        if (panel) {
            panel.style.display = modeSelect?.value === 'stereo' ? 'block' : 'none';
        }
    } catch (e) {
        console.error('Failed to load stereo status:', e);
    }
    
    // Mode change handler
    document.getElementById('triangulation-mode')?.addEventListener('change', async (e) => {
        const mode = e.target.value;
        const panel = document.getElementById('stereo-calibration-panel');
        
        if (mode === 'stereo') {
            // Check if calibration exists
            const response = await fetch(`${DART_DETECT_URL}/v1/stereo/status`);
            const data = await response.json();
            
            if (!data.stereo_available) {
                panel.style.display = 'block';
                alert('Stereo calibration required. Follow the steps below to calibrate.');
                e.target.value = 'ellipse'; // Revert until calibrated
                return;
            }
        }
        
        // Set mode
        try {
            await fetch(`${DART_DETECT_URL}/v1/stereo/set-mode`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ mode })
            });
            
            panel.style.display = mode === 'stereo' ? 'block' : 'none';
        } catch (e) {
            alert('Failed to set mode: ' + e.message);
        }
    });
    
    // Get checkerboard pattern
    document.getElementById('get-checkerboard-btn')?.addEventListener('click', async () => {
        try {
            const response = await fetch(`${DART_DETECT_URL}/v1/stereo/checkerboard?cols=9&rows=6&square_mm=25`);
            const data = await response.json();
            
            const modal = document.getElementById('checkerboard-modal');
            const img = document.getElementById('checkerboard-image');
            const download = document.getElementById('checkerboard-download');
            
            img.src = data.image;
            download.href = data.image;
            modal.style.display = 'block';
        } catch (e) {
            alert('Failed to get checkerboard: ' + e.message);
        }
    });
    
    // Capture calibration image
    document.getElementById('capture-stereo-btn')?.addEventListener('click', async () => {
        try {
            // Get current camera images from calibrations
            const calibrations = await fetch(`${DART_GAME_URL}/api/settings/calibrations`).then(r => r.json());
            
            // Capture from each camera
            const cameras = [];
            for (let i = 0; i < 3; i++) {
                try {
                    const snapResp = await fetch(`${DART_SENSOR_URL}/cameras/${i}/snapshot`);
                    const snapData = await snapResp.json();
                    if (snapData.image) {
                        cameras.push({
                            camera_id: `cam${i}`,
                            image: snapData.image
                        });
                    }
                } catch (e) {
                    console.warn(`Camera ${i} not available`);
                }
            }
            
            if (cameras.length === 0) {
                alert('No cameras available. Make sure DartSensor is running.');
                return;
            }
            
            // Send to stereo capture endpoint
            const response = await fetch(`${DART_DETECT_URL}/v1/stereo/capture`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ cameras, board_id: 'default' })
            });
            const data = await response.json();
            
            // Update count
            document.getElementById('capture-count').textContent = `${data.total_captures} images captured`;
            
            // Enable calibration button if enough captures
            const calibBtn = document.getElementById('run-stereo-calibration-btn');
            if (calibBtn) {
                calibBtn.disabled = !data.ready_to_calibrate;
            }
            
            // Show previews
            const preview = document.getElementById('stereo-preview');
            preview.style.display = 'block';
            
            for (const result of data.results) {
                if (result.success && result.preview) {
                    const img = document.getElementById(`stereo-preview-${result.camera_id}`);
                    if (img) {
                        img.src = `data:image/jpeg;base64,${result.preview}`;
                    }
                }
            }
            
            // Show status
            const failed = data.results.filter(r => !r.success);
            if (failed.length > 0) {
                alert(`Checkerboard not detected in: ${failed.map(r => r.camera_id).join(', ')}`);
            }
        } catch (e) {
            alert('Capture failed: ' + e.message);
        }
    });
    
    // Run calibration
    document.getElementById('run-stereo-calibration-btn')?.addEventListener('click', async () => {
        if (!confirm('Run stereo calibration? This will process all captured images.')) return;
        
        try {
            const response = await fetch(`${DART_DETECT_URL}/v1/stereo/calibrate`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ board_id: 'default' })
            });
            const data = await response.json();
            
            if (data.success) {
                alert(`Calibration complete!\n\nCameras: ${data.cameras_calibrated.join(', ')}\nReprojection error: ${data.reprojection_error?.toFixed(3) || 'N/A'} px`);
                
                // Refresh status
                initStereoCalibration();
                
                // Enable stereo mode
                document.getElementById('triangulation-mode').value = 'stereo';
                await fetch(`${DART_DETECT_URL}/v1/stereo/set-mode`, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ mode: 'stereo' })
                });
            } else {
                alert('Calibration failed: ' + (data.error || 'Unknown error'));
            }
        } catch (e) {
            alert('Calibration failed: ' + e.message);
        }
    });
    
    // Clear captures
    document.getElementById('clear-stereo-captures-btn')?.addEventListener('click', async () => {
        if (!confirm('Clear all captured calibration images?')) return;
        
        try {
            await fetch(`${DART_DETECT_URL}/v1/stereo/clear-captures`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ board_id: 'default' })
            });
            
            document.getElementById('capture-count').textContent = '0 images captured';
            document.getElementById('run-stereo-calibration-btn').disabled = true;
            document.getElementById('stereo-preview').style.display = 'none';
        } catch (e) {
            alert('Failed to clear: ' + e.message);
        }
    });
}



function initAccuracy() {
    initStereoCalibration();  // Initialize stereo calibration UI
    // Benchmark toggle handler
    const benchmarkToggle = document.getElementById('benchmark-enabled');
    if (benchmarkToggle) {
        // Load initial state
        loadBenchmarkStatus();
        
        // Handle toggle change
        benchmarkToggle.addEventListener('change', async () => {
            try {
                const endpoint = benchmarkToggle.checked 
                    ? `${DART_DETECT_URL}/v1/benchmark/enable`
                    : `${DART_DETECT_URL}/v1/benchmark/disable`;
                    
                const response = await fetch(endpoint, { method: 'POST' });
                const data = await response.json();
                console.log(`Benchmark logging ${data.enabled ? 'enabled' : 'disabled'}`);
            } catch (e) {
                console.error('Failed to toggle benchmark:', e);
            }
        });
    }
    
    // Refresh button
    document.getElementById('refresh-accuracy-btn')?.addEventListener('click', loadBenchmarkGames);
    
    // Re-run Benchmark button - replays all stored darts through current detection
    document.getElementById('rerun-benchmark-btn')?.addEventListener('click', async () => {
        const modal = document.getElementById('benchmark-results-modal');
        const resultsDiv = document.getElementById('benchmark-results-content');
        
        if (modal) {
            modal.style.display = 'flex';
            modal.classList.remove('hidden');
        }
        resultsDiv.innerHTML = '<div style="text-align: center; padding: 30px;"><span style="font-size: 2rem;">⏳</span><p style="color: var(--paper); margin-top: 10px;">Running benchmark replay...</p><p style="color: var(--paper-muted);">This may take a minute.</p></div>';
        
        try {
            const response = await fetch(`${DART_DETECT_URL}/v1/benchmark/replay-all-darts`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ board_id: 'default', limit: 200 })
            });
            const data = await response.json();
            
            if (data.error) {
                resultsDiv.innerHTML = `<div style="color: #ff6b6b; padding: 20px;">Error: ${data.error}</div>`;
                return;
            }
            
            // Build results display
            let html = `
                <div style="display: flex; gap: 30px; flex-wrap: wrap; margin-bottom: 20px; padding: 15px; background: #0a0a0a; border-radius: 8px;">
                    <div>
                        <span style="color: var(--gold); font-size: 2rem; font-weight: bold;">${data.total_darts}</span>
                        <span style="color: var(--paper-muted); display: block;">Total Darts</span>
                    </div>
                    <div>
                        <span style="color: #4ecdc4; font-size: 2rem; font-weight: bold;">${data.consistency}</span>
                        <span style="color: var(--paper-muted); display: block;">Consistency</span>
                    </div>
                    <div>
                        <span style="color: var(--paper); font-size: 2rem; font-weight: bold;">${data.matches_original}/${data.total_darts}</span>
                        <span style="color: var(--paper-muted); display: block;">Match Original</span>
                    </div>
                    <div>
                        <span style="color: #ff6b6b; font-size: 2rem; font-weight: bold;">${data.had_corrections}</span>
                        <span style="color: var(--paper-muted); display: block;">Had Corrections</span>
                    </div>
                    <div>
                        <span style="color: #51cf66; font-size: 2rem; font-weight: bold;">${data.correction_fix_rate}</span>
                        <span style="color: var(--paper-muted); display: block;">Corrections Fixed</span>
                    </div>
                </div>
            `;
            
            // Show mismatches
            const mismatches = data.results.filter(r => !r.matches_original || r.had_correction);
            if (mismatches.length > 0) {
                html += '<h3 style="color: var(--gold); margin: 15px 0 10px;">Differences & Corrections</h3>';
                html += '<table style="width: 100%; border-collapse: collapse; font-size: 0.9rem;">';
                html += '<tr style="background: #333;"><th style="padding: 8px; text-align: left;">Round</th><th>Dart</th><th>Original</th><th>Replay</th><th>Correction</th><th>Status</th></tr>';
                
                for (const r of mismatches) {
                    const orig = r.original ? `${r.original.multiplier > 1 ? (r.original.multiplier === 3 ? 'T' : 'D') : 'S'}${r.original.segment}` : '-';
                    const replay = r.new_result ? `${r.new_result.multiplier > 1 ? (r.new_result.multiplier === 3 ? 'T' : 'D') : 'S'}${r.new_result.segment}` : '-';
                    const corr = r.corrected_to ? `${r.corrected_to.multiplier > 1 ? (r.corrected_to.multiplier === 3 ? 'T' : 'D') : 'S'}${r.corrected_to.segment}` : '-';
                    const status = r.now_correct === true ? '✅' : (r.now_correct === false ? '❌' : (r.matches_original ? '=' : '≠'));
                    const rowColor = r.now_correct === true ? 'rgba(81, 207, 102, 0.2)' : (r.now_correct === false ? 'rgba(255, 107, 107, 0.2)' : 'transparent');
                    
                    html += `<tr style="background: ${rowColor}; border-bottom: 1px solid #333;">
                        <td style="padding: 6px 8px; color: var(--paper-muted);">${r.round}</td>
                        <td style="padding: 6px 8px; color: var(--paper);">${r.dart}</td>
                        <td style="padding: 6px 8px; color: var(--paper);">${orig}</td>
                        <td style="padding: 6px 8px; color: ${r.matches_original ? 'var(--paper)' : '#ffd93d'};">${replay}</td>
                        <td style="padding: 6px 8px; color: #ff6b6b;">${corr}</td>
                        <td style="padding: 6px 8px; text-align: center;">${status}</td>
                    </tr>`;
                }
                html += '</table>';
            } else {
                html += '<p style="color: #51cf66; padding: 15px;">All darts matched! No differences found.</p>';
            }
            
            resultsDiv.innerHTML = html;
            
        } catch (e) {
            console.error('Benchmark replay failed:', e);
            resultsDiv.innerHTML = `<div style="color: #ff6b6b; padding: 20px;">Error: ${e.message}</div>`;
        }
    });
    
    // Clear benchmark data button
    document.getElementById('clear-benchmark-btn')?.addEventListener('click', async () => {
        if (!confirm('Are you sure you want to delete ALL benchmark data? This cannot be undone.')) {
            return;
        }
        try {
            const response = await fetch(`${DART_DETECT_URL}/v1/benchmark/clear`, { method: 'POST' });
            const data = await response.json();
            if (data.cleared) {
                alert('Benchmark data cleared!');
                loadBenchmarkGames(); // Refresh the list
            } else {
                alert('Failed to clear: ' + (data.error || 'Unknown error'));
            }
        } catch (e) {
            console.error('Failed to clear benchmark:', e);
            alert('Error clearing benchmark data: ' + e.message);
        }
    });
    
    // Load data when tab is opened
    document.querySelector('[data-tab="accuracy"]')?.addEventListener('click', () => {
        setTimeout(() => {
            loadBenchmarkStatus();
            loadBenchmarkGames();
        }, 100);
    });
}


// ==================== MODEL SELECTION ====================

const MODEL_DESCRIPTIONS = {
    "default": "Balanced speed/accuracy, INT8 optimized",
    "best": "Newer architecture, potentially better accuracy",
    "rect": "Non-square input, higher precision",
    "square": "Square input variant",
    "384x640": "Smaller rect input, faster inference",
    "552x960": "Medium rect input, balanced",
    "736x1280": "Large rect input, highest resolution"
};

// ==================== CALIBRATION MODEL SELECTION ====================

async function loadCalibrationModel() {
    try {
        const resp = await fetch(`${DART_DETECT_URL}/v1/calibration-models`);
        if (resp.ok) {
            const data = await resp.json();
            const select = document.getElementById('calibration-model-select');
            const status = document.getElementById('cal-model-status');
            
            // Populate dropdown options
            if (select && data.models) {
                select.innerHTML = '';
                for (const [key, info] of Object.entries(data.models)) {
                    const opt = document.createElement('option');
                    opt.value = key;
                    opt.textContent = info.name || key;
                    opt.title = info.description || '';
                    select.appendChild(opt);
                }
                select.value = data.active || 'default';
            }
            if (status) {
                status.textContent = `Active: ${data.active || 'default'}`;
                status.style.color = 'var(--gold)';
            }
        }
    } catch (err) {
        console.error('Failed to load calibration model:', err);
    }
}

async function applyCalibrationModel() {
    const select = document.getElementById('calibration-model-select');
    const btn = document.getElementById('apply-cal-model-btn');
    const status = document.getElementById('cal-model-status');
    
    if (!select) return;
    
    const model = select.value;
    btn.disabled = true;
    btn.textContent = '⏳ Switching...';
    status.textContent = '';
    
    try {
        const resp = await fetch(`${DART_DETECT_URL}/v1/calibration-models/select`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ model })
        });
        
        if (resp.ok) {
            const data = await resp.json();
            status.textContent = `✓ Switched to ${data.active}`;
            status.style.color = 'var(--success)';
        } else {
            const err = await resp.json();
            status.textContent = `✗ ${err.error || 'Failed'}`;
            status.style.color = 'var(--error)';
        }
    } catch (err) {
        status.textContent = `✗ Error: ${err.message}`;
        status.style.color = 'var(--error)';
    }
    
    btn.disabled = false;
    btn.textContent = 'Apply';
}



async function loadCurrentModel() {
    try {
        const resp = await fetch(`${DART_DETECT_URL}/v1/models`);
        if (resp.ok) {
            const data = await resp.json();
            const select = document.getElementById('detection-model-select');
            const status = document.getElementById('model-status');
            const desc = document.getElementById('model-description');
            
            if (select) {
                select.value = data.active || 'default';
            }
            if (status) {
                status.textContent = `Currently active: ${data.active || 'default'}`;
                status.style.color = 'var(--gold)';
            }
            if (desc) {
                desc.textContent = MODEL_DESCRIPTIONS[data.active] || '';
            }
        }
    } catch (err) {
        console.error('Failed to load current model:', err);
    }
}

async function applyDetectionModel() {
    const select = document.getElementById('detection-model-select');
    const btn = document.getElementById('apply-model-btn');
    const status = document.getElementById('model-status');
    
    if (!select) return;
    
    const model = select.value;
    btn.disabled = true;
    btn.textContent = '⏳ Switching & Recalibrating...';
    status.textContent = '';
    status.style.color = 'var(--paper-muted)';
    
    // Show progress overlay - dark modal popup
    const overlay = document.createElement('div');
    overlay.id = 'model-switch-overlay';
    overlay.style.cssText = `
        position: fixed; top: 0; left: 0; right: 0; bottom: 0;
        background: rgba(0,0,0,0.95); z-index: 1000;
        display: flex; align-items: center; justify-content: center;
    `;
    overlay.innerHTML = `
        <div style="background: #1a1a1a; border: 2px solid var(--gold); border-radius: 12px; 
                    padding: 40px; max-width: 450px; text-align: center; box-shadow: 0 0 50px rgba(0,0,0,0.8);">
            <div style="color: var(--gold); font-size: 4rem; margin-bottom: 20px;">🔄</div>
            <h2 style="color: var(--gold); margin: 0 0 15px 0;">Switching Model</h2>
            <div style="color: var(--paper); font-size: 1.1rem; margin-bottom: 20px;">
                Recalibrating all cameras...
            </div>
            <div style="color: var(--paper-muted); font-size: 0.9rem; line-height: 1.5;">
                Grabbing fresh images and running calibration with the new model. This may take 10-20 seconds.
            </div>
            <div style="margin-top: 25px;">
                <div style="background: #333; border-radius: 8px; height: 6px; overflow: hidden;">
                    <div style="background: linear-gradient(90deg, var(--gold), #f4cf67); height: 100%; width: 30%; animation: pulse 1.5s ease-in-out infinite;"></div>
                </div>
            </div>
        </div>
        <style>
            @keyframes pulse {
                0%, 100% { width: 30%; opacity: 1; }
                50% { width: 70%; opacity: 0.7; }
            }
        </style>
    `;
    document.body.appendChild(overlay);
    
    try {
        const resp = await fetch(`${DART_DETECT_URL}/v1/models/select-and-recalibrate`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ model })
        });
        
        overlay.remove();
        const data = await resp.json();
        
        if (data.success) {
            status.textContent = `✓ Switched to ${data.model}, calibrated ${data.cameras_calibrated}/${data.total_cameras} cameras`;
            status.style.color = '#22c55e';
            
            // Refresh calibration display
            await loadStoredCalibrations();
            await selectCamera(selectedCamera);
            
            // Show success modal with details
            showModelSwitchResults(data);
        } else {
            status.textContent = `✗ ${data.error || 'Failed'}`;
            status.style.color = '#ef4444';
        }
    } catch (err) {
        overlay.remove();
        console.error('Model switch failed:', err);
        status.textContent = '✗ Error: ' + err.message;
        status.style.color = '#ef4444';
    } finally {
        btn.disabled = false;
        btn.textContent = 'Apply Model';
    }
}

function showModelSwitchResults(data) {
    const modal = document.createElement('div');
    modal.className = 'benchmark-modal';
    modal.style.cssText = `
        position: fixed; top: 0; left: 0; right: 0; bottom: 0;
        background: rgba(0,0,0,0.9); z-index: 1000;
        display: flex; align-items: center; justify-content: center;
        padding: 20px;
    `;
    
    const details = data.details || {};
    const calibrations = details.calibration || [];
    
    let calHtml = calibrations.map(c => {
        const icon = c.success ? '✅' : '❌';
        const quality = c.success ? ` (quality: ${(c.quality * 100).toFixed(0)}%)` : '';
        const error = c.error ? ` - ${c.error}` : '';
        return `<div style="padding: 8px 0; border-bottom: 1px solid #333;">
            ${icon} <strong>${c.camera_id}</strong>${quality}${error}
        </div>`;
    }).join('');
    
    modal.innerHTML = `
        <div style="background: #1a1a1a; border: 2px solid var(--gold); border-radius: 12px; 
                    max-width: 500px; width: 100%; padding: 20px;">
            <div style="display: flex; justify-content: space-between; align-items: center; margin-bottom: 20px;">
                <h2 style="color: var(--gold); margin: 0;">🔄 Model Switched</h2>
                <button onclick="this.closest('.benchmark-modal').remove()" 
                        style="background: none; border: none; color: var(--paper); font-size: 1.5rem; cursor: pointer;">✕</button>
            </div>
            
            <div style="background: #0a0a0a; padding: 15px; border-radius: 8px; margin-bottom: 15px;">
                <div style="color: var(--paper-muted); margin-bottom: 5px;">New Model</div>
                <div style="color: var(--gold); font-size: 1.3rem; font-weight: bold;">${data.model_info?.name || data.model}</div>
                <div style="color: var(--paper-muted); font-size: 0.9rem;">${data.model_info?.description || ''}</div>
            </div>
            
            <h3 style="color: var(--paper); margin: 0 0 10px 0;">Calibration Results</h3>
            <div style="background: #0a0a0a; padding: 15px; border-radius: 8px; margin-bottom: 15px;">
                ${calHtml || '<div style="color: var(--paper-muted);">No cameras calibrated</div>'}
            </div>
            
            <div style="display: grid; grid-template-columns: 1fr 1fr; gap: 15px; margin-bottom: 20px;">
                <div style="background: #0a0a0a; padding: 15px; border-radius: 8px; text-align: center;">
                    <div style="font-size: 2rem; color: #22c55e; font-weight: bold;">${data.cameras_calibrated || 0}</div>
                    <div style="color: var(--paper-muted);">Calibrated</div>
                </div>
                <div style="background: #0a0a0a; padding: 15px; border-radius: 8px; text-align: center;">
                    <div style="font-size: 2rem; color: var(--gold); font-weight: bold;">${data.cameras_saved || 0}</div>
                    <div style="color: var(--paper-muted);">Saved to DB</div>
                </div>
            </div>
            
            <div style="text-align: center;">
                <button onclick="this.closest('.benchmark-modal').remove()" 
                        class="btn btn-primary" style="padding: 12px 30px;">
                    ✓ Done
                </button>
            </div>
        </div>
    `;
    document.body.appendChild(modal);
}


// Update description when selection changes
function onModelSelectChange() {
    const select = document.getElementById('detection-model-select');
    const desc = document.getElementById('model-description');
    if (select && desc) {
        desc.textContent = MODEL_DESCRIPTIONS[select.value] || '';
    }
}

// Wire up model selection events
document.addEventListener('DOMContentLoaded', () => {
    const applyBtn = document.getElementById('apply-model-btn');
    const modelSelect = document.getElementById('detection-model-select');
    
    if (applyBtn) {
        applyBtn.addEventListener('click', applyDetectionModel);
    }
    if (modelSelect) {
        modelSelect.addEventListener('change', onModelSelectChange);
    }
    
    // Calibration model button
    const applyCalBtn = document.getElementById('apply-cal-model-btn');
    if (applyCalBtn) {
        applyCalBtn.addEventListener('click', applyCalibrationModel);
    }
    
    // Load calibration model status
    loadCalibrationModel();
    
    // Load current model when accuracy tab is shown
    const accuracyTab = document.querySelector('[data-tab="accuracy"]');
    if (accuracyTab) {
        accuracyTab.addEventListener('click', () => {
            setTimeout(loadCurrentModel, 100);
        });
    }
});


// ==================== CONFIDENCE THRESHOLD ====================

async function loadConfidenceThreshold() {
    try {
        const resp = await fetch(`${DART_DETECT_URL}/v1/settings/threshold`);
        if (resp.ok) {
            const data = await resp.json();
            const slider = document.getElementById('confidence-threshold-slider');
            const valueSpan = document.getElementById('confidence-threshold-value');
            
            if (slider) {
                slider.value = data.threshold;
            }
            if (valueSpan) {
                valueSpan.textContent = data.threshold.toFixed(2);
            }
        }
    } catch (err) {
        console.error('Failed to load confidence threshold:', err);
    }
}

async function applyConfidenceThreshold() {
    const slider = document.getElementById('confidence-threshold-slider');
    const btn = document.getElementById('apply-threshold-btn');
    
    if (!slider) return;
    
    const threshold = parseFloat(slider.value);
    
    if (btn) btn.disabled = true;
    
    try {
        const resp = await fetch(`${DART_DETECT_URL}/v1/settings/threshold`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ threshold: threshold })
        });
        
        const data = await resp.json();
        
        if (resp.ok && data.success) {
            console.log(`Threshold set to ${data.current}`);
            // Brief visual feedback
            if (btn) {
                btn.textContent = 'Applied!';
                setTimeout(() => { btn.textContent = 'Apply'; }, 1500);
            }
        }
    } catch (err) {
        console.error('Failed to apply threshold:', err);
    } finally {
        if (btn) btn.disabled = false;
    }
}

function onThresholdSliderChange() {
    const slider = document.getElementById('confidence-threshold-slider');
    const valueSpan = document.getElementById('confidence-threshold-value');
    if (slider && valueSpan) {
        valueSpan.textContent = parseFloat(slider.value).toFixed(2);
    }
}

// Wire up threshold events
document.addEventListener('DOMContentLoaded', () => {
    const thresholdSlider = document.getElementById('confidence-threshold-slider');
    const applyThresholdBtn = document.getElementById('apply-threshold-btn');
    
    if (thresholdSlider) {
        thresholdSlider.addEventListener('input', onThresholdSliderChange);
    }
    if (applyThresholdBtn) {
        applyThresholdBtn.addEventListener('click', applyConfidenceThreshold);
    }
    
    // Also load threshold when accuracy tab is shown
    const accuracyTab = document.querySelector('[data-tab="accuracy"]');
    if (accuracyTab) {
        const originalClick = accuracyTab.onclick;
        accuracyTab.addEventListener('click', () => {
            setTimeout(loadConfidenceThreshold, 100);
        });
    }
});


// ==================== DETECTION METHOD ====================

async function loadDetectionMethod() {
    try {
        const resp = await fetch(`${DART_DETECT_URL}/v1/settings/method`);
        if (resp.ok) {
            const data = await resp.json();
            const select = document.getElementById('detection-method-select');
            if (select && data.method) {
                select.value = data.method;
            }
        }
    } catch (err) {
        console.error('Failed to load detection method:', err);
    }
}

async function applyDetectionMethod() {
    const select = document.getElementById('detection-method-select');
    const statusSpan = document.getElementById('method-status');
    
    if (!select) return;
    
    const method = select.value;
    
    try {
        if (statusSpan) statusSpan.textContent = 'Applying...';
        
        const resp = await fetch(`${DART_DETECT_URL}/v1/settings/method`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ method: method })
        });
        
        if (resp.ok) {
            const data = await resp.json();
            
            // Trigger recalibration when switching methods to refresh overlays
            if (statusSpan) statusSpan.textContent = 'Recalibrating cameras...';
            try {
                const recalResp = await fetch(`${DART_DETECT_URL}/v1/recalibrate-all`, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' }
                });
                if (recalResp.ok) {
                    // Reload stored calibrations to get fresh overlays
                    await loadStoredCalibrations();
                    selectCamera(selectedCamera);  // Refresh current view
                }
            } catch (recalErr) {
                console.warn('Recalibration failed:', recalErr);
            }
            
            if (statusSpan) {
                statusSpan.textContent = `✓ Using ${method} detection`;
                statusSpan.style.color = '#22c55e';
            }
        } else {
            const err = await resp.json();
            if (statusSpan) {
                statusSpan.textContent = `✗ ${err.error || 'Failed'}`;
                statusSpan.style.color = '#ef4444';
            }
        }
    } catch (err) {
        console.error('Failed to set detection method:', err);
        if (statusSpan) {
            statusSpan.textContent = '✗ API error';
            statusSpan.style.color = '#ef4444';
        }
    }
}

// Wire up detection method events
document.addEventListener('DOMContentLoaded', () => {
    const methodBtn = document.getElementById('apply-method-btn');
    if (methodBtn) {
        methodBtn.addEventListener('click', applyDetectionMethod);
    }
    
    // Load detection method when accuracy tab shown
    const accuracyTab = document.querySelector('[data-tab="accuracy"]');
    if (accuracyTab) {
        accuracyTab.addEventListener('click', () => {
            setTimeout(loadDetectionMethod, 150);
        });
    }
});


// ==================== SCORING MODE ====================

async function loadScoringMode() {
    try {
        const resp = await fetch(`${DART_DETECT_URL}/v1/scoring-mode`);
        if (resp.ok) {
            const data = await resp.json();
            const select = document.getElementById('scoring-mode-select');
            if (select && data.scoring_mode) {
                select.value = data.scoring_mode;
            }
        }
    } catch (err) {
        console.error('Failed to load scoring mode:', err);
    }
}

async function applyScoringMode() {
    const select = document.getElementById('scoring-mode-select');
    const statusSpan = document.getElementById('scoring-status');
    
    if (!select) return;
    
    const mode = select.value;
    
    try {
        if (statusSpan) statusSpan.textContent = 'Applying...';
        
        const resp = await fetch(`${DART_DETECT_URL}/v1/scoring-mode`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ mode: mode })
        });
        
        if (resp.ok) {
            const data = await resp.json();
            if (statusSpan) {
                statusSpan.textContent = `✓ Using ${mode} scoring`;
                statusSpan.style.color = '#22c55e';
            }
        } else {
            const err = await resp.json();
            if (statusSpan) {
                statusSpan.textContent = `✗ ${err.error || 'Failed'}`;
                statusSpan.style.color = '#ef4444';
            }
        }
    } catch (err) {
        console.error('Failed to set scoring mode:', err);
        if (statusSpan) {
            statusSpan.textContent = '✗ API error';
            statusSpan.style.color = '#ef4444';
        }
    }
}

document.addEventListener('DOMContentLoaded', () => {
    const scoringBtn = document.getElementById('apply-scoring-btn');
    if (scoringBtn) {
        scoringBtn.addEventListener('click', applyScoringMode);
    }
    
    // Load scoring mode when accuracy tab shown
    const accuracyTab = document.querySelector('[data-tab="accuracy"]');
    if (accuracyTab) {
        accuracyTab.addEventListener('click', () => {
            setTimeout(loadScoringMode, 200);
        });
    }
});

// ==================== AUTO-TUNE ====================

async function runAutoTune() {
    const btn = document.getElementById('auto-tune-btn');
    if (btn) {
        btn.disabled = true;
        btn.textContent = '⏳ Analyzing...';
    }
    
    // Simple loading overlay
    const overlay = document.createElement('div');
    overlay.id = 'autotune-overlay';
    overlay.style.cssText = `
        position: fixed; top: 0; left: 0; right: 0; bottom: 0;
        background: rgba(0,0,0,0.8); z-index: 1000;
        display: flex; align-items: center; justify-content: center;
        flex-direction: column;
    `;
    overlay.innerHTML = `
        <div style="color: var(--gold); font-size: 3rem; margin-bottom: 20px;">🔧</div>
        <div style="color: var(--paper); font-size: 1.2rem;">Analyzing benchmark data...</div>
        <div style="color: var(--paper-muted); margin-top: 10px;">This may take a few seconds</div>
    `;
    document.body.appendChild(overlay);
    
    try {
        const resp = await fetch(`${DART_DETECT_URL}/v1/benchmark/auto-tune`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' }
        });
        
        overlay.remove();
        
        const data = await resp.json();
        
        if (!resp.ok || !data.success) {
            alert(`Auto-tune failed: ${data.error || 'Unknown error'}`);
            return;
        }
        
        // Show results modal
        showAutoTuneResults(data);
        
    } catch (err) {
        overlay.remove();
        console.error('Auto-tune failed:', err);
        alert('Auto-tune failed: ' + err.message);
    } finally {
        if (btn) {
            btn.disabled = false;
            btn.textContent = '🔧 Auto-Tune';
        }
    }
}



function showAutoTuneResults(data) {
    const modal = document.createElement('div');
    modal.className = 'benchmark-modal';
    modal.style.cssText = `
        position: fixed; top: 0; left: 0; right: 0; bottom: 0;
        background: rgba(0,0,0,0.9); z-index: 1000;
        display: flex; align-items: center; justify-content: center;
        padding: 20px;
    `;
    
    const summary = data.summary || {};
    const analysis = data.analysis || {};
    const recommendations = data.recommendations || [];
    const nextSteps = data.next_steps || [];
    
    // Build recommendations HTML
    let recsHtml = '';
    for (const rec of recommendations) {
        const typeColor = {
            'calibration': '#f59e0b',
            'focus': '#3b82f6', 
            'normal': '#22c55e',
            'ok': '#22c55e'
        }[rec.type] || '#888';
        
        recsHtml += `
            <div style="background: #0a0a0a; border-left: 4px solid ${typeColor}; padding: 12px; margin-bottom: 10px; border-radius: 0 6px 6px 0;">
                <div style="display: flex; justify-content: space-between; align-items: center;">
                    <strong style="color: ${typeColor};">${rec.issue}</strong>
                    <span style="background: #333; padding: 2px 8px; border-radius: 4px; font-size: 0.8rem;">${rec.count} errors</span>
                </div>
                <p style="color: var(--paper-muted); margin: 8px 0; font-size: 0.9rem;">${rec.description}</p>
                <p style="color: var(--paper); margin: 0; font-size: 0.9rem;">👉 <strong>${rec.action}</strong></p>
            </div>
        `;
    }
    
    // Build next steps HTML
    let stepsHtml = nextSteps.map(step => 
        `<div style="padding: 8px 0; border-bottom: 1px solid #333; color: var(--paper);">${step}</div>`
    ).join('');
    
    // Build analysis breakdown
    const analysisItems = [
        { label: 'Zone Errors (wrong multiplier)', value: analysis.zone_errors || 0, color: '#f59e0b' },
        { label: 'Adjacent Segment Errors', value: analysis.adjacent_errors || 0, color: '#22c55e' },
        { label: 'Opposite Side Errors', value: analysis.opposite_errors || 0, color: '#ef4444' },
        { label: 'Missed Detections', value: analysis.missed_detections || 0, color: '#3b82f6' },
        { label: 'Camera Disagreements', value: analysis.camera_disagreements || 0, color: '#a855f7' }
    ];
    
    let analysisHtml = analysisItems.map(item => `
        <div style="display: flex; justify-content: space-between; padding: 6px 0; border-bottom: 1px solid #222;">
            <span style="color: var(--paper-muted);">${item.label}</span>
            <span style="color: ${item.color}; font-weight: bold;">${item.value}</span>
        </div>
    `).join('');
    
    // Camera issues
    let camIssuesHtml = '';
    const camIssues = analysis.detection_issues_by_camera || {};
    if (Object.keys(camIssues).length > 0) {
        camIssuesHtml = '<div style="margin-top: 10px;"><strong style="color: var(--paper-muted);">Detection Failures by Camera:</strong>';
        for (const [cam, count] of Object.entries(camIssues)) {
            camIssuesHtml += `<span style="margin-left: 15px; color: #ef4444;">${cam}: ${count}</span>`;
        }
        camIssuesHtml += '</div>';
    }
    
    modal.innerHTML = `
        <div style="background: #1a1a1a; border: 2px solid var(--gold); border-radius: 12px; 
                    max-width: 800px; width: 100%; max-height: 90vh; overflow-y: auto; padding: 20px;">
            <div style="display: flex; justify-content: space-between; align-items: center; margin-bottom: 20px;">
                <h2 style="color: var(--gold); margin: 0;">🔧 Auto-Tune Analysis</h2>
                <button onclick="this.closest('.benchmark-modal').remove()" 
                        style="background: none; border: none; color: var(--paper); font-size: 1.5rem; cursor: pointer;">✕</button>
            </div>
            
            <!-- Summary Stats -->
            <div style="display: grid; grid-template-columns: repeat(4, 1fr); gap: 15px; margin-bottom: 20px;">
                <div style="background: #0a0a0a; padding: 15px; border-radius: 8px; text-align: center;">
                    <div style="font-size: 1.8rem; color: var(--gold); font-weight: bold;">${summary.current_accuracy || '--'}</div>
                    <div style="color: var(--paper-muted); font-size: 0.85rem;">Accuracy</div>
                </div>
                <div style="background: #0a0a0a; padding: 15px; border-radius: 8px; text-align: center;">
                    <div style="font-size: 1.8rem; color: var(--paper); font-weight: bold;">${summary.total_darts || 0}</div>
                    <div style="color: var(--paper-muted); font-size: 0.85rem;">Total Darts</div>
                </div>
                <div style="background: #0a0a0a; padding: 15px; border-radius: 8px; text-align: center;">
                    <div style="font-size: 1.8rem; color: #ef4444; font-weight: bold;">${summary.total_corrections || 0}</div>
                    <div style="color: var(--paper-muted); font-size: 0.85rem;">Corrections</div>
                </div>
                <div style="background: #0a0a0a; padding: 15px; border-radius: 8px; text-align: center;">
                    <div style="font-size: 1.8rem; color: #22c55e; font-weight: bold;">${summary.corrections_fixed || 0}</div>
                    <div style="color: var(--paper-muted); font-size: 0.85rem;">Auto-Fixed</div>
                </div>
            </div>
            
            <!-- Error Analysis -->
            <div style="background: #0a0a0a; padding: 15px; border-radius: 8px; margin-bottom: 20px;">
                <h3 style="color: var(--paper); margin: 0 0 10px 0;">📊 Error Breakdown</h3>
                ${analysisHtml}
                ${camIssuesHtml}
            </div>
            
            <!-- Recommendations -->
            <div style="margin-bottom: 20px;">
                <h3 style="color: var(--paper); margin: 0 0 15px 0;">💡 Recommendations</h3>
                ${recsHtml || '<p style="color: var(--paper-muted);">No issues detected!</p>'}
            </div>
            
            <!-- Next Steps -->
            <div style="background: #0a0a0a; padding: 15px; border-radius: 8px;">
                <h3 style="color: var(--gold); margin: 0 0 10px 0;">📋 Next Steps</h3>
                ${stepsHtml || '<p style="color: var(--paper-muted);">Keep playing!</p>'}
            </div>
            
            <div style="margin-top: 20px; text-align: center;">
                <button onclick="this.closest('.benchmark-modal').remove()" 
                        class="btn btn-primary" style="padding: 12px 30px;">
                    ✓ Got It
                </button>
            </div>
        </div>
    `;
    document.body.appendChild(modal);
}


async function applyAutoTuneConfig(config) {
    try {
        const resp = await fetch(`${DART_DETECT_URL}/v1/benchmark/apply-config`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(config)
        });
        
        const data = await resp.json();
        
        if (data.success) {
            // Update the slider to show new value
            const slider = document.getElementById('confidence-threshold-slider');
            const valueSpan = document.getElementById('confidence-threshold-value');
            if (slider && config.confidence_threshold) {
                slider.value = config.confidence_threshold;
                if (valueSpan) valueSpan.textContent = config.confidence_threshold.toFixed(2);
            }
            
            alert(`Config applied!\n\nConfidence threshold: ${config.confidence_threshold}\n\nNote: Restart DartDetect to apply boundary_weight and polar_threshold changes.`);
        } else {
            alert('Failed to apply config: ' + (data.error || 'Unknown error'));
        }
    } catch (err) {
        alert('Failed to apply config: ' + err.message);
    }
}

// Wire up auto-tune button
document.addEventListener('DOMContentLoaded', () => {
    document.getElementById('auto-tune-btn')?.addEventListener('click', runAutoTune);
});


// ============================================================================
// Improvement Loop
// ============================================================================

function initImprovementLoop() {
    const btn = document.getElementById('improve-loop-btn');
    if (btn) {
        btn.addEventListener('click', runImprovementLoop);
    }
}

async function runImprovementLoop() {
    const btn = document.getElementById('improve-loop-btn');
    if (btn) {
        btn.disabled = true;
        btn.textContent = '⏳ Running...';
    }
    
    // Show progress modal
    const progressModal = document.createElement('div');
    progressModal.className = 'benchmark-modal';
    progressModal.id = 'improve-progress-modal';
    progressModal.style.cssText = `
        position: fixed; top: 0; left: 0; right: 0; bottom: 0;
        background: rgba(0,0,0,0.95); z-index: 1000;
        display: flex; align-items: center; justify-content: center;
        padding: 20px;
    `;
    progressModal.innerHTML = `
        <div style="background: #1a1a1a; border: 2px solid #9333ea; border-radius: 12px; 
                    max-width: 500px; width: 100%; padding: 30px; text-align: center;">
            <h2 style="color: #9333ea; margin: 0 0 20px 0;">🔄 Improvement Loop</h2>
            <p style="color: var(--paper-muted); margin-bottom: 20px;" id="improve-status">Starting improvement loop...</p>
            <div style="background: #333; border-radius: 8px; height: 24px; overflow: hidden;">
                <div id="improve-progress-bar" style="background: linear-gradient(90deg, #9333ea, #c084fc); height: 100%; width: 0%; transition: width 0.3s;"></div>
            </div>
            <div style="color: var(--paper-muted); margin-top: 10px; font-size: 0.9em;" id="improve-detail">Testing parameter combinations...</div>
            <div style="margin-top: 20px;">
                <button onclick="stopImprovementLoop()" class="btn btn-danger" style="background: #8b0000;">⏹️ Stop</button>
            </div>
        </div>
    `;
    document.body.appendChild(progressModal);
    
    // Start polling for progress
    let pollInterval = setInterval(async () => {
        try {
            const progResp = await fetch(`${DART_DETECT_URL}/v1/benchmark/improve/status`);
            const prog = await progResp.json();
            
            if (prog.running) {
                document.getElementById('improve-status').textContent = prog.status || 'Processing...';
                document.getElementById('improve-detail').textContent = 
                    `Iteration ${prog.iteration} • Best: ${prog.best_accuracy?.toFixed(1) || '--'}%`;
                
                // Simple progress based on history length (assuming ~24 configs)
                const pct = Math.min(95, (prog.history?.length || 0) / 24 * 100);
                document.getElementById('improve-progress-bar').style.width = pct + '%';
            }
        } catch (e) {
            // Ignore polling errors
        }
    }, 1000);
    
    try {
        const resp = await fetch(`${DART_DETECT_URL}/v1/benchmark/improve`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' }
        });
        
        clearInterval(pollInterval);
        progressModal.remove();
        
        const data = await resp.json();
        
        if (!resp.ok || !data.success) {
            alert(`Improvement loop failed: ${data.error || 'Unknown error'}`);
            return;
        }
        
        // Show results
        showImprovementResults(data);
        
    } catch (err) {
        clearInterval(pollInterval);
        progressModal.remove();
        console.error('Improvement loop failed:', err);
        alert('Improvement loop failed: ' + err.message);
    } finally {
        if (btn) {
            btn.disabled = false;
            btn.textContent = '🔄 Improvement Loop';
        }
    }
}

async function stopImprovementLoop() {
    try {
        await fetch(`${DART_DETECT_URL}/v1/benchmark/improve/stop`, { method: 'POST' });
    } catch (e) {
        console.error('Failed to stop:', e);
    }
}

function showImprovementResults(data) {
    const modal = document.createElement('div');
    modal.className = 'benchmark-modal';
    modal.style.cssText = `
        position: fixed; top: 0; left: 0; right: 0; bottom: 0;
        background: rgba(0,0,0,0.9); z-index: 1000;
        display: flex; align-items: center; justify-content: center;
        padding: 20px;
    `;
    
    let resultsHtml = '';
    const sorted = [...(data.all_results || [])].sort((a, b) => (b.score || 0) - (a.score || 0)).slice(0, 10);
    
    for (const r of sorted) {
        if (r.error) continue;
        const isBest = r.config.confidence === data.best_config?.confidence_threshold;
        resultsHtml += `
            <tr style="${isBest ? 'background: #1a4d1a;' : ''}">
                <td>${r.config.confidence}</td>
                <td>${r.config.polar_threshold}</td>
                <td>${r.corrections_fixed || 0}</td>
                <td>${r.fix_rate || 0}%</td>
                <td>${r.consistency || 0}%</td>
                <td><strong>${r.score || 0}</strong></td>
            </tr>
        `;
    }
    
    modal.innerHTML = `
        <div style="background: #1a1a1a; border: 2px solid #9333ea; border-radius: 12px; 
                    max-width: 700px; width: 100%; max-height: 90vh; overflow-y: auto; padding: 20px;">
            <div style="display: flex; justify-content: space-between; align-items: center; margin-bottom: 20px;">
                <h2 style="color: #9333ea; margin: 0;">🔄 Improvement Results</h2>
                <button onclick="this.closest('.benchmark-modal').remove()" 
                        style="background: none; border: none; color: var(--paper); font-size: 1.5rem; cursor: pointer;">✕</button>
            </div>
            
            <div style="display: grid; grid-template-columns: 1fr 1fr; gap: 20px; margin-bottom: 20px;">
                <div style="background: #0a0a0a; padding: 15px; border-radius: 8px; border: 1px solid #333;">
                    <div style="font-size: 2rem; color: #9333ea; font-weight: bold;">${data.best_corrections_fixed || 0}</div>
                    <div style="color: var(--paper-muted);">Corrections Fixed</div>
                </div>
                <div style="background: #0a0a0a; padding: 15px; border-radius: 8px; border: 1px solid #333;">
                    <div style="font-size: 2rem; color: var(--gold); font-weight: bold;">${data.best_score?.toFixed(1) || 0}</div>
                    <div style="color: var(--paper-muted);">Best Score</div>
                </div>
            </div>
            
            <div style="background: #0a0a0a; padding: 15px; border-radius: 8px; border: 1px solid #333; margin-bottom: 20px;">
                <h3 style="color: var(--paper); margin: 0 0 10px 0;">🏆 Best Configuration</h3>
                <div style="color: var(--paper-muted);">
                    Confidence: <strong style="color: #9333ea;">${data.best_config?.confidence_threshold || '--'}</strong><br>
                    Polar Threshold: <strong style="color: #9333ea;">${data.best_config?.polar_threshold || '--'}</strong>
                </div>
            </div>
            
            <h3 style="color: var(--paper); margin: 0 0 10px 0;">📊 Top Configurations</h3>
            <div style="overflow-x: auto;">
                <table style="width: 100%; border-collapse: collapse; color: var(--paper); font-size: 0.9rem;">
                    <thead>
                        <tr style="background: #333; color: var(--gold);">
                            <th style="padding: 8px; text-align: left;">Conf</th>
                            <th style="padding: 8px; text-align: left;">Polar</th>
                            <th style="padding: 8px; text-align: left;">Fixed</th>
                            <th style="padding: 8px; text-align: left;">Fix%</th>
                            <th style="padding: 8px; text-align: left;">Cons%</th>
                            <th style="padding: 8px; text-align: left;">Score</th>
                        </tr>
                    </thead>
                    <tbody>${resultsHtml}</tbody>
                </table>
            </div>
            
            <div style="margin-top: 20px; text-align: center;">
                <button onclick="applyBestConfig()" class="btn btn-primary" style="background: #9333ea; padding: 12px 30px; font-size: 1.1rem;">
                    ✅ Apply Best Configuration
                </button>
            </div>
            
            <p style="color: var(--paper-muted); text-align: center; margin-top: 15px; font-size: 0.85rem;">
                Tested ${data.iterations || 0} configurations in ${data.elapsed_seconds || 0}s
            </p>
        </div>
    `;
    document.body.appendChild(modal);
}

async function applyBestConfig() {
    try {
        const resp = await fetch(`${DART_DETECT_URL}/v1/benchmark/improve/apply-best`, {
            method: 'POST'
        });
        const data = await resp.json();
        
        if (data.success) {
            alert('Best configuration applied!\n\nRestart DartDetect and run benchmark to verify.');
            document.querySelector('.benchmark-modal')?.remove();
        } else {
            alert('Failed to apply: ' + (data.error || 'Unknown error'));
        }
    } catch (err) {
        alert('Failed to apply: ' + err.message);
    }
}



// ============================================================================
// Detection Tuning Tab
// ============================================================================

let tuningAutoRefresh = false;
let tuningInterval = null;

function initTuning() {
    const slider = document.getElementById('threshold-slider');
    const valueSpan = document.getElementById('threshold-value');
    const refreshBtn = document.getElementById('refresh-tuning');
    const captureBtn = document.getElementById('capture-baseline');
    const autoBtn = document.getElementById('auto-refresh-toggle');
    const cameraSelect = document.getElementById('tuning-camera');
    
    if (!slider) return; // Tab doesn't exist
    
    slider.addEventListener('input', () => {
        valueSpan.textContent = slider.value;
    });
    
    slider.addEventListener('change', () => {
        refreshTuningPreview();
    });
    
    cameraSelect.addEventListener('change', () => {
        // Don't auto-refresh on camera change - need new baseline
        document.getElementById('tuning-status').textContent = 
            'Camera changed. Capture new baseline for this camera.';
    });
    
    captureBtn.addEventListener('click', captureBaseline);
    refreshBtn.addEventListener('click', refreshTuningPreview);
    
    autoBtn.addEventListener('click', () => {
        tuningAutoRefresh = !tuningAutoRefresh;
        autoBtn.textContent = tuningAutoRefresh ? '⏸️ Stop' : '▶️ Auto';
        autoBtn.classList.toggle('active', tuningAutoRefresh);
        
        if (tuningAutoRefresh) {
            tuningInterval = setInterval(refreshTuningPreview, 500);
        } else if (tuningInterval) {
            clearInterval(tuningInterval);
            tuningInterval = null;
        }
    });
}

async function captureBaseline() {
    const camera = document.getElementById('tuning-camera')?.value || 'cam0';
    const status = document.getElementById('tuning-status');
    const img = document.getElementById('tuning-image');
    
    status.textContent = 'Capturing baseline...';
    
    try {
        const resp = await fetch(`${DART_DETECT_URL}/v1/tuning/threshold?camera_id=${camera}&reset_baseline=true`);
        const data = await resp.json();
        
        if (data.error) {
            status.textContent = `Error: ${data.error}`;
            return;
        }
        
        if (data.image) {
            img.src = `data:image/jpeg;base64,${data.image}`;
            status.textContent = data.message || 'Baseline captured! Throw a dart and click Refresh.';
        }
    } catch (err) {
        status.textContent = `Failed: ${err.message}`;
    }
}

async function refreshTuningPreview() {
    const threshold = document.getElementById('threshold-slider')?.value || 25;
    const camera = document.getElementById('tuning-camera')?.value || 'cam0';
    const img = document.getElementById('tuning-image');
    const status = document.getElementById('tuning-status');
    const pixelCount = document.getElementById('mask-pixel-count');
    
    if (!img) return;
    
    try {
        const showRaw = document.getElementById('show-pipeline')?.checked || false;
        const resp = await fetch(`${DART_DETECT_URL}/v1/tuning/threshold?threshold=${threshold}&camera_id=${camera}&show_raw=${showRaw}`);
        const data = await resp.json();
        
        if (data.error) {
            status.textContent = `Error: ${data.error}`;
            return;
        }
        
        if (data.baseline_captured) {
            // First call - baseline was just captured
            status.textContent = data.message || 'Baseline captured!';
        } else {
            status.textContent = `Camera: ${data.camera_id} | Threshold: ${data.threshold}`;
        }
        
        if (data.image) {
            img.src = `data:image/jpeg;base64,${data.image}`;
            pixelCount.textContent = `Mask pixels: ${data.mask_pixels?.toLocaleString() || '--'}`;
        }
    } catch (err) {
        status.textContent = `Failed: ${err.message}`;
    }
}

// Call initTuning after DOM loaded
if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', initTuning);
} else {
    initTuning();
}
