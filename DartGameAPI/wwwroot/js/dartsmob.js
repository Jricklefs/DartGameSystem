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
    initDartCorrection();
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
    
    // Next turn
    document.getElementById('next-turn-btn')?.addEventListener('click', nextTurn);
    
    // New game
    document.getElementById('new-game-btn')?.addEventListener('click', () => {
        showScreen('setup-screen');
    });
    
    // Initialize game selection state
    const legsSection = document.getElementById('legs-section');
    const categorySelect = document.getElementById('game-category');
    if (legsSection && categorySelect) {
        legsSection.style.display = categorySelect.value === 'practice' ? 'none' : '';
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