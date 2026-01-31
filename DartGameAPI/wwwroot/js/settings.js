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

// ============================================================================
// Initialization
// ============================================================================

document.addEventListener('DOMContentLoaded', () => {
    initTabs();
    initBackground();
    loadThemeSettings();
    loadCalibrationStatus();
    loadCameraSnapshots();
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
            
            // Update tabs
            tabs.forEach(t => t.classList.remove('active'));
            tab.classList.add('active');
            
            // Update sections
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
    // Load saved theme
    const theme = JSON.parse(localStorage.getItem('dartsmob-theme') || '{}');
    selectedBackgrounds = theme.backgrounds || [...DEFAULT_BACKGROUNDS];
    customBackgrounds = theme.customBackgrounds || [];
    
    // Set initial background
    if (selectedBackgrounds.length > 0) {
        document.getElementById('background-layer').style.backgroundImage = 
            `url('${selectedBackgrounds[0]}')`;
    }
    
    // Set overlay opacity
    const opacity = theme.overlayOpacity ?? 70;
    document.getElementById('background-overlay').style.background = 
        `rgba(0, 0, 0, ${opacity / 100})`;
}

function loadThemeSettings() {
    const theme = JSON.parse(localStorage.getItem('dartsmob-theme') || '{}');
    
    // Branding
    document.getElementById('theme-app-name').value = theme.appName || 'DartsMob';
    document.getElementById('theme-tagline').value = theme.tagline || "The Family's Game";
    
    // Backgrounds
    selectedBackgrounds = theme.backgrounds || [...DEFAULT_BACKGROUNDS];
    customBackgrounds = theme.customBackgrounds || [];
    renderBackgroundGallery();
    
    // Slideshow
    document.getElementById('slideshow-toggle').checked = theme.slideshow !== false;
    document.getElementById('slideshow-speed').value = theme.slideshowSpeed || 30000;
    
    // Overlay
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
    
    // Add click handlers
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

async function loadCalibrationStatus() {
    const apiStatus = document.getElementById('detect-api-status');
    const calStatus = document.getElementById('cal-status');
    const calDate = document.getElementById('cal-date');
    
    try {
        // Check DartDetect API
        const healthRes = await fetch(`${DART_DETECT_URL}/health`, { 
            mode: 'cors',
            signal: AbortSignal.timeout(3000)
        });
        
        if (healthRes.ok) {
            const health = await healthRes.json();
            apiStatus.textContent = '🟢 Online';
            apiStatus.className = 'status-value status-online';
            
            // Check calibrations
            const calRes = await fetch(`${DART_DETECT_URL}/v1/calibrations`);
            const calibrations = await calRes.json();
            calibrationData = {};
            calibrations.forEach(c => calibrationData[c.camera_id] = c);
            
            if (calibrations.length > 0) {
                calStatus.textContent = `✅ ${calibrations.length} camera(s)`;
                calStatus.className = 'status-value status-online';
                
                const latest = calibrations.reduce((a, b) => 
                    new Date(a.created_at) > new Date(b.created_at) ? a : b
                );
                calDate.textContent = new Date(latest.created_at).toLocaleString();
            } else {
                calStatus.textContent = '❌ Not calibrated';
                calStatus.className = 'status-value status-offline';
                calDate.textContent = '—';
            }
        } else {
            throw new Error('API not responding');
        }
    } catch (e) {
        apiStatus.textContent = '🔴 Offline';
        apiStatus.className = 'status-value status-offline';
        calStatus.textContent = '⚠️ API unavailable';
        calStatus.className = 'status-value status-warning';
    }
}

async function loadCameraSnapshots() {
    for (let i = 0; i < 3; i++) {
        const img = document.getElementById(`cal-cam${i}-img`);
        const status = document.getElementById(`cal-cam${i}-status`);
        const quality = document.getElementById(`cal-cam${i}-quality`);
        const card = document.querySelector(`.camera-card[data-cam="${i}"]`);
        
        try {
            const res = await fetch(`${DART_DETECT_URL}/cameras/${i}/snapshot`);
            if (res.ok) {
                const data = await res.json();
                img.src = `data:image/jpeg;base64,${data.image}`;
                img.classList.add('loaded');
                status.textContent = '🟢';
                
                // Check if calibrated
                const cal = calibrationData[`cam${i}`];
                if (cal) {
                    card.classList.add('calibrated');
                    quality.textContent = `✅ ${Math.round(cal.quality * 100)}%`;
                    quality.className = 'camera-quality calibrated';
                } else {
                    quality.textContent = 'Not calibrated';
                    quality.className = 'camera-quality';
                }
            } else {
                throw new Error('Camera unavailable');
            }
        } catch (e) {
            status.textContent = '🔴';
            card.classList.add('offline');
            quality.textContent = 'Offline';
            quality.className = 'camera-quality offline';
        }
    }
}

async function calibrateAllCameras() {
    const btn = document.getElementById('calibrate-all-btn');
    btn.disabled = true;
    btn.textContent = '⏳ Calibrating...';
    
    try {
        // Get snapshots for all available cameras
        const cameras = [];
        for (let i = 0; i < 3; i++) {
            try {
                const res = await fetch(`${DART_DETECT_URL}/cameras/${i}/snapshot`);
                if (res.ok) {
                    const data = await res.json();
                    cameras.push({ camera_id: `cam${i}`, image: data.image });
                }
            } catch (e) {
                console.log(`Camera ${i} not available`);
            }
        }
        
        if (cameras.length === 0) {
            alert('No cameras available for calibration');
            return;
        }
        
        // Send calibration request
        const res = await fetch(`${DART_DETECT_URL}/v1/calibrate`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ cameras })
        });
        
        const result = await res.json();
        
        // Count successes
        const successes = result.results.filter(r => r.success).length;
        const failures = result.results.filter(r => !r.success).length;
        
        // Update UI
        result.results.forEach(r => {
            const camNum = r.camera_id.replace('cam', '');
            const card = document.querySelector(`.camera-card[data-cam="${camNum}"]`);
            const quality = document.getElementById(`cal-cam${camNum}-quality`);
            const img = document.getElementById(`cal-cam${camNum}-img`);
            
            if (r.success) {
                card.classList.remove('failed');
                card.classList.add('calibrated');
                quality.textContent = `✅ ${Math.round(r.quality * 100)}%`;
                quality.className = 'camera-quality calibrated';
                if (r.overlay_image) {
                    img.src = `data:image/png;base64,${r.overlay_image}`;
                }
            } else {
                card.classList.remove('calibrated');
                card.classList.add('failed');
                quality.textContent = `❌ Failed`;
                quality.className = 'camera-quality failed';
            }
        });
        
        // Show result
        if (failures === 0) {
            alert(`✅ Calibration complete!\n${successes} camera(s) calibrated successfully.`);
        } else {
            alert(`⚠️ Calibration finished with issues.\n✅ ${successes} succeeded\n❌ ${failures} failed`);
        }
        
        // Refresh status
        await loadCalibrationStatus();
        
    } catch (e) {
        alert(`Calibration error: ${e.message}`);
    } finally {
        btn.disabled = false;
        btn.textContent = '🎯 Calibrate All Cameras';
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
    
    // Database (check via API)
    try {
        const res = await fetch(`${DART_GAME_URL}/api/players`);
        setStatus('sys-database', res.ok);
    } catch (e) {
        setStatus('sys-database', false);
    }
    
    // SignalR - just show as connected if we're on the page
    setStatus('sys-signalr', true, 'Available');
}

function setStatus(id, online, text) {
    const el = document.getElementById(id);
    el.textContent = text || (online ? '🟢 Online' : '🔴 Offline');
    el.className = `status-value ${online ? 'status-online' : 'status-offline'}`;
}

// ============================================================================
// Event Listeners
// ============================================================================

function initEventListeners() {
    // Refresh snapshots
    document.getElementById('refresh-snapshots-btn').addEventListener('click', () => {
        loadCameraSnapshots();
    });
    
    // Calibrate
    document.getElementById('calibrate-all-btn').addEventListener('click', () => {
        calibrateAllCameras();
    });
    
    // Overlay opacity
    document.getElementById('overlay-opacity').addEventListener('input', (e) => {
        const value = e.target.value;
        document.getElementById('opacity-value').textContent = `${value}%`;
        document.getElementById('background-overlay').style.background = 
            `rgba(0, 0, 0, ${value / 100})`;
    });
    
    // Save theme
    document.getElementById('save-theme-btn').addEventListener('click', saveTheme);
    
    // Reset theme
    document.getElementById('reset-theme-btn').addEventListener('click', resetTheme);
    
    // Upload background
    document.getElementById('bg-upload').addEventListener('change', handleBackgroundUpload);
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
