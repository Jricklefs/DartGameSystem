#!/bin/bash
# DartSensor installation script for Raspberry Pi

set -e

echo "=== DartSensor Installation ==="

# Check if running on Pi
if [[ ! -f /proc/device-tree/model ]] || ! grep -q "Raspberry Pi" /proc/device-tree/model 2>/dev/null; then
    echo "Warning: This doesn't appear to be a Raspberry Pi"
    echo "Continuing anyway..."
fi

# Update system
echo "Updating system packages..."
sudo apt-get update
sudo apt-get upgrade -y

# Install dependencies
echo "Installing system dependencies..."
sudo apt-get install -y \
    python3 \
    python3-pip \
    python3-venv \
    python3-opencv \
    libopencv-dev \
    libcamera-apps \
    libcamera-dev

# Create virtual environment
echo "Creating Python virtual environment..."
python3 -m venv venv
source venv/bin/activate

# Install Python packages
echo "Installing Python packages..."
pip install --upgrade pip
pip install -r requirements.txt

# Copy config if needed
if [[ ! -f config/settings.yaml ]]; then
    echo "Creating config from template..."
    cp config/settings.example.yaml config/settings.yaml
    echo ">>> Edit config/settings.yaml with your server URL and camera settings"
fi

# Create systemd service
echo "Creating systemd service..."
sudo tee /etc/systemd/system/dartsensor.service > /dev/null <<EOF
[Unit]
Description=DartSensor - Dart Detection Client
After=network.target

[Service]
Type=simple
User=$USER
WorkingDirectory=$(pwd)
ExecStart=$(pwd)/venv/bin/python src/main.py -c config/settings.yaml
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
EOF

echo "=== Installation Complete ==="
echo ""
echo "Next steps:"
echo "  1. Edit config/settings.yaml with your settings"
echo "  2. Test manually: source venv/bin/activate && python src/main.py"
echo "  3. Enable service: sudo systemctl enable dartsensor"
echo "  4. Start service: sudo systemctl start dartsensor"
echo ""
