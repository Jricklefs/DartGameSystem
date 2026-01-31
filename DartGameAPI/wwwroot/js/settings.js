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
let storedCalibrations = {};  // From database
let selectedCamera = 0;
let mark20Mode = false;

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
// Calibration
// ============================================================================

async function initCalibration() {
    // Set up camera button listeners
    document.querySelectorAll('.cam-btn-sm').forEach(btn => {
        btn.addEventListener('click', () => {
            selectCamera(parseInt(btn.dataset.cam));
        });
    });
    
    // Set up image click for Mark 20
    const mainImg = document.getElementById('main-camera-img');
    mainImg.addEventListener('click', handleImageClick);
    
    // Load stored calibrations from database (not live cameras)
    await loadStoredCalibrations();
    
    // Show first camera's stored calibration
    selectCamera(0);
}

async function loadStoredCalibrations() {
    try {
        const res = await fetch(`${DART_GAME_URL}/api/calibrations`, {
            signal: AbortSignal.timeout(3000)
        });
        
        if (res.ok) {
            const calibrations = await res.json();
            storedCalibrations = {};
            calibrations.forEach(c => {
                storedCalibrations[c.cameraId] = c;
            });
            
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
    
    // Update button states
    document.querySelectorAll('.cam-btn-sm').forEach(btn => {
        btn.classList.toggle('active', parseInt(btn.dataset.cam) === camIndex);
    });
    
    const img = document.getElementById('main-camera-img');
    const loading = document.getElementById('main-camera-loading');
    const offline = document.getElementById('main-camera-offline');
    const qualityLabel = document.getElementById('cam-quality-label');
    
    const stored = storedCalibrations[`cam${camIndex}`];
    
    // Show stored calibration if available (use overlayImagePath now)
    if (stored && stored.overlayImagePath) {
        img.src = stored.overlayImagePath;
        img.classList.add('loaded');
        loading.classList.add('hidden');
        offline.classList.add('hidden');
        
        qualityLabel.textContent = `✅ Stored: ${Math.round(stored.quality * 100)}%`;
        qualityLabel.className = 'cam-quality-label calibrated';
    } else {
        // No stored calibration - show placeholder
        img.classList.remove('loaded');
        loading.classList.add('hidden');
        offline.classList.remove('hidden');
        offline.querySelector('span').textContent = '📷 No calibration stored - click Calibrate';
        
        qualityLabel.textContent = '❌ Not Calibrated';
        qualityLabel.className = 'cam-quality-label failed';
    }
}

async function refreshCurrentCamera() {
    // Get live snapshot from camera (for preview before calibrating)
    const img = document.getElementById('main-camera-img');
    const loading = document.getElementById('main-camera-loading');
    const offline = document.getElementById('main-camera-offline');
    const qualityLabel = document.getElementById('cam-quality-label');
    
    img.classList.remove('loaded');
    loading.classList.remove('hidden');
    loading.querySelector('span').textContent = '📷 Loading live view...';
    offline.classList.add('hidden');
    
    try {
        const res = await fetch(`${DART_DETECT_URL}/cameras/${selectedCamera}/snapshot`, {
            signal: AbortSignal.timeout(5000)
        });
        
        if (res.ok) {
            const data = await res.json();
            img.src = `data:image/jpeg;base64,${data.image}`;
            img.classList.add('loaded');
            loading.classList.add('hidden');
            
            qualityLabel.textContent = '📷 Live Preview';
            qualityLabel.className = 'cam-quality-label';
        } else {
            throw new Error('Camera unavailable');
        }
    } catch (e) {
        console.error(`Camera ${selectedCamera} error:`, e);
        loading.classList.add('hidden');
        offline.classList.remove('hidden');
        offline.querySelector('span').textContent = '❌ Camera Offline';
        qualityLabel.textContent = 'Camera Offline';
        qualityLabel.className = 'cam-quality-label failed';
    }
}

async function calibrateCurrentCamera() {
    const btn = document.getElementById('calibrate-btn');
    const qualityLabel = document.getElementById('cam-quality-label');
    const img = document.getElementById('main-camera-img');
    const loading = document.getElementById('main-camera-loading');
    
    btn.disabled = true;
    btn.textContent = '⏳ Calibrating...';
    qualityLabel.textContent = 'Calibrating...';
    qualityLabel.className = 'cam-quality-label';
    loading.classList.remove('hidden');
    loading.querySelector('span').textContent = '🎯 Running calibration...';
    
    try {
        // Get fresh snapshot
        const snapRes = await fetch(`${DART_DETECT_URL}/cameras/${selectedCamera}/snapshot`);
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
                    calibrationData: JSON.stringify(camResult)
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
            qualityLabel.textContent = `✅ Stored: ${Math.round(camResult.quality * 100)}%`;
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
    const img = document.getElementById('main-camera-img');
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
// Event Listeners
// ============================================================================

function initEventListeners() {
    // Refresh button
    document.getElementById('refresh-btn')?.addEventListener('click', refreshCurrentCamera);
    
    // Calibrate button
    document.getElementById('calibrate-btn')?.addEventListener('click', calibrateCurrentCamera);
    
    // Mark 20 button
    document.getElementById('mark20-btn')?.addEventListener('click', toggleMark20Mode);
    
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
