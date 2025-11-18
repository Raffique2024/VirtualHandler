#!/bin/bash

# === Get script directory (contains .sh and handlerpc.json) ===
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# === Find handlerpc.json in same directory ===
JSON_FILE=$(find "$SCRIPT_DIR" -maxdepth 1 -type f -name "handlerpc.json" | head -n1)

if [[ -z "$JSON_FILE" ]]; then
    echo "handlerpc.json not found in script directory: $SCRIPT_DIR"
    exit 1
fi

# === Read WorkingDirectory from json (source path on host) ===
WORKDIR=$(jq -r '.WorkingDirectory' "$JSON_FILE")
if [[ -z "$WORKDIR" || "$WORKDIR" == "null" ]]; then
    echo "WorkingDirectory missing in handlerpc.json"
    exit 1
fi

# === ZIP + SHA come from WorkingDirectory on host ===
ZIP_FILE="$WORKDIR/SLT_TestProgram_release.zip"
CHECKSUM_FILE="$WORKDIR/SLT_TestProgram_release.sha1"

if [[ ! -f "$ZIP_FILE" || ! -f "$CHECKSUM_FILE" ]]; then
    echo "Required files not found in WorkingDirectory: $WORKDIR"
    echo "Expected:"
    echo "  $ZIP_FILE"
    echo "  $CHECKSUM_FILE"
    exit 1
fi

# === Deployment directory inside Docker / remote host ===
TARGET_DIR="/home/groq/Desktop/SLTGroq/Groq_SLT_Automation"
UNZIP_DIR="$TARGET_DIR/SLT_TestProgram_release"

SSH_USER="root"
SSH_PASS="docker"

# === Load hosts from handlerpc.json ===
HOSTS=$(jq -r '.SshHosts | join(",")' "$JSON_FILE")

# Max parallel deployments
MAX_PARALLEL=16

# Allow docker containers to access host X11
xhost +local:

# === Function: retry SSH connection ===
connect_retry() {
    local HOST=$1
    local PORT=$2
    local MAX_RETRIES=5
    local RETRY_DELAY=2
    local COUNT=0

    while [ $COUNT -lt $MAX_RETRIES ]; do
        sshpass -p "$SSH_PASS" ssh -o StrictHostKeyChecking=no -o ConnectTimeout=5 -p $PORT $SSH_USER@$HOST exit && return 0
        echo "Connection to $HOST:$PORT failed. Retrying in $RETRY_DELAY seconds..."
        sleep $RETRY_DELAY
        COUNT=$((COUNT+1))
    done
    echo "Failed to connect to $HOST:$PORT after $MAX_RETRIES attempts"
    return 1
}

# === Function: deploy to host ===
deploy_to_host() {
    local HOSTPORT=$1
    local HOST=${HOSTPORT%%:*}
    local PORT=${HOSTPORT##*:}

    echo "Deploying to $HOST:$PORT ..."

    ssh-keygen -f "$HOME/.ssh/known_hosts" -R "[$HOST]:$PORT" 2>/dev/null || true

    connect_retry $HOST $PORT || return

    # Create target directory inside Docker
    sshpass -p "$SSH_PASS" ssh -Y -o StrictHostKeyChecking=no -p $PORT $SSH_USER@$HOST \
        "mkdir -p '$TARGET_DIR'"

    # Copy ZIP and SHA files from host to Docker
    sshpass -p "$SSH_PASS" scp -P $PORT "$ZIP_FILE" "$SSH_USER@$HOST:$TARGET_DIR/"
    sshpass -p "$SSH_PASS" scp -P $PORT "$CHECKSUM_FILE" "$SSH_USER@$HOST:$TARGET_DIR/"

    # Run deployment inside Docker
    sshpass -p "$SSH_PASS" ssh -Y -t -o StrictHostKeyChecking=no -p $PORT $SSH_USER@$HOST bash << EOF
WORKDIR="$TARGET_DIR"
ZIP_FILE="\$WORKDIR/$(basename "$ZIP_FILE")"
CHECKSUM_FILE="\$WORKDIR/$(basename "$CHECKSUM_FILE")"
UNZIP_DIR="\$WORKDIR/SLT_TestProgram_release"

cd "\$WORKDIR" || { echo "Cannot cd to \$WORKDIR"; exit 1; }

echo "Verifying checksum..."
SHA_EXPECTED=\$(cut -d ' ' -f1 "\$CHECKSUM_FILE")
SHA_ACTUAL=\$(sha1sum "\$ZIP_FILE" | cut -d ' ' -f1)

if [ "\$SHA_EXPECTED" == "\$SHA_ACTUAL" ]; then
    echo "Checksum matches."

    # Stop previous LotStartProduction.sh processes
    PID=\$(pgrep -f "LotStartProduction.sh")
    if [ ! -z "\$PID" ]; then
        echo "Program is already running (PID \$PID). Stopping..."
        kill -9 \$PID
    fi

    echo "Cleaning old files..."
    mkdir -p "\$UNZIP_DIR"
    find "\$UNZIP_DIR" -mindepth 1 -exec rm -rf {} + 2>/dev/null || true

    echo "Unzipping new program..."
    unzip -o "\$ZIP_FILE" -d "\$UNZIP_DIR"

    # Make script executable
    chmod +x "\$UNZIP_DIR/LotStartProduction.sh"

    echo "Starting program with GUI..."
    export DISPLAY=$DISPLAY
    cd "\$UNZIP_DIR"
    ./LotStartProduction.sh &

else
    echo "Checksum mismatch. Aborting."
fi
EOF

    echo "Deployment finished for $HOST:$PORT"
}

# === Parallel host deployment ===
IFS=',' read -ra HOST_ARRAY <<< "$HOSTS"
count=0
for HOSTPORT in "${HOST_ARRAY[@]}"; do
    deploy_to_host "$HOSTPORT" &
    count=$((count+1))
    if [ $count -ge $MAX_PARALLEL ]; then
        wait
        count=0
    fi
done

wait
echo "All deployments completed."

