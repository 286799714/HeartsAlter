/**
 * Colyseus Cloud Deployment Configuration.
 * See documentation: https://docs.colyseus.io/deployment/cloud
 */

module.exports = {
  apps: [{
    name: "colyseus-app",
    script: "build/index.js",
    time: true,
    watch: false,
    // The default local MatchMaker/driver is process-local. Running several
    // forked workers makes a lobby-created seat reservation land in one worker
    // while the subsequent WebSocket join can be routed to another, producing
    // a misleading "seat already occupied" error. Use one worker until a
    // shared Redis presence/driver is configured.
    instances: 1,
    exec_mode: "fork",
    wait_ready: true,
    max_memory_restart: "512M",
  }],
};
