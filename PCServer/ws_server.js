
const WebSocket = require('ws');
const http = require('http');

const PORT = 3000;
const server = http.createServer((req, res) => {
  res.writeHead(200, { 'Content-Type': 'text/plain' });
  res.end('WebRTC Signaling Server (Quest3 -> PC)');
});

const wss = new WebSocket.Server({ server });

// Track connected peers by role
const peers = { quest: null, pc: null };

wss.on('connection', (ws, req) => {
  let role = null;
  console.log('[Signaling] New connection from', req.socket.remoteAddress);

  ws.on('message', (data) => {
    let msg;
    try { msg = JSON.parse(data); } catch { return; }
      console.log('[Signaling] Received message:', msg);
    // First message must declare role: { type: 'register', role: 'quest' | 'pc' }
    if (msg.type === 'register') {
      role = msg.role; // 'quest' or 'pc'
      peers[role] = ws;
      console.log(`[Signaling] Registered as: ${role}`);
      ws.send(JSON.stringify({ type: 'registered', role }));

      // If both are connected, tell quest to initiate the offer
      if (peers.quest && peers.pc) {
        console.log('[Signaling] Both peers connected. Telling Quest to create offer.');
        peers.quest.send(JSON.stringify({ type: 'ready' }));
      }
      return;
    }

    // Relay SDP offer from Quest -> PC
    if (msg.type === 'offer' && peers.pc) {
      console.log('[Signaling] Relaying offer to PC');
      peers.pc.send(JSON.stringify(msg));
      return;
    }

    // Relay SDP answer from PC -> Quest
    if (msg.type === 'answer' && peers.quest) {
      console.log('[Signaling] Relaying answer to Quest');
      peers.quest.send(JSON.stringify(msg));
      return;
    }

    // Relay ICE candidates bidirectionally
    if (msg.type === 'ice') {
      const target = role === 'quest' ? peers.pc : peers.quest;
      if (target) {
        console.log(`[Signaling] Relaying ICE from ${role}`);
        target.send(JSON.stringify(msg));
      }
      return;
    }
  });

  ws.on('close', () => {
    if (role && peers[role] === ws) {
      console.log(`[Signaling] ${role} disconnected`);
      peers[role] = null;
    }
  });

  ws.on('error', (err) => console.error('[Signaling] WS error:', err));
});

server.listen(PORT, '0.0.0.0', () => {
  console.log(`[Signaling] Server running on ws://0.0.0.0:${PORT}`);
  console.log(`[Signaling] Quest and PC should connect to your LAN IP on port ${PORT}`);
});
