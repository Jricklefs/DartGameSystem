/**
 * DartsMob - Client JavaScript
 * Real-time dart game UI with SignalR
 */

// ==========================================================================
// State
// ==========================================================================

let connection = null;
let currentGame = null;
let selectedMode = 'Practice';
let selectedBestOf = 5;
const boardId = 'default';

// ==========================================================================
// SignalR Connection
// ==========================================================================

async function initConnection() {
    connection = new signalR.HubConnectionBuilder()
        .withUrl('/gamehub')
        .withAutomaticReconnect()
        .configureLogging(signalR.LogLevel.Information)
        .build();

    // Connection events
    connection.onreconnecting(() => {
        updateConnectionStatus('Reconnecting...', 'disconnected');
    });

    connection.onreconnected(() => {
        updateConnectionStatus('Connected', 'connected');
        connection.invoke('JoinBoard', boardId);
    });

    connection.onclose(() => {
        updateConnectionStatus('Disconnected', 'disconnected');
    });

    // Game events
    connection.on('DartThrown', handleDartThrown);
    connection.on('BoardCleared', handleBoardCleared);
    connection.on('GameStarted', handleGameStarted);
    connection.on('GameEnded', handleGameEnded);
    connection.on('TurnEnded', handleTurnEnded);

    try {
        await connection.start();
        updateConnectionStatus('Connected', 'connected');
        await connection.invoke('JoinBoard', boardId);
    } catch (err) {
        console.error('SignalR connection error:', err);
        updateConnectionStatus('Connection Failed', 'disconnected');
    }
}

function updateConnectionStatus(text, className) {
    const el = document.getElementById('connection-status');
    el.textContent = text;
    el.className = className;
}

// ==========================================================================
// Event Handlers
// ==========================================================================

function handleDartThrown(data) {
    console.log('Dart thrown:', data);
    currentGame = data.game;
    
    // Update UI
    updateScoreboard();
    updateCurrentTurn();
    showThrowCallout(data.dart);
}

function handleBoardCleared(data) {
    console.log('Board cleared:', data);
    // Could add visual feedback
}

function handleGameStarted(data) {
    console.log('Game started:', data);
    currentGame = data;
    showPanel('game-panel');
    updateScoreboard();
    updateCurrentTurn();
}

function handleGameEnded(data) {
    console.log('Game ended:', data);
    document.getElementById('winner-name').textContent = data.winnerName || 'Unknown';
    document.getElementById('winner-darts').textContent = `Darts thrown by each player`;
    showPanel('gameover-panel');
}

function handleTurnEnded(data) {
    console.log('Turn ended:', data);
    // Clear current turn display
    clearCurrentTurn();
}

// ==========================================================================
// UI Updates
// ==========================================================================

function updateScoreboard() {
    if (!currentGame) return;
    
    const scoreboard = document.getElementById('scoreboard');
    scoreboard.innerHTML = currentGame.players.map((player, index) => `
        <div class="player-card ${index === currentGame.currentPlayerIndex ? 'active' : ''}">
            <div class="name">${escapeHtml(player.name)}</div>
            <div class="score">${player.score}</div>
            <div class="player-stats">
                <span class="darts-thrown">${player.dartsThrown} darts</span>
                ${currentGame.legsToWin > 1 ? `<span class="legs-won">Legs: ${player.legsWon}</span>` : ''}
            </div>
        </div>
    `).join('');
    
    document.getElementById('game-mode-display').textContent = formatMode(currentGame.mode);
    
    // Update legs display
    const legsDisplay = document.getElementById('legs-display');
    if (legsDisplay && currentGame.legsToWin > 1) {
        legsDisplay.textContent = `Leg ${currentGame.currentLeg} • Best of ${currentGame.totalLegs}`;
        legsDisplay.style.display = 'inline';
    } else if (legsDisplay) {
        legsDisplay.style.display = 'none';
    }
}

function updateCurrentTurn() {
    if (!currentGame?.currentTurn) {
        clearCurrentTurn();
        return;
    }
    
    const darts = currentGame.currentTurn.darts || [];
    const slots = document.querySelectorAll('.dart-slot');
    
    slots.forEach((slot, i) => {
        if (darts[i]) {
            slot.classList.remove('empty');
            slot.classList.add('hit');
            slot.innerHTML = `
                ${darts[i].score}
                <span class="zone">${darts[i].zone}</span>
            `;
        } else {
            slot.classList.add('empty');
            slot.classList.remove('hit');
            slot.innerHTML = '—';
        }
    });
    
    document.getElementById('turn-score').textContent = currentGame.currentTurn.turnScore || 0;
}

function clearCurrentTurn() {
    const slots = document.querySelectorAll('.dart-slot');
    slots.forEach(slot => {
        slot.classList.add('empty');
        slot.classList.remove('hit');
        slot.innerHTML = '—';
    });
    document.getElementById('turn-score').textContent = '0';
}

function showThrowCallout(dart) {
    const callout = document.getElementById('last-throw');
    callout.querySelector('.throw-zone').textContent = dart.zone.toUpperCase();
    callout.querySelector('.throw-score').textContent = dart.score;
    
    callout.classList.remove('hidden', 'show');
    void callout.offsetWidth; // Force reflow
    callout.classList.add('show');
    
    setTimeout(() => {
        callout.classList.add('hidden');
        callout.classList.remove('show');
    }, 1500);
}

function showPanel(panelId) {
    document.querySelectorAll('.panel').forEach(p => p.classList.add('hidden'));
    document.getElementById(panelId).classList.remove('hidden');
}

// ==========================================================================
// API Calls
// ==========================================================================

async function startGame() {
    const inputs = document.querySelectorAll('.player-input');
    const players = Array.from(inputs)
        .map(i => i.value.trim())
        .filter(name => name.length > 0);
    
    if (players.length === 0) {
        players.push('Player 1');
    }
    
    try {
        const response = await fetch('/api/games', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                boardId: boardId,
                mode: selectedMode,
                playerNames: players,
                bestOf: selectedBestOf
            })
        });
        
        if (response.ok) {
            currentGame = await response.json();
            showPanel('game-panel');
            updateScoreboard();
            updateCurrentTurn();
        } else {
            console.error('Failed to start game:', await response.text());
        }
    } catch (err) {
        console.error('Error starting game:', err);
    }
}

async function endGame() {
    if (!currentGame) return;
    
    try {
        await fetch(`/api/games/${currentGame.id}/end`, { method: 'POST' });
        currentGame = null;
        showPanel('setup-panel');
    } catch (err) {
        console.error('Error ending game:', err);
    }
}

// ==========================================================================
// Dartboard Drawing
// ==========================================================================

function drawDartboard() {
    const svg = document.getElementById('dartboard');
    const cx = 200, cy = 200;
    
    // Segment order around the board (clockwise from top)
    const segments = [20, 1, 18, 4, 13, 6, 10, 15, 2, 17, 3, 19, 7, 16, 8, 11, 14, 9, 12, 5];
    
    // Radii
    const rOuter = 180;      // Outer double
    const rDouble = 170;     // Inner double
    const rTripleOuter = 115; // Outer triple
    const rTripleInner = 105; // Inner triple  
    const rBullOuter = 35;    // Outer bull
    const rBullInner = 15;    // Inner bull (bullseye)
    
    let html = '';
    
    // Background circle
    html += `<circle cx="${cx}" cy="${cy}" r="${rOuter}" fill="#1a1510" stroke="#d4a84b" stroke-width="2"/>`;
    
    // Draw segments
    for (let i = 0; i < 20; i++) {
        const angle1 = (i * 18 - 99) * Math.PI / 180;  // -99 to start at 20
        const angle2 = ((i + 1) * 18 - 99) * Math.PI / 180;
        
        const isEven = i % 2 === 0;
        const singleColor = isEven ? '#1a1510' : '#f5e6c8';
        const multiColor = isEven ? '#1a4d2e' : '#8b1a1a';
        
        // Single outer (between double and triple)
        html += drawSegment(cx, cy, rDouble, rTripleOuter, angle1, angle2, singleColor);
        
        // Triple
        html += drawSegment(cx, cy, rTripleOuter, rTripleInner, angle1, angle2, multiColor);
        
        // Single inner (between triple and bull)
        html += drawSegment(cx, cy, rTripleInner, rBullOuter, angle1, angle2, singleColor);
        
        // Double (outer ring)
        html += drawSegment(cx, cy, rOuter, rDouble, angle1, angle2, multiColor);
    }
    
    // Bull rings
    html += `<circle cx="${cx}" cy="${cy}" r="${rBullOuter}" fill="#1a4d2e" stroke="#d4a84b" stroke-width="1"/>`;
    html += `<circle cx="${cx}" cy="${cy}" r="${rBullInner}" fill="#8b1a1a" stroke="#d4a84b" stroke-width="1"/>`;
    
    // Wire effect (segment lines)
    for (let i = 0; i < 20; i++) {
        const angle = (i * 18 - 99) * Math.PI / 180;
        const x1 = cx + rBullOuter * Math.cos(angle);
        const y1 = cy + rBullOuter * Math.sin(angle);
        const x2 = cx + rOuter * Math.cos(angle);
        const y2 = cy + rOuter * Math.sin(angle);
        html += `<line x1="${x1}" y1="${y1}" x2="${x2}" y2="${y2}" stroke="#d4a84b" stroke-width="1" opacity="0.5"/>`;
    }
    
    // Ring wires
    [rDouble, rTripleOuter, rTripleInner].forEach(r => {
        html += `<circle cx="${cx}" cy="${cy}" r="${r}" fill="none" stroke="#d4a84b" stroke-width="1" opacity="0.5"/>`;
    });
    
    svg.innerHTML = html;
}

function drawSegment(cx, cy, r1, r2, angle1, angle2, fill) {
    const x1 = cx + r1 * Math.cos(angle1);
    const y1 = cy + r1 * Math.sin(angle1);
    const x2 = cx + r1 * Math.cos(angle2);
    const y2 = cy + r1 * Math.sin(angle2);
    const x3 = cx + r2 * Math.cos(angle2);
    const y3 = cy + r2 * Math.sin(angle2);
    const x4 = cx + r2 * Math.cos(angle1);
    const y4 = cy + r2 * Math.sin(angle1);
    
    return `<path d="M${x1},${y1} A${r1},${r1} 0 0,1 ${x2},${y2} L${x3},${y3} A${r2},${r2} 0 0,0 ${x4},${y4} Z" fill="${fill}"/>`;
}

// ==========================================================================
// Helpers
// ==========================================================================

function escapeHtml(text) {
    const div = document.createElement('div');
    div.textContent = text;
    return div.innerHTML;
}

function formatMode(mode) {
    switch (mode) {
        case 'Practice': return 'PRACTICE';
        case 'Game501': return '501';
        case 'Game301': return '301';
        case 'Cricket': return 'CRICKET';
        default: return mode;
    }
}

// ==========================================================================
// Event Listeners
// ==========================================================================

document.addEventListener('DOMContentLoaded', () => {
    // Draw dartboard
    drawDartboard();
    
    // Connect to SignalR
    initConnection();
    
    // Game mode buttons
    document.querySelectorAll('.game-mode-btn').forEach(btn => {
        btn.addEventListener('click', () => {
            document.querySelectorAll('.game-mode-btn').forEach(b => b.classList.remove('active'));
            btn.classList.add('active');
            selectedMode = btn.dataset.mode;
            
            // Show/hide legs selector (hide for Practice)
            const legsGroup = document.getElementById('legs-group');
            if (legsGroup) {
                legsGroup.style.display = selectedMode === 'Practice' ? 'none' : 'block';
            }
        });
    });
    
    // Legs (best of) buttons
    document.querySelectorAll('.legs-btn').forEach(btn => {
        btn.addEventListener('click', () => {
            document.querySelectorAll('.legs-btn').forEach(b => b.classList.remove('active'));
            btn.classList.add('active');
            selectedBestOf = parseInt(btn.dataset.legs);
        });
    });
    
    // Add player button
    document.getElementById('add-player-btn').addEventListener('click', () => {
        const list = document.getElementById('players-list');
        const count = list.querySelectorAll('.player-input').length + 1;
        const input = document.createElement('input');
        input.type = 'text';
        input.className = 'player-input';
        input.placeholder = `Player ${count}`;
        list.appendChild(input);
    });
    
    // Start game
    document.getElementById('start-game-btn').addEventListener('click', startGame);
    
    // End game
    document.getElementById('end-game-btn').addEventListener('click', endGame);
    
    // New game
    document.getElementById('new-game-btn').addEventListener('click', () => {
        showPanel('setup-panel');
    });
});

// ==========================================================================
// Settings Modal
// ==========================================================================

const DETECT_API = 'http://192.168.0.158:8000';

function initSettings() {
    const settingsBtn = document.getElementById('settings-btn');
    const modal = document.getElementById('settings-modal');
    const closeBtn = document.getElementById('close-settings');
    const tabBtns = document.querySelectorAll('.tab-btn');
    const camCountBtns = document.querySelectorAll('.cam-count-btn');
    
    // Open settings
    settingsBtn?.addEventListener('click', () => {
        modal.classList.remove('hidden');
        checkSystemStatus();
        loadCameraStatus();
        loadCalibrationStatus();
    });
    
    // Close settings
    closeBtn?.addEventListener('click', () => {
        modal.classList.add('hidden');
    });
    
    // Close on backdrop click
    modal?.addEventListener('click', (e) => {
        if (e.target === modal) {
            modal.classList.add('hidden');
        }
    });
    
    // Tab switching
    tabBtns.forEach(btn => {
        btn.addEventListener('click', () => {
            const tabId = btn.dataset.tab;
            
            // Update buttons
            tabBtns.forEach(b => b.classList.remove('active'));
            btn.classList.add('active');
            
            // Update content
            document.querySelectorAll('.tab-content').forEach(tc => tc.classList.add('hidden'));
            document.getElementById(`tab-${tabId}`).classList.remove('hidden');
            
            // Auto-load snapshots when calibration tab is opened
            if (tabId === 'calibration') {
                loadCalibrationSnapshots();
            }
        });
    });
    
    // Camera count selector
    camCountBtns.forEach(btn => {
        btn.addEventListener('click', () => {
            const count = parseInt(btn.dataset.count);
            camCountBtns.forEach(b => b.classList.remove('active'));
            btn.classList.add('active');
            updateCameraCount(count);
        });
    });
    
    // Refresh cameras button
    document.getElementById('refresh-cameras')?.addEventListener('click', loadCameraStatus);
    
    // Refresh snapshots button
    document.getElementById('refresh-snapshots-btn')?.addEventListener('click', loadCalibrationSnapshots);
    
    // Calibrate all cameras button
    document.getElementById('calibrate-all-btn')?.addEventListener('click', calibrateAllCameras);
}

async function loadCameraStatus() {
    const previews = document.querySelectorAll('.camera-preview');
    
    try {
        const response = await fetch(`${DETECT_API}/cameras`);
        if (response.ok) {
            const data = await response.json();
            
            for (let i = 0; i < 3; i++) {
                const statusEl = document.getElementById(`cam${i}-status`);
                const cam = data.cameras?.find(c => c.index === i);
                
                if (cam && cam.connected) {
                    statusEl.textContent = '● Online';
                    statusEl.className = 'cam-status online';
                } else {
                    statusEl.textContent = '○ Offline';
                    statusEl.className = 'cam-status offline';
                }
            }
        }
    } catch (e) {
        console.error('Failed to load camera status:', e);
        for (let i = 0; i < 3; i++) {
            const statusEl = document.getElementById(`cam${i}-status`);
            statusEl.textContent = '? Unknown';
            statusEl.className = 'cam-status';
        }
    }
}

function updateCameraCount(count) {
    const previews = document.querySelectorAll('.camera-preview');
    previews.forEach((preview, index) => {
        preview.style.display = index < count ? 'block' : 'none';
    });
    
    // TODO: Save camera count preference to API
    console.log(`Camera count set to: ${count}`);
}

// Store snapshots for calibration
let cameraSnapshots = {};

async function loadCalibrationSnapshots() {
    // First check API and load status
    await loadCalibrationStatus();
    
    // Get list of connected cameras
    try {
        const response = await fetch(`${DETECT_API}/cameras`);
        if (!response.ok) return;
        
        const data = await response.json();
        const cameras = data.cameras || [];
        
        // Load snapshot for each camera
        for (const cam of cameras) {
            const camIndex = cam.index;
            const imgEl = document.getElementById(`cal-cam${camIndex}-img`);
            const statusEl = document.getElementById(`cal-cam${camIndex}-status`);
            const card = document.querySelector(`.cal-camera-card[data-cam="${camIndex}"]`);
            
            if (!imgEl || !statusEl) continue;
            
            statusEl.textContent = '⏳';
            
            try {
                // Get snapshot as base64
                const snapResponse = await fetch(`${DETECT_API}/cameras/${camIndex}/snapshot`);
                if (snapResponse.ok) {
                    const snapData = await snapResponse.json();
                    cameraSnapshots[camIndex] = snapData.image;
                    
                    // Display image
                    imgEl.src = `data:image/jpeg;base64,${snapData.image}`;
                    imgEl.classList.add('loaded');
                    statusEl.textContent = '✅';
                } else {
                    statusEl.textContent = '❌';
                    card?.classList.add('failed');
                }
            } catch (e) {
                console.error(`Failed to load snapshot for camera ${camIndex}:`, e);
                statusEl.textContent = '❌';
            }
        }
    } catch (e) {
        console.error('Failed to load cameras:', e);
    }
}

async function calibrateAllCameras() {
    const btn = document.getElementById('calibrate-all-btn');
    btn.textContent = '⏳ Calibrating...';
    btn.disabled = true;
    
    const cameraIndices = Object.keys(cameraSnapshots);
    if (cameraIndices.length === 0) {
        alert('No camera snapshots loaded. Click "Refresh Snapshots" first.');
        btn.textContent = '🎯 Calibrate All Cameras';
        btn.disabled = false;
        return;
    }
    
    try {
        // Build calibration request with all camera images
        const cameras = cameraIndices.map(idx => ({
            camera_id: `cam${idx}`,
            image: cameraSnapshots[idx]
        }));
        
        const response = await fetch(`${DETECT_API}/v1/calibrate`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ cameras })
        });
        
        if (!response.ok) {
            throw new Error('Calibration request failed');
        }
        
        const result = await response.json();
        let successCount = 0;
        let failCount = 0;
        
        // Update each camera card with result
        for (const camResult of result.results || []) {
            const camIndex = camResult.camera_id.replace('cam', '');
            const card = document.querySelector(`.cal-camera-card[data-cam="${camIndex}"]`);
            const statusEl = document.getElementById(`cal-cam${camIndex}-status`);
            const imgEl = document.getElementById(`cal-cam${camIndex}-img`);
            
            if (camResult.success) {
                successCount++;
                card?.classList.remove('failed');
                card?.classList.add('calibrated');
                statusEl.textContent = `✅ ${Math.round(camResult.quality * 100)}%`;
                
                // Show overlay image if returned
                if (camResult.overlay_image) {
                    imgEl.src = `data:image/jpeg;base64,${camResult.overlay_image}`;
                }
            } else {
                failCount++;
                card?.classList.remove('calibrated');
                card?.classList.add('failed');
                statusEl.textContent = '❌ Failed';
            }
        }
        
        // Update overall status
        await loadCalibrationStatus();
        
        // Show summary
        if (failCount === 0) {
            alert(`✅ Calibration complete!\n\n${successCount} camera(s) calibrated successfully.`);
        } else {
            alert(`⚠️ Calibration finished with issues.\n\n✅ ${successCount} succeeded\n❌ ${failCount} failed`);
        }
        
    } catch (e) {
        console.error('Calibration error:', e);
        alert(`❌ Calibration error:\n${e.message}`);
    } finally {
        btn.textContent = '🎯 Calibrate All Cameras';
        btn.disabled = false;
    }
}

async function loadCalibrationStatus() {
    const statusEl = document.getElementById('cal-status');
    const dateEl = document.getElementById('cal-date');
    
    // Check DartDetect API health first
    let detectApiHealthy = false;
    try {
        const healthResponse = await fetch(`${DETECT_API}/health`);
        if (healthResponse.ok) {
            const health = await healthResponse.json();
            detectApiHealthy = health.status === 'healthy';
            
            // Update API indicator if we have one
            const apiIndicator = document.getElementById('detect-api-status');
            if (apiIndicator) {
                apiIndicator.textContent = detectApiHealthy ? '🟢 Online' : '🔴 Offline';
                apiIndicator.className = detectApiHealthy ? 'status-online' : 'status-offline';
            }
        }
    } catch (e) {
        console.log('DartDetect API not reachable');
    }
    
    if (!detectApiHealthy) {
        statusEl.textContent = '⚠️ Detect API Offline';
        statusEl.className = 'cal-badge api-offline';
        dateEl.textContent = 'Start DartDetect API first';
        return;
    }
    
    // Check for saved calibrations in DartDetect API
    try {
        const calResponse = await fetch(`${DETECT_API}/v1/calibrations`, {
            headers: { 'Authorization': 'Bearer local' }
        });
        
        if (calResponse.ok) {
            const calibrations = await calResponse.json();
            
            if (calibrations && calibrations.length > 0) {
                // Find most recent calibration
                const latest = calibrations.sort((a, b) => 
                    new Date(b.created_at) - new Date(a.created_at)
                )[0];
                
                statusEl.textContent = `✅ Calibrated (${calibrations.length} camera${calibrations.length > 1 ? 's' : ''})`;
                statusEl.className = 'cal-badge calibrated';
                dateEl.textContent = latest.created_at 
                    ? new Date(latest.created_at).toLocaleString() 
                    : 'Recently';
                    
                // Show quality if available
                if (latest.quality) {
                    dateEl.textContent += ` • Quality: ${(latest.quality * 100).toFixed(0)}%`;
                }
            } else {
                statusEl.textContent = '❌ Not Calibrated';
                statusEl.className = 'cal-badge not-calibrated';
                dateEl.textContent = 'Click "Start Calibration" below';
            }
        }
    } catch (e) {
        console.error('Failed to load calibration status:', e);
        // Fallback to Game API
        try {
            const response = await fetch('/api/games/boards');
            if (response.ok) {
                const boards = await response.json();
                const board = boards[0];
                
                if (board?.isCalibrated) {
                    statusEl.textContent = '✅ Calibrated';
                    statusEl.className = 'cal-badge calibrated';
                    dateEl.textContent = board.lastCalibration 
                        ? new Date(board.lastCalibration).toLocaleString() 
                        : 'Unknown';
                } else {
                    statusEl.textContent = '❌ Not Calibrated';
                    statusEl.className = 'cal-badge not-calibrated';
                    dateEl.textContent = 'Click "Start Calibration" below';
                }
            }
        } catch (e2) {
            console.error('Failed to load board status:', e2);
        }
    }
}

async function showCalibrationOverlay() {
    const cameraSelect = document.getElementById('cal-camera-select');
    const camIndex = cameraSelect.value;
    const container = document.querySelector('.overlay-container');
    const noFeedMsg = document.querySelector('.no-feed-msg');
    const feedImg = document.getElementById('cal-camera-feed');
    const overlay = document.getElementById('cal-overlay');
    
    try {
        // Get camera frame with calibration overlay
        const response = await fetch(`${DETECT_API}/calibration/preview?camera=${camIndex}`);
        if (response.ok) {
            const blob = await response.blob();
            feedImg.src = URL.createObjectURL(blob);
            container.classList.add('active');
            noFeedMsg.style.display = 'none';
            
            // Draw calibration rings on overlay
            drawCalibrationRings(overlay);
        } else {
            // Fallback: try to get just the camera frame
            const frameResponse = await fetch(`${DETECT_API}/cameras/${camIndex}/frame`);
            if (frameResponse.ok) {
                const blob = await frameResponse.blob();
                feedImg.src = URL.createObjectURL(blob);
                container.classList.add('active');
                noFeedMsg.style.display = 'none';
                drawCalibrationRings(overlay);
            } else {
                throw new Error('Could not get camera frame');
            }
        }
    } catch (e) {
        console.error('Failed to show overlay:', e);
        alert('Could not connect to DartDetect API. Make sure it is running on port 8000.');
    }
}

function drawCalibrationRings(svg) {
    // Clear existing
    svg.innerHTML = '';
    
    // These would come from calibration data - using placeholder positions
    const cx = 320, cy = 240;  // Center of 640x480 frame
    const scale = 1.5;
    
    // Dartboard ring radii (in mm, scaled for display)
    const rings = [
        { r: 6.35 * scale, color: '#ff4444', name: 'Double Bull' },
        { r: 15.9 * scale, color: '#ffe66d', name: 'Bull' },
        { r: 99 * scale, color: '#4ecdc4', name: 'Triple Inner' },
        { r: 107 * scale, color: '#ff6b6b', name: 'Triple Outer' },
        { r: 162 * scale, color: '#4ecdc4', name: 'Double Inner' },
        { r: 170 * scale, color: '#4ecdc4', name: 'Double Outer' },
    ];
    
    rings.forEach(ring => {
        const circle = document.createElementNS('http://www.w3.org/2000/svg', 'circle');
        circle.setAttribute('cx', cx);
        circle.setAttribute('cy', cy);
        circle.setAttribute('r', ring.r);
        circle.setAttribute('fill', 'none');
        circle.setAttribute('stroke', ring.color);
        circle.setAttribute('stroke-width', '2');
        circle.setAttribute('opacity', '0.7');
        svg.appendChild(circle);
    });
    
    // Draw segment lines
    for (let i = 0; i < 20; i++) {
        const angle = (i * 18 - 9) * Math.PI / 180;  // 18 degrees per segment, offset by 9
        const line = document.createElementNS('http://www.w3.org/2000/svg', 'line');
        line.setAttribute('x1', cx);
        line.setAttribute('y1', cy);
        line.setAttribute('x2', cx + Math.cos(angle) * 170 * scale);
        line.setAttribute('y2', cy + Math.sin(angle) * 170 * scale);
        line.setAttribute('stroke', '#888');
        line.setAttribute('stroke-width', '1');
        line.setAttribute('opacity', '0.5');
        svg.appendChild(line);
    }
}

async function startCalibration() {
    const camSelect = document.getElementById('cal-camera-select');
    const camIndex = camSelect?.value || '0';
    const btn = document.getElementById('start-calibration-btn');
    
    btn.textContent = '⏳ Calibrating...';
    btn.disabled = true;
    
    try {
        // First get the camera frame
        const frameResponse = await fetch(`${DETECT_API}/cameras/${camIndex}/frame`);
        if (!frameResponse.ok) {
            throw new Error('Could not get camera frame');
        }
        
        // Convert to base64
        const blob = await frameResponse.blob();
        const base64 = await blobToBase64(blob);
        
        // Send to calibration endpoint
        const calibrateResponse = await fetch(`${DETECT_API}/v1/calibrate`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                cameras: [{
                    camera_id: `cam${camIndex}`,
                    image: base64.split(',')[1]  // Remove data:image/jpeg;base64, prefix
                }]
            })
        });
        
        if (!calibrateResponse.ok) {
            throw new Error('Calibration request failed');
        }
        
        const result = await calibrateResponse.json();
        const camResult = result.results?.[0];
        
        if (camResult?.success) {
            alert(`✅ Calibration successful!\n\nQuality: ${(camResult.quality * 100).toFixed(0)}%\nTop segment: ${camResult.segment_at_top}`);
            
            // Show the overlay image if returned
            if (camResult.overlay_image) {
                const feedImg = document.getElementById('cal-camera-feed');
                feedImg.src = `data:image/jpeg;base64,${camResult.overlay_image}`;
            }
        } else {
            alert(`❌ Calibration failed:\n${camResult?.error || 'Unknown error'}`);
        }
    } catch (e) {
        console.error('Calibration error:', e);
        alert(`❌ Calibration error:\n${e.message}\n\nMake sure DartDetect API is running on port 8000.`);
    } finally {
        btn.textContent = '🎯 Start Calibration';
        btn.disabled = false;
    }
}

function blobToBase64(blob) {
    return new Promise((resolve, reject) => {
        const reader = new FileReader();
        reader.onloadend = () => resolve(reader.result);
        reader.onerror = reject;
        reader.readAsDataURL(blob);
    });
}

async function checkSystemStatus() {
    // Game API
    try {
        const response = await fetch('/health');
        setSystemStatus('sys-game-api', response.ok);
    } catch {
        setSystemStatus('sys-game-api', false);
    }
    
    // Detect API
    try {
        const response = await fetch(`${DETECT_API}/health`);
        setSystemStatus('sys-detect-api', response.ok);
    } catch {
        setSystemStatus('sys-detect-api', false);
    }
    
    // Database (via players endpoint)
    try {
        const response = await fetch('/api/players');
        setSystemStatus('sys-database', response.ok);
    } catch {
        setSystemStatus('sys-database', false);
    }
    
    // SignalR
    setSystemStatus('sys-signalr', connection?.state === 'Connected');
}

function setSystemStatus(elementId, isOnline) {
    const el = document.getElementById(elementId);
    if (el) {
        el.textContent = isOnline ? '● Online' : '○ Offline';
        el.className = 'status-indicator ' + (isOnline ? 'online' : 'offline');
    }
}

// Initialize settings when DOM is ready
document.addEventListener('DOMContentLoaded', initSettings);

// ==========================================================================
// Fullscreen Support
// ==========================================================================

function initFullscreen() {
    const btn = document.getElementById('fullscreen-btn');
    if (btn) {
        btn.addEventListener('click', toggleFullscreen);
    }
    
    // Update button icon on fullscreen change
    document.addEventListener('fullscreenchange', updateFullscreenButton);
    document.addEventListener('webkitfullscreenchange', updateFullscreenButton);
    
    // Check for kiosk mode in URL (?kiosk=1)
    const params = new URLSearchParams(window.location.search);
    if (params.get('kiosk') === '1' || params.get('fullscreen') === '1') {
        // Prompt fullscreen on first user interaction
        const enterFullscreenOnce = () => {
            requestFullscreen();
            document.removeEventListener('click', enterFullscreenOnce);
            document.removeEventListener('touchstart', enterFullscreenOnce);
        };
        document.addEventListener('click', enterFullscreenOnce);
        document.addEventListener('touchstart', enterFullscreenOnce);
        
        // Show a subtle hint
        showFullscreenHint();
    }
}

function showFullscreenHint() {
    const hint = document.createElement('div');
    hint.id = 'fullscreen-hint';
    hint.innerHTML = '👆 Tap anywhere to enter fullscreen';
    hint.style.cssText = `
        position: fixed;
        bottom: 100px;
        left: 50%;
        transform: translateX(-50%);
        background: rgba(212, 168, 75, 0.9);
        color: #1a1510;
        padding: 15px 25px;
        border-radius: 10px;
        font-family: 'Special Elite', monospace;
        font-size: 1.1rem;
        z-index: 9999;
        animation: pulse 2s infinite;
    `;
    document.body.appendChild(hint);
    
    // Remove after entering fullscreen or after 10 seconds
    setTimeout(() => hint.remove(), 10000);
    document.addEventListener('fullscreenchange', () => hint.remove(), { once: true });
}

function requestFullscreen() {
    const elem = document.documentElement;
    if (elem.requestFullscreen) {
        elem.requestFullscreen();
    } else if (elem.webkitRequestFullscreen) {
        elem.webkitRequestFullscreen();
    }
}

function toggleFullscreen() {
    if (!document.fullscreenElement && !document.webkitFullscreenElement) {
        requestFullscreen();
    } else {
        if (document.exitFullscreen) {
            document.exitFullscreen();
        } else if (document.webkitExitFullscreen) {
            document.webkitExitFullscreen();
        }
    }
}

function updateFullscreenButton() {
    const btn = document.getElementById('fullscreen-btn');
    if (!btn) return;
    
    const isFullscreen = document.fullscreenElement || document.webkitFullscreenElement;
    btn.textContent = isFullscreen ? '⛶' : '⛶';
    btn.title = isFullscreen ? 'Exit Fullscreen' : 'Enter Fullscreen';
}

// Initialize on load
document.addEventListener('DOMContentLoaded', initFullscreen);

// ==========================================================================
// Theme / Background Customization
// ==========================================================================

let themeConfig = null;
let slideshowTimer = null;
let currentBgIndex = 0;
let selectedBackgrounds = [];

async function initTheme() {
    // Load theme config
    try {
        const response = await fetch('/config/theme.json');
        if (response.ok) {
            themeConfig = await response.json();
            applyTheme();
            loadBackgroundGallery();
        }
    } catch (e) {
        console.log('Using default theme');
        themeConfig = getDefaultTheme();
        applyTheme();
    }
    
    // Load saved customizations from localStorage
    const savedTheme = localStorage.getItem('dartsmob-theme');
    if (savedTheme) {
        try {
            const custom = JSON.parse(savedTheme);
            Object.assign(themeConfig, custom);
            applyTheme();
        } catch (e) {}
    }
    
    setupThemeControls();
}

function getDefaultTheme() {
    return {
        appName: "DartsMob",
        tagline: "The Family's Game",
        theme: {
            backgroundImages: [
                "/images/backgrounds/speakeasy-1.jpg",
                "/images/backgrounds/speakeasy-2.jpg",
                "/images/backgrounds/speakeasy-3.jpg",
                "/images/backgrounds/speakeasy-4.jpg",
                "/images/backgrounds/speakeasy-5.jpg"
            ],
            backgroundMode: "slideshow",
            slideshowInterval: 30000,
            overlayOpacity: 0.7
        }
    };
}

function applyTheme() {
    if (!themeConfig) return;
    
    // Apply app name
    const titleEl = document.querySelector('.title');
    if (titleEl && themeConfig.appName) {
        const parts = themeConfig.appName.split(/(?=[A-Z])/);
        if (parts.length >= 2) {
            titleEl.innerHTML = `${parts[0]}<span class="accent">${parts.slice(1).join('')}</span>`;
        } else {
            titleEl.textContent = themeConfig.appName;
        }
    }
    
    // Apply tagline
    const taglineEl = document.querySelector('.tagline');
    if (taglineEl && themeConfig.tagline) {
        taglineEl.textContent = `— ${themeConfig.tagline} —`;
    }
    
    // Apply background
    const backgrounds = themeConfig.theme?.backgroundImages || [];
    selectedBackgrounds = backgrounds;
    
    if (backgrounds.length > 0) {
        setBackground(backgrounds[0]);
        
        if (themeConfig.theme?.backgroundMode === 'slideshow' && backgrounds.length > 1) {
            startSlideshow();
        }
    }
    
    // Apply overlay opacity
    const overlay = document.getElementById('background-overlay');
    if (overlay && themeConfig.theme?.overlayOpacity !== undefined) {
        overlay.style.background = `rgba(0, 0, 0, ${themeConfig.theme.overlayOpacity})`;
    }
}

function setBackground(url) {
    const bgLayer = document.getElementById('background-layer');
    if (bgLayer) {
        bgLayer.style.backgroundImage = `url('${url}')`;
    }
}

function startSlideshow() {
    if (slideshowTimer) clearInterval(slideshowTimer);
    
    const interval = themeConfig.theme?.slideshowInterval || 30000;
    slideshowTimer = setInterval(() => {
        if (selectedBackgrounds.length > 1) {
            currentBgIndex = (currentBgIndex + 1) % selectedBackgrounds.length;
            setBackground(selectedBackgrounds[currentBgIndex]);
        }
    }, interval);
}

function stopSlideshow() {
    if (slideshowTimer) {
        clearInterval(slideshowTimer);
        slideshowTimer = null;
    }
}

function loadBackgroundGallery() {
    const gallery = document.getElementById('background-gallery');
    if (!gallery || !themeConfig?.theme?.backgroundImages) return;
    
    gallery.innerHTML = themeConfig.theme.backgroundImages.map((url, index) => `
        <div class="bg-thumbnail ${index === 0 ? 'selected' : ''}" 
             style="background-image: url('${url}')"
             data-url="${url}"
             onclick="selectBackground(this)">
        </div>
    `).join('');
}

function selectBackground(el) {
    const url = el.dataset.url;
    
    // Toggle selection
    el.classList.toggle('selected');
    
    // Update selected backgrounds
    selectedBackgrounds = Array.from(document.querySelectorAll('.bg-thumbnail.selected'))
        .map(thumb => thumb.dataset.url);
    
    // If at least one selected, show it
    if (selectedBackgrounds.length > 0) {
        setBackground(selectedBackgrounds[0]);
        currentBgIndex = 0;
    }
}

function setupThemeControls() {
    // App name
    const nameInput = document.getElementById('theme-app-name');
    if (nameInput && themeConfig?.appName) {
        nameInput.value = themeConfig.appName;
    }
    
    // Tagline
    const taglineInput = document.getElementById('theme-tagline');
    if (taglineInput && themeConfig?.tagline) {
        taglineInput.value = themeConfig.tagline;
    }
    
    // Slideshow toggle
    const slideshowToggle = document.getElementById('slideshow-toggle');
    if (slideshowToggle) {
        slideshowToggle.checked = themeConfig?.theme?.backgroundMode === 'slideshow';
        slideshowToggle.addEventListener('change', () => {
            if (slideshowToggle.checked) {
                startSlideshow();
            } else {
                stopSlideshow();
            }
        });
    }
    
    // Slideshow speed
    const speedSelect = document.getElementById('slideshow-speed');
    if (speedSelect && themeConfig?.theme?.slideshowInterval) {
        speedSelect.value = themeConfig.theme.slideshowInterval;
        speedSelect.addEventListener('change', () => {
            themeConfig.theme.slideshowInterval = parseInt(speedSelect.value);
            if (slideshowTimer) {
                startSlideshow(); // Restart with new speed
            }
        });
    }
    
    // Overlay opacity
    const opacitySlider = document.getElementById('overlay-opacity');
    const opacityValue = document.getElementById('opacity-value');
    if (opacitySlider) {
        const currentOpacity = (themeConfig?.theme?.overlayOpacity || 0.7) * 100;
        opacitySlider.value = currentOpacity;
        if (opacityValue) opacityValue.textContent = `${Math.round(currentOpacity)}%`;
        
        opacitySlider.addEventListener('input', () => {
            const opacity = opacitySlider.value / 100;
            if (opacityValue) opacityValue.textContent = `${opacitySlider.value}%`;
            const overlay = document.getElementById('background-overlay');
            if (overlay) overlay.style.background = `rgba(0, 0, 0, ${opacity})`;
        });
    }
    
    // Save button
    const saveBtn = document.getElementById('save-theme-btn');
    if (saveBtn) {
        saveBtn.addEventListener('click', saveTheme);
    }
    
    // Reset button
    const resetBtn = document.getElementById('reset-theme-btn');
    if (resetBtn) {
        resetBtn.addEventListener('click', resetTheme);
    }
    
    // Upload button
    const uploadInput = document.getElementById('bg-upload');
    if (uploadInput) {
        uploadInput.addEventListener('change', handleBgUpload);
    }
}

function saveTheme() {
    const nameInput = document.getElementById('theme-app-name');
    const taglineInput = document.getElementById('theme-tagline');
    const slideshowToggle = document.getElementById('slideshow-toggle');
    const speedSelect = document.getElementById('slideshow-speed');
    const opacitySlider = document.getElementById('overlay-opacity');
    
    const customTheme = {
        appName: nameInput?.value || themeConfig.appName,
        tagline: taglineInput?.value || themeConfig.tagline,
        theme: {
            backgroundImages: selectedBackgrounds,
            backgroundMode: slideshowToggle?.checked ? 'slideshow' : 'static',
            slideshowInterval: parseInt(speedSelect?.value) || 30000,
            overlayOpacity: (opacitySlider?.value || 70) / 100
        }
    };
    
    localStorage.setItem('dartsmob-theme', JSON.stringify(customTheme));
    Object.assign(themeConfig, customTheme);
    applyTheme();
    
    alert('Theme saved!');
}

function resetTheme() {
    localStorage.removeItem('dartsmob-theme');
    themeConfig = getDefaultTheme();
    applyTheme();
    loadBackgroundGallery();
    setupThemeControls();
    alert('Theme reset to default!');
}

async function handleBgUpload(e) {
    const file = e.target.files[0];
    if (!file) return;
    
    // Convert to base64 and store locally
    const reader = new FileReader();
    reader.onload = function(event) {
        const dataUrl = event.target.result;
        
        // Add to gallery
        const gallery = document.getElementById('background-gallery');
        const thumb = document.createElement('div');
        thumb.className = 'bg-thumbnail selected';
        thumb.style.backgroundImage = `url('${dataUrl}')`;
        thumb.dataset.url = dataUrl;
        thumb.onclick = function() { selectBackground(this); };
        gallery.appendChild(thumb);
        
        // Add to selected
        selectedBackgrounds.push(dataUrl);
        setBackground(dataUrl);
    };
    reader.readAsDataURL(file);
}

// Initialize theme on load
document.addEventListener('DOMContentLoaded', initTheme);
