// === preflight.js - Add to wwwroot/js/ ===
// Pre-flight check and status display for game start

class PreflightChecker {
    constructor(boardId = 'default') {
        this.boardId = boardId;
        this.statusElement = null;
        this.startButton = null;
        this.pollingInterval = null;
    }

    init(statusElementId, startButtonId) {
        this.statusElement = document.getElementById(statusElementId);
        this.startButton = document.getElementById(startButtonId);
        
        // Initial check
        this.check();
        
        // Poll every 3 seconds for status updates
        this.pollingInterval = setInterval(() => this.check(), 3000);
    }

    stop() {
        if (this.pollingInterval) {
            clearInterval(this.pollingInterval);
            this.pollingInterval = null;
        }
    }

    async check() {
        try {
            const response = await fetch(`/api/games/preflight/${this.boardId}`);
            const result = await response.json();
            this.updateUI(result);
            return result;
        } catch (error) {
            console.error('Preflight check failed:', error);
            this.showError('Unable to check system status');
            return null;
        }
    }

    updateUI(result) {
        if (!this.statusElement) return;

        const html = `
            <div class="preflight-panel ${result.canStart ? 'ready' : 'not-ready'}">
                <h4>System Status</h4>
                <div class="status-items">
                    ${this.renderStatusItem('📷', 'Cameras', 
                        result.cameraCount > 0 ? `${result.calibratedCount}/${result.cameraCount} calibrated` : 'None registered',
                        result.cameraCount > 0 && result.calibratedCount === result.cameraCount)}
                    ${this.renderStatusItem('🎯', 'Calibration', 
                        result.calibratedCount === result.cameraCount && result.cameraCount > 0 ? 'Ready' : 'Incomplete',
                        result.calibratedCount === result.cameraCount && result.cameraCount > 0)}
                    ${this.renderStatusItem('📡', 'Sensor', 
                        result.sensorConnected ? 'Connected' : 'Disconnected',
                        result.sensorConnected)}
                </div>
                ${result.issues.length > 0 ? this.renderIssues(result.issues) : ''}
            </div>
        `;
        
        this.statusElement.innerHTML = html;

        // Update start button
        if (this.startButton) {
            this.startButton.disabled = !result.canStart;
            this.startButton.title = result.canStart ? 'Start Game' : result.issues.map(i => i.message).join(', ');
        }
    }

    renderStatusItem(icon, label, value, isOk) {
        return `
            <div class="status-item ${isOk ? 'ok' : 'error'}">
                <span class="status-icon">${icon}</span>
                <span class="status-label">${label}</span>
                <span class="status-value">${value}</span>
                <span class="status-indicator">${isOk ? '✓' : '✗'}</span>
            </div>
        `;
    }

    renderIssues(issues) {
        return `
            <div class="preflight-issues">
                ${issues.map(issue => `
                    <div class="issue ${issue.severity}">
                        <span class="issue-icon">${issue.severity === 'error' ? '⚠️' : 'ℹ️'}</span>
                        <span class="issue-message">${issue.message}</span>
                    </div>
                `).join('')}
                <a href="/settings.html" class="fix-link">Go to Settings →</a>
            </div>
        `;
    }

    showError(message) {
        if (!this.statusElement) return;
        this.statusElement.innerHTML = `
            <div class="preflight-panel error">
                <span class="error-icon">⚠️</span>
                <span class="error-message">${message}</span>
            </div>
        `;
    }
}

// CSS styles to add to dartsmob.css
const preflightStyles = `
.preflight-panel {
    background: rgba(0, 0, 0, 0.7);
    border-radius: 8px;
    padding: 15px 20px;
    margin-bottom: 20px;
    border: 2px solid #333;
}

.preflight-panel.ready {
    border-color: #4CAF50;
}

.preflight-panel.not-ready {
    border-color: #f44336;
}

.preflight-panel h4 {
    margin: 0 0 15px 0;
    color: #ccc;
    font-size: 14px;
    text-transform: uppercase;
    letter-spacing: 1px;
}

.status-items {
    display: flex;
    flex-direction: column;
    gap: 10px;
}

.status-item {
    display: flex;
    align-items: center;
    gap: 10px;
    padding: 8px 12px;
    background: rgba(255, 255, 255, 0.05);
    border-radius: 4px;
}

.status-item.ok {
    border-left: 3px solid #4CAF50;
}

.status-item.error {
    border-left: 3px solid #f44336;
}

.status-icon {
    font-size: 18px;
}

.status-label {
    color: #999;
    min-width: 100px;
}

.status-value {
    color: #fff;
    flex: 1;
}

.status-indicator {
    font-size: 16px;
    font-weight: bold;
}

.status-item.ok .status-indicator {
    color: #4CAF50;
}

.status-item.error .status-indicator {
    color: #f44336;
}

.preflight-issues {
    margin-top: 15px;
    padding-top: 15px;
    border-top: 1px solid #333;
}

.issue {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 8px;
    margin-bottom: 8px;
    border-radius: 4px;
}

.issue.error {
    background: rgba(244, 67, 54, 0.2);
    color: #ff8a80;
}

.issue.warning {
    background: rgba(255, 152, 0, 0.2);
    color: #ffcc80;
}

.fix-link {
    display: inline-block;
    margin-top: 10px;
    color: #64b5f6;
    text-decoration: none;
}

.fix-link:hover {
    text-decoration: underline;
}

button:disabled {
    opacity: 0.5;
    cursor: not-allowed;
}
`;

// Export for use
if (typeof module !== 'undefined') {
    module.exports = { PreflightChecker };
}
