/**
 * DartsMob - Main Game JavaScript
 * Mobile-first redesign
 */

// ==========================================================================
// Configuration
// ==========================================================================

const DETECT_API = 'http://192.168.0.158:8000';
let boardId = 'default';
// Fetch actual board ID from API
fetch('/api/boards/current').then(r => r.json()).then(b => { boardId = b.id; console.log('Board:', b.name, b.id); }).catch(() => {});

// ==========================================================================
// Centralized Logging
// ==========================================================================

async function logEvent(level, category, message, data = null) {
    try {
        await fetch('/api/logs', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                source: 'UI',
                level: level,
                category: category,
                message: message,
                data: data,
                gameId: currentGame?.id || null
            })
        });
    } catch (e) {
        // Silently fail - don't break the app for logging
        console.error('Log failed:', e);
    }
}

// Convenience wrappers
const log = {
    debug: (cat, msg, data) => logEvent('DEBUG', cat, msg, data),
    info: (cat, msg, data) => logEvent('INFO', cat, msg, data),
    warn: (cat, msg, data) => logEvent('WARN', cat, msg, data),
    error: (cat, msg, data) => logEvent('ERROR', cat, msg, data)
};

// ==========================================================================
// State
// ==========================================================================

let connection = null;
let currentGame = null;
let selectedMode = 'Practice';
let selectedBestOf = 5;
let lookingForMatch = false;  // Looking for match toggle state

// ==========================================================================
// Initialization
// ==========================================================================

document.addEventListener('DOMContentLoaded', () => {
    initTheme();
    initConnection();
    initEventListeners();
    initFullscreen();
    initDartCorrection();
    initPlayerManagement();
    initOnlinePlay();
});

// ==========================================================================
// Theme
// ==========================================================================

function initTheme() {
    const theme = JSON.parse(localStorage.getItem('dartsmob-theme') || '{}');
    
    // Apply app name
    const titleEl = document.getElementById('app-title');
    if (titleEl && theme.appName) {
        // Split camelCase or just use as-is
        const name = theme.appName;
        if (name.toLowerCase().includes('mob')) {
            const parts = name.split(/(?=mob)/i);
            titleEl.innerHTML = `${parts[0]}<span class="accent">${parts[1] || ''}</span>`;
        } else {
            titleEl.textContent = name;
        }
    }
    
    // Apply tagline
    const taglineEl = document.getElementById('app-tagline');
    if (taglineEl && theme.tagline) {
        taglineEl.textContent = `— ${theme.tagline} —`;
    }
    
    // Apply background
    const backgrounds = theme.backgrounds || [
        '/images/backgrounds/speakeasy-1.jpg',
        '/images/backgrounds/speakeasy-2.jpg',
        '/images/backgrounds/speakeasy-3.jpg',
        '/images/backgrounds/speakeasy-4.jpg',
        '/images/backgrounds/speakeasy-5.jpg'
    ];
    
    if (backgrounds.length > 0) {
        // Shuffle backgrounds randomly
        const shuffled = [...backgrounds].sort(() => Math.random() - 0.5);
        setBackground(shuffled[0]);
        
        // Start slideshow if enabled
        if (theme.slideshow !== false && shuffled.length > 1) {
            let idx = 0;
            setInterval(() => {
                idx = (idx + 1) % shuffled.length;
                setBackground(shuffled[idx]);
            }, theme.slideshowSpeed || 30000);
        }
    }
    
    // Apply overlay opacity
    const overlay = document.getElementById('background-overlay');
    if (overlay) {
        const opacity = (theme.overlayOpacity ?? 70) / 100;
        overlay.style.background = `rgba(0, 0, 0, ${opacity})`;
    }
}

function setBackground(url) {
    const layer = document.getElementById('background-layer');
    if (layer) {
        layer.style.backgroundImage = `url('${url}')`;
    }
}

// ==========================================================================
// SignalR Connection
// ==========================================================================

async function initConnection() {
    connection = new signalR.HubConnectionBuilder()
        .withUrl('/gamehub')
        .withAutomaticReconnect()
        .configureLogging(signalR.LogLevel.Warning)
        .build();

    connection.onreconnecting(() => updateConnectionStatus('Reconnecting...', ''));
    connection.onreconnected(() => {
        updateConnectionStatus('Connected', 'connected');
        connection.invoke('JoinBoard', boardId);
    });
    connection.onclose(() => updateConnectionStatus('Disconnected', ''));

    // Game events
    connection.on('DartThrown', handleDartThrown);
    connection.on('DartRemoved', handleDartRemoved);
    connection.on('BoardCleared', handleBoardCleared);
    connection.on('GameStarted', handleGameStarted);
    connection.on('GameEnded', handleGameEnded);
    connection.on('TurnEnded', handleTurnEnded);
    connection.on('DartNotFound', handleDartNotFound);
    connection.on('LegWon', handleLegWon);

    try {
        await connection.start();
        updateConnectionStatus('Connected', 'connected');
        await connection.invoke('JoinBoard', boardId);
    } catch (err) {
        console.error('SignalR error:', err);
        updateConnectionStatus('Offline', '');
    }
}

function updateConnectionStatus(text, className) {
    const el = document.getElementById('connection-status');
    if (el) {
        el.textContent = `🔌 ${text}`;
        el.className = `connection ${className}`;
    }
}

// ==========================================================================
// Game Event Handlers
// ==========================================================================

// Audio settings - stored in localStorage
const audioSettings = {
    mode: localStorage.getItem('audio-mode') || 'off',  // 'off', 'tts', 'files'
    volume: parseInt(localStorage.getItem('audio-volume') || '80') / 100,
    
    save() {
        localStorage.setItem('audio-mode', this.mode);
        localStorage.setItem('audio-volume', Math.round(this.volume * 100));
    },
    
    load() {
        this.mode = localStorage.getItem('audio-mode') || 'off';
        this.volume = parseInt(localStorage.getItem('audio-volume') || '80') / 100;
    }
};

// Audio for dart scores - uses pre-recorded files
const dartAudio = {
    cache: {},
    
    // Preload common audio files
    preload() {
        if (audioSettings.mode !== 'files') return;
        
        const files = [
            'miss', 'bust', 'bullseye', 'double-bullseye',
            ...Array.from({length: 20}, (_, i) => `${i + 1}`),
            ...Array.from({length: 20}, (_, i) => `double-${i + 1}`),
            ...Array.from({length: 20}, (_, i) => `triple-${i + 1}`)
        ];
        files.forEach(name => {
            const audio = new Audio(`/audio/${name}.mp3`);
            audio.preload = 'auto';
            audio.volume = audioSettings.volume;
            this.cache[name] = audio;
        });
        console.log('Audio files preloaded');
    },
    
    play(name) {
        if (audioSettings.mode === 'off') return;
        
        if (audioSettings.mode === 'files') {
            // Try cached version first
            if (this.cache[name]) {
                this.cache[name].currentTime = 0;
                this.cache[name].volume = audioSettings.volume;
                this.cache[name].play().catch(() => {});
                return;
            }
            // Fallback to creating new audio
            const audio = new Audio(`/audio/${name}.mp3`);
            audio.volume = audioSettings.volume;
            audio.play().catch(() => {});
        }
    },
    
    // TTS version
    speak(text) {
        if (audioSettings.mode !== 'tts' || !window.speechSynthesis) return;
        
        const utterance = new SpeechSynthesisUtterance(text);
        utterance.rate = 1.1;
        utterance.volume = audioSettings.volume;
        speechSynthesis.speak(utterance);
    }
};

// Initialize audio on first user interaction (browser autoplay policy)
document.addEventListener('click', () => dartAudio.preload(), { once: true });

function speakDartScore(dart) {
    if (!dart || audioSettings.mode === 'off') return;
    
    const zone = dart.zone?.toLowerCase() || '';
    const segment = dart.segment || 0;
    const multiplier = dart.multiplier || 0;
    let audioFile = '';
    let ttsText = '';
    
    // Check for bull first (segment 0 or 25 with multiplier > 0)
    if (zone === 'inner_bull' || zone === 'bullseye' || zone === 'double_bull' || 
        (segment === 0 && multiplier === 2) || (segment === 25 && multiplier === 2)) {
        audioFile = 'double-bullseye';
        ttsText = 'Double Bullseye';
    } else if (zone === 'outer_bull' || zone === 'bull' || 
               (segment === 0 && multiplier === 1) || (segment === 25 && multiplier === 1)) {
        audioFile = 'bullseye';
        ttsText = 'Bullseye';
    } else if (zone === 'miss' || (segment === 0 && multiplier === 0)) {
        audioFile = 'miss';
        ttsText = 'Miss';
    } else if (zone === 'double' || zone === 'double_ring') {
        audioFile = `double-${segment}`;
        ttsText = `Double ${segment}`;
    } else if (zone === 'triple' || zone === 'triple_ring') {
        audioFile = `triple-${segment}`;
        ttsText = `Triple ${segment}`;
    } else {
        // Single - just the number
        audioFile = `${segment}`;
        ttsText = `${segment}`;
    }
    
    if (audioSettings.mode === 'files') {
        dartAudio.play(audioFile);
    } else if (audioSettings.mode === 'tts') {
        dartAudio.speak(ttsText);
    }
}

function speakBust() {
    if (audioSettings.mode === 'off') return;
    
    if (audioSettings.mode === 'files') {
        dartAudio.play('bust');
    } else if (audioSettings.mode === 'tts') {
        dartAudio.speak('Bust');
    }
}

function handleDartThrown(data) {
    const receiveTime = Date.now();
    
    // Log to centralized logging
    const dart = data.dart;
    log.info('Scoring', `Dart: ${dart?.zone || 'unknown'} ${dart?.segment || 0} = ${dart?.score || 0}`, dart);
    
    // Track timing - detectionTime comes from sensor
    if (data.detectionTime) {
        const latencyMs = receiveTime - data.detectionTime;
        log.debug('Timing', `Detection to UI: ${latencyMs}ms`);
    }
    
    currentGame = data.game;
    updateScoreboard();
    updateCurrentTurn();
    
    // Check for bust - show bust popup instead of normal dart popup
    if (data.game?.currentTurn?.busted) {
        showBustPopup(data.dart);
        setTimeout(() => speakBust(), 300);
    } else {
        showThrowPopup(data.dart);
        speakDartScore(data.dart);
    }
    
    // Check for winner - game state Finished = 2
    if (data.game?.state === 2 || data.game?.state === 'Finished') {
        const winnerName = data.game.winnerName || 
                          data.game.players?.find(p => p.id === data.game.winnerId)?.name ||
                          'Winner';
        console.log('🏆 Game finished via dart! Winner:', winnerName);
        setTimeout(() => showWinnerModal(winnerName), 1000);
    }
}

function handleBoardCleared(data) {
    console.log('Board cleared - PPD stays visible until Next Player');
    // Do NOT clear PPD boxes here - player just removed darts
    // PPD and turn total stay visible until Next Player is pressed
}

function handleDartRemoved(data) {
    console.log('Dart removed (false detection):', data);
    if (data.game) {
        currentGame = data.game;
        updateScoreboard();
        updateCurrentTurn();
    }
}

function handleGameStarted(data) {
    console.log('Game started:', data);
    currentGame = data;
    showScreen('game-screen');
    updateScoreboard();
    updateCurrentTurn();
}

function handleGameEnded(data) {
    console.log('🏆 Game ended! Full data:', JSON.stringify(data));
    
    // Update game state
    if (data.game) {
        currentGame = data.game;
        updateScoreboard();
    }
    
    // Find winner name from various possible properties
    const winnerName = data.winnerName || data.WinnerName || data.winner || 
                       (data.players?.find(p => p.id === data.winnerId)?.name) ||
                       'Winner';
    
    console.log('🏆 Showing winner modal for:', winnerName);
    
    // Small delay to let any dart animations finish
    setTimeout(() => {
        showWinnerModal(winnerName);
    }, 500);
}

function handleTurnEnded(data) {
    console.log('Turn ended:', data);
    // Update game state with new turn info (round may have increased)
    if (data.game) {
        currentGame = data.game;
    }
    // Clear PPD boxes and turn total for new player's turn
    clearCurrentTurn();
    // Update scoreboard (shows new current player, round, etc)
    updateScoreboard();
}

// ==========================================================================
// UI Updates
// ==========================================================================

function showScreen(screenId) {
    document.querySelectorAll('.screen').forEach(s => s.classList.add('hidden'));
    document.getElementById(screenId)?.classList.remove('hidden');
}

function updateScoreboard() {
    if (!currentGame) return;
    
    const scoreboard = document.getElementById('scoreboard');
    if (!scoreboard) return;
    
    scoreboard.innerHTML = currentGame.players.map((player, idx) => `
        <div class="player-card ${idx === currentGame.currentPlayerIndex ? 'active' : ''}">
            <div class="player-info">
                <span class="player-name">${escapeHtml(player.name)}</span>
                <div class="player-stats">
                    <span>${player.dartsThrown} darts</span>
                    ${currentGame.legsToWin > 1 ? `<span class="legs">Legs: ${player.legsWon}</span>` : ''}
                </div>
            </div>
            <span class="player-score">${player.score}</span>
        </div>
    `).join('');
    
    // Update header
    document.getElementById('game-mode-display').textContent = formatMode(currentGame.mode);
    
    const legsDisplay = document.getElementById('legs-display');
    if (legsDisplay) {
        if (currentGame.legsToWin > 1) {
            legsDisplay.textContent = `Leg ${currentGame.currentLeg}/${currentGame.totalLegs}`;
            legsDisplay.style.display = '';
        } else {
            legsDisplay.style.display = 'none';
        }
    }
    
    // Update round display
    updateRoundDisplay();
}

function updateCurrentTurn() {
    console.log('[updateCurrentTurn] currentGame:', currentGame);
    console.log('[updateCurrentTurn] currentTurn:', currentGame?.currentTurn);
    
    if (!currentGame?.currentTurn) {
        clearCurrentTurn();
        return;
    }
    
    const darts = currentGame.currentTurn.darts || [];
    console.log('[updateCurrentTurn] darts array:', darts);
    
    const slots = document.querySelectorAll('.dart-slot');
    console.log('[updateCurrentTurn] found slots:', slots.length);
    
    slots.forEach((slot, i) => {
        if (darts[i]) {
            slot.classList.add('hit');
            slot.textContent = darts[i].score;
            console.log(`[updateCurrentTurn] slot ${i} = ${darts[i].score}`);
        } else {
            slot.classList.remove('hit');
            slot.textContent = '—';
        }
    });
    
    const turnScore = currentGame.currentTurn.turnScore || 0;
    document.getElementById('turn-score').textContent = turnScore;
    console.log('[updateCurrentTurn] turnScore:', turnScore);
}

function clearCurrentTurn() {
    document.querySelectorAll('.dart-slot').forEach(slot => {
        slot.classList.remove('hit');
        slot.textContent = '—';
    });
    document.getElementById('turn-score').textContent = '0';
}

function showThrowPopup(dart) {
    const popup = document.getElementById('throw-popup');
    if (!popup) return;
    
    // Build short prefix: S=single, D=double, T=triple
    let prefix = '';
    const zone = dart.zone?.toLowerCase() || '';
    const segment = dart.segment || 0;
    const score = dart.score || 0;
    
    if (zone === 'double' || zone === 'double_ring') {
        prefix = 'D';
    } else if (zone === 'triple' || zone === 'triple_ring') {
        prefix = 'T';
    } else if (zone === 'inner_bull' || zone === 'bullseye') {
        prefix = 'BULL';
    } else if (zone === 'outer_bull') {
        prefix = 'BULL';
    } else if (zone === 'miss' || segment === 0) {
        prefix = 'MISS';
    } else {
        prefix = 'S';  // Single
    }
    
    // Top line: "D20", "T19", "S5", "BULL", "MISS"
    const topText = (prefix === 'BULL' || prefix === 'MISS') ? prefix : `${prefix}${segment}`;
    
    // Bottom line: Score (e.g., "40", "57", "25")
    popup.querySelector('.throw-zone').textContent = topText;
    popup.querySelector('.throw-value').textContent = score;
    
    popup.classList.remove('hidden', 'show');
    void popup.offsetWidth; // Force reflow
    popup.classList.add('show');
    
    setTimeout(() => {
        popup.classList.add('hidden');
        popup.classList.remove('show');
    }, 1500);
}

function showBustPopup(dart) {
    // Show the bust modal instead of just a popup
    showBustModal();
}

function showBustModal() {
    // Remove existing modal if any
    document.getElementById('bust-modal')?.remove();
    
    const turn = currentGame?.currentTurn;
    const darts = turn?.darts || [];
    const player = currentGame?.players?.[currentGame.currentPlayerIndex];
    
    // Build darts list HTML
    let dartsHtml = '';
    darts.forEach((d, i) => {
        const dartText = formatDartShort(d);
        dartsHtml += `
            <div style="display: flex; justify-content: space-between; align-items: center; padding: 10px; background: #222; border-radius: 8px; margin-bottom: 8px;">
                <span style="font-size: 1.3rem; color: var(--paper);">Dart ${i + 1}: <strong>${dartText}</strong> (${d.score})</span>
                <button onclick="openCorrectionForDart(${i})" style="background: #d4af37; color: #000; border: none; padding: 8px 16px; border-radius: 6px; cursor: pointer; font-weight: bold;">✏️ Correct</button>
            </div>
        `;
    });
    
    const modal = document.createElement('div');
    modal.id = 'bust-modal';
    modal.style.cssText = `
        position: fixed; top: 0; left: 0; right: 0; bottom: 0;
        background: rgba(0,0,0,0.95); z-index: 1000;
        display: flex; align-items: center; justify-content: center;
        padding: 20px;
    `;
    modal.innerHTML = `
        <div style="background: linear-gradient(180deg, #2a1a1a 0%, #1a0a0a 100%); border: 3px solid #ff4444; border-radius: 16px; padding: 30px; max-width: 500px; width: 100%; text-align: center;">
            <h1 style="color: #ff4444; font-size: 3rem; margin: 0 0 10px 0; font-family: 'Bebas Neue', sans-serif; letter-spacing: 0.1em; text-shadow: 0 0 20px rgba(255,68,68,0.5);">💥 BUSTED! 💥</h1>
            <p style="color: var(--paper-muted); margin: 0 0 20px 0;">${player?.name || 'Player'} - Score reverted to ${turn?.scoreBeforeBust || player?.score}</p>
            
            <div style="margin-bottom: 20px; text-align: left;">
                ${dartsHtml || '<p style="color: var(--paper-muted);">No darts recorded</p>'}
            </div>
            
            <p style="color: #ffaa00; margin-bottom: 20px; font-size: 0.95rem;">
                🎯 Remove your darts from the board.<br>
                You can correct any dart before confirming.
            </p>
            
            <button id="confirm-bust-btn" onclick="confirmBust()" style="background: linear-gradient(180deg, #ff5555 0%, #cc3333 100%); color: white; border: none; padding: 15px 40px; border-radius: 8px; font-size: 1.3rem; font-weight: bold; cursor: pointer; font-family: 'Bebas Neue', sans-serif; letter-spacing: 0.1em;">
                ✓ CONFIRM BUST
            </button>
        </div>
    `;
    
    document.body.appendChild(modal);
}

function formatDartShort(dart) {
    const zone = dart?.zone?.toLowerCase() || '';
    const segment = dart?.segment || 0;
    
    if (zone === 'inner_bull' || zone === 'bullseye' || zone === 'double_bull') return 'BULL';
    if (zone === 'outer_bull') return 'S-BULL';
    if (zone === 'miss' || segment === 0) return 'MISS';
    if (zone === 'double' || zone === 'double_ring') return `D${segment}`;
    if (zone === 'triple' || zone === 'triple_ring') return `T${segment}`;
    return `S${segment}`;
}

async function confirmBust() {
    const btn = document.getElementById('confirm-bust-btn');
    if (btn) {
        btn.disabled = true;
        btn.textContent = '⏳ Confirming...';
    }
    
    try {
        // Call API to confirm the bust and end turn
        const resp = await fetch(`/api/games/${currentGame.id}/confirm-bust`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' }
        });
        
        if (resp.ok) {
            const data = await resp.json();
            currentGame = data.game || data;
            updateScoreboard();
            updateCurrentTurn();
        }
    } catch (err) {
        console.error('Error confirming bust:', err);
    }
    
    // Close modal
    document.getElementById('bust-modal')?.remove();
}

function openCorrectionForDart(dartIndex) {
    // Close bust modal temporarily
    document.getElementById('bust-modal')?.remove();
    
    // Open the correction modal for this dart
    correctionDartIndex = dartIndex;
    correctionSegment = 20;
    correctionMultiplier = 1;
    
    const correctionModal = document.getElementById('correction-modal');
    if (correctionModal) {
        correctionModal.classList.remove('hidden');
        updateCorrectionPreview();
    }
}

function handleLegWon(data) {
    console.log('🎯 Leg won!', data);
    
    // Update player leg counts in local state
    if (currentGame && data.game?.Players) {
        data.game.Players.forEach(p => {
            const local = currentGame.players?.find(lp => lp.id === p.Id);
            if (local) local.legsWon = p.LegsWon;
        });
    }
    
    showLegWonModal(data.winnerName, data.legsWon, data.legsToWin, data.game?.Players || []);
}

function showLegWonModal(winnerName, legsWon, legsToWin, players) {
    let modal = document.getElementById('leg-won-modal');
    if (!modal) {
        modal = document.createElement('div');
        modal.id = 'leg-won-modal';
        modal.className = 'modal hidden';
        modal.innerHTML = `
            <div class="modal-content winner-content">
                <h2 class="winner-title">🎯 LEG WON! 🎯</h2>
                <div class="winner-name"></div>
                <div class="leg-standings"></div>
                <button class="btn btn-primary" id="leg-won-ok" style="margin-top: 20px;">Continue</button>
            </div>
        `;
        document.body.appendChild(modal);
        
        document.getElementById('leg-won-ok').addEventListener('click', async () => {
            modal.classList.add('hidden');
            // Refresh game state from server to get new leg
            try {
                const resp = await fetch('/api/games/board/' + boardId);
                if (resp.ok) {
                    currentGame = await resp.json();
                    updateScoreboard();
                    updateCurrentTurn();
                }
            } catch (e) { console.error('Failed to refresh game:', e); }
        });
    }
    
    modal.querySelector('.winner-name').textContent = winnerName;
    
    // Build standings
    const standings = players.map(p => 
        `${p.Name}: ${p.LegsWon} / ${legsToWin}`
    ).join('  •  ');
    modal.querySelector('.leg-standings').textContent = standings;
    
    modal.classList.remove('hidden');
    
    if (audioSettings.mode === 'tts') {
        dartAudio.speak(`${winnerName} wins the leg! ${legsWon} of ${legsToWin}.`);
    }
}

function showWinnerModal(winner) {
    // Create modal if it doesn't exist
    let modal = document.getElementById('winner-modal');
    if (!modal) {
        modal = document.createElement('div');
        modal.id = 'winner-modal';
        modal.className = 'modal hidden';
        modal.innerHTML = `
            <div class="modal-content winner-content">
                <h2 class="winner-title">🏆 WINNER! 🏆</h2>
                <div class="winner-name"></div>
                <div class="winner-buttons">
                    <button class="btn btn-primary" id="winner-play-again">🎯 Play Again</button>
                    <button class="btn btn-secondary" id="winner-quit">🚪 Quit</button>
                </div>
            </div>
        `;
        document.body.appendChild(modal);
        
        document.getElementById('winner-play-again').addEventListener('click', () => {
            modal.classList.add('hidden');
            // Restart with same players
            location.reload();  // Simple reload for now
        });
        
        document.getElementById('winner-quit').addEventListener('click', () => {
            modal.classList.add('hidden');
            showScreen('setup-screen');
        });
    }
    
    modal.querySelector('.winner-name').textContent = winner.name || winner;
    modal.classList.remove('hidden');
    
    // Announce winner
    if (audioSettings.mode === 'tts') {
        dartAudio.speak(`${winner.name || winner} wins!`);
    }
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
    
    // Get game mode and rules from dropdowns
    const gameMode = getSelectedGameMode();
    const rules = getSelectedRules();
    
    // Disable start button and show loading state
    const startBtn = document.getElementById('start-game-btn');
    if (startBtn) {
        startBtn.disabled = true;
        startBtn.textContent = '⏳ Starting...';
    }
    
    try {
        const response = await fetch('/api/games', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                boardId,
                mode: gameMode,
                playerNames: players,
                bestOf: selectedBestOf,
                requireDoubleOut: rules['double-out'] ?? false,
                startingScore: getSelectedStartingScore(),
                doubleIn: rules['double-in'] ?? false,
                masterOut: rules['master-out'] ?? false,
                rules: rules
            })
        });
        
        if (response.ok) {
            currentGame = await response.json();
            showScreen('game-screen');
            updateScoreboard();
            updateCurrentTurn();
        } else {
            // Parse error response and show meaningful message
            const error = await response.json();
            showStartGameError(error);
        }
    } catch (err) {
        console.error('Error starting game:', err);
        showStartGameError({ error: 'Network error', message: 'Could not connect to server' });
    } finally {
        // Re-enable start button
        if (startBtn) {
            startBtn.disabled = false;
            startBtn.textContent = 'START GAME';
        }
    }
}

function showStartGameError(error) {
    // Map error codes to user-friendly messages
    const messages = {
        'NO_CAMERAS': '📷 No cameras registered. Go to Settings to set up cameras.',
        'NOT_CALIBRATED': '🎯 Cameras not calibrated. Go to Settings → Calibration.',
        'SENSOR_DISCONNECTED': '📡 DartSensor not connected. Please start the sensor.',
        'BOARD_NOT_FOUND': '🎯 Board not found. Check your settings.'
    };
    
    const friendlyMessage = messages[error.code] || error.message || error.error || 'Failed to start game';
    
    // Show error modal
    let modal = document.getElementById('start-error-modal');
    if (!modal) {
        // Create modal if it doesn't exist
        modal = document.createElement('div');
        modal.id = 'start-error-modal';
        modal.className = 'modal hidden';
        modal.innerHTML = `
            <div class="modal-backdrop" onclick="document.getElementById('start-error-modal').classList.add('hidden')"></div>
            <div class="modal-content art-deco-card" style="max-width: 400px; text-align: center;">
                <h2 style="color: var(--gold); margin-bottom: 1rem;">⚠️ Cannot Start Game</h2>
                <p id="start-error-message" style="margin-bottom: 1.5rem; color: #ccc;"></p>
                <button class="btn-gold" onclick="document.getElementById('start-error-modal').classList.add('hidden')">OK</button>
            </div>
        `;
        document.body.appendChild(modal);
    }
    
    document.getElementById('start-error-message').textContent = friendlyMessage;
    modal.classList.remove('hidden');
}

async function endGame() {
    // Show confirmation modal instead of ending immediately
    document.getElementById('end-game-modal')?.classList.remove('hidden');
}

async function confirmEndGame() {
    document.getElementById('end-game-modal')?.classList.add('hidden');
    
    if (!currentGame) {
        showScreen('setup-screen');
        return;
    }
    
    try {
        await fetch(`/api/games/${currentGame.id}/end`, { method: 'POST' });
    } catch (err) {
        console.error('Error ending game:', err);
    } finally {
        currentGame = null;
        showScreen('setup-screen');
    }
}

async function nextTurn() {
    if (!currentGame) return;
    
    try {
        const response = await fetch(`/api/games/${currentGame.id}/next-turn`, { method: 'POST' });
        if (response.ok) {
            const result = await response.json();
            currentGame = result.game;
            updateScoreboard();
            updateCurrentTurn();
            updateRoundDisplay();
        }
    } catch (err) {
        console.error('Error advancing turn:', err);
    }
}

function updateRoundDisplay() {
    const roundDisplay = document.getElementById('round-display');
    if (roundDisplay && currentGame) {
        roundDisplay.textContent = `Round ${currentGame.currentRound || 1}`;
    }
}

// ==========================================================================
// Event Listeners
// ==========================================================================

// Game definitions with category-specific options
const gameConfig = {
    x01: {
        label: '🔢 X01',
        variants: [
            { value: '20', label: '🐛 Debug 20' },
            { value: '301', label: '301' },
            { value: '501', label: '501' },
            { value: '401', label: '401' },
            { value: '601', label: '601' },
            { value: '701', label: '701' },
            { value: '801', label: '801' },
            { value: '901', label: '901' },
            { value: '1001', label: '1001' }
        ],
        defaultVariant: '501',
        rules: [
            { id: 'double-in', label: 'Double In', default: false },
            { id: 'double-out', label: 'Double Out', default: false }
        ]
    },
    cricket: {
        label: '🦗 Cricket',
        variants: [
            { value: 'CricketStandard', label: 'Standard' },
            { value: 'CricketCutThroat', label: 'Cut-Throat' },
            { value: 'CricketNoPoints', label: 'No Points (Close Only)' }
        ],
        defaultVariant: 'CricketStandard',
        rules: []
    },
    around: {
        label: '🔄 Around the World',
        variants: [
            { value: 'AroundTheClock', label: 'Around the Clock' },
            { value: 'Shanghai', label: 'Shanghai' }
        ],
        defaultVariant: 'AroundTheClock',
        rules: [
            { id: 'include-bull', label: 'Include Bull', default: true }
        ]
    },
    killer: {
        label: '💀 Killer',
        variants: [
            { value: 'Killer', label: 'Killer' },
            { value: 'BlindKiller', label: 'Blind Killer' }
        ],
        defaultVariant: 'Killer',
        rules: [
            { id: 'lives-5', label: '5 Lives (vs 3)', default: false }
        ]
    },
    practice: {
        label: '🎯 Practice',
        variants: [
            { value: 'FreePlay', label: 'Free Play (Count Up)' },
            { value: 'DoublesTraining', label: 'Doubles Training' },
            { value: 'TriplesTraining', label: 'Triples Training' },
            { value: 'BullseyeTraining', label: 'Bullseye Training' }
        ],
        defaultVariant: 'FreePlay',
        rules: []
    }
};

function initEventListeners() {
    // Game category dropdown
    const categorySelect = document.getElementById('game-category');
    if (categorySelect) {
        categorySelect.addEventListener('change', function() {
            const category = this.value;
            updateVariants(category);
            updateRulesSection(category);
            
            // Show/hide legs section (hide for practice)
            const legsSection = document.getElementById('legs-section');
            if (legsSection) {
                legsSection.style.display = category === 'practice' ? 'none' : '';
            }
        });
        
        // Initialize for default selection
        updateVariants(categorySelect.value);
        updateRulesSection(categorySelect.value);
    }
    
    // Legs buttons
    document.querySelectorAll('.legs-btn').forEach(btn => {
        btn.addEventListener('click', () => {
            document.querySelectorAll('.legs-btn').forEach(b => b.classList.remove('active'));
            btn.classList.add('active');
            selectedBestOf = parseInt(btn.dataset.legs);
        });
    });
    
    // Add player - handled in initPlayerManagement
    
    // Start game
    document.getElementById('start-game-btn')?.addEventListener('click', startGame);
    
    // End game - now shows confirmation
    document.getElementById('end-game-btn')?.addEventListener('click', endGame);
    
    // End game confirmation modal handlers
    document.getElementById('end-game-cancel')?.addEventListener('click', () => {
        document.getElementById('end-game-modal')?.classList.add('hidden');
    });
    document.getElementById('end-game-confirm')?.addEventListener('click', confirmEndGame);
    document.querySelector('#end-game-modal .modal-backdrop')?.addEventListener('click', () => {
        document.getElementById('end-game-modal')?.classList.add('hidden');
    });
    
    // Next turn
    document.getElementById('next-turn-btn')?.addEventListener('click', nextTurn);
    
    // New game
    document.getElementById('new-game-btn')?.addEventListener('click', () => {
        showScreen('setup-screen');
    });
    
    // Initialize game selection state
    const legsSection = document.getElementById('legs-section');
    const catSelect = document.getElementById('game-category');
    if (legsSection && catSelect) {
        legsSection.style.display = catSelect.value === 'practice' ? 'none' : '';
    }
}

function updateVariants(category) {
    const variantSelect = document.getElementById('game-variant');
    const variantSection = document.getElementById('variant-section');
    const config = gameConfig[category];

    if (!variantSection || !variantSelect || !config) return;

    const variants = config.variants || [];
    
    if (variants.length <= 1) {
        variantSection.style.display = 'none';
        if (variants.length === 1) {
            variantSelect.innerHTML = `<option value="${variants[0].value}">${variants[0].label}</option>`;
        }
        return;
    }

    variantSection.style.display = '';
    variantSelect.innerHTML = variants.map(v => 
        `<option value="${v.value}">${v.label}</option>`
    ).join('');
    
    // Set default
    if (config.defaultVariant) {
        variantSelect.value = config.defaultVariant;
    }
}

function updateRulesSection(category) {
    const rulesSection = document.getElementById('rules-section');
    const rulesContainer = document.querySelector('.rules-options');
    const config = gameConfig[category];
    
    if (!rulesSection || !rulesContainer) return;

    const rules = config?.rules || [];
    
    if (rules.length === 0) {
        rulesSection.style.display = 'none';
        return;
    }

    rulesSection.style.display = '';
    rulesContainer.innerHTML = rules.map(rule => `
        <label class="rule-option">
            <input type="checkbox" id="rule-${rule.id}" ${rule.default ? 'checked' : ''}> ${rule.label}
        </label>
    `).join('');
}

function getSelectedGameMode() {
    const category = document.getElementById('game-category')?.value || 'x01';
    const variant = document.getElementById('game-variant')?.value;
    const config = gameConfig[category];
    const gameVariant = variant || config?.defaultVariant || '501';
    if (category === 'x01') {
        if (gameVariant === '20') return 'Debug20';
        return 'X01';
    } else {
        return gameVariant;
    }
}

function getSelectedStartingScore() {
    const category = document.getElementById('game-category')?.value || 'x01';
    if (category !== 'x01') return 0;
    const variant = document.getElementById('game-variant')?.value || '501';
    if (variant === '20') return 20;
    return parseInt(variant) || 501;
}

function getSelectedRules() {
    const category = document.getElementById('game-category')?.value || 'x01';
    const config = gameConfig[category];
    const rules = {};
    
    if (config?.rules) {
        config.rules.forEach(rule => {
            const checkbox = document.getElementById(`rule-${rule.id}`);
            rules[rule.id] = checkbox?.checked ?? rule.default;
        });
    }
    
    return rules;
}

// ==========================================================================
// Fullscreen
// ==========================================================================

function initFullscreen() {
    const btn = document.getElementById('fullscreen-btn');
    if (btn) {
        btn.addEventListener('click', toggleFullscreen);
    }
    
    // Kiosk mode
    const params = new URLSearchParams(window.location.search);
    if (params.get('kiosk') === '1' || params.get('fullscreen') === '1') {
        const enterFS = () => {
            requestFullscreen();
            document.removeEventListener('click', enterFS);
            document.removeEventListener('touchstart', enterFS);
        };
        document.addEventListener('click', enterFS);
        document.addEventListener('touchstart', enterFS);
    }
}

function requestFullscreen() {
    const el = document.documentElement;
    if (el.requestFullscreen) el.requestFullscreen();
    else if (el.webkitRequestFullscreen) el.webkitRequestFullscreen();
}

function toggleFullscreen() {
    if (!document.fullscreenElement && !document.webkitFullscreenElement) {
        requestFullscreen();
    } else {
        if (document.exitFullscreen) document.exitFullscreen();
        else if (document.webkitExitFullscreen) document.webkitExitFullscreen();
    }
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
    // Ensure mode is a string
    if (typeof mode !== 'string') {
        mode = String(mode || '');
    }
    
    // Handle Debug mode
    if (mode === 'Debug20') return '🐛 Debug 20';

    // Handle X01 games
    const x01Match = mode.match(/^Game(\d+)$/);
    if (x01Match) return x01Match[1];
    
    // Handle other modes
    switch (mode) {
        case 'Practice': 
        case 'CountUp':
        case 'HighScore':
            return 'PRACTICE';
        case 'CricketStandard': return 'CRICKET';
        case 'CricketCutThroat': return 'CUT-THROAT';
        case 'AroundTheClock': return 'AROUND';
        case 'Shanghai': return 'SHANGHAI';
        case 'Killer': return 'KILLER';
        default: return mode.toUpperCase();
    }
}

// ==========================================================================

// Remove a false dart (e.g. phantom detection during clearing)
async function removeDart(dartIndex) {
    if (!currentGame?.id) return;
    
    if (!confirm(`Remove dart ${dartIndex + 1}? (Use for false detections)`)) return;
    
    try {
        const resp = await fetch(`/api/games/${currentGame.id}/remove-dart`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ dartIndex: dartIndex })
        });
        if (!resp.ok) {
            const err = await resp.json();
            console.error('Failed to remove dart:', err);
            return;
        }
        const result = await resp.json();
        console.log('Dart removed:', result);
        // UI updates via SignalR DartRemoved event
    } catch (e) {
        console.error('Error removing dart:', e);
    }
}

// Dart Correction
// ==========================================================================

let correctionDartIndex = null;
let correctionInput = '';  // String input like "20" or "bull"
let correctionMultiplier = 1;  // 1=single, 2=double, 3=triple

function initDartCorrection() {
    // Make dart slots clickable
    document.querySelectorAll('.dart-slot').forEach(slot => {
        slot.addEventListener('click', () => {
            const dartIndex = parseInt(slot.dataset.dart);
            openCorrectionModal(dartIndex);
        });
    });
    
    // Number keypad (0-9)
    document.querySelectorAll('.keypad .key-btn').forEach(btn => {
        btn.addEventListener('click', () => {
            const val = btn.dataset.val;
            appendDigit(val);
        });
    });
    
    // Special buttons (Bull, Miss)
    document.querySelectorAll('.special-btn').forEach(btn => {
        btn.addEventListener('click', () => {
            const special = btn.dataset.special;
            if (special === 'bull') {
                correctionInput = 'bull';
                correctionMultiplier = 1;  // Reset to single bull
            } else if (special === 'miss') {
                correctionInput = 'miss';
                correctionMultiplier = 1;
            }
            updateCorrectionDisplay();
        });
    });
    
    // Modifier buttons (Double, Triple)
    document.querySelectorAll('.mod-btn').forEach(btn => {
        btn.addEventListener('click', () => {
            const mod = btn.dataset.mod;
            // Toggle - if already active, turn off (go back to single)
            if (btn.classList.contains('active')) {
                btn.classList.remove('active');
                correctionMultiplier = 1;
            } else {
                document.querySelectorAll('.mod-btn').forEach(b => b.classList.remove('active'));
                btn.classList.add('active');
                correctionMultiplier = mod === 'double' ? 2 : 3;
            }
            updateCorrectionDisplay();
        });
    });
    
    // Cancel button
    document.getElementById('correction-cancel')?.addEventListener('click', closeCorrectionModal);
    
    // Bounce Out button - mark dart as excluded from benchmark (bounced out, can't correct)
    document.getElementById('correction-bounceout')?.addEventListener('click', markBounceOut);
    
    // False button - remove this dart entirely (phantom detection)
    document.getElementById('correction-false')?.addEventListener('click', removeFalseDart);
    
    // Clear button - resets the input
    document.getElementById('correction-clear')?.addEventListener('click', () => {
        correctionInput = '';
        correctionMultiplier = 1;
        document.querySelectorAll('.mod-btn').forEach(b => b.classList.remove('active'));
        updateCorrectionDisplay();
    });
    
    // Backdrop click to close
    document.querySelector('#dart-correction-modal .modal-backdrop')?.addEventListener('click', closeCorrectionModal);
    
    // Confirm/Enter button
    document.getElementById('correction-confirm')?.addEventListener('click', submitCorrection);
}

function appendDigit(digit) {
    // If input is special (bull/miss), clear it
    if (correctionInput === 'bull' || correctionInput === 'miss') {
        correctionInput = '';
    }
    
    // Build number string
    const newInput = correctionInput + digit;
    const num = parseInt(newInput, 10);
    
    // Valid dart segments are 1-20, but we type 0-9
    // Allow up to 2 digits, max value 20
    if (num <= 20) {
        correctionInput = newInput;
    } else if (digit !== '0' && correctionInput === '') {
        // Single digit 1-9 is valid
        correctionInput = digit;
    }
    
    updateCorrectionDisplay();
}

function getSegmentAndScore() {
    if (correctionInput === 'miss' || correctionInput === '') {
        return { segment: 0, score: 0, display: 'MISS' };
    }
    
    if (correctionInput === 'bull') {
        const score = correctionMultiplier === 2 ? 50 : 25;
        const display = correctionMultiplier === 2 ? 'D-BULL' : 'BULL';
        return { segment: 25, score, display };
    }
    
    const segment = parseInt(correctionInput, 10);
    if (isNaN(segment) || segment < 1 || segment > 20) {
        return { segment: 0, score: 0, display: '—' };
    }
    
    const score = segment * correctionMultiplier;
    const prefix = correctionMultiplier === 3 ? 'T' : correctionMultiplier === 2 ? 'D' : 'S';
    const display = prefix + segment;
    
    return { segment, score, display };
}

function updateCorrectionDisplay() {
    const displayEl = document.getElementById('correction-input-display');
    const { display, score } = getSegmentAndScore();
    
    if (correctionInput === '' && correctionMultiplier === 1) {
        displayEl.textContent = '—';
    } else {
        // Show S12, D20, T19, BULL, D-BULL, MISS
        displayEl.textContent = display;
    }
}

function openCorrectionModal(dartIndex) {
    if (!currentGame) return;
    
    correctionDartIndex = dartIndex;
    correctionInput = '';
    correctionMultiplier = 1;
    
    // Reset UI
    document.querySelectorAll('.mod-btn').forEach(b => b.classList.remove('active'));
    
    document.getElementById('correction-dart-num').textContent = dartIndex + 1;
    document.getElementById('correction-input-display').textContent = '—';
    
    document.getElementById('dart-correction-modal')?.classList.remove('hidden');
}

function closeCorrectionModal() {
    document.getElementById('dart-correction-modal')?.classList.add('hidden');
    correctionDartIndex = null;
}

async function submitCorrection() {
    // Handle "Not Detected" mode - submit as manual throw instead of correction
    if (window._notDetectedMode) {
        window._notDetectedMode = false;
        
        // Reset modal title
        const modalTitle = document.querySelector('#dart-correction-modal .modal-title');
        if (modalTitle) modalTitle.textContent = 'Correct Dart';
        
        let segment, multiplier;
        if (correctionInput === 'miss') {
            segment = 0;
            multiplier = 0;
        } else if (correctionInput === 'bull') {
            segment = 25;
            multiplier = correctionMultiplier;  // 1=outer bull (25), 2=inner bull (50)
        } else {
            segment = parseInt(correctionInput);
            multiplier = correctionMultiplier || 1;
        }
        
        if (isNaN(segment) && correctionInput !== 'miss') {
            console.log('[NOT-DETECTED] Invalid input:', correctionInput);
            return;
        }
        
        console.log('[NOT-DETECTED] Submitting manual dart: S' + segment + 'x' + multiplier);
        
        // Submit as manual throw
        fetch('/api/games/' + currentGame.id + '/manual', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ segment: segment, multiplier: multiplier })
        })
        .then(r => r.json())
        .then(data => {
            console.log('[NOT-DETECTED] Manual dart submitted:', data);
            closeCorrectionModal();
        })
        .catch(err => {
            console.error('[NOT-DETECTED] Failed:', err);
            closeCorrectionModal();
        });
        
        return;  // Don't fall through to normal correction logic
    }
    
    // Original correction logic follows...
    const { segment, score, display } = getSegmentAndScore();
    
    if (correctionDartIndex === null || !currentGame) {
        return;
    }
    
    // Allow miss (segment 0)
    if (correctionInput === '' && segment === 0 && correctionMultiplier === 1) {
        // Nothing entered - treat as cancel or require input
        return;
    }
    
    // Get the original dart for logging
    const originalDart = currentGame.currentTurn?.darts?.[correctionDartIndex];

        // If no dart at this index, use manual entry instead of correct
        if (!originalDart) {
            const { segment, score, display } = getSegmentAndScore();
            if (segment === 0 && correctionInput === '') return;
            try {
                const resp = await fetch(`/api/games/${currentGame.id}/manual`, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ segment: segment, multiplier: correctionMultiplier })
                });
                if (resp.ok) {
                    const result = await resp.json();
                    currentGame = result.game;
                    updateScoreboard();
                    updateCurrentTurn();
                }
            } catch (e) { console.error('Manual dart failed:', e); }
            closeCorrectionModal();
            return;
        }
    const originalSegment = originalDart?.segment || 0;
    const originalMultiplier = originalDart?.multiplier || 1;
    const originalScore = originalDart?.score || 0;
    const originalZone = originalDart?.zone || '';
    
    try {
        const response = await fetch(`/api/games/${currentGame.id}/correct`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                dartIndex: correctionDartIndex,
                segment: segment,
                multiplier: correctionMultiplier
            })
        });
        
        if (!response.ok) {
            const err = await response.json();
            alert(err.error || 'Failed to correct dart');
            return;
        }
        
        const result = await response.json();
        currentGame = result.game;
        
        // Log correction for training data analysis
        const correctionLog = {
            timestamp: new Date().toISOString(),
            gameId: currentGame.id,
            dartIndex: correctionDartIndex,
            original: {
                segment: originalSegment,
                multiplier: originalMultiplier,
                score: originalScore,
                zone: originalZone
            },
            corrected: {
                segment: segment,
                multiplier: correctionMultiplier,
                score: score,
                display: display
            }
        };
        
        // Log to centralized system
        log.info('Correction', `Corrected dart ${correctionDartIndex}: ${correctionLog.original.zone} ${correctionLog.original.segment} → ${correctionLog.corrected.display}`, correctionLog);
        
        // Store corrections in localStorage for later export
        try {
            const corrections = JSON.parse(localStorage.getItem('dart-corrections') || '[]');
            corrections.push(correctionLog);
            localStorage.setItem('dart-corrections', JSON.stringify(corrections));
            console.log(`[CORRECTION] Saved! Total corrections: ${corrections.length}`);
        } catch (e) {
            console.error('[CORRECTION] Failed to save:', e);
            log.error('Correction', 'Failed to save to localStorage', { error: e.message });
        }
        
        updateScoreboard();
        updateCurrentTurn();
        closeCorrectionModal();
        
    } catch (e) {
        console.error('Correction error:', e);
        log.error('Correction', 'Correction failed', { error: e.message });
        alert('Failed to correct dart');
    }
}

async function removeFalseDart() {
    if (correctionDartIndex === null || !currentGame) {
        return;
    }
    
    // Get the dart info for logging
    const removedDart = currentGame.currentTurn?.darts?.[correctionDartIndex];
    if (!removedDart) {
        closeCorrectionModal();
        return;
    }
    
    try {
        const response = await fetch(`/api/games/${currentGame.id}/remove-dart`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                dartIndex: correctionDartIndex
            })
        });
        
        if (!response.ok) {
            const err = await response.json();
            alert(err.error || 'Failed to remove dart');
            return;
        }
        
        const result = await response.json();
        currentGame = result.game;
        
        // Log the removal
        const removalLog = {
            timestamp: new Date().toISOString(),
            gameId: currentGame.id,
            dartIndex: correctionDartIndex,
            removed: {
                segment: removedDart.segment,
                multiplier: removedDart.multiplier,
                score: removedDart.score,
                zone: removedDart.zone
            },
            reason: 'false_detection'
        };
        
        log.info('FalseDart', `Removed false dart ${correctionDartIndex}: ${removedDart.zone} = ${removedDart.score}`, removalLog);
        
        // Store in localStorage
        try {
            const falseDarts = JSON.parse(localStorage.getItem('false-darts') || '[]');
            falseDarts.push(removalLog);
            localStorage.setItem('false-darts', JSON.stringify(falseDarts));
            console.log(`[FALSE DART] Saved! Total false darts: ${falseDarts.length}`);
        } catch (e) {
            console.error('[FALSE DART] Failed to save:', e);
            log.error('FalseDart', 'Failed to save to localStorage', { error: e.message });
        }
        
        updateScoreboard();
        updateCurrentTurn();
        closeCorrectionModal();
        
    } catch (e) {
        console.error('Remove dart error:', e);
        log.error('FalseDart', 'Remove failed', { error: e.message });
        alert('Failed to remove dart');
    }
}

async function markBounceOut() {
    // Mark this dart as "bounce out" - excluded from benchmark training data
    // The dart still happened (scores as 0/miss) but shouldn't be used for detection training
    if (correctionDartIndex === null || !currentGame) {
        return;
    }
    
    const dart = currentGame.currentTurn?.darts?.[correctionDartIndex];
    
    try {
        // Call benchmark API to mark as excluded
        await fetch(`${DETECT_API}/v1/benchmark/exclude-dart`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                game_id: currentGame.id,
                dart_index: correctionDartIndex,
                reason: 'bounce_out'
            })
        }).catch(() => {}); // Don't fail if DartDetect is down
        
        // Also correct the dart to MISS (score 0) since it bounced out
        const response = await fetch(`/api/games/${currentGame.id}/correct`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                dartIndex: correctionDartIndex,
                segment: 0,
                multiplier: 1
            })
        });
        
        if (response.ok) {
            const result = await response.json();
            currentGame = result.game;
        }
        
        log.info('BounceOut', `Marked dart ${correctionDartIndex} as bounce out - excluded from benchmark`, { 
            dart, gameId: currentGame.id 
        });
        
        updateScoreboard();
        updateCurrentTurn();
        closeCorrectionModal();
        
    } catch (e) {
        console.error('Bounce out error:', e);
        log.error('BounceOut', 'Failed to mark bounce out', { error: e.message });
        alert('Failed to mark dart as bounce out');
    }
}

// ==========================================================================
// Player Management
// ==========================================================================

let selectedRegisteredPlayers = [];

function initPlayerManagement() {
    // Add player button - shows popup with options
    document.getElementById('add-player-btn')?.addEventListener('click', openAddPlayerModal);
    
    // Add player modal handlers
    document.getElementById('add-player-close')?.addEventListener('click', closeAddPlayerModal);
    document.querySelector('#add-player-modal .modal-backdrop')?.addEventListener('click', closeAddPlayerModal);
    
    // Add player option buttons
    document.querySelectorAll('.add-player-option').forEach(btn => {
        btn.addEventListener('click', () => {
            const type = btn.dataset.type;
            handleAddPlayerType(type);
        });
    });
    
    // Remove player buttons (delegated)
    document.getElementById('players-list')?.addEventListener('click', (e) => {
        if (e.target.classList.contains('btn-remove-player')) {
            const row = e.target.closest('.player-row');
            if (row && document.querySelectorAll('.player-row').length > 1) {
                row.remove();
                renumberPlayers();
            }
        }
    });
    
    // Open player select modal
    document.getElementById('select-players-btn')?.addEventListener('click', openPlayerSelectModal);
    
    // Close modal
    document.getElementById('player-select-close')?.addEventListener('click', closePlayerSelectModal);
    document.getElementById('player-select-done')?.addEventListener('click', applySelectedPlayers);
    
    // Tab switching
    document.querySelectorAll('.player-select-tabs .tab-btn').forEach(btn => {
        btn.addEventListener('click', () => {
            document.querySelectorAll('.player-select-tabs .tab-btn').forEach(b => b.classList.remove('active'));
            btn.classList.add('active');
            
            document.querySelectorAll('.tab-content').forEach(t => t.classList.add('hidden'));
            document.getElementById(btn.dataset.tab + '-tab')?.classList.remove('hidden');
        });
    });
    
    // Register new player
    document.getElementById('register-player-btn')?.addEventListener('click', registerNewPlayer);
    
    // Modal backdrop click to close
    document.querySelector('#player-select-modal .modal-backdrop')?.addEventListener('click', closePlayerSelectModal);
}

function addPlayerRow() {
    const list = document.getElementById('players-list');
    const count = list.querySelectorAll('.player-row').length + 1;
    
    const row = document.createElement('div');
    row.className = 'player-row';
    row.innerHTML = `
        <input type="text" class="player-input" placeholder="Player ${count}">
        <button class="btn-remove-player" title="Remove">✕</button>
    `;
    list.appendChild(row);
}

function addBotPlayer() {
    const list = document.getElementById('players-list');
    const botNames = ['Bot Easy', 'Bot Medium', 'Bot Hard', 'Bot Pro'];
    const existingBots = Array.from(list.querySelectorAll('.player-input'))
        .filter(i => i.value.startsWith('Bot')).length;
    const botName = botNames[Math.min(existingBots, botNames.length - 1)];
    
    const row = document.createElement('div');
    row.className = 'player-row';
    row.innerHTML = `
        <input type="text" class="player-input" value="${botName}" data-is-bot="true">
        <button class="btn-remove-player" title="Remove">✕</button>
    `;
    list.appendChild(row);
}

function openAddPlayerModal() {
    document.getElementById('add-player-modal')?.classList.remove('hidden');
}

function closeAddPlayerModal() {
    document.getElementById('add-player-modal')?.classList.add('hidden');
}

function handleAddPlayerType(type) {
    closeAddPlayerModal();
    
    switch (type) {
        case 'basic':
            addPlayerRow();
            break;
        case 'bot':
            addBotPlayer();
            break;
        case 'registered':
            openPlayerSelectModal();
            break;
        case 'register-new':
            openPlayerSelectModal();
            // Switch to register tab
            setTimeout(() => {
                document.querySelector('.tab-btn[data-tab="register"]')?.click();
            }, 100);
            break;
    }
}

function renumberPlayers() {
    document.querySelectorAll('.player-row').forEach((row, i) => {
        const input = row.querySelector('.player-input');
        if (input && !input.value) {
            input.placeholder = `Player ${i + 1}`;
        }
    });
}

async function openPlayerSelectModal() {
    const modal = document.getElementById('player-select-modal');
    modal?.classList.remove('hidden');
    selectedRegisteredPlayers = [];
    await loadRegisteredPlayers();
}

function closePlayerSelectModal() {
    document.getElementById('player-select-modal')?.classList.add('hidden');
}

async function loadRegisteredPlayers() {
    const listEl = document.getElementById('registered-players-list');
    if (!listEl) return;
    
    listEl.innerHTML = '<p class="loading">Loading players...</p>';
    
    try {
        const response = await fetch('/api/players');
        if (!response.ok) throw new Error('Failed to load');
        
        const players = await response.json();
        
        if (players.length === 0) {
            listEl.innerHTML = '<p class="no-players">No registered players yet. Create one in the "New Player" tab.</p>';
            return;
        }
        
        listEl.innerHTML = players.map(p => `
            <div class="registered-player" data-id="${p.id}" data-nickname="${escapeHtml(p.nickname)}">
                <div class="avatar">${p.nickname.charAt(0).toUpperCase()}</div>
                <div class="info">
                    <div class="nickname">${escapeHtml(p.nickname)}</div>
                    <div class="stats">${p.gamesPlayed || 0} games played</div>
                </div>
                <span class="check">✓</span>
            </div>
        `).join('');
        
        // Click to select
        listEl.querySelectorAll('.registered-player').forEach(el => {
            el.addEventListener('click', () => {
                el.classList.toggle('selected');
                const id = el.dataset.id;
                const nickname = el.dataset.nickname;
                
                if (el.classList.contains('selected')) {
                    selectedRegisteredPlayers.push({ id, nickname });
                } else {
                    selectedRegisteredPlayers = selectedRegisteredPlayers.filter(p => p.id !== id);
                }
            });
        });
        
    } catch (err) {
        console.error('Error loading players:', err);
        listEl.innerHTML = '<p class="no-players">Error loading players. Is the API running?</p>';
    }
}

function applySelectedPlayers() {
    if (selectedRegisteredPlayers.length === 0) {
        closePlayerSelectModal();
        return;
    }
    
    const list = document.getElementById('players-list');
    list.innerHTML = '';
    
    selectedRegisteredPlayers.forEach((player, i) => {
        const row = document.createElement('div');
        row.className = 'player-row';
        row.innerHTML = `
            <input type="text" class="player-input" value="${escapeHtml(player.nickname)}" data-player-id="${player.id}">
            ${i > 0 ? '<button class="btn-remove-player" title="Remove">✕</button>' : ''}
        `;
        list.appendChild(row);
    });
    
    closePlayerSelectModal();
}

async function registerNewPlayer() {
    const nickname = document.getElementById('reg-nickname')?.value.trim();
    const email = document.getElementById('reg-email')?.value.trim();
    
    if (!nickname) {
        alert('Please enter a nickname');
        return;
    }
    
    try {
        const response = await fetch('/api/players', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ nickname, email })
        });
        
        if (!response.ok) {
            const error = await response.text();
            alert('Error: ' + error);
            return;
        }
        
        // Clear form
        document.getElementById('reg-nickname').value = '';
        document.getElementById('reg-email').value = '';
        
        // Switch to registered tab and reload
        document.querySelector('.tab-btn[data-tab="registered"]')?.click();
        await loadRegisteredPlayers();
        
    } catch (err) {
        console.error('Error registering player:', err);
        alert('Failed to register player');
    }
}
// ==========================================================================
// Online Play
// ==========================================================================

let onlineConnection = null;
let currentMatch = null;
let onlinePlayer = null;

async function initOnlinePlay() {
    // Online play button in setup
    document.getElementById('online-play-btn')?.addEventListener('click', openOnlineLobby);
    document.getElementById('online-lobby-close')?.addEventListener('click', closeOnlineLobby);
    
    // Create match
    document.getElementById('create-match-btn')?.addEventListener('click', createOnlineMatch);
    
    // Join match
    document.getElementById('join-match-btn')?.addEventListener('click', () => {
        const code = document.getElementById('match-code-input')?.value.trim();
        if (code) joinOnlineMatch(code);
    });
    
    // Start match (host only)
    document.getElementById('start-online-match-btn')?.addEventListener('click', startOnlineMatch);
    
    // Leave match
    document.getElementById('leave-match-btn')?.addEventListener('click', leaveOnlineMatch);
    
    // Chat
    document.getElementById('online-chat-send')?.addEventListener('click', sendOnlineChat);
    document.getElementById('online-chat-input')?.addEventListener('keypress', (e) => {
        if (e.key === 'Enter') sendOnlineChat();
    });
    
    // Looking for match toggle
    document.getElementById('looking-for-match-toggle')?.addEventListener('change', toggleLookingForMatch);
    
    // Fetch online count for footer display
    fetchOnlineCount();
    setInterval(fetchOnlineCount, 30000); // Refresh every 30s
}

async function fetchOnlineCount() {
    try {
        const r = await fetch('/api/online/status');
        if (r.ok) {
            const data = await r.json();
            const el = document.getElementById('online-count-display');
            if (el) el.textContent = data.onlineCount || 0;
        }
    } catch (e) {
        console.log('Could not fetch online count');
    }
}

// Toggle looking for match - stays in queue while playing locally
async function toggleLookingForMatch(e) {
    lookingForMatch = e.target.checked;
    const statusEl = document.getElementById('match-search-status');
    
    if (lookingForMatch) {
        // Connect to online hub and join queue
        try {
            await connectOnlineHub();
            await onlineConnection.invoke('JoinMatchmakingQueue', 'Game501', 'anyone', null, null);
            if (statusEl) {
                statusEl.textContent = '🔍 Searching for opponent...';
                statusEl.classList.add('active');
            }
        } catch (err) {
            console.error('Failed to join queue:', err);
            e.target.checked = false;
            lookingForMatch = false;
        }
    } else {
        // Leave queue
        try {
            await onlineConnection?.invoke('LeaveMatchmakingQueue');
            if (statusEl) {
                statusEl.textContent = '';
                statusEl.classList.remove('active');
            }
        } catch (err) {
            console.error('Failed to leave queue:', err);
        }
    }
}

async function connectOnlineHub() {
    if (onlineConnection?.state === signalR.HubConnectionState.Connected) {
        return onlineConnection;
    }

    onlineConnection = new signalR.HubConnectionBuilder()
        .withUrl('/onlinehub')
        .withAutomaticReconnect()
        .configureLogging(signalR.LogLevel.Information)
        .build();

    // Event handlers
    onlineConnection.on('Registered', (data) => {
        onlinePlayer = data;
        console.log('Registered as:', data.displayName);
    });

    onlineConnection.on('MatchCreated', (data) => {
        currentMatch = data;
        showMatchLobby(data, true);
    });

    onlineConnection.on('PlayerJoined', (data) => {
        updateMatchPlayers(data.players);
        addChatMessage('System', `${data.displayName} joined the match`);
    });

    onlineConnection.on('PlayerLeft', (data) => {
        addChatMessage('System', `${data.displayName} left the match`);
    });

    onlineConnection.on('MatchStarted', (data) => {
        currentMatch = data;
        closeOnlineLobby();
        startOnlineGame(data);
    });

    onlineConnection.on('DartRelayed', (data) => {
        handleOnlineDart(data);
    });

    onlineConnection.on('ScoreUpdated', (data) => {
        updateOnlineScore(data);
    });

    onlineConnection.on('TurnEnded', (data) => {
        handleOnlineTurnEnd(data);
    });

    onlineConnection.on('LegWon', (data) => {
        addChatMessage('System', `🎯 ${data.winnerName} won the leg!`);
    });

    onlineConnection.on('MatchWon', (data) => {
        showOnlineWinner(data);
    });

    onlineConnection.on('ChatMessage', (data) => {
        addChatMessage(data.displayName, data.message);
    });

    onlineConnection.on('OpenMatches', (matches) => {
        displayOpenMatches(matches);
    });

    onlineConnection.on('Error', (message) => {
        alert('Error: ' + message);
    });

    try {
        await onlineConnection.start();
        console.log('Connected to online hub');
        
        // Register with display name
        const playerName = document.querySelector('.player-input')?.value || 'Player';
        await onlineConnection.invoke('Register', playerName);
        
        return onlineConnection;
    } catch (err) {
        console.error('Failed to connect to online hub:', err);
        throw err;
    }
}

function openOnlineLobby() {
    document.getElementById('online-lobby-modal')?.classList.remove('hidden');
    connectOnlineHub().then(() => {
        onlineConnection.invoke('GetOpenMatches');
    });
}

function closeOnlineLobby() {
    document.getElementById('online-lobby-modal')?.classList.add('hidden');
}

async function createOnlineMatch() {
    if (!onlineConnection) await connectOnlineHub();
    
    const gameMode = getSelectedGameMode();
    const bestOf = selectedBestOf;
    
    await onlineConnection.invoke('CreateMatch', gameMode, bestOf);
}

async function joinOnlineMatch(code) {
    if (!onlineConnection) await connectOnlineHub();
    await onlineConnection.invoke('JoinMatch', code);
}

function showMatchLobby(match, isHost) {
    document.getElementById('lobby-browse')?.classList.add('hidden');
    document.getElementById('lobby-match')?.classList.remove('hidden');
    
    document.getElementById('match-code-display').textContent = match.matchCode;
    document.getElementById('match-game-mode').textContent = formatMode(match.gameMode);
    document.getElementById('match-best-of').textContent = `Best of ${match.bestOf}`;
    
    // Show/hide start button based on host status
    const startBtn = document.getElementById('start-online-match-btn');
    if (startBtn) {
        startBtn.style.display = isHost ? '' : 'none';
    }
}

function updateMatchPlayers(players) {
    const listEl = document.getElementById('match-players-list');
    if (!listEl) return;
    
    listEl.innerHTML = players.map(p => `
        <div class="match-player ${p.isHost ? 'host' : ''}">
            <span class="player-name">${escapeHtml(p.displayName)}</span>
            ${p.isHost ? '<span class="host-badge">HOST</span>' : ''}
        </div>
    `).join('');
    
    // Enable start if 2 players
    const startBtn = document.getElementById('start-online-match-btn');
    if (startBtn && players.length >= 2) {
        startBtn.disabled = false;
    }
}

async function startOnlineMatch() {
    if (!onlineConnection) return;
    await onlineConnection.invoke('StartMatch');
}

async function leaveOnlineMatch() {
    if (!onlineConnection) return;
    await onlineConnection.invoke('LeaveMatch');
    
    document.getElementById('lobby-match')?.classList.add('hidden');
    document.getElementById('lobby-browse')?.classList.remove('hidden');
    currentMatch = null;
}

function displayOpenMatches(matches) {
    const listEl = document.getElementById('open-matches-list');
    if (!listEl) return;
    
    if (matches.length === 0) {
        listEl.innerHTML = '<p class="no-matches">No open matches. Create one!</p>';
        return;
    }
    
    listEl.innerHTML = matches.map(m => `
        <div class="open-match" onclick="joinOnlineMatch('${m.matchCode}')">
            <div class="match-info">
                <span class="match-host">${escapeHtml(m.hostName)}'s Game</span>
                <span class="match-mode">${formatMode(m.gameMode)} • Best of ${m.bestOf}</span>
            </div>
            <button class="btn-join">Join</button>
        </div>
    `).join('');
}

function startOnlineGame(matchData) {
    // Set up game with online players
    isOnlineGame = true;
    
    // Create game via API but mark as online
    const players = matchData.players.map(p => p.displayName);
    
    fetch('/api/games', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
            boardId: boardId,
            mode: matchData.gameMode,
            playerNames: players,
            bestOf: matchData.bestOf,
            isOnline: true,
            matchCode: matchData.matchCode
        })
    }).then(r => r.json()).then(game => {
        currentGame = game;
        showScreen('game-screen');
        updateScoreboard();
        updateCurrentTurn();
    });
}

function handleOnlineDart(data) {
    // Show opponent's dart throw
    if (data.playerId !== onlinePlayer?.playerId) {
        showThrowPopup({
            zone: data.zone,
            score: data.score
        });
        addChatMessage('System', `${data.playerName} threw ${data.zone} ${data.segment} (${data.score})`);
    }
}

function updateOnlineScore(data) {
    // Update opponent's score in UI
    if (currentGame) {
        const player = currentGame.players.find(p => p.id === data.playerId || p.name === data.playerId);
        if (player) {
            player.score = data.score;
            player.dartsThrown = data.dartsThrown;
            player.legsWon = data.legsWon;
            updateScoreboard();
        }
    }
}

function handleOnlineTurnEnd(data) {
    if (currentGame) {
        currentGame.currentPlayerIndex = currentGame.players.findIndex(
            p => p.name === data.nextPlayerName
        );
        updateScoreboard();
        clearCurrentTurn();
    }
}

function showOnlineWinner(data) {
    document.getElementById('winner-name').textContent = data.winnerName;
    document.getElementById('winner-stats').textContent = `Won ${data.legsWon} legs`;
    showScreen('gameover-screen');
}

// Relay local dart throws to online opponents
async function relayDartToOnline(dart) {
    if (!onlineConnection || !currentMatch) return;
    
    await onlineConnection.invoke('RelayDart', 
        dart.segment, 
        dart.multiplier, 
        dart.score, 
        dart.zone
    );
}

async function relayScoreToOnline(player) {
    if (!onlineConnection || !currentMatch) return;
    
    await onlineConnection.invoke('RelayScoreUpdate',
        player.id || player.name,
        player.score,
        player.dartsThrown,
        player.legsWon || 0
    );
}

async function relayTurnEndToOnline(turnScore, busted) {
    if (!onlineConnection || !currentMatch) return;
    
    await onlineConnection.invoke('RelayTurnEnd', turnScore, busted);
}

function addChatMessage(sender, message) {
    const chatEl = document.getElementById('online-chat-messages');
    if (!chatEl) return;
    
    const msgEl = document.createElement('div');
    msgEl.className = 'chat-message';
    msgEl.innerHTML = `<span class="sender">${escapeHtml(sender)}:</span> ${escapeHtml(message)}`;
    chatEl.appendChild(msgEl);
    chatEl.scrollTop = chatEl.scrollHeight;
}

async function sendOnlineChat() {
    const input = document.getElementById('online-chat-input');
    const message = input?.value.trim();
    
    if (!message || !onlineConnection) return;
    
    await onlineConnection.invoke('SendChat', message);
    input.value = '';
}

let isOnlineGame = false;

// ==========================================================================
// Debug/Training Data Export
// ==========================================================================

function exportCorrections() {
    const corrections = JSON.parse(localStorage.getItem('dart-corrections') || '[]');
    if (corrections.length === 0) {
        console.log('No corrections to export');
        return null;
    }
    
    const blob = new Blob([JSON.stringify(corrections, null, 2)], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `dart-corrections-${new Date().toISOString().split('T')[0]}.json`;
    a.click();
    URL.revokeObjectURL(url);
    
    console.log(`Exported ${corrections.length} corrections`);
    return corrections;
}

function clearCorrections() {
    localStorage.removeItem('dart-corrections');
    console.log('Corrections cleared');
}

function getCorrections() {
    return JSON.parse(localStorage.getItem('dart-corrections') || '[]');
}

// Make available in console
window.exportCorrections = exportCorrections;
window.clearCorrections = clearCorrections;
window.getCorrections = getCorrections;



// ==========================================================================
// Dart Not Detected
// ==========================================================================
// When a dart is thrown but the cameras don't detect it, this button
// opens the correction modal so the player can manually enter the score.
// The dart is submitted as a manual throw to keep dart count in sync.
// This preserves benchmark data integrity - we know a dart was missed.

function dartNotDetected() {
    if (!currentGame || currentGame.state !== 1) {
        console.log('[NOT-DETECTED] No active game');
        return;
    }
    
    const dartsThrown = currentGame.currentTurn?.darts?.length || 0;
    if (dartsThrown >= 3) {
        console.log('[NOT-DETECTED] Already 3 darts this turn');
        return;
    }
    
    console.log('[NOT-DETECTED] Opening correction modal for undetected dart ' + (dartsThrown + 1));
    
    // Set up correction state for next dart index
    correctionDartIndex = dartsThrown;  // 0, 1, or 2
    correctionInput = '';
    correctionMultiplier = 1;
    
    // Flag this as a "not detected" manual entry (not a correction of existing dart)
    window._notDetectedMode = true;
    
    // Update modal title to indicate this is a manual entry
    const modalTitle = document.querySelector('#dart-correction-modal .modal-title');
    if (modalTitle) {
        modalTitle.textContent = '🚫 Dart Not Detected - Enter Score';
    }
    
    // Open the correction modal
    const modal = document.getElementById('dart-correction-modal');
    if (modal) {
        modal.classList.remove('hidden');
        updateCorrectionDisplay();
    }
}

// Override submitCorrection to handle not-detected mode
const _originalSubmitCorrection = typeof submitCorrection === 'function' ? submitCorrection : null;

// ===== Dart Not Found Toast =====
// Shows a toast notification when DartSensor detects motion but DartDetect
// can't find a dart. Prompts the user to use the manual entry button.
function handleDartNotFound(data) {
    console.log('Motion detected but no dart found');
    showToast('Dart not scored \u2014 use \ud83d\udeab to enter manually', 'warning');
}

// ===== Toast Notification Helper =====
// Creates a temporary floating notification that auto-dismisses after 3 seconds.
function showToast(message, type = 'info') {
    const toast = document.createElement('div');
    toast.className = `toast-notification toast-${type}`;
    toast.textContent = message;
    toast.style.cssText = `
        position: fixed;
        top: 20px;
        left: 50%;
        transform: translateX(-50%);
        padding: 12px 24px;
        border-radius: 8px;
        color: white;
        font-weight: bold;
        z-index: 10000;
        animation: fadeInOut 3s ease-in-out;
        background: ${type === 'warning' ? '#e67e22' : '#3498db'};
        box-shadow: 0 4px 12px rgba(0,0,0,0.3);
    `;
    document.body.appendChild(toast);
    setTimeout(() => toast.remove(), 3000);
}
