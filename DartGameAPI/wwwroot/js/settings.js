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

// Dartboard segment order (clockwise starting from 20 at top)
const SEGMENT_ORDER = [20, 1, 18, 4, 13, 6, 10, 15, 2, 17, 3, 19, 7, 16, 8, 11, 14, 9, 12, 5];

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
    const mainImg = document.getElementById('main-camera-img');
    mainImg.addEventListener('click', handleImageClick);
    
    // Load stored calibrations from database (not live cameras)
    await loadStoredCalibrations();
    
    // Show first camera's stored calibration
    selectCamera(0);
}

// Update the calibration view based on mode
function updateCalibrationView() {
    const baseImg = document.getElementById('camera-base-img');
    const overlayImg = document.getElementById('main-camera-img');
    
    if (calibrationViewMode === 'combined' && lastCameraSnapshot) {
        // Show both: camera feed underneath, overlay on top
        baseImg.src = lastCameraSnapshot;
        baseImg.style.display = 'block';
        overlayImg.style.position = 'absolute';
        overlayImg.style.zIndex = '2';
    } else {
        // Overlay only - hide base image
        baseImg.style.display = 'none';
        overlayImg.style.position = 'relative';
        overlayImg.style.zIndex = '1';
    }
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
    
    const img = document.getElementById('main-camera-img');
    const baseImg = document.getElementById('camera-base-img');
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
        
        // If we have calibration image, use it as base for combined view
        if (stored.calibrationImagePath) {
            lastCameraSnapshot = stored.calibrationImagePath;
        } else {
            lastCameraSnapshot = null;
        }
        updateCalibrationView();
        
        // Show 20-angle info if Mark 20 was used
        const angleInfo = stored.twentyAngle ? ` (20 at ${Math.round(stored.twentyAngle)}°)` : '';
        
        qualityLabel.textContent = `✅ Stored: ${Math.round(stored.quality * 100)}%${angleInfo}`;
        qualityLabel.className = 'cam-quality-label calibrated';
    } else {
        // No stored calibration - show placeholder
        img.classList.remove('loaded');
        baseImg.style.display = 'none';
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
    const img = document.getElementById('main-camera-img');
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
    const img = document.getElementById('main-camera-img');
    
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
    const img = document.getElementById('main-camera-img');
    
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
// Event Listeners
// ============================================================================

function initEventListeners() {
    // Refresh button
    document.getElementById('refresh-btn')?.addEventListener('click', refreshCurrentCamera);
    
    // Focus button
    document.getElementById('focus-btn')?.addEventListener('click', toggleFocusMode);
    
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

function initAccuracy() {
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
