/**
 * DartsMob - Main Game JavaScript
 * Mobile-first redesign
 */

// ==========================================================================
// Configuration
// ==========================================================================

const DETECT_API = 'http://192.168.0.158:8000';
const boardId = 'default';

// ==========================================================================
// State
// ==========================================================================

let connection = null;
let currentGame = null;
let selectedMode = 'Practice';
let selectedBestOf = 5;

// ==========================================================================
// Initialization
// ==========================================================================

document.addEventListener('DOMContentLoaded', () => {
    initTheme();
    initConnection();
    initEventListeners();
    initFullscreen();
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
        '/images/backgrounds/speakeasy1.jpg',
        '/images/backgrounds/speakeasy2.jpg',
        '/images/backgrounds/speakeasy3.jpg'
    ];
    
    if (backgrounds.length > 0) {
        setBackground(backgrounds[0]);
        
        // Start slideshow if enabled
        if (theme.slideshow !== false && backgrounds.length > 1) {
            let idx = 0;
            setInterval(() => {
                idx = (idx + 1) % backgrounds.length;
                setBackground(backgrounds[idx]);
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
    connection.on('BoardCleared', handleBoardCleared);
    connection.on('GameStarted', handleGameStarted);
    connection.on('GameEnded', handleGameEnded);
    connection.on('TurnEnded', handleTurnEnded);

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

function handleDartThrown(data) {
    console.log('Dart thrown:', data);
    currentGame = data.game;
    updateScoreboard();
    updateCurrentTurn();
    showThrowPopup(data.dart);
}

function handleBoardCleared(data) {
    console.log('Board cleared');
}

function handleGameStarted(data) {
    console.log('Game started:', data);
    currentGame = data;
    showScreen('game-screen');
    updateScoreboard();
    updateCurrentTurn();
}

function handleGameEnded(data) {
    console.log('Game ended:', data);
    document.getElementById('winner-name').textContent = data.winnerName || 'Winner';
    document.getElementById('winner-stats').textContent = `${data.darts || '?'} darts`;
    showScreen('gameover-screen');
}

function handleTurnEnded(data) {
    console.log('Turn ended');
    clearCurrentTurn();
}

// ==========================================================================
// UI Updates
// ==========================================================================

function showScreen(screenId) {
    document.querySelectorAll('.screen').forEach(s => s.classList.add('hidden'));
    document.getElementById(screenId)?.classList.remove('hidden');
    
    // Show/hide header based on screen
    const header = document.getElementById('main-header');
    if (header) {
        header.style.display = screenId === 'setup-screen' ? 'flex' : 'none';
    }
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
            slot.classList.add('hit');
            slot.textContent = darts[i].score;
        } else {
            slot.classList.remove('hit');
            slot.textContent = '—';
        }
    });
    
    document.getElementById('turn-score').textContent = currentGame.currentTurn.turnScore || 0;
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
    
    popup.querySelector('.throw-zone').textContent = dart.zone?.toUpperCase() || '';
    popup.querySelector('.throw-value').textContent = dart.score;
    
    popup.classList.remove('hidden', 'show');
    void popup.offsetWidth; // Force reflow
    popup.classList.add('show');
    
    setTimeout(() => {
        popup.classList.add('hidden');
        popup.classList.remove('show');
    }, 1500);
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
                boardId,
                mode: selectedMode,
                playerNames: players,
                bestOf: selectedBestOf
            })
        });
        
        if (response.ok) {
            currentGame = await response.json();
            showScreen('game-screen');
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

// ==========================================================================
// Event Listeners
// ==========================================================================

function initEventListeners() {
    // Game mode buttons
    document.querySelectorAll('.mode-btn').forEach(btn => {
        btn.addEventListener('click', () => {
            document.querySelectorAll('.mode-btn').forEach(b => b.classList.remove('active'));
            btn.classList.add('active');
            selectedMode = btn.dataset.mode;
            
            // Show/hide legs selector
            const legsSection = document.getElementById('legs-section');
            if (legsSection) {
                legsSection.style.display = selectedMode === 'Practice' ? 'none' : '';
            }
        });
    });
    
    // Legs buttons
    document.querySelectorAll('.legs-btn').forEach(btn => {
        btn.addEventListener('click', () => {
            document.querySelectorAll('.legs-btn').forEach(b => b.classList.remove('active'));
            btn.classList.add('active');
            selectedBestOf = parseInt(btn.dataset.legs);
        });
    });
    
    // Add player
    document.getElementById('add-player-btn')?.addEventListener('click', () => {
        const list = document.getElementById('players-list');
        const count = list.querySelectorAll('.player-input').length + 1;
        const input = document.createElement('input');
        input.type = 'text';
        input.className = 'player-input';
        input.placeholder = `Player ${count}`;
        list.appendChild(input);
    });
    
    // Start game
    document.getElementById('start-game-btn')?.addEventListener('click', startGame);
    
    // End game
    document.getElementById('end-game-btn')?.addEventListener('click', endGame);
    
    // New game
    document.getElementById('new-game-btn')?.addEventListener('click', () => {
        showScreen('setup-screen');
    });
    
    // Hide legs section initially for Practice mode
    const legsSection = document.getElementById('legs-section');
    if (legsSection) {
        legsSection.style.display = 'none';
    }
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
    switch (mode) {
        case 'Practice': return 'PRACTICE';
        case 'Game501': return '501';
        case 'Game301': return '301';
        case 'Cricket': return 'CRICKET';
        default: return mode;
    }
}
