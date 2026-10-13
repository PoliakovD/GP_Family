const target = process.env['API_URL'] || 'http://localhost:5276';

module.exports = {
  '/api': {
    target,
    secure: false,
    changeOrigin: true,
    logLevel: 'info',
  },
  '/attachments': {
    target,
    secure: false,
    changeOrigin: true,
  },
  // SignalR-хаб чата семьи (ADR-0019): ws — апгрейд до WebSocket; без него клиент откатится на long polling.
  '/hubs': {
    target,
    secure: false,
    changeOrigin: true,
    ws: true,
  },
  '/bot': {
    target,
    secure: false,
    changeOrigin: true,
  },
};
