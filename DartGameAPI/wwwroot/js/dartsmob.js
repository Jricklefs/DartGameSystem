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
    
    // Get game mode and rules from dropdowns
    const gameMode = getSelectedGameMode();
    const rules = getSelectedRules();
    
    try {
        const response = await fetch('/api/games', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                boardId,
                mode: gameMode,
                playerNames: players,
                bestOf: selectedBestOf,
                rules: rules
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
            { value: '301', label: '301' },
            { value: '501', label: '501' },
            { value: '701', label: '701' },
            { value: '1001', label: '1001' }
        ],
        defaultVariant: '501',
        rules: [
            { id: 'double-in', label: 'Double In', default: false },
            { id: 'double-out', label: 'Double Out', default: true }
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
    
    // End game
    document.getElementById('end-game-btn')?.addEventListener('click', endGame);
    
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
    
    // Use variant if selected, otherwise default
    const gameVariant = variant || config?.defaultVariant || '501';
    
    if (category === 'x01') {
        return `Game${gameVariant}`;
    } else {
        return gameVariant;
    }
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
    const { segment, score, display } = getSegmentAndScore();
    
    if (correctionDartIndex === null || !currentGame) {
        return;
    }
    
    // Allow miss (segment 0)
    if (correctionInput === '' && segment === 0 && correctionMultiplier === 1) {
        // Nothing entered - treat as cancel or require input
        return;
    }
    
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
        updateScoreboard();
        updateCurrentTurn();
        closeCorrectionModal();
        
    } catch (e) {
        console.error('Correction error:', e);
        alert('Failed to correct dart');
    }
}
// ==========================================================================
// Player Management
// ==========================================================================

let selectedRegisteredPlayers = [];

function initPlayerManagement() {
    // Add player button - now creates a row with remove button
    document.getElementById('add-player-btn')?.addEventListener('click', addPlayerRow);
    
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
