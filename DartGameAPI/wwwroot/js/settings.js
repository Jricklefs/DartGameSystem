/**
 * DartsMob Settings Page
 */

const DART_DETECT_URL = 'http://192.168.0.158:8000';
const DART_GAME_URL = window.location.origin;

// Default backgrounds
const DEFAULT_BACKGROUNDS = [
    '/images/backgrounds/speakeasy-1.jpg',
    '/images/backgrounds/speakeasy-2.jpg',
    '/images/backgrounds/speakeasy-3.jpg',
    '/images/backgrounds/speakeasy-4.jpg',
    '/images/backgrounds/speakeasy-5.jpg'
];

// State
let selectedBackgrounds = [];
let customBackgrounds = [];
let calibrationData = {};
let cameraSnapshots = {};
let selectedCamera = 0;

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
});

// ============================================================================
// Tab Navigation
// ============================================================================

function initTabs() {
    const tabs = document.querySelectorAll('.nav-tab');
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
    const allBackgrounds = [...DEFAULT_BACKGROUNDS, ...customBackgrounds];
    
    gallery.innerHTML = allBackgrounds.map(bg => `
        <div class="bg-thumb ${selectedBackgrounds.includes(bg) ? 'selected' : ''}" 
             style="background-image: url('${bg}')"
             data-bg="${bg}">
            ${customBackgrounds.includes(bg) ? '<span class="custom-badge">Custom</span>' : ''}
        </div>
    `).join('');
    
    gallery.querySelectorAll('.bg-thumb').forEach(thumb => {
        thumb.addEventListener('click', () => {
            const bg = thumb.dataset.bg;
            if (selectedBackgrounds.includes(bg)) {
                selectedBackgrounds = selectedBackgrounds.filter(b => b !== bg);
                thumb.classList.remove('selected');
            } else {
                selectedBackgrounds.push(bg);
                thumb.classList.add('selected');
            }
        });
    });
}

// ============================================================================
// Calibration - New Design
// ============================================================================

async function initCalibration() {
    // Set up camera button listeners
    document.querySelectorAll('.cam-btn').forEach(btn => {
        btn.addEventListener('click', () => {
            selectCamera(parseInt(btn.dataset.cam));
        });
    });
    
    // Load calibration status for all cameras
    await loadCalibrationStatus();
    
    // Load the first camera
    selectCamera(0);
}

async function loadCalibrationStatus() {
    try {
        const res = await fetch(`${DART_DETECT_URL}/v1/calibrations`, {
            signal: AbortSignal.timeout(3000)
        });
        
        if (res.ok) {
            const calibrations = await res.json();
            calibrationData = {};
            calibrations.forEach(c => {
                calibrationData[c.camera_id] = c;
            });
            
            // Update all camera button indicators
            for (let i = 0; i < 3; i++) {
                updateCameraIndicator(i);
            }
        }
    } catch (e) {
        console.error('Failed to load calibrations:', e);
    }
}

function updateCameraIndicator(camIndex) {
    const indicator = document.getElementById(`cam-ind-${camIndex}`);
    if (!indicator) return;
    
    const cal = calibrationData[`cam${camIndex}`];
    const isOnline = cameraSnapshots[camIndex] !== undefined;
    
    indicator.classList.remove('calibrated', 'not-calibrated', 'offline');
    
    if (cal) {
        indicator.classList.add('calibrated');
        indicator.title = `Calibrated: ${Math.round(cal.quality * 100)}%`;
    } else if (isOnline) {
        indicator.classList.add('not-calibrated');
        indicator.title = 'Not calibrated';
    } else {
        indicator.classList.add('offline');
        indicator.title = 'Offline';
    }
}

async function selectCamera(camIndex) {
    selectedCamera = camIndex;
    
    // Update button states
    document.querySelectorAll('.cam-btn').forEach(btn => {
        btn.classList.toggle('active', parseInt(btn.dataset.cam) === camIndex);
    });
    
    // Show loading state
    const img = document.getElementById('main-camera-img');
    const loading = document.getElementById('main-camera-loading');
    const offline = document.getElementById('main-camera-offline');
    const qualityLabel = document.getElementById('cam-quality-label');
    
    img.classList.remove('loaded');
    loading.classList.remove('hidden');
    offline.classList.add('hidden');
    
    // Check if we have cached snapshot
    if (cameraSnapshots[camIndex]) {
        showCameraImage(camIndex);
        return;
    }
    
    // Load snapshot
    try {
        const res = await fetch(`${DART_DETECT_URL}/cameras/${camIndex}/snapshot`, {
            signal: AbortSignal.timeout(5000)
        });
        
        if (res.ok) {
            const data = await res.json();
            cameraSnapshots[camIndex] = data.image;
            showCameraImage(camIndex);
            updateCameraIndicator(camIndex);
        } else {
            throw new Error('Camera unavailable');
        }
    } catch (e) {
        console.error(`Camera ${camIndex} error:`, e);
        loading.classList.add('hidden');
        offline.classList.remove('hidden');
        qualityLabel.textContent = 'Camera Offline';
        qualityLabel.className = 'cam-quality-label failed';
    }
}

function showCameraImage(camIndex) {
    const img = document.getElementById('main-camera-img');
    const loading = document.getElementById('main-camera-loading');
    const offline = document.getElementById('main-camera-offline');
    const qualityLabel = document.getElementById('cam-quality-label');
    
    const snapshot = cameraSnapshots[camIndex];
    const cal = calibrationData[`cam${camIndex}`];
    
    if (snapshot) {
        // Check if it's an overlay image (from calibration) or raw snapshot
        const isOverlay = cal && cal.overlay_image;
        img.src = `data:image/jpeg;base64,${isOverlay ? cal.overlay_image : snapshot}`;
        img.classList.add('loaded');
        loading.classList.add('hidden');
        offline.classList.add('hidden');
    }
    
    // Update quality label
    if (cal) {
        qualityLabel.textContent = `✅ Calibrated: ${Math.round(cal.quality * 100)}%`;
        qualityLabel.className = 'cam-quality-label calibrated';
    } else {
        qualityLabel.textContent = '❌ Not Calibrated';
        qualityLabel.className = 'cam-quality-label failed';
    }
}

async function refreshCurrentCamera() {
    // Clear cached snapshot
    delete cameraSnapshots[selectedCamera];
    await selectCamera(selectedCamera);
}

async function calibrateCurrentCamera() {
    const btn = document.getElementById('calibrate-btn');
    const qualityLabel = document.getElementById('cam-quality-label');
    
    btn.disabled = true;
    btn.textContent = '⏳ Calibrating...';
    qualityLabel.textContent = 'Calibrating...';
    qualityLabel.className = 'cam-quality-label';
    
    try {
        // Get fresh snapshot
        const snapRes = await fetch(`${DART_DETECT_URL}/cameras/${selectedCamera}/snapshot`);
        if (!snapRes.ok) throw new Error('Could not get camera snapshot');
        
        const snapData = await snapRes.json();
        cameraSnapshots[selectedCamera] = snapData.image;
        
        // Send calibration request
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
            // Store calibration data
            calibrationData[`cam${selectedCamera}`] = {
                camera_id: `cam${selectedCamera}`,
                quality: camResult.quality,
                overlay_image: camResult.overlay_image,
                created_at: new Date().toISOString()
            };
            
            // Show overlay image
            if (camResult.overlay_image) {
                const img = document.getElementById('main-camera-img');
                img.src = `data:image/png;base64,${camResult.overlay_image}`;
            }
            
            qualityLabel.textContent = `✅ Calibrated: ${Math.round(camResult.quality * 100)}%`;
            qualityLabel.className = 'cam-quality-label calibrated';
            
            updateCameraIndicator(selectedCamera);
            
        } else {
            throw new Error(camResult?.error || 'Calibration failed');
        }
        
    } catch (e) {
        console.error('Calibration error:', e);
        qualityLabel.textContent = `❌ Failed: ${e.message}`;
        qualityLabel.className = 'cam-quality-label failed';
        updateCameraIndicator(selectedCamera);
    } finally {
        btn.disabled = false;
        btn.textContent = '🎯 Calibrate';
    }
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
// Event Listeners
// ============================================================================

function initEventListeners() {
    // Refresh button
    document.getElementById('refresh-btn')?.addEventListener('click', refreshCurrentCamera);
    
    // Calibrate button
    document.getElementById('calibrate-btn')?.addEventListener('click', calibrateCurrentCamera);
    
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
