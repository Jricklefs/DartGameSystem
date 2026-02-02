// === camera-manager.js - Add to wwwroot/js/ ===
// Camera registration and calibration management for settings page

class CameraManager {
    constructor(boardId = 'default') {
        this.boardId = boardId;
        this.cameras = [];
        this.containerElement = null;
    }

    init(containerId) {
        this.containerElement = document.getElementById(containerId);
        this.load();
    }

    async load() {
        try {
            const response = await fetch(`/api/boards/${this.boardId}/cameras`);
            this.cameras = await response.json();
            this.render();
        } catch (error) {
            console.error('Failed to load cameras:', error);
            this.showError('Failed to load cameras');
        }
    }

    async register(cameraId, deviceIndex, displayName) {
        try {
            const response = await fetch(`/api/boards/${this.boardId}/cameras`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ cameraId, deviceIndex, displayName })
            });
            
            if (!response.ok) {
                const error = await response.json();
                throw new Error(error.error || 'Failed to register camera');
            }
            
            await this.load();
            return true;
        } catch (error) {
            console.error('Failed to register camera:', error);
            alert(error.message);
            return false;
        }
    }

    async remove(cameraId) {
        if (!confirm(`Remove camera ${cameraId}?`)) return;
        
        try {
            const response = await fetch(`/api/boards/${this.boardId}/cameras/${cameraId}`, {
                method: 'DELETE'
            });
            
            if (!response.ok) {
                throw new Error('Failed to remove camera');
            }
            
            await this.load();
        } catch (error) {
            console.error('Failed to remove camera:', error);
            alert(error.message);
        }
    }

    async calibrate(cameraId) {
        // This triggers the existing calibration flow
        // The calibration endpoint will update camera status on completion
        const statusEl = document.getElementById(`cam-status-${cameraId}`);
        if (statusEl) {
            statusEl.innerHTML = '<span class="calibrating">Calibrating...</span>';
        }
        
        try {
            // Call the existing calibration endpoint
            const response = await fetch(`/api/calibrate/${cameraId}`, {
                method: 'POST'
            });
            
            if (!response.ok) {
                throw new Error('Calibration failed');
            }
            
            const result = await response.json();
            
            // Update camera calibration status
            await fetch(`/api/boards/${this.boardId}/cameras/${cameraId}/calibration`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    isCalibrated: result.success,
                    quality: result.quality
                })
            });
            
            await this.load();
        } catch (error) {
            console.error('Calibration failed:', error);
            if (statusEl) {
                statusEl.innerHTML = '<span class="error">Calibration failed</span>';
            }
        }
    }

    render() {
        if (!this.containerElement) return;

        const html = `
            <div class="camera-manager">
                <div class="camera-header">
                    <h3>Cameras</h3>
                    <button class="btn-add-camera" onclick="cameraManager.showAddDialog()">
                        + Add Camera
                    </button>
                </div>
                
                <div class="camera-list">
                    ${this.cameras.length === 0 ? 
                        '<p class="no-cameras">No cameras registered. Click "Add Camera" to get started.</p>' :
                        this.cameras.map(cam => this.renderCamera(cam)).join('')
                    }
                </div>
                
                <div class="camera-summary">
                    ${this.renderSummary()}
                </div>
            </div>
            
            <!-- Add Camera Dialog -->
            <div id="add-camera-dialog" class="dialog hidden">
                <div class="dialog-content">
                    <h4>Add Camera</h4>
                    <div class="form-group">
                        <label>Camera ID</label>
                        <input type="text" id="new-camera-id" placeholder="cam0" />
                    </div>
                    <div class="form-group">
                        <label>Device Index (USB)</label>
                        <input type="number" id="new-device-index" value="0" min="0" />
                    </div>
                    <div class="form-group">
                        <label>Display Name (optional)</label>
                        <input type="text" id="new-display-name" placeholder="Left Camera" />
                    </div>
                    <div class="dialog-buttons">
                        <button onclick="cameraManager.hideAddDialog()">Cancel</button>
                        <button class="primary" onclick="cameraManager.addCamera()">Add</button>
                    </div>
                </div>
            </div>
        `;
        
        this.containerElement.innerHTML = html;
    }

    renderCamera(cam) {
        const qualityPct = cam.calibrationQuality ? Math.round(cam.calibrationQuality * 100) : 0;
        const lastCal = cam.lastCalibration ? new Date(cam.lastCalibration).toLocaleDateString() : 'Never';
        
        return `
            <div class="camera-card ${cam.isCalibrated ? 'calibrated' : 'uncalibrated'}">
                <div class="camera-info">
                    <div class="camera-name">
                        <span class="camera-icon">📷</span>
                        <span class="camera-id">${cam.cameraId}</span>
                        ${cam.displayName ? `<span class="camera-display-name">(${cam.displayName})</span>` : ''}
                    </div>
                    <div class="camera-details">
                        <span>Device: ${cam.deviceIndex}</span>
                        <span id="cam-status-${cam.cameraId}">
                            ${cam.isCalibrated ? 
                                `<span class="calibrated-badge">✓ Calibrated (${qualityPct}%)</span>` : 
                                '<span class="uncalibrated-badge">✗ Not calibrated</span>'}
                        </span>
                        <span class="last-cal">Last: ${lastCal}</span>
                    </div>
                </div>
                <div class="camera-actions">
                    <button onclick="cameraManager.calibrate('${cam.cameraId}')" class="btn-calibrate">
                        🎯 Calibrate
                    </button>
                    <button onclick="cameraManager.remove('${cam.cameraId}')" class="btn-remove">
                        🗑️
                    </button>
                </div>
            </div>
        `;
    }

    renderSummary() {
        const calibratedCount = this.cameras.filter(c => c.isCalibrated).length;
        const totalCount = this.cameras.length;
        const allCalibrated = calibratedCount === totalCount && totalCount > 0;
        
        return `
            <div class="summary ${allCalibrated ? 'ready' : 'not-ready'}">
                <span class="summary-icon">${allCalibrated ? '✓' : '⚠️'}</span>
                <span class="summary-text">
                    ${totalCount === 0 ? 'No cameras registered' :
                      allCalibrated ? 'All cameras calibrated - ready to play!' :
                      `${calibratedCount}/${totalCount} cameras calibrated`}
                </span>
                ${!allCalibrated && totalCount > 0 ? 
                    '<button onclick="cameraManager.calibrateAll()" class="btn-calibrate-all">Calibrate All</button>' : 
                    ''}
            </div>
        `;
    }

    showAddDialog() {
        const dialog = document.getElementById('add-camera-dialog');
        if (dialog) {
            dialog.classList.remove('hidden');
            // Auto-suggest next camera ID
            const nextIndex = this.cameras.length;
            document.getElementById('new-camera-id').value = `cam${nextIndex}`;
            document.getElementById('new-device-index').value = nextIndex;
        }
    }

    hideAddDialog() {
        const dialog = document.getElementById('add-camera-dialog');
        if (dialog) {
            dialog.classList.add('hidden');
        }
    }

    async addCamera() {
        const cameraId = document.getElementById('new-camera-id').value.trim();
        const deviceIndex = parseInt(document.getElementById('new-device-index').value, 10);
        const displayName = document.getElementById('new-display-name').value.trim() || null;
        
        if (!cameraId) {
            alert('Camera ID is required');
            return;
        }
        
        if (await this.register(cameraId, deviceIndex, displayName)) {
            this.hideAddDialog();
        }
    }

    async calibrateAll() {
        const uncalibrated = this.cameras.filter(c => !c.isCalibrated);
        for (const cam of uncalibrated) {
            await this.calibrate(cam.cameraId);
        }
    }
}

// CSS styles for camera manager
const cameraManagerStyles = `
.camera-manager {
    background: rgba(0, 0, 0, 0.6);
    border-radius: 8px;
    padding: 20px;
    margin-bottom: 20px;
}

.camera-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    margin-bottom: 20px;
}

.camera-header h3 {
    margin: 0;
    color: #fff;
}

.btn-add-camera {
    background: #4CAF50;
    color: white;
    border: none;
    padding: 8px 16px;
    border-radius: 4px;
    cursor: pointer;
}

.camera-list {
    display: flex;
    flex-direction: column;
    gap: 10px;
}

.no-cameras {
    color: #999;
    text-align: center;
    padding: 30px;
}

.camera-card {
    display: flex;
    justify-content: space-between;
    align-items: center;
    background: rgba(255, 255, 255, 0.05);
    padding: 15px;
    border-radius: 6px;
    border-left: 4px solid #666;
}

.camera-card.calibrated {
    border-left-color: #4CAF50;
}

.camera-card.uncalibrated {
    border-left-color: #f44336;
}

.camera-info {
    flex: 1;
}

.camera-name {
    display: flex;
    align-items: center;
    gap: 8px;
    margin-bottom: 5px;
}

.camera-id {
    font-weight: bold;
    color: #fff;
}

.camera-display-name {
    color: #999;
    font-size: 14px;
}

.camera-details {
    display: flex;
    gap: 20px;
    font-size: 13px;
    color: #aaa;
}

.calibrated-badge {
    color: #4CAF50;
}

.uncalibrated-badge {
    color: #f44336;
}

.camera-actions {
    display: flex;
    gap: 10px;
}

.btn-calibrate {
    background: #2196F3;
    color: white;
    border: none;
    padding: 6px 12px;
    border-radius: 4px;
    cursor: pointer;
}

.btn-remove {
    background: transparent;
    border: 1px solid #666;
    color: #999;
    padding: 6px 10px;
    border-radius: 4px;
    cursor: pointer;
}

.btn-remove:hover {
    border-color: #f44336;
    color: #f44336;
}

.camera-summary {
    margin-top: 20px;
    padding-top: 15px;
    border-top: 1px solid #333;
}

.summary {
    display: flex;
    align-items: center;
    gap: 10px;
    padding: 10px 15px;
    border-radius: 4px;
}

.summary.ready {
    background: rgba(76, 175, 80, 0.2);
    color: #4CAF50;
}

.summary.not-ready {
    background: rgba(244, 67, 54, 0.2);
    color: #ff8a80;
}

.btn-calibrate-all {
    margin-left: auto;
    background: #ff9800;
    color: white;
    border: none;
    padding: 6px 12px;
    border-radius: 4px;
    cursor: pointer;
}

.dialog {
    position: fixed;
    top: 0;
    left: 0;
    right: 0;
    bottom: 0;
    background: rgba(0, 0, 0, 0.8);
    display: flex;
    justify-content: center;
    align-items: center;
    z-index: 1000;
}

.dialog.hidden {
    display: none;
}

.dialog-content {
    background: #2a2a2a;
    padding: 25px;
    border-radius: 8px;
    min-width: 300px;
}

.dialog-content h4 {
    margin: 0 0 20px 0;
    color: #fff;
}

.form-group {
    margin-bottom: 15px;
}

.form-group label {
    display: block;
    color: #aaa;
    font-size: 13px;
    margin-bottom: 5px;
}

.form-group input {
    width: 100%;
    padding: 8px 12px;
    background: #333;
    border: 1px solid #444;
    border-radius: 4px;
    color: #fff;
}

.dialog-buttons {
    display: flex;
    gap: 10px;
    justify-content: flex-end;
    margin-top: 20px;
}

.dialog-buttons button {
    padding: 8px 16px;
    border-radius: 4px;
    cursor: pointer;
}

.dialog-buttons button.primary {
    background: #4CAF50;
    color: white;
    border: none;
}

.calibrating {
    color: #ff9800;
    animation: pulse 1s infinite;
}

@keyframes pulse {
    0%, 100% { opacity: 1; }
    50% { opacity: 0.5; }
}
`;

// Initialize global instance
let cameraManager;
